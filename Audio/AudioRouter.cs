using NAudio.CoreAudioApi;
using NAudio.Wave;
using StretchCordNG.Models;
using StretchCordNG.Services;

namespace StretchCordNG.Audio
{
    public enum MutePhase
    {
        /// <summary>Muting is not wanted (routing to a spare device, or disabled).</summary>
        Inactive,
        /// <summary>Waiting to hear the game before muting so the safety check can run.</summary>
        Armed,
        /// <summary>Measuring before/after to confirm capture does not go silent when muted.</summary>
        Verifying,
        /// <summary>Muting confirmed safe; the game's own speakers are off.</summary>
        Active,
        /// <summary>Muting would silence our own capture — left the game audible.</summary>
        Blocked
    }

    /// <summary>
    /// Decides *where* the captured game audio is replayed and *whether* the game's own
    /// audio session has to be muted, so the player hears the game exactly once.
    ///
    /// Rules:
    ///  1. Auto mode prefers a spare render endpoint that nothing is playing on, so the
    ///     copy Discord needs goes to a device you are not listening to.
    ///  2. If there is no spare endpoint, the copy has to share your device with the game,
    ///     so the game's own sessions are muted. Because it is unclear whether Windows taps
    ///     process audio before or after the session volume, muting is only committed after
    ///     a before/after level check proves our capture stays alive. If it does not, muting
    ///     is abandoned and the user is told to use a second output.
    /// </summary>
    public sealed class AudioRouter : IDisposable
    {
        private const float LoudThreshold = 0.015f;   // RMS that counts as "the game is audible"
        private const float CollapseRatio = 0.15f;    // post/pre below this ⇒ capture died
        private const int WindowMs = 1200;

        private readonly PlaybackEngine _engine = new();
        private readonly object _stateGate = new();

        private AppSettings _settings = new();
        private int? _targetPid;
        private HashSet<int>? _treePids;

        private MutePhase _phase = MutePhase.Inactive;
        private long _armedTicks;
        private long _windowStartTicks;
        private double _windowSum;
        private int _windowSamples;
        private float _preAverage;
        private bool _mutedByUs;
        private bool _waitingNotified;
        private Timer? _rescanTimer;
        private bool _disposed;

        public IReadOnlyList<OutputDevice> Devices { get; private set; } = Array.Empty<OutputDevice>();
        public OutputDevice? ActiveDevice { get; private set; }
        public bool UsingAutoRoute { get; private set; } = true;
        public MutePhase Phase { get; private set; } = MutePhase.Inactive;
        public string Summary { get; private set; } = "Select a window to begin.";
        public string? Warning { get; private set; }

        /// <summary>Raised whenever anything user-visible changed (any thread).</summary>
        public event Action? StateChanged;

        public PlaybackEngine Engine => _engine;
        public double BufferedMs => _engine.BufferedMs;
        public bool IsMutingGame => Phase == MutePhase.Active;

        // ────────────────────────── devices ──────────────────────────

        public void RefreshDevices()
        {
            var list = new List<OutputDevice>();
            string? defaultId = null;

            try
            {
                using var enumerator = new MMDeviceEnumerator();

                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def.ID;
                }
                catch { /* no default device */ }

                foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var device = new OutputDevice(endpoint.ID, endpoint.FriendlyName,
                        isDefault: endpoint.ID == defaultId)
                    {
                        HasPlayingSessions = AudioSessionService.HasActiveSessions(endpoint.ID, out _)
                    };
                    list.Add(device);
                    endpoint.Dispose();
                }
            }
            catch { /* audio subsystem unavailable */ }

            if (list.Count == 0)
                list.Add(new OutputDevice(null, "Windows default output", isDefault: true));

            Devices = list;
        }

        /// <summary>Chooses the endpoint playback should use under the current settings.</summary>
        private OutputDevice ResolveDevice(AppSettings settings)
        {
            var defaultDevice = Devices.FirstOrDefault(d => d.IsDefault) ?? Devices[0];

            if (!string.IsNullOrWhiteSpace(settings.AudioOutputId))
            {
                var manual = Devices.FirstOrDefault(d => d.Id == settings.AudioOutputId);
                if (manual != null) return manual;

                AppLog.Write("audio: the saved output device is gone — falling back to the automatic choice");
            }

            // Auto: the copy must be played somewhere the player cannot hear it, because
            // muting the game is usually not an option — Windows hands process-loopback
            // capture the audio *after* the session volume, so muting the game silences
            // Discord too (the mute check below proves this at runtime and reverts).
            //
            // Virtual endpoints are the right target: nothing is wired to them, so the
            // replay is inaudible to the player while this process still renders it and
            // Discord still taps it. A spare that already has audio playing is a last
            // resort — something else is using it.
            // Inaudibility comes first: a virtual cable beats an idle monitor, because a
            // monitor is only silent if its volume happens to be down, and a device that is
            // already playing something (not us) is the worst choice of all.
            var spare = Devices
                .Where(d => !d.IsDefault)
                .OrderByDescending(d => LooksVirtual(d.Name))
                .ThenBy(d => d.HasPlayingSessions)
                .FirstOrDefault();

            return spare ?? defaultDevice;
        }

        // ────────────────────────── configuration ──────────────────────────

        public void Reconfigure(AppSettings settings, int? targetPid)
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _settings = settings;
                _targetPid = targetPid;
                _engine.LatencyMs = Math.Clamp(settings.AudioLatencyMs, 10, 300);
            }

            RefreshDevices();
            ApplyRouting();
        }

        private void ApplyRouting()
        {
            AppSettings settings;
            int? pid;
            lock (_stateGate)
            {
                if (_disposed) return;
                settings = _settings;
                pid = _targetPid;
            }

            var device = ResolveDevice(settings);
            ActiveDevice = device;
            UsingAutoRoute = string.IsNullOrWhiteSpace(settings.AudioOutputId);

            _engine.SetOutputDevice(device.Id);

            // Muting the game is only needed when the copy is played on the same device
            // the game itself uses — otherwise the player hears the game live (once) and
            // the copy goes to an endpoint they cannot hear. If muting silences our own
            // capture as well, the check below detects it and the warning says what to do.
            bool wantMute = device.IsDefault && settings.AutoMuteGame && pid.HasValue;

            StopRescan();

            if (!wantMute)
            {
                SetPhase(MutePhase.Inactive);
                UnmuteGame();
                BuildSummary(device, settings, muted: false);
            }
            else
            {
                _treePids = NativeMethods.GetProcessTree(pid!.Value);
                lock (_stateGate)
                {
                    _windowStartTicks = 0;
                    _preAverage = 0;
                    _waitingNotified = false;
                    _armedTicks = Environment.TickCount64;
                }
                SetPhase(MutePhase.Armed);
                BuildSummary(device, settings, muted: false, mutingPending: true);
            }

            StateChanged?.Invoke();
        }

        /// <summary>Endpoints named like virtual cables are not connected to speakers.</summary>
        private static bool LooksVirtual(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Note: the GPU driver name is deliberately not a keyword — "MAG 255XF
            // (NVIDIA High Definition Audio)" is a real monitor with real speakers, and
            // treating it as virtual is how the copy ended up audible in the first place.
            return name.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || name.Contains("dummy", StringComparison.OrdinalIgnoreCase)
                || name.Contains("cable", StringComparison.OrdinalIgnoreCase)
                || name.Contains("voicemod", StringComparison.OrdinalIgnoreCase)
                || name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
                || name.Contains("steam streaming", StringComparison.OrdinalIgnoreCase);
        }

        private void BuildSummary(OutputDevice device, AppSettings settings, bool muted, bool mutingPending = false)
        {
            Warning = null;

            if (muted)
            {
                Summary = $"Playing the game through {device.ShortName} and muting the game's own output — " +
                          $"you hear it once, about {settings.AudioLatencyMs} ms behind.";
                return;
            }

            if (mutingPending)
            {
                Summary = device.IsDefault
                    ? $"One output device — checking the game's audio, then muting it so you only hear it once."
                    : $"Replaying to {device.ShortName}.";
                return;
            }

            Summary = device.IsDefault
                ? "Replaying to your main output device."
                : $"Playing the copy through “{device.ShortName}” — you keep hearing the game live, Discord gets the copy.";

            if (!device.IsDefault)
            {
                Warning = $"Deafening check: if you can hear “{device.ShortName}” yourself, the game will sound twice. " +
                          "Pick a virtual or unused output, or turn that device's volume down.";
            }
            else if (!settings.AutoMuteGame)
            {
                Warning = "“Mute game audio” is off, so anything you play through this output is a second copy of the game.";
            }
            else
            {
                Warning = "Waiting for the game to play a sound before the game's own output can be muted.";
            }
        }

        // ────────────────────────── mute verification ──────────────────────────

        /// <summary>Called from the capture thread for every packet.</summary>
        public void HandleCaptureLevel(float level)
        {
            MutePhase phase;
            lock (_stateGate)
            {
                if (_disposed) return;
                phase = _phase;
            }

            if (phase == MutePhase.Armed) StepArmed(level);
            else if (phase == MutePhase.Verifying) StepVerifying(level);
        }

        private void StepArmed(float level)
        {
            long now = Environment.TickCount64;
            bool raiseState = false;
            bool proceedToMute = false;

            lock (_stateGate)
            {
                if (_windowStartTicks == 0)
                {
                    if (level < LoudThreshold)
                    {
                        // Nothing audible yet — wait for the game to make a sound so the
                        // before/after comparison actually means something. We keep waiting
                        // for as long as it takes; a quiet game has nothing to double up.
                        if (!_waitingNotified && now - _armedTicks > 3000)
                        {
                            _waitingNotified = true;
                            Summary = "Waiting for the game to play audio — muting starts as soon as there is something to verify.";
                            raiseState = true;
                        }
                    }
                    else
                    {
                        _windowStartTicks = now;
                        _windowSum = 0;
                        _windowSamples = 0;
                    }
                }

                if (_windowStartTicks != 0)
                {
                    _windowSum += level;
                    _windowSamples++;

                    if (now - _windowStartTicks >= WindowMs)
                    {
                        _preAverage = (float)(_windowSum / Math.Max(1, _windowSamples));
                        _windowStartTicks = 0;
                        proceedToMute = true;
                    }
                }
            }

            if (raiseState) StateChanged?.Invoke();
            if (!proceedToMute) return;

            // Mute, then measure what our capture looks like afterwards.
            var pids = _treePids;
            if (pids == null) { SetPhase(MutePhase.Inactive); return; }

            int muted = AudioSessionService.MuteProcesses(pids);
            if (muted <= 0)
            {
                // The session may not exist yet (game still starting) — keep waiting, but
                // say so, because "the game is loud yet nothing could be muted" is the one
                // case where the user keeps hearing two copies.
                AppLog.Write("audio: game is audible but no audio session could be muted yet — retrying");
                lock (_stateGate) { _windowStartTicks = 0; }
                return;
            }

            _mutedByUs = true;
            AppLog.Write($"audio: muting the game's own output ({pids.Count} process ids, {muted} session(s)) so it cannot be heard twice");

            lock (_stateGate)
            {
                _windowStartTicks = Environment.TickCount64;
                _windowSum = 0;
                _windowSamples = 0;
            }
            SetPhase(MutePhase.Verifying);
            StateChanged?.Invoke();
        }

        private void StepVerifying(float level)
        {
            float pre;
            lock (_stateGate)
            {
                if (_windowStartTicks == 0) return;
                _windowSum += level;
                _windowSamples++;

                if (Environment.TickCount64 - _windowStartTicks < WindowMs) return;

                pre = _preAverage;
                var post = (float)(_windowSum / Math.Max(1, _windowSamples));
                _windowStartTicks = 0;

                if (pre > LoudThreshold && post < pre * CollapseRatio)
                {
                    // Our own capture died when the game went quiet ⇒ Windows taps this
                    // process after its session volume. Unmuting is the only safe choice.
                    AudioSessionService.UnmuteProcesses(_treePids ?? new HashSet<int>());
                    AppLog.Write("audio: muting the game silenced our own capture — unmuted again (Windows taps audio after the session volume on this machine)");
                    Warning = "Couldn't remove the echo automatically: Windows captures the game's audio after its volume, " +
                              "so muting the game would silence Discord too. Choose a second output device (HDMI/monitor, " +
                              "headset + speakers, or a virtual cable).";
                    SetPhase(MutePhase.Blocked);
                    StopRescan();
                    StateChanged?.Invoke();
                    return;
                }

                SetPhase(MutePhase.Active);
                AppLog.Write($"audio: game muted and capture still alive (pre={pre:0.####} post={post:0.####}) — playing it once");
                StartRescan();
                BuildSummary(ActiveDevice ?? Devices.FirstOrDefault() ?? new OutputDevice(null, "Windows default output", true),
                    _settings, muted: true);
                StateChanged?.Invoke();
            }
        }

        // ────────────────────────── background re-scan ──────────────────────────

        private void StartRescan()
        {
            StopRescan();
            _rescanTimer = new Timer(_ =>
            {
                var pids = _treePids;
                if (pids == null) return;
                try { AudioSessionService.MuteProcesses(pids); } catch { }
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }

        private void StopRescan()
        {
            var timer = _rescanTimer;
            _rescanTimer = null;
            timer?.Dispose();
        }

        private void UnmuteGame()
        {
            if (!_mutedByUs) return;   // never undo a mute the user set themselves

            var pids = _treePids;
            _mutedByUs = false;
            if (pids == null || pids.Count == 0) return;
            try { AudioSessionService.UnmuteProcesses(pids); } catch { }
        }

        private void SetPhase(MutePhase phase)
        {
            lock (_stateGate) { _phase = phase; Phase = phase; }
        }

        // ────────────────────────── playback ──────────────────────────

        public void Feed(byte[] data, int count, WaveFormat format) => _engine.Feed(data, count, format);

        public void NotifyCaptureStopped()
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _windowStartTicks = 0;
                _preAverage = 0;
            }

            StopRescan();
            if (Phase == MutePhase.Verifying)
            {
                // Never leave the game muted if we never confirmed the check.
                UnmuteGame();
                SetPhase(MutePhase.Armed);
            }
            else if (Phase == MutePhase.Active)
            {
                SetPhase(MutePhase.Armed);
                StopRescan();
                UnmuteGame();
            }

            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            StopRescan();
            UnmuteGame();
            _engine.Dispose();
        }
    }
}
