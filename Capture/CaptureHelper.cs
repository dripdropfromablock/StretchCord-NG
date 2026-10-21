using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace StretchCordNG.Capture
{
    /// <summary>Creates a <see cref="GraphicsCaptureItem"/> from a raw handle.</summary>
    public static class CaptureHelper
    {
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComVisible(true)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(IntPtr hwnd, [In] ref Guid riid);
            IntPtr CreateForMonitor(IntPtr hmon, [In] ref Guid riid);
        }

        private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentNullException(nameof(hwnd));

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = IidGraphicsCaptureItem;
            var ptr = interop.CreateForWindow(hwnd, ref iid);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("Windows rejected this window for capture.");

            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr);
        }

        public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
        {
            if (hmon == IntPtr.Zero) throw new ArgumentNullException(nameof(hmon));

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = IidGraphicsCaptureItem;
            var ptr = interop.CreateForMonitor(hmon, ref iid);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("Windows rejected this monitor for capture.");

            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr);
        }
    }
}
