using System.Runtime.InteropServices;
using System.Text;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Thin Win32 helpers shared by the stream window (monitor-exact fullscreen),
    /// the cursor overlay (screen → frame mapping) and the window picker.
    /// </summary>
    public static class NativeMethods
    {
        public const int GWLStyle = -16;
        public const int WSCaption = 0x00C00000;   // thick frame + caption
        public const int WSSizeBox = 0x00040000;

        public const uint SWPNoZOrder = 0x0004;
        public const uint SWPNoActivate = 0x0010;
        public const uint SWPFrameChanged = 0x0020;
        public const uint SWPShowWindow = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptCursor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        public const int CURSOR_SHOWING = 0x00000001;
        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        /// <summary>Makes the standard title bar match the app's dark theme.</summary>
        public static void ApplyDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
        }

        public const int VK_SHIFT = 0x10;
        public const int VK_F11 = 0x7A;
        public const int VK_ESCAPE = 0x1B;

        /// <summary>Monitor bounds in physical pixels for the monitor hosting a window.</summary>
        public static bool TryGetMonitorRect(IntPtr hwnd, out RECT rect)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) { rect = default; return false; }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) { rect = default; return false; }

            rect = info.rcMonitor;
            return true;
        }

        /// <summary>
        /// The on-screen bounds of a window as Windows actually draws it
        /// (DWM's extended frame excludes the invisible resize borders that inflate GetWindowRect).
        /// </summary>
        public static RECT GetVisibleRect(IntPtr hwnd)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) == 0)
            {
                if (rect.Width > 0 && rect.Height > 0) return rect;
            }

            return GetWindowRect(hwnd, out var r) ? r : default;
        }

        /// <summary>
        /// Current cursor position in physical screen coordinates, plus whatever the
        /// compositor is willing to say about the pointer itself.
        ///
        /// The *position* always comes from GetCursorPos. That distinction is the point of
        /// this method: a game in exclusive fullscreen takes the pointer over for itself,
        /// and then GetCursorInfo stops reporting a cursor object altogether (measured:
        /// flags=0, hCursor=NULL) even though GetCursorPos keeps returning a perfectly live
        /// position. Reading the position through GetCursorInfo is what made our pointer
        /// disappear from the share in exactly that mode.
        /// </summary>
        public static bool TryGetCursor(out POINT screenPoint, out IntPtr hCursor, out bool visible)
        {
            screenPoint = default;
            hCursor = IntPtr.Zero;
            visible = false;

            if (!GetCursorPos(out screenPoint)) return false;

            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref info))
            {
                hCursor = info.hCursor;
                visible = (info.flags & CURSOR_SHOWING) != 0 && hCursor != IntPtr.Zero;
            }

            return true;
        }

        /// <summary>Standard arrow cursor, used when the OS exposes no cursor bitmap at all.</summary>
        public static IntPtr SystemArrowCursor() => LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);

        // MAKEINTRESOURCE: the value is the resource id in the low word, so it has to be
        // passed in a pointer-sized argument - declaring this parameter as int would hand
        // the callee a 32-bit value where it reads 64 bits.
        public const int IDC_ARROW = 32512;

        /// <summary>Builds the set of descendant process ids for a root process (for audio muting).</summary>
        public static HashSet<int> GetProcessTree(int rootPid)
        {
            var result = new HashSet<int> { rootPid };
            var children = new Dictionary<int, List<int>>();

            var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (!children.TryGetValue(entry.th32ParentProcessID, out var list))
                            children[entry.th32ParentProcessID] = list = new List<int>();
                        list.Add((int)entry.th32ProcessID);
                    } while (Process32Next(snapshot, ref entry));
                }

                var queue = new Queue<int>();
                queue.Enqueue(rootPid);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!children.TryGetValue(current, out var kids)) continue;
                    foreach (var kid in kids)
                        if (result.Add(kid))
                            queue.Enqueue(kid);
                }
            }
            catch { /* best effort */ }
            finally { CloseHandle(snapshot); }

            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
    }
}
