using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StretchCordNG.Models;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Enumerates visible, capturable top-level windows.
    /// Filters out tool windows, the shell, our own process and untitled windows.
    /// </summary>
    public static class WindowEnumerator
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public static List<WindowInfo> GetCapturableWindows()
        {
            var windows = new List<WindowInfo>();
            var shellWindow = GetShellWindow();
            var selfPid = (uint)Environment.ProcessId;
            var selfName = Process.GetCurrentProcess().ProcessName;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == shellWindow) return true;
                if (!IsWindowVisible(hwnd)) return true;

                // Minimized windows are kept: a fullscreen game minimizes itself when it
                // loses focus, so switching to StretchCord to pick it is precisely when it
                // is iconic. Hiding those rows made the game unpickable.
                bool minimized = IsIconic(hwnd);

                // Skip windows that cannot be captured meaningfully.
                var className = new StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);
                var cls = className.ToString();
                if (cls is "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
                    return true;

                var length = GetWindowTextLength(hwnd);
                if (length == 0) return true;

                var title = new StringBuilder(length + 1);
                GetWindowText(hwnd, title, title.Capacity);
                var titleText = title.ToString().Trim();
                if (titleText.Length == 0) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == selfPid) return true;

                string processName = "Unknown";
                string? processPath = null;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                    if (processName.Equals(selfName, StringComparison.OrdinalIgnoreCase)) return true;
                    try { processPath = proc.MainModule?.FileName; } catch { /* access denied */ }
                }
                catch { return true; } // process exited

                GetWindowRect(hwnd, out var rect);

                windows.Add(new WindowInfo
                {
                    Hwnd = hwnd,
                    Title = titleText,
                    ProcessName = processName,
                    ProcessId = (int)pid,
                    ExecutablePath = processPath,
                    Width = minimized ? 0 : Math.Max(0, rect.Right - rect.Left),
                    Height = minimized ? 0 : Math.Max(0, rect.Bottom - rect.Top),
                    IsMinimized = minimized
                });

                return true;
            }, IntPtr.Zero);

            // Most likely to be the game: larger windows first, then alphabetically.
            // Minimized windows sink to the bottom - they are still pickable, just less
            // likely to be the thing you meant, and their placeholder rect is meaningless.
            windows.Sort((a, b) =>
            {
                if (a.IsMinimized != b.IsMinimized) return a.IsMinimized ? 1 : -1;

                var area = ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height);
                return area != 0
                    ? area
                    : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            return windows;
        }

        /// <summary>Finds the current top-level window for a handle (handles are recycled).</summary>
        public static WindowInfo? Find(IntPtr hwnd)
            => GetCapturableWindows().FirstOrDefault(w => w.Hwnd == hwnd);
    }
}
