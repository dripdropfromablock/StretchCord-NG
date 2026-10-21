using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StretchCordNG.Services;
using StretchCordNG.ViewModels;

namespace StretchCordNG.UI
{
    /// <summary>
    /// The window you share in Discord.
    ///
    /// Fullscreen here is done properly: instead of WindowStyle=None + Maximized (which
    /// sizes the window against the *screen* and overflows it under PerMonitorV2, shifting
    /// the whole picture), the window is positioned with SetWindowPos against the exact
    /// monitor rectangle in physical pixels and restored to the saved bounds afterwards.
    /// </summary>
    public partial class StreamWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly DispatcherTimer _hideTimer;
        private DispatcherTimer? _hintTimer;

        private bool _fullscreen;
        private bool _adjustingAspect;
        private NativeMethods.RECT _restoreRect;
        private bool _hasRestoreRect;

        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_NCHITTEST = 0x0084;

        public StreamWindow(MainViewModel viewModel)
        {
            _vm = viewModel;
            InitializeComponent();
            DataContext = _vm;

            if (_vm.Settings.StreamWindowWidth >= 320 && _vm.Settings.StreamWindowHeight >= 180)
            {
                Width = _vm.Settings.StreamWindowWidth;
                Height = _vm.Settings.StreamWindowHeight;
            }

            Topmost = _vm.Settings.AlwaysOnTop;
            UpdatePinVisual();

            Surface.SetShowCursor(_vm.ShowCursor);
            Surface.SetStretch(_vm.StretchFill);
            Surface.SetSourceWindow(_vm.Source?.Hwnd ?? IntPtr.Zero);

            _vm.FramePresented += OnFrame;
            _vm.StateChanged += NotifyStateChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideChrome(); };
            _hideTimer.Start();

            Root.MouseMove += (_, _) => ShowChrome();
            SizeChanged += (_, _) => ApplyAspectLock();

            Loaded += (_, _) =>
            {
                NotifyStateChanged();
                Activate();
                Keyboard.Focus(this);
                ShowChrome();
            };

            Closed += (_, _) =>
            {
                _hideTimer.Stop();
                _hintTimer?.Stop();
                _vm.FramePresented -= OnFrame;
                _vm.StateChanged -= NotifyStateChanged;
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                Persist();
            };

            NotifyStateChanged();
        }

        // â”€â”€ frames â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void OnFrame(System.Windows.Interop.D3DImage image) => Surface.ShowFrame(image);

        public void NotifyStateChanged()
        {
            if (_vm.VideoActive) return;
            Surface.ShowEmpty("Waiting for video", "Press â€œStart streamâ€ in the main window");
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ShowCursor):
                    Surface.SetShowCursor(_vm.ShowCursor);
                    break;
                case nameof(MainViewModel.StretchFill):
                    Surface.SetStretch(_vm.StretchFill);
                    break;
                case nameof(MainViewModel.Source):
                    Surface.SetSourceWindow(_vm.Source?.Hwnd ?? IntPtr.Zero);
                    break;
            }
        }

        // â”€â”€ chrome â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void ShowChrome()
        {
            Chrome.BeginAnimation(OpacityProperty, null);
            Chrome.Opacity = 1;
            Chrome.IsHitTestVisible = true;

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideChrome()
        {
            Chrome.IsHitTestVisible = false;
            Chrome.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
                {
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        private void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                return;
            }

            if (_fullscreen) return;

            try { DragMove(); }
            catch { /* mouse released already */ }
        }

        private void DragArea_MouseUp(object sender, MouseButtonEventArgs e) { }

        // â”€â”€ buttons â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            _vm.Settings.AlwaysOnTop = Topmost;
            UpdatePinVisual();
            ShowChrome();
        }

        private void UpdatePinVisual()
        {
            if (BtnPin == null) return;
            BtnPin.Foreground = Topmost
                ? System.Windows.Media.Brushes.DeepSkyBlue
                : (System.Windows.Media.Brush)FindResource("B.TextDim");
            BtnPin.Opacity = Topmost ? 1 : 0.8;
        }

        private void Root_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _fullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        // â”€â”€ fullscreen â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void ToggleFullscreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!_fullscreen)
            {
                if (NativeMethods.GetWindowRect(hwnd, out var current))
                {
                    _restoreRect = current;
                    _hasRestoreRect = true;
                }

                if (!NativeMethods.TryGetMonitorRect(hwnd, out var monitor)) return;

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    monitor.Left, monitor.Top, monitor.Width, monitor.Height,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                _fullscreen = true;
                ShowFullscreenHint();
            }
            else
            {
                if (_hasRestoreRect)
                {
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                        _restoreRect.Left, _restoreRect.Top,
                        _restoreRect.Width, _restoreRect.Height,
                        SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                }

                _fullscreen = false;
                FullscreenHint.Visibility = Visibility.Collapsed;
                _hintTimer?.Stop();
            }

            ShowChrome();
        }

        private void ShowFullscreenHint()
        {
            FullscreenHint.Visibility = Visibility.Visible;

            _hintTimer?.Stop();
            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _hintTimer.Tick += (_, _) =>
            {
                _hintTimer.Stop();
                FullscreenHint.Visibility = Visibility.Collapsed;
            };
            _hintTimer.Start();
        }

        // â”€â”€ resize behaviour â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void ApplyAspectLock()
        {
            if (_fullscreen || !_vm.Settings.LockAspect || _adjustingAspect) return;
            if (WindowState != WindowState.Normal) return;

            // WindowStyle=None â‡’ window size == client size, so a 16:9 window really is
            // a 16:9 picture with no non-client area throwing the ratio off.
            var expected = Math.Round(Width * 9.0 / 16.0);
            if (Math.Abs(Height - expected) < 1) return;

            _adjustingAspect = true;
            try { Height = expected; }
            finally { _adjustingAspect = false; }
        }

        /// <summary>Borderless resize: map the window edges to Win32 hit-test codes.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
                source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_NCHITTEST || _fullscreen || ResizeMode != ResizeMode.CanResize)
                return IntPtr.Zero;

            var packed = lParam.ToInt64();
            var point = new NativeMethods.POINT
            {
                X = (short)(packed & 0xFFFF),
                Y = (short)((packed >> 16) & 0xFFFF)
            };

            if (!NativeMethods.GetWindowRect(hwnd, out var r))
                return IntPtr.Zero;

            const int border = 7;
            bool left = point.X <= r.Left + border;
            bool right = point.X >= r.Right - border;
            bool top = point.Y <= r.Top + border;
            bool bottom = point.Y >= r.Bottom - border;

            int hit = (top, left, right, bottom) switch
            {
                (true, true, _, _) => 13,               // HTTOPLEFT
                (true, false, true, _) => 14,           // HTTOPRIGHT
                (false, true, _, true) => 16,           // HTBOTTOMLEFT
                (false, false, true, true) => 17,       // HTBOTTOMRIGHT
                (_, true, _, _) => 10,                  // HTLEFT
                (_, _, true, _) => 11,                  // HTRIGHT
                (true, _, _, _) => 12,                  // HTTOP
                (false, _, _, true) => 15,              // HTBOTTOM
                _ => 0
            };

            if (hit == 0) return IntPtr.Zero;

            handled = true;
            return (IntPtr)hit;
        }

        private void Persist()
        {
            if (WindowState == WindowState.Normal && !_fullscreen && Width >= 320 && Height >= 180)
            {
                _vm.Settings.StreamWindowWidth = Width;
                _vm.Settings.StreamWindowHeight = Height;
            }
            _vm.Settings.AlwaysOnTop = Topmost;
            _vm.Save();
        }
    }
}
