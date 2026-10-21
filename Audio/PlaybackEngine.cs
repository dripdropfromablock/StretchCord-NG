using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StretchCordNG.Audio
{
    /// <summary>
    /// Plays captured game audio through StretchCord so Discord can transmit it.
    ///
    /// A bounded ring buffer that:
    ///  - keeps a fixed, small presentation latency (configurable),
    ///  - zero-fills instead of stalling when the capture hiccups (no clicks, no drift),
    ///  - drops the oldest audio rather than accumulating delay if playback is starved,
    ///  - survives device changes without tearing down the graph mid-read.
    ///
    /// Two separate locks are used on purpose: the WASAPI thread only ever takes
    /// <see cref="_ringGate"/>, so disposing the output graph under <see cref="_graphGate"/>
    /// can never deadlock against a Read() in flight.
    /// </summary>
    public sealed class PlaybackEngine : IDisposable
    {
        private readonly object _graphGate = new();
        private readonly object _ringGate = new();

        private byte[] _buffer = Array.Empty<byte>();
        private int _read;
        private int _write;
        private int _count;

        private WasapiOut? _out;
        private MMDevice? _device;
        private WaveFormat? _format;
        private string? _deviceId;
        private bool _disposed;
        private bool _started;

        /// <summary>Amount of audio queued before playback starts consuming it.</summary>
        public int LatencyMs { get; set; } = 45;

        public string? OutputDeviceId => _deviceId;
        public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

        /// <summary>Audio currently queued, in milliseconds.</summary>
        public double BufferedMs
        {
            get
            {
                lock (_ringGate)
                {
                    var f = _format;
                    if (f == null || f.AverageBytesPerSecond == 0) return 0;
                    return 1000.0 * _count / f.AverageBytesPerSecond;
                }
            }
        }

        /// <summary>Null selects the Windows default render device.</summary>
        public void SetOutputDevice(string? deviceId)
        {
            if (string.Equals(_deviceId, deviceId, StringComparison.Ordinal)) return;
            _deviceId = deviceId;

            lock (_graphGate)
            {
                var f = _format;
                if (f != null && !_disposed) Rebuild(f);
            }
        }

        /// <summary>Queue captured audio. Safe to call from the capture thread.</summary>
        public void Feed(byte[] data, int byteCount, WaveFormat format)
        {
            if (_disposed || byteCount <= 0) return;

            lock (_graphGate)
            {
                if (_disposed) return;

                if (_format == null || !SameFormat(_format, format))
                {
                    _format = format;
                    Rebuild(format);
                }
                else if (_out == null)
                {
                    Rebuild(format);
                }
            }

            lock (_ringGate)
            {
                EnsureCapacity(format);
                if (_buffer.Length == 0) return;

                int toWrite = Math.Min(byteCount, _buffer.Length);
                int free = _buffer.Length - _count;

                if (toWrite >= free)
                {
                    // Playback is behind: drop the oldest audio so latency never grows.
                    int overflow = toWrite - free + 1;
                    if (overflow >= _count)
                    {
                        _read = 0; _write = 0; _count = 0;
                    }
                    else
                    {
                        _read = (_read + overflow) % _buffer.Length;
                        _count -= overflow;
                    }
                }

                int first = Math.Min(toWrite, _buffer.Length - _write);
                Buffer.BlockCopy(data, 0, _buffer, _write, first);
                if (toWrite > first) Buffer.BlockCopy(data, first, _buffer, 0, toWrite - first);
                _write = (_write + toWrite) % _buffer.Length;
                _count += toWrite;

                // Begin once the preroll buffer holds enough audio for a smooth ramp-up.
                if (!_started && _count >= BytesFor(LatencyMs))
                {
                    var output = _out;
                    if (output != null)
                    {
                        _started = true;
                        try { output.Play(); } catch { _started = false; }
                    }
                }
            }
        }

        private void Rebuild(WaveFormat format)
        {
            // Caller holds _graphGate.
            WasapiOut? old = _out;
            MMDevice? oldDevice = _device;
            _out = null;
            _device = null;
            _started = false;

            if (old != null)
            {
                try { old.Stop(); } catch { }
                try { old.Dispose(); } catch { }
            }
            try { oldDevice?.Dispose(); } catch { }

            if (_disposed) return;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!string.IsNullOrWhiteSpace(_deviceId))
                {
                    try { _device = enumerator.GetDevice(_deviceId); }
                    catch { _device = null; _deviceId = null; } // endpoint was unplugged
                }

                _out = _device != null
                    ? new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: LatencyMs)
                    : new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: LatencyMs);

                _out.Init(new RingProvider(this));
                // Play() is called from Feed() once the preroll buffer is full.
            }
            catch (Exception)
            {
                try { _out?.Dispose(); } catch { }
                _out = null;
            }
        }

        private int BytesFor(int ms)
        {
            var f = _format;
            if (f == null || ms <= 0) return 0;
            return Math.Max(f.BlockAlign, f.AverageBytesPerSecond * ms / 1000);
        }

        private void EnsureCapacity(WaveFormat format)
        {
            // Hold at least 250 ms so capture jitter is absorbed without audible dropouts
            // (the capture thread is event-driven now, so jitter is already small; the old
            // 500 ms floor just held memory the ring never used).
            int target = Math.Max(BytesFor(LatencyMs) * 4, format.AverageBytesPerSecond / 4);
            if (_buffer.Length >= target) return;

            var grown = new byte[target];
            if (_count > 0 && _buffer.Length > 0)
            {
                int first = Math.Min(_count, _buffer.Length - _read);
                Buffer.BlockCopy(_buffer, _read, grown, 0, first);
                if (_count > first) Buffer.BlockCopy(_buffer, 0, grown, first, _count - first);
            }

            _buffer = grown;
            _read = 0;
            _write = _count % _buffer.Length;
        }

        private static bool SameFormat(WaveFormat a, WaveFormat b)
            => a.SampleRate == b.SampleRate &&
               a.Channels == b.Channels &&
               a.BitsPerSample == b.BitsPerSample &&
               a.Encoding == b.Encoding;

        public void Stop()
        {
            lock (_ringGate)
            {
                _read = 0; _write = 0; _count = 0;
                _started = false;
            }

            lock (_graphGate)
            {
                try { _out?.Stop(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_graphGate)
            {
                if (_disposed) return;
                _disposed = true;

                var output = _out;
                var device = _device;
                _out = null;
                _device = null;

                try { output?.Stop(); } catch { }
                try { output?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
            }

            lock (_ringGate)
            {
                _read = 0; _write = 0; _count = 0;
            }
        }

        /// <summary>Wave source view over the ring buffer; silence when starved.</summary>
        private sealed class RingProvider : IWaveProvider
        {
            private readonly PlaybackEngine _owner;
            public RingProvider(PlaybackEngine owner) => _owner = owner;

            public WaveFormat WaveFormat => _owner._format ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

            public int Read(byte[] buffer, int offset, int count)
            {
                lock (_owner._ringGate)
                {
                    int available = Math.Min(count, _owner._count);
                    if (available > 0)
                    {
                        int first = Math.Min(available, _owner._buffer.Length - _owner._read);
                        Buffer.BlockCopy(_owner._buffer, _owner._read, buffer, offset, first);
                        if (available > first)
                            Buffer.BlockCopy(_owner._buffer, 0, buffer, offset + first, available - first);

                        _owner._read = (_owner._read + available) % _owner._buffer.Length;
                        _owner._count -= available;
                    }

                    if (available < count)
                        Array.Clear(buffer, offset + available, count - available);

                    return count;
                }
            }
        }
    }
}
