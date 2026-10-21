using System.IO;

namespace StretchCordNG.Models
{
    /// <summary>A capturable top-level window plus the process that owns it.</summary>
    public sealed class WindowInfo
    {
        public IntPtr Hwnd { get; init; }
        public string Title { get; init; } = string.Empty;
        public string ProcessName { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public string? ExecutablePath { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>
        /// Minimized windows are listed too, because that is exactly the state a game is in
        /// when you switch to StretchCord to pick it. An exclusive-fullscreen game minimizes
        /// itself the moment it loses focus, so a picker that hid minimized windows could
        /// never list the very games this app exists for.
        /// </summary>
        public bool IsMinimized { get; init; }

        /// <summary>Size label used in the picker and the source card.</summary>
        public string SizeLabel => IsMinimized
            ? "minimized"
            : Width > 0 && Height > 0 ? $"{Width}×{Height}" : "minimized";

        /// <summary>Icon for the owning process, cached per path.</summary>
        public System.Windows.Media.ImageSource? Icon => ProcessIconCache.Get(ExecutablePath);

        public override string ToString()
        {
            var title = string.IsNullOrWhiteSpace(Title) ? "(no title)" : Title;
            return $"{title} — {ProcessName}";
        }
    }

    /// <summary>Caches small process icons so the picker stays cheap.</summary>
    public static class ProcessIconCache
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.ImageSource?> Cache = new();

        public static System.Windows.Media.ImageSource? Get(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Cache.GetOrAdd(path, static p =>
            {
                try
                {
                    if (!File.Exists(p)) return null;
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                    if (icon == null) return null;

                    var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                    return null;
                }
            });
        }
    }
}
