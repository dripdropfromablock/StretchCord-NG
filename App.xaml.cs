using System.Windows;
using System.Windows.Threading;

namespace StretchCordNG
{
    public partial class App : Application
    {
        /// <summary>
        /// Unhandled exceptions are surfaced instead of killing the process silently.
        /// Capture/COM failures raised on a background thread would otherwise take the app down
        /// without any feedback to the user.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StretchCordNG] Fatal: {ex}");
                    Services.AppLog.Exception("Fatal", ex);
                }
            };

            base.OnStartup(e);

            // Initialise WinRT on the UI thread (required by Windows.Graphics.Capture).
            global::Windows.System.DispatcherQueue.GetForCurrentThread();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[StretchCordNG] {e.Exception}");
            Services.AppLog.Exception("Unhandled", e.Exception);
            MessageBox.Show(
                $"Something went wrong:\n\n{e.Exception.Message}\n\n(details: {Services.AppLog.FilePath})",
                "StretchCord NG", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
