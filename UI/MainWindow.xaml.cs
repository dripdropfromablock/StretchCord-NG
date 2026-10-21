using System;
using System.Windows;
using System.Windows.Threading;
using StretchCordNG.Models;
using StretchCordNG.Services;
using StretchCordNG.ViewModels;

namespace StretchCordNG.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        private readonly DispatcherTimer _tickTimer;
        private StreamWindow? _streamWindow;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            _vm.FramePresented += image => Preview.ShowFrame(image);
            _vm.Error += message => MessageBox.Show(this, message, "StretchCord",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _vm.StateChanged += OnStateChanged;

            _vm.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(MainViewModel.ShowCursor):
                        Preview.SetShowCursor(_vm.ShowCursor);
                        break;
                    case nameof(MainViewModel.StretchFill):
                        Preview.SetStretch(_vm.StretchFill);
                        break;
                    case nameof(MainViewModel.Source):
                        Preview.SetSourceWindow(_vm.Source?.Hwnd ?? IntPtr.Zero);
                        break;
                }
            };

            ApplySurfaceState();

            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tickTimer.Tick += (_, _) => _vm.Tick();
            _tickTimer.Start();

            Closed += (_, _) =>
            {
                _tickTimer.Stop();
                _vm.Dispose();
                _streamWindow?.Close();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.ApplyDarkTitleBar(hwnd);
        }

        private void ApplySurfaceState()
        {
            Preview.SetShowCursor(_vm.ShowCursor);
            Preview.SetStretch(_vm.StretchFill);
            Preview.SetSourceWindow(_vm.Source?.Hwnd ?? IntPtr.Zero);
        }

        private void OnStateChanged()
        {
            _streamWindow?.NotifyStateChanged();
        }

        private void BtnPickWindow_Click(object sender, RoutedEventArgs e)
        {
            var picker = new WindowPickerDialog { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedWindow != null)
                _vm.ChangeSource(picker.SelectedWindow);
        }

        private void BtnStream_Click(object sender, RoutedEventArgs e) => _vm.ToggleStream();

        private void BtnDiagnostics_Click(object sender, RoutedEventArgs e) => _vm.SaveDiagnostics();

        private void BtnNudgeLeft_Click(object sender, RoutedEventArgs e) => _vm.NudgePointer(-1, 0);

        private void BtnNudgeRight_Click(object sender, RoutedEventArgs e) => _vm.NudgePointer(1, 0);

        private void BtnNudgeUp_Click(object sender, RoutedEventArgs e) => _vm.NudgePointer(0, -1);

        private void BtnNudgeDown_Click(object sender, RoutedEventArgs e) => _vm.NudgePointer(0, 1);

        private void BtnResetCalibration_Click(object sender, RoutedEventArgs e) => _vm.ResetCalibration();

        private void BtnOpenStream_Click(object sender, RoutedEventArgs e)
        {
            if (_streamWindow == null || !_streamWindow.IsLoaded)
            {
                // Deliberately not an owned window: an owned window is hidden whenever the
                // owner minimizes, which would kill a live stream.
                _streamWindow = new StreamWindow(_vm);
                _streamWindow.Show();
            }
            else
            {
                if (_streamWindow.WindowState == WindowState.Minimized)
                    _streamWindow.WindowState = WindowState.Normal;
                _streamWindow.Activate();
            }
        }
    }
}
