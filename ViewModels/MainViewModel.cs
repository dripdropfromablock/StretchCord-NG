using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using StretchCordNG.Audio;
using StretchCordNG.Capture;
using StretchCordNG.Models;
using StretchCordNG.Services;

namespace StretchCordNG.ViewModels
{
    /// <summary>
    /// Owns the whole pipeline: window capture → D3DImage presentation, process audio →
    /// routing/playback, plus settings persistence and all user-visible status.
    /// </summary>
    public sealed class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly VideoCaptureService _capture = new();
        private readonly D3DImagePresenter _presenter;
        private readonly ProcessAudioCapture _audioCapture = new();
        private readonly AudioRouter _router = new();
        private readonly Dispatcher _dispatcher;

        private AppSettings _settings;

        // ── bindable state ────────────────────────────────────────────────
        private WindowInfo? _source;
        private bool _videoActive;
        private bool _audioActive;
        private string _statusMessage = "Ready.";
        private float _audioLevel;
        private double _fps;
        private int _sourceWidth;
        private int _sourceHeight;
        private bool _showCursor;
        private bool _stretchFill = true;
        private bool _preferEfficiencyCores = true;
        private int _maxFps = 60;
        private bool _autoMuteGame = true;
        private int _audioLatencyMs = 45;
        private DeviceChoice? _selectedDevice;
        private string _audioSummary = "No output device selected yet.";
        private string? _audioWarning;
        private bool _busy;
        private bool _syncingDevices;
        private float _smoothed;
        private long _lastLevelNotify;

        /// <summary>Manual pointer correction for the game currently selected.</summary>
        private PointerCalibration _calibration = new();

        public MainViewModel()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _settings = SettingsStore.Load();

            _presenter = new D3DImagePresenter(_capture,
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
            {
                MaxFps = _settings.MaxFps
            };

            ApplySettingsToState();

            // ── video wiring ──────────────────────────────────────────────
            _presenter.FramePresented += image => FramePresented?.Invoke(image);

            _capture.Error += message =>
            {
                AppLog.Write($"Video: {message}");
                PostStatus($"Video: {message}");
            };
            _capture.SourceSizeChanged += (w, h) => _dispatcher.InvokeAsync(() =>
            {
                SourceWidth = w;
                SourceHeight = h;
            });
            _capture.SourceClosed += () => _dispatcher.InvokeAsync(() =>
            {
                if (!VideoActive) return;
                VideoActive = false;
                StatusMessage = "The captured window closed — video stopped.";
                RaiseStateChanged();
            });

            // ── audio wiring ──────────────────────────────────────────────
            _audioCapture.AudioDataAvailable += (data, count, format) => _router.Feed(data, count, format);
            _audioCapture.LevelChanged += OnCaptureLevel;
            _audioCapture.Error += message => _dispatcher.InvokeAsync(() =>
            {
                AppLog.Write($"Audio: {message}");
                AudioActive = false;
                _audioCapture.StopCapture();
                _router.NotifyCaptureStopped();
                StatusMessage = $"Audio: {message}";
                RaiseStateChanged();
            });

            _router.StateChanged += () => _dispatcher.InvokeAsync(SyncRouterState);

            RefreshDevices();
            SelectLastWindow();
            LoadCalibrationForSource();
        }

        // ── events for the view ───────────────────────────────────────────
        public event Action<System.Windows.Interop.D3DImage>? FramePresented;
        public event Action<string>? Error;
        public event Action? StateChanged;

        // ── properties ────────────────────────────────────────────────────
        public AppSettings Settings => _settings;

        public WindowInfo? Source
        {
            get => _source;
            set
            {
                if (!SetProperty(ref _source, value)) return;
                OnPropertyChanged(nameof(SourceLabel));
                OnPropertyChanged(nameof(HasSource));
                OnPropertyChanged(nameof(CanStart));
                SaveSettings();

                // A correction belongs to one game, so switching game switches correction.
                LoadCalibrationForSource();
            }
        }

        public string SourceLabel => _source == null
            ? "No window selected"
            : $"{_source.Title} — {_source.ProcessName} (PID {_source.ProcessId}, {_source.SizeLabel})";

        public bool HasSource => _source != null;
        public bool CanStart => _source != null && !_busy;

        public bool VideoActive
        {
            get => _videoActive;
            private set
            {
                if (!SetProperty(ref _videoActive, value)) return;
                OnPropertyChanged(nameof(StreamButtonLabel));
                OnPropertyChanged(nameof(IsIdle));
                RaiseStateChanged();
            }
        }

        public bool AudioActive
        {
            get => _audioActive;
            private set
            {
                if (!SetProperty(ref _audioActive, value)) return;
                OnPropertyChanged(nameof(StreamButtonLabel));
                OnPropertyChanged(nameof(IsIdle));
                RaiseStateChanged();
            }
        }

        public bool IsIdle => !VideoActive && !AudioActive;

        public string StreamButtonLabel => (VideoActive || AudioActive) ? "Stop stream" : "Start stream";

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public float AudioLevel
        {
            get => _audioLevel;
            private set => SetProperty(ref _audioLevel, value);
        }

        public double Fps
        {
            get => _fps;
            private set => SetProperty(ref _fps, value);
        }

        public int SourceWidth
        {
            get => _sourceWidth;
            private set
            {
                if (!SetProperty(ref _sourceWidth, value)) return;
                OnPropertyChanged(nameof(SourceSizeLabel));
            }
        }

        public int SourceHeight
        {
            get => _sourceHeight;
            private set => SetProperty(ref _sourceHeight, value);
        }

        public string SourceSizeLabel => _sourceWidth > 0
            ? $"{_sourceWidth}×{_sourceHeight} · {_presenter.FramesPerSecond:0} fps"
            : "waiting for frames…";

        public ObservableCollection<DeviceChoice> Devices { get; } = new();

        public DeviceChoice? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (!SetProperty(ref _selectedDevice, value)) return;
                if (_syncingDevices || value == null) return;

                _settings.AudioOutputId = value.Device?.Id;
                SaveSettings();
                ApplyAudioRouting();
            }
        }

        public bool ShowCursor
        {
            get => _showCursor;
            set
            {
                if (!SetProperty(ref _showCursor, value)) return;
                _settings.ShowCursor = value;
                SaveSettings();
                RaiseStateChanged();
            }
        }

        public bool PreferEfficiencyCores
        {
            get => _preferEfficiencyCores;
            set
            {
                if (!SetProperty(ref _preferEfficiencyCores, value)) return;
                _settings.PreferEfficiencyCores = value;
                SaveSettings();
                CpuTopology.SetEnabled(value);
            }
        }

        public bool StretchFill
        {
            get => _stretchFill;
            set
            {
                if (!SetProperty(ref _stretchFill, value)) return;
                _settings.StretchMode = value ? StretchMode.Fill : StretchMode.Fit;
                SaveSettings();
                RaiseStateChanged();
            }
        }

        public int MaxFps
        {
            get => _maxFps;
            set
            {
                if (!SetProperty(ref _maxFps, value)) return;
                _settings.MaxFps = value;
                _presenter.MaxFps = value;
                SaveSettings();
            }
        }

        public bool AutoMuteGame
        {
            get => _autoMuteGame;
            set
            {
                if (!SetProperty(ref _autoMuteGame, value)) return;
                _settings.AutoMuteGame = value;
                SaveSettings();
                ApplyAudioRouting();
            }
        }

        public int AudioLatencyMs
        {
            get => _audioLatencyMs;
            set
            {
                if (!SetProperty(ref _audioLatencyMs, value)) return;
                _settings.AudioLatencyMs = value;
                SaveSettings();
                ApplyAudioRouting();
            }
        }

        public string AudioSummary
        {
            get => _audioSummary;
            private set => SetProperty(ref _audioSummary, value);
        }

        public string? AudioWarning
        {
            get => _audioWarning;
            private set
            {
                if (SetProperty(ref _audioWarning, value))
                    OnPropertyChanged(nameof(HasAudioWarning));
            }
        }

        public bool HasAudioWarning => !string.IsNullOrEmpty(_audioWarning);

        // ── commands ──────────────────────────────────────────────────────

        public IReadOnlyList<ValueOption> FpsOptions { get; } = new[]
        {
            new ValueOption(30, "30 fps — smoothest"),
            new ValueOption(60, "60 fps — recommended"),
            new ValueOption(120, "120 fps — high refresh"),
            new ValueOption(0, "Unlimited"),
        };

        public IReadOnlyList<ValueOption> LatencyOptions { get; } = new[]
        {
            new ValueOption(25, "25 ms — minimum"),
            new ValueOption(45, "45 ms — balanced"),
            new ValueOption(90, "90 ms — smoothest"),
        };
        // Commands are wired from the view because they need dialogs.

        public void ToggleStream()
        {
            if (VideoActive || AudioActive) StopStream();
            else StartStream();
        }

        /// <summary>Swap the captured window, keeping the stream running if it was live.</summary>
        public void ChangeSource(WindowInfo window)
        {
            bool wasLive = VideoActive || AudioActive;
            if (wasLive) StopStream();

            Source = window;
            RememberSelectedWindow();

            if (wasLive) StartStream();
        }

        // ── pipeline control ──────────────────────────────────────────────
        public void StartStream()
        {
            if (Source == null)
            {
                StatusMessage = "Pick the game window first.";
                RaiseStateChanged();
                return;
            }

            StartVideo();
            StartAudio();
        }

        public void StopStream()
        {
            StopVideo();
            StopAudio();
        }

        private void StartVideo()
        {
            if (Source == null || VideoActive) return;

            _busy = true;
            OnPropertyChanged(nameof(CanStart));
            try
            {
                if (!VideoCaptureService.IsSupported())
                    throw new InvalidOperationException(
                        "Windows.Graphics.Capture is not available. " +
                        "StretchCord needs Windows 10 version 1903 or later.");

                _capture.Start(Source);
                _presenter.MaxFps = _settings.MaxFps;
                VideoActive = true;

                // A minimized game hands us its placeholder rectangle until it is restored,
                // so say that instead of appearing to do nothing.
                StatusMessage = Source.IsMinimized
                    ? $"{Source.ProcessName} is minimized — switch back to it and the picture appears."
                    : $"Capturing {Source.ProcessName}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Video error: {ex.Message}";
                AppLog.Exception("StartVideo", ex);
                Error?.Invoke($"Could not start video capture:\n{ex.Message}");
            }
            finally
            {
                _busy = false;
                OnPropertyChanged(nameof(CanStart));
            }
        }

        private void StopVideo()
        {
            if (!VideoActive) return;
            _capture.Stop();

            // Nothing is streaming: give the shared presentation texture back now instead
            // of holding it until the next run (it is recreated on the first new frame).
            _presenter.ReleasePresentationSurface();

            VideoActive = false;
            StatusMessage = "Video stopped.";
        }

        private void StartAudio()
        {
            if (Source == null || AudioActive) return;

            try
            {
                _audioCapture.StartCapture(Source.ProcessId, includeProcessTree: true);
                AudioActive = true;
                ApplyAudioRouting();
                StatusMessage = AudioActive && VideoActive
                    ? "Streaming — video and audio live."
                    : "Audio capture started.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Audio error: {ex.Message}";
                AppLog.Exception("StartAudio", ex);
                Error?.Invoke($"Could not start audio capture:\n{ex.Message}");
            }
        }

        private void StopAudio()
        {
            if (!AudioActive) return;
            _audioCapture.StopCapture();
            _router.NotifyCaptureStopped();
            _router.Engine.Stop();
            AudioLevel = 0;
            AudioActive = false;
            StatusMessage = VideoActive ? "Video still running." : "Audio stopped.";
        }

        private void ApplyAudioRouting()
        {
            if (!AudioActive)
            {
                RefreshDevices();
                SyncRouterState();
                return;
            }

            _router.Reconfigure(_settings, Source?.ProcessId);
            RefreshDevices();
            SyncRouterState();
        }

        private void RefreshDevices()
        {
            _router.RefreshDevices();

            var previousId = _settings.AudioOutputId;
            _syncingDevices = true;
            try
            {
                Devices.Clear();
                Devices.Add(new DeviceChoice(null, "Auto — hear the game exactly once (recommended)"));

                foreach (var device in _router.Devices)
                    Devices.Add(new DeviceChoice(device, device.ToString()));

                SelectedDevice = Devices.FirstOrDefault(c =>
                        c.Device == null
                            ? string.IsNullOrEmpty(previousId)
                            : c.Device.Id == previousId)
                    ?? Devices[0];
            }
            finally
            {
                _syncingDevices = false;
            }
        }

        private void SyncRouterState()
        {
            AudioSummary = _router.Summary;
            AudioWarning = _router.Warning;
            OnPropertyChanged(nameof(IsGameMuted));
            OnPropertyChanged(nameof(MuteStateLabel));
        }

        public bool IsGameMuted => _router.IsMutingGame;

        public string MuteStateLabel => _router.IsMutingGame
            ? "GAME MUTED — ONCE ONLY"
            : _router.Phase == MutePhase.Armed || _router.Phase == MutePhase.Verifying
                ? "CHECKING MUTE…"
                : string.Empty;

        private void OnCaptureLevel(float level)
        {
            _router.HandleCaptureLevel(level);

            // Smoothed meter, throttled so we don't raise property changes on every packet.
            _smoothed = _smoothed * 0.75f + level * 0.25f;
            var now = Environment.TickCount64;
            if (now - _lastLevelNotify < 60) return;
            _lastLevelNotify = now;

            var value = Math.Min(1f, _smoothed * 3.2f);
            _dispatcher.InvokeAsync(() => AudioLevel = value, DispatcherPriority.Background);
        }

        // ── fps / diagnostics tick (called by the view) ───────────────────
        public void Tick()
        {
            Fps = _presenter.FramesPerSecond;
            OnPropertyChanged(nameof(SourceSizeLabel));
        }

        private void SelectLastWindow()
        {
            if (string.IsNullOrEmpty(_settings.LastProcessName)) return;

            var match = WindowEnumerator
                .GetCapturableWindows()
                .FirstOrDefault(w => string.Equals(w.ProcessName, _settings.LastProcessName, StringComparison.OrdinalIgnoreCase));

            if (match != null) Source = match;
        }

        public void RememberSelectedWindow()
        {
            if (_source == null) return;
            _settings.LastProcessName = _source.ProcessName;
            SaveSettings();
        }

        public void PostStatus(string message)
            => _dispatcher.InvokeAsync(() => StatusMessage = message);

        public void Save() => SaveSettings();

        public void RaiseStateChanged() => StateChanged?.Invoke();

        private void SaveSettings() => SettingsStore.Save(_settings);

        private void ApplySettingsToState()
        {
            _showCursor = _settings.ShowCursor;
            _stretchFill = _settings.StretchMode == StretchMode.Fill;
            _preferEfficiencyCores = _settings.PreferEfficiencyCores;
            CpuTopology.SetEnabled(_preferEfficiencyCores);
            _maxFps = _settings.MaxFps;
            _autoMuteGame = _settings.AutoMuteGame;
            _audioLatencyMs = _settings.AudioLatencyMs;
        }

        // ── pointer calibration (per game) ─────────────────────────────────

        /// <summary>Which game the correction below belongs to.</summary>
        public string CalibrationScope => _source == null
            ? "Applies to whichever game you pick, and is remembered per game."
            : $"Applies to {_source.ProcessName} only, and is remembered for next time.";

        public string CalibrationSummary => _calibration.IsIdentity
            ? "Mapping as detected — adjust only if the pointer sits in the wrong place."
            : "Adjusted: " + _calibration.Describe();

        public string CalibrationOffsetText
            => $"nudge {_calibration.OffsetX:+0.##;-0.##;0}, {_calibration.OffsetY:+0.##;-0.##;0} px";

        public string CalibrationScaleXText
        {
            get => _calibration.ScaleX.ToString("0.###", CultureInfo.InvariantCulture);
            set => SetCalibrationPart(0, value);
        }

        public string CalibrationScaleYText
        {
            get => _calibration.ScaleY.ToString("0.###", CultureInfo.InvariantCulture);
            set => SetCalibrationPart(1, value);
        }

        private void SetCalibrationPart(int part, string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return;

            switch (part)
            {
                case 0: _calibration.ScaleX = value; break;
                case 1: _calibration.ScaleY = value; break;
            }

            ApplyCalibration();
        }

        /// <summary>Nudges the pointer correction in whole frame pixels.</summary>
        public void NudgePointer(double dx, double dy)
        {
            _calibration.OffsetX += dx;
            _calibration.OffsetY += dy;
            ApplyCalibration();
        }

        public void ResetCalibration()
        {
            _calibration = new PointerCalibration();
            ApplyCalibration();
        }

        private void ApplyCalibration()
        {
            _calibration.Clamp();
            CursorOverlayService.SetCalibration(_calibration);

            if (_source == null)
            {
                // A correction is stored per game, so with no game there is nowhere to put
                // it. Say so instead of appearing to save it.
                StatusMessage = "Pick a game window first - the pointer correction is saved per game.";
                NotifyCalibration();
                return;
            }

            var key = _source.ProcessName.ToLowerInvariant();
            if (_calibration.IsIdentity) _settings.PointerCalibrations.Remove(key);
            else _settings.PointerCalibrations[key] = _calibration.Clone();
            SaveSettings();

            NotifyCalibration();
        }

        private void LoadCalibrationForSource()
        {
            _calibration = new PointerCalibration();

            if (_source != null)
            {
                var key = _source.ProcessName.ToLowerInvariant();
                foreach (var pair in _settings.PointerCalibrations)
                {
                    if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    _calibration = pair.Value ?? new PointerCalibration();
                    break;
                }
            }

            CursorOverlayService.SetCalibration(_calibration);
            OnPropertyChanged(nameof(CalibrationScope));
            NotifyCalibration();
        }

        private void NotifyCalibration()
        {
            OnPropertyChanged(nameof(CalibrationSummary));
            OnPropertyChanged(nameof(CalibrationOffsetText));
            OnPropertyChanged(nameof(CalibrationScaleXText));
            OnPropertyChanged(nameof(CalibrationScaleYText));
        }

        /// <summary>
        /// Writes everything needed to diagnose a picture or pointer problem into one file
        /// and shows it, so a report does not depend on reproducing the session.
        /// </summary>
        public void SaveDiagnostics()
        {
            var context = new StringBuilder();
            context.AppendLine($"source             : {SourceLabel}");
            context.AppendLine($"streaming          : video={VideoActive} audio={AudioActive} fps={Fps:0.0} cap={MaxFps}");
            context.AppendLine($"video settings     : fill={StretchFill} showCursor={ShowCursor} lockAspect={_settings.LockAspect}");
            context.AppendLine($"frames             : presented={_presenter.AdmittedFrames} dropped={_presenter.DroppedFrames} (gpu path, no cpu copies)");
            context.AppendLine($"pointer calibration: {_calibration.Describe()}");
            context.AppendLine($"audio device       : {AudioSummary}");
            if (!string.IsNullOrWhiteSpace(AudioWarning)) context.AppendLine($"audio warning      : {AudioWarning}");
            context.AppendLine($"settings file      : {SettingsStore.FilePath}");
            context.AppendLine($"settings           : {JsonSerializer.Serialize(_settings)}");

            try
            {
                var path = DiagnosticsReport.Write(context.ToString().TrimEnd(),
                    _source?.Hwnd ?? IntPtr.Zero, _presenter.Width, _presenter.Height);

                StatusMessage = $"Diagnostics written to {path}";
                DiagnosticsReport.Reveal(path);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not write diagnostics: {ex.Message}";
            }
        }

        public void Dispose()
        {
            StopStream();
            _presenter.Dispose();
            _capture.Dispose();
            _audioCapture.Dispose();
            _router.Dispose();
            SaveSettings();
        }
    }

    /// <summary>One entry of the output-device selector ("Auto" has no device).</summary>
    public sealed class DeviceChoice
    {
        public OutputDevice? Device { get; }
        public string Label { get; }

        public DeviceChoice(OutputDevice? device, string label)
        {
            Device = device;
            Label = label;
        }

        public override string ToString() => Label;
    }
}
