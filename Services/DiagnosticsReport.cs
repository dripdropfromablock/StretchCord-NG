using System.Diagnostics;
using System.IO;
using System.Text;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Writes everything needed to answer "why does it look like that" into one file: the
    /// window geometry as Windows reports it, which coordinate space the pointer mapping
    /// chose, the capture state, the audio decision and the recent log.
    /// </summary>
    public static class DiagnosticsReport
    {
        /// <summary>Writes the report and returns its path.</summary>
        public static string Write(string context, IntPtr sourceHwnd, int frameWidth, int frameHeight)
        {
            var text = new StringBuilder();

            text.AppendLine("StretchCord NG diagnostics");
            text.AppendLine($"  written    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"  version    : {typeof(DiagnosticsReport).Assembly.GetName().Version}");
            text.AppendLine($"  os         : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process)");
            text.AppendLine($"  log file   : {AppLog.FilePath}");
            text.AppendLine();

            text.AppendLine("State");
            foreach (var line in context.Split('\n'))
                text.AppendLine("  " + line.TrimEnd());
            text.AppendLine();

            text.AppendLine("Display");
            text.AppendLine($"  primary mode        : {NativeMethods.GetSystemMetrics(0)}x{NativeMethods.GetSystemMetrics(1)}");
            text.AppendLine($"  virtual screen      : {NativeMethods.GetSystemMetrics(78)}x{NativeMethods.GetSystemMetrics(79)}" +
                            $" at {NativeMethods.GetSystemMetrics(76)},{NativeMethods.GetSystemMetrics(77)}");
            text.AppendLine($"  cpu placement       : {CpuTopology.Describe}");
            text.AppendLine();

            text.AppendLine("Captured window");
            if (sourceHwnd == IntPtr.Zero)
            {
                text.AppendLine("  (no source window selected)");
            }
            else
            {
                text.AppendLine($"  hwnd                : 0x{sourceHwnd.ToInt64():X}");
                text.AppendLine($"  frame handed to us  : {frameWidth}x{frameHeight}");

                var window = NativeMethods.GetVisibleRect(sourceHwnd);
                text.AppendLine($"  visible window rect : {window.Width}x{window.Height} at {window.Left},{window.Top}");

                if (NativeMethods.GetWindowRect(sourceHwnd, out var raw))
                    text.AppendLine($"  GetWindowRect       : {raw.Width}x{raw.Height} at {raw.Left},{raw.Top}");

                if (NativeMethods.GetClientRect(sourceHwnd, out var client))
                {
                    var origin = new NativeMethods.POINT();
                    NativeMethods.ClientToScreen(sourceHwnd, ref origin);
                    text.AppendLine($"  client rect         : {client.Width}x{client.Height} at {origin.X},{origin.Y}");
                }

                if (NativeMethods.TryGetMonitorRect(sourceHwnd, out var monitor))
                    text.AppendLine($"  monitor (as reported): {monitor.Width}x{monitor.Height} at {monitor.Left},{monitor.Top}");

                text.AppendLine($"  minimized           : {NativeMethods.IsIconic(sourceHwnd)}");
            }
            text.AppendLine();

            text.AppendLine("Pointer");
            if (NativeMethods.TryGetCursor(out var point, out var hCursor, out bool showing))
            {
                text.AppendLine($"  GetCursorPos        : ({point.X},{point.Y})");
                text.AppendLine($"  GetCursorInfo       : hCursor=0x{hCursor.ToInt64():X} showing={showing}" +
                                (hCursor == IntPtr.Zero ? "  <-- no cursor object (typical of exclusive fullscreen)" : string.Empty));
            }
            else
            {
                text.AppendLine("  GetCursorPos        : failed");
            }

            text.AppendLine($"  mapping decision    : {CursorOverlayService.LastDecision}");
            text.AppendLine();

            text.AppendLine($"Recent log (last 60 lines)");
            try
            {
                if (File.Exists(AppLog.FilePath))
                {
                    var lines = File.ReadAllLines(AppLog.FilePath);
                    foreach (var line in lines.Skip(Math.Max(0, lines.Length - 60)))
                        text.AppendLine("  " + line);
                }
                else
                {
                    text.AppendLine("  (no log yet)");
                }
            }
            catch (Exception ex)
            {
                text.AppendLine($"  (could not read the log: {ex.Message})");
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StretchCord", "diagnostics");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"StretchCordNG-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            AppLog.Write($"diagnostics: wrote {path}");

            return path;
        }

        /// <summary>Opens the file's folder with the report selected.</summary>
        public static void Reveal(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Write($"diagnostics: could not open Explorer: {ex.Message}");
            }
        }
    }
}
