using System.Runtime.InteropServices;
using NAudio.Wave;
using StretchCordNG.Services;

namespace StretchCordNG.Audio
{
    /// <summary>
    /// The exact 18-byte WAVEFORMATEX the audio engine expects across the COM boundary.
    /// NAudio's WaveFormat class cannot be used for this call — it marshals as a managed
    /// object and WASAPI rejects the result with E_INVALIDARG.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;

        public static WaveFormatEx Create(int sampleRate, int bitsPerSample, int channels, bool ieeeFloat = false)
        {
            int blockAlign = channels * Math.Max(bitsPerSample, 8) / 8;
            return new WaveFormatEx
            {
                wFormatTag = (ushort)(ieeeFloat ? 3 : 1), // WAVE_FORMAT_IEEE_FLOAT / PCM
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
                nBlockAlign = (ushort)blockAlign,
                wBitsPerSample = (ushort)bitsPerSample,
                cbSize = 0
            };
        }
    }

    /// <summary>
    /// Captures only the selected process tree's audio using Windows process-loopback activation.
    ///
    /// This intentionally does NOT fall back to full system loopback: system-loopback plus
    /// playback creates an infinite feedback loop when Discord captures StretchCord's own output.
    ///
    /// The capture thread is event-driven: the audio client signals an event whenever a
    /// packet is ready, and the thread waits on it instead of waking every few milliseconds
    /// to poll. That removes the fixed polling latency floor and the constant CPU wake-ups
    /// of the previous generation while keeping the same failure surface.
    ///
    /// NOTE: the byte[] handed to <see cref="AudioDataAvailable"/> is reused — consume it
    /// synchronously (which the playback ring buffer does).
    /// </summary>
    public sealed class ProcessAudioCapture : IDisposable
    {
        public event Action<byte[], int, WaveFormat>? AudioDataAvailable;
        public event Action<float>? LevelChanged;
        public event Action<string>? Error;

        private Thread? _captureThread;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public bool IsCapturing => _captureThread?.IsAlive == true;

        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
        private const ushort VT_BLOB = 65;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        private const uint COINIT_MULTITHREADED = 0x0;

        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        public void StartCapture(int processId, bool includeProcessTree = true)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProcessAudioCapture));
            if (_captureThread?.IsAlive == true) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _captureThread = new Thread(() => CaptureLoop(processId, includeProcessTree, token))
            {
                IsBackground = true,
                Name = "ProcessLoopbackAudioCapture",
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            var thread = _captureThread;
            _captureThread = null;
            if (thread != null && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
                thread.Join(3000);
            _cts?.Dispose();
            _cts = null;
        }

        private void CaptureLoop(int processId, bool includeTree, CancellationToken token)
        {
            // Audio capture is a streaming worker: run it on the efficiency cores so the
            // game keeps its performance cores. Priority still orders work within them.
            CpuTopology.EnsureThreadPlacement();

            int coInit = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            bool comInitialized = coInit >= 0 || coInit == RPC_E_CHANGED_MODE;

            IAudioClient? audioClient = null;
            try
            {
                if (!TryActivateProcessLoopback(processId, includeTree, out audioClient, out var failure))
                {
                    Error?.Invoke(failure ?? "Could not start process-loopback audio capture.");
                    return;
                }

                if (audioClient == null)
                {
                    Error?.Invoke("Process-loopback activation returned no audio client.");
                    return;
                }

                var (wfx, format) = ResolveFormat(audioClient);

                using var packetReady = new AutoResetEvent(false);
                var streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK
                                | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;

                int hr = audioClient.Initialize(
                    AUDCLNT_SHAREMODE_SHARED,
                    streamFlags,
                    0, 0, ref wfx, Guid.Empty);

                if (hr < 0)
                {
                    // Retry with plain 48 kHz PCM16 in case the endpoint rejected that format.
                    wfx = WaveFormatEx.Create(48000, 16, 2);
                    format = new WaveFormat(48000, 16, 2);
                    hr = audioClient.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        streamFlags,
                        0, 0, ref wfx, Guid.Empty);
                }

                if (hr < 0)
                {
                    Error?.Invoke($"IAudioClient.Initialize failed: 0x{hr:X8}");
                    return;
                }

                hr = audioClient.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    // Event signalling is unsupported on this endpoint: keep working through
                    // the timed wait below, which degrades to polling at the timeout rate.
                    AppLog.Write($"audio: SetEventHandle failed (0x{hr:X8}) — falling back to timed waits");
                }

                hr = audioClient.GetService(IID_IAudioCaptureClient, out object captureObject);
                if (hr < 0 || captureObject is not IAudioCaptureClient captureClient)
                {
                    Error?.Invoke($"GetService(IAudioCaptureClient) failed: 0x{hr:X8}");
                    return;
                }

                hr = audioClient.Start();
                if (hr < 0)
                {
                    Error?.Invoke($"IAudioClient.Start failed: 0x{hr:X8}");
                    return;
                }

                try
                {
                    PumpLoop(captureClient, format, packetReady, token);
                }
                finally
                {
                    try { audioClient.Stop(); } catch { /* already stopped */ }
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Audio capture failed: {ex.Message}");
            }
            finally
            {
                if (audioClient != null)
                {
                    try { Marshal.FinalReleaseComObject(audioClient); } catch { }
                }

                if (comInitialized && coInit != RPC_E_CHANGED_MODE)
                    CoUninitialize();
            }
        }

        /// <summary>
        /// Ask the endpoint for its mix format so we can capture without resampling, then
        /// hand IAudioClient a *plain* (non-EXTENSIBLE) WAVEFORMATEX. Passing the raw
        /// extensible blob is not safe here: a marshalled 18-byte struct cannot carry the
        /// 22 extra bytes WAVEFORMATEXTENSIBLE declares, and WASAPI answers E_INVALIDARG.
        /// </summary>
        private static (WaveFormatEx wfx, WaveFormat format) ResolveFormat(IAudioClient audioClient)
        {
            try
            {
                int hr = audioClient.GetMixFormat(out IntPtr ptr);
                if (hr >= 0 && ptr != IntPtr.Zero)
                {
                    try
                    {
                        var raw = Marshal.PtrToStructure<WaveFormatEx>(ptr);

                        bool isFloat = raw.wFormatTag == 3; // WAVE_FORMAT_IEEE_FLOAT
                        if (raw.wFormatTag == 0xFFFE && raw.cbSize >= 22)
                        {
                            var sub = Marshal.PtrToStructure<Guid>(IntPtr.Add(ptr, 24));
                            isFloat = sub == KsDataFormatSubtypeIeeeFloat;
                        }

                        int channels = raw.nChannels;
                        int rate = (int)raw.nSamplesPerSec;
                        if (channels is > 0 and <= 8 && rate is >= 8000 and <= 384000)
                        {
                            if (isFloat)
                            {
                                var wfx = WaveFormatEx.Create(rate, 32, channels, ieeeFloat: true);
                                return (wfx, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
                            }

                            int bits = raw.wBitsPerSample > 0 ? raw.wBitsPerSample : 16;
                            var pcm = WaveFormatEx.Create(rate, bits, channels);
                            return (pcm, new WaveFormat(rate, bits, channels));
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(ptr);
                    }
                }
            }
            catch { /* fall through */ }

            var fallback = WaveFormatEx.Create(48000, 32, 2, ieeeFloat: true);
            return (fallback, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        }

        private static readonly Guid KsDataFormatSubtypeIeeeFloat =
            new("00000003-0000-0010-8000-00aa00389b71");

        private bool TryActivateProcessLoopback(
            int processId,
            bool includeTree,
            out IAudioClient? audioClient,
            out string? failure)
        {
            audioClient = null;
            failure = null;

            var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = unchecked((uint)processId),
                    ProcessLoopbackMode = includeTree
                        ? PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE
                        : PROCESS_LOOPBACK_MODE.EXCLUDE_TARGET_PROCESS_TREE
                }
            };

            IntPtr activationParamsPtr = IntPtr.Zero;
            IActivateAudioInterfaceAsyncOperation? asyncOperation = null;
            var completionHandler = new ActivationCompletionHandler();

            try
            {
                activationParamsPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
                Marshal.StructureToPtr(activationParams, activationParamsPtr, false);

                var propVariant = new PROPVARIANT
                {
                    vt = VT_BLOB,
                    blob = new BLOB
                    {
                        cbSize = unchecked((uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>()),
                        pBlobData = activationParamsPtr
                    }
                };

                Guid iid = IID_IAudioClient;
                int hr = ActivateAudioInterfaceAsync(
                    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    ref iid,
                    ref propVariant,
                    completionHandler,
                    out asyncOperation);

                if (hr < 0)
                {
                    failure = $"ActivateAudioInterfaceAsync failed: 0x{hr:X8}";
                    return false;
                }

                if (!completionHandler.Wait(TimeSpan.FromSeconds(8)))
                {
                    failure = "Process-loopback activation timed out.";
                    return false;
                }

                if (completionHandler.CallbackHResult < 0)
                {
                    failure = $"Process-loopback activation callback failed: 0x{completionHandler.CallbackHResult:X8}";
                    return false;
                }

                if (completionHandler.ActivationHResult < 0)
                {
                    failure = $"Process-loopback activation failed: 0x{completionHandler.ActivationHResult:X8} " +
                              "(process-loopback requires Windows 10 2004 or later)";
                    return false;
                }

                if (completionHandler.ActivatedInterface is not IAudioClient client)
                {
                    failure = "Process-loopback activation did not return an IAudioClient.";
                    return false;
                }

                audioClient = client;
                return true;
            }
            finally
            {
                completionHandler.Dispose();
                if (asyncOperation != null)
                {
                    try { Marshal.FinalReleaseComObject(asyncOperation); } catch { }
                }

                if (activationParamsPtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(activationParamsPtr);
            }
        }

        /// <summary>
        /// Waits on the packet-ready event, then drains everything queued. A 100 ms timeout
        /// keeps the loop alive if an endpoint stops signalling the event (the documented
        /// fallback for loopback streams that never became event-driven).
        /// </summary>
        private void PumpLoop(IAudioCaptureClient capture, WaveFormat fmt, AutoResetEvent packetReady, CancellationToken token)
        {
            var reusable = new byte[Math.Max(fmt.SampleRate * fmt.BlockAlign / 10, 4096)];
            var handles = new WaitHandle[] { packetReady, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles, 100);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (index == 1) break; // cancelled
                if (!Drain(capture, fmt, reusable, token)) return;
            }
        }

        /// <summary>Drains every queued packet. Returns false when the capture failed.</summary>
        private bool Drain(IAudioCaptureClient capture, WaveFormat fmt, byte[] reusable, CancellationToken token)
        {
            int hr = capture.GetNextPacketSize(out uint packetSize);
            if (hr < 0)
            {
                Error?.Invoke($"Audio GetNextPacketSize failed: 0x{hr:X8}");
                return false;
            }

            while (packetSize > 0 && !token.IsCancellationRequested)
            {
                hr = capture.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
                if (hr < 0)
                {
                    Error?.Invoke($"Audio GetBuffer failed: 0x{hr:X8}");
                    return false;
                }

                int byteCount = checked((int)(numFrames * (uint)fmt.BlockAlign));
                try
                {
                    if (byteCount > 0)
                    {
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            // Nothing to play, but the meter must fall back to zero.
                            LevelChanged?.Invoke(0f);
                        }
                        else
                        {
                            if (byteCount > reusable.Length)
                            {
                                reusable = new byte[byteCount];
                            }

                            Marshal.Copy(dataPtr, reusable, 0, byteCount);
                            LevelChanged?.Invoke(ComputeRms(reusable, byteCount, fmt));
                            AudioDataAvailable?.Invoke(reusable, byteCount, fmt);
                        }
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(numFrames);
                }

                hr = capture.GetNextPacketSize(out packetSize);
                if (hr < 0)
                {
                    Error?.Invoke($"Audio GetNextPacketSize failed: 0x{hr:X8}");
                    return false;
                }
            }

            return true;
        }

        private static float ComputeRms(byte[] data, int byteCount, WaveFormat fmt)
        {
            int bytesPerSample = fmt.BitsPerSample / 8;
            if (bytesPerSample <= 0) return 0f;

            int sampleCount = byteCount / bytesPerSample;
            if (sampleCount <= 0) return 0f;

            double sum = 0;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < byteCount; i += 4)
                {
                    float v = BitConverter.ToSingle(data, i);
                    sum += v * v;
                }
            }
            else
            {
                for (int i = 0; i + 1 < byteCount; i += 2)
                {
                    double v = BitConverter.ToInt16(data, i) / 32768.0;
                    sum += v * v;
                }
            }

            return (float)Math.Sqrt(sum / sampleCount);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopCapture();
        }

        private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
        {
            private readonly ManualResetEventSlim _completed = new(false);

            public int CallbackHResult { get; private set; }
            public int ActivationHResult { get; private set; } = unchecked((int)0x8000FFFF);
            public object? ActivatedInterface { get; private set; }

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    CallbackHResult = operation.GetActivateResult(out int activationHr, out object activatedInterface);
                    ActivationHResult = activationHr;
                    ActivatedInterface = activatedInterface;
                    return 0;
                }
                catch (Exception ex)
                {
                    CallbackHResult = Marshal.GetHRForException(ex);
                    return CallbackHResult;
                }
                finally
                {
                    _completed.Set();
                }
            }

            public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);

            public void Dispose() => _completed.Dispose();
        }

        // ───────────────────────── COM plumbing ─────────────────────────

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            ref PROPVARIANT activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private enum AUDIOCLIENT_ACTIVATION_TYPE : uint { DEFAULT = 0, PROCESS_LOOPBACK = 1 }

        private enum PROCESS_LOOPBACK_MODE : uint
        {
            INCLUDE_TARGET_PROCESS_TREE = 0,
            EXCLUDE_TARGET_PROCESS_TREE = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public uint TargetProcessId;
            public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLOB
        {
            public uint cbSize;
            public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public BLOB blob;
        }

        [ComImport]
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WaveFormatEx pFormat, Guid audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
            [PreserveSig] int GetStreamLatency(out long phnsLatency);
            [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
            [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx pFormat, out IntPtr ppClosestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
            [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }
    }
}
