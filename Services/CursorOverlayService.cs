using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Where the mouse pointer sits inside the captured picture.
    ///
    /// This is the fix for "the pointer appears somewhere else": instead of trusting the
    /// compositor's cursor capture (which lands in the wrong place for GPU-stretched
    /// fullscreen games), the position is computed from the pointer's screen coordinates
    /// against the source window's real rectangle, then scaled with exactly the same
    /// transform that is applied to the video.
    ///
    /// Two things about exclusive fullscreen are worth knowing, both measured on a real
    /// CS2 session rather than assumed:
    ///
    ///  1. The game takes the pointer over, so the OS stops reporting a cursor object
    ///     (flags=0, hCursor=NULL) while the position stays live. The overlay therefore
    ///     reads the position from GetCursorPos and falls back to the standard arrow when
    ///     there is no bitmap to draw.
    ///  2. The frame is the game's *render* resolution (the display mode), while the
    ///     pointer is reported in the window's coordinate space - a window that keeps the
    ///     previous desktop size (measured: display mode 1280x960, window 1920x1080). The
    ///     two spaces differ by exactly the scale between them, so the pointer is mapped
    ///     from the window rectangle when the frame matches the display mode instead.
    /// </summary>
    public readonly record struct CursorPlacement(bool Visible, double X, double Y, double Width, double Height);

    public sealed class CursorOverlayService
    {
        private static readonly ConcurrentDictionary<IntPtr, BitmapSource?> Cache = new();
        private static BitmapSource? _lastKnownCursor;
        private static long _lastDiagMs;
        private static volatile Models.PointerCalibration _calibration = new();

        /// <summary>
        /// Manual correction for the game being streamed (identity unless the user adjusted
        /// it). Set per game by the owning view model; the surfaces pick it up on their next
        /// tick, so an adjustment is visible immediately.
        /// </summary>
        public static void SetCalibration(Models.PointerCalibration? calibration)
            => _calibration = calibration ?? new Models.PointerCalibration();

        /// <summary>Last mapping decision, quoted by the diagnostics report.</summary>
        public static string LastDecision { get; private set; } = "no mapping computed yet";

        /// <summary>
        /// Maps screen cursor coordinates into the source frame and returns a rect in
        /// *frame* pixels. Returns <c>Visible = false</c> when there is no pointer position
        /// to work with, when the app hides the pointer on screen, or when it sits outside
        /// the captured window.
        /// </summary>
        public static CursorPlacement PlaceInFrame(IntPtr hwnd, int frameWidth, int frameHeight)
        {
            long now = Environment.TickCount64;
            bool trace = now - _lastDiagMs > 2000;

            // Every exit reports itself. The previous version logged only the successful
            // mappings, so "no pointer in the share" produced a completely silent log and
            // was indistinguishable from "the feature is switched off".
            CursorPlacement Bail(string reason)
            {
                string line = $"cursor: not drawn ({reason}) hwnd=0x{hwnd.ToInt64():X} frame={frameWidth}x{frameHeight}";
                if (trace)
                {
                    _lastDiagMs = now;
                    LastDecision = line;
                    AppLog.Write(line);
                }
                return default;
            }

            if (hwnd == IntPtr.Zero) return Bail("no source window");
            if (frameWidth <= 0 || frameHeight <= 0) return Bail("no frame size yet");

            if (!NativeMethods.TryGetCursor(out var point, out var hCursor, out bool showing))
                return Bail("no pointer position available");

            // A real handle that is not showing means an ordinary window hid the pointer
            // (typing, idle timeout): respect that. No handle at all is the exclusive
            // fullscreen case - the game owns the pointer, and the viewer still has to see
            // where it is, so that one is drawn.
            if (!showing && hCursor != IntPtr.Zero)
                return Bail("pointer hidden on screen");

            if (!NativeMethods.GetClientRect(hwnd, out var client)) return Bail("no client rect");
            if (client.Width <= 0 || client.Height <= 0) return Bail("empty client rect");

            var clientOrigin = new NativeMethods.POINT();           // (0,0) in client space
            if (!NativeMethods.ClientToScreen(hwnd, ref clientOrigin)) return Bail("ClientToScreen failed");

            var clientBounds = new NativeMethods.RECT
            {
                Left = clientOrigin.X,
                Top = clientOrigin.Y,
                Right = clientOrigin.X + client.Width,
                Bottom = clientOrigin.Y + client.Height
            };

            var windowBounds = NativeMethods.GetVisibleRect(hwnd);

            var origin = PickOrigin(hwnd, clientBounds, windowBounds, frameWidth, frameHeight, out string space);
            if (origin.Width <= 0 || origin.Height <= 0) return Bail("no usable window rect");

            double sx = (double)frameWidth / origin.Width;
            double sy = (double)frameHeight / origin.Height;

            double x = (point.X - origin.Left) * sx;
            double y = (point.Y - origin.Top) * sy;

            // Manual correction, remembered per game, applied on top of the detected
            // mapping. Identity by default, so nothing changes unless it was asked for.
            var calibration = _calibration;
            x = x * calibration.ScaleX + calibration.OffsetX;
            y = y * calibration.ScaleY + calibration.OffsetY;

            // Pointer outside the captured picture: don't draw a stray cursor.
            if (x < 0 || x >= frameWidth || y < 0 || y >= frameHeight)
                return Bail($"pointer outside the picture ({(int)x},{(int)y})");

            var cursor = CursorImage(hCursor, out double hotX, out double hotY);
            bool fallbackArrow = hCursor == IntPtr.Zero;

            string decision =
                $"cursor: screen=({point.X},{point.Y}) frame={frameWidth}x{frameHeight} " +
                $"space={space} origin=({origin.Left},{origin.Top}) size={origin.Width}x{origin.Height} " +
                $"client={client.Width}x{client.Height} window={windowBounds.Width}x{windowBounds.Height} " +
                $"scale=({sx:0.###},{sy:0.###}) -> ({x:0.#},{y:0.#})" +
                $" ({point.X - origin.Left},{point.Y - origin.Top} in window space)" +
                (calibration.IsIdentity ? string.Empty : $" calibration[{calibration.Describe()}]") +
                (fallbackArrow ? " [system arrow: the OS reports no cursor bitmap here]" : string.Empty);

            if (trace)
            {
                _lastDiagMs = now;
                LastDecision = decision;
                AppLog.Write(decision);
            }

            if (cursor == null)
                return new CursorPlacement(true, x, y, 0, 0);

            // The pointer bitmap is scaled with the picture, exactly like the frame content.
            double w = cursor.PixelWidth * sx;
            double h = cursor.PixelHeight * sy;

            return new CursorPlacement(true, x - hotX * sx, y - hotY * sy, w, h);
        }

        /// <summary>
        /// Returns whichever rectangle the pointer is actually expressed in.
        ///
        /// In the ordinary windowed case the frame *is* the client area, and the caption and
        /// resize borders it excludes are exactly what used to be added as a bogus offset.
        ///
        /// Exclusive fullscreen breaks that assumption in a way that is invisible to the
        /// frame's size: the game changes the display mode to its render resolution
        /// (measured 1280x960) while its window keeps the size of the desktop it came from
        /// (measured 1920x1080). The frame then matches the *mode*, and it can also match the
        /// client rect exactly - while the pointer is still reported in that larger window
        /// space. Choosing the client there scales by 1 instead of 2/3 and throws the
        /// pointer a third of the way across the picture, which is precisely the error a
        /// stretched 4:3 menu showed: the drawn pointer sat at the raw screen position while
        /// the item under it was highlighted at 2/3 of it.
        ///
        /// The state is detectable without guessing: a window larger than the display area
        /// Windows reports for the monitor cannot be the thing being captured - the frame is
        /// that display mode, and the window space is where the pointer lives.
        /// </summary>
        private static NativeMethods.RECT PickOrigin(
            IntPtr hwnd, in NativeMethods.RECT clientBounds, in NativeMethods.RECT windowBounds,
            int frameWidth, int frameHeight, out string space)
        {
            if (NativeMethods.TryGetMonitorRect(hwnd, out var monitor) &&
                monitor.Width > 0 && monitor.Height > 0 &&
                (windowBounds.Width > monitor.Width || windowBounds.Height > monitor.Height))
            {
                space = $"window (fullscreen: display mode {monitor.Width}x{monitor.Height} < window {windowBounds.Width}x{windowBounds.Height})";
                return windowBounds;
            }

            double clientError = SizeError(clientBounds, frameWidth, frameHeight);
            double windowError = SizeError(windowBounds, frameWidth, frameHeight);

            if (clientError < windowError)
            {
                space = "client";
                return clientBounds;
            }

            space = clientError == windowError ? "window (same size as client)" : "window";
            return windowBounds;
        }

        private static double SizeError(in NativeMethods.RECT rect, int frameWidth, int frameHeight)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return double.MaxValue;

            return Math.Abs(Math.Log((double)frameWidth / rect.Width))
                 + Math.Abs(Math.Log((double)frameHeight / rect.Height));
        }

        /// <summary>Scales a frame-space rect into the coordinates of a display element.</summary>
        public static Rect ToElement(in CursorPlacement placement, double frameWidth, double frameHeight,
            double elementWidth, double elementHeight)
        {
            if (frameWidth <= 0 || frameHeight <= 0)
                return Rect.Empty;

            double kx = elementWidth / frameWidth;
            double ky = elementHeight / frameHeight;

            return new Rect(placement.X * kx, placement.Y * ky,
                placement.Width * kx, placement.Height * ky);
        }

        /// <summary>Current cursor bitmap, or the last one seen, or the standard arrow.</summary>
        public static BitmapSource? CurrentCursorImage() => CursorImage(default, out _, out _);

        /// <summary>
        /// The pointer bitmap for a cursor handle, with the hotspot in bitmap pixels.
        ///
        /// When there is no handle to ask (exclusive fullscreen) the last cursor we ever saw
        /// is reused, and failing that the standard arrow - a share without any pointer on
        /// it is worse than one drawn with the system arrow.
        /// </summary>
        public static BitmapSource? CursorImage(IntPtr handle, out double hotX, out double hotY)
        {
            hotX = 0;
            hotY = 0;

            if (handle == IntPtr.Zero)
            {
                if (_lastKnownCursor != null) return _lastKnownCursor;

                handle = NativeMethods.SystemArrowCursor();
                if (handle == IntPtr.Zero) return null;
            }

            var image = Cache.GetOrAdd(handle, static h =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(h);
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                    return null;
                }
            });

            if (image == null) return null;

            if (NativeMethods.GetIconInfo(handle, out var iconInfo))
            {
                hotX = iconInfo.xHotspot;
                hotY = iconInfo.yHotspot;
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            }

            _lastKnownCursor = image;
            return image;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
