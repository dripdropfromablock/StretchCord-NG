using System.IO;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Tiny append-only log. WPF exceptions otherwise vanish into a message box, which makes
    /// bugs reported by users impossible to diagnose.
    /// </summary>
    public static class AppLog
    {
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StretchCord", "log-ng.txt");

        public static void Write(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }

        public static void Exception(string context, Exception ex)
            => Write($"{context}: {ex}");
    }
}
