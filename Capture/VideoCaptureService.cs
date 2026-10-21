using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using StretchCordNG.Interop;
using StretchCordNG.Models;
using StretchCordNG.Services;
using WinRT;

namespace StretchCordNG.Capture
{
    /// <summary>
    /// Captures a single window through Windows.Graphics.Capture on the GPU.
    ///
    /// Frames arrive as raw <c>ID3D11Texture2D</c> pointers and stay on the GPU for their
    /// whole life — the presenter copies them into the presentation texture with a single
    /// <c>CopyResource</c>, and nothing in this pipeline ever maps or marshals pixels.
    ///
    /// The device is created on the adapter that owns the source window's monitor, so the
    /// capture pool, the copy and the presentation share one GPU even on multi-adapter systems.
    /// </summary>
    public sealed class VideoCaptureService : IDisposable
    {
        /// <summary>
        /// A captured frame. <paramref name="texture"/> is only valid during the call —
        /// the reference is released as soon as every handler returns.
        /// </summary>
        public event Action<nint, int, int>? FrameArrived;
        public event Action<string>? Error;
        public event Action? SourceClosed;
        public event Action<int, int>? SourceSizeChanged;

        private readonly object _gate = new();

        private D3DCaptureDevice? _device;
        private nint _deviceMonitor;
        private IDirect3DDevice? _winrtDevice;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;

        private SizeInt32 _lastSize;
        private bool _disposed;

        private long _framesReceived;

        internal D3DCaptureDevice? Device => _device;
        public bool IsCapturing { get { lock (_gate) return _session != null; } }
        public SizeInt32 SourceSize { get; private set; }
        public int FramesReceived => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _framesReceived));

        public static bool IsSupported()
        {
            try
            {
                return Windows.Foundation.Metadata.ApiInformation.IsApiContractPresent(
                    "Windows.Foundation.UniversalApiContract", 8);
            }
            catch { return false; }
        }

        /// <summary>
        /// Ensures a D3D device exists for the window's monitor. Called from Start, so a
        /// window that moved to another display re-binds the capture to the right GPU.
        /// </summary>
        private void EnsureDevice(WindowInfo window)
        {
            var monitor = NativeMethods.MonitorFromWindow(window.Hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (_device != null && _deviceMonitor == monitor) return;

            Stop();
            _device?.Dispose();
            _device = null;
            _winrtDevice = null;

            var device = D3DCaptureDevice.Create(monitor);
            _device = device;
            _deviceMonitor = monitor;
            _winrtDevice = device.WinrtDevice;
        }

        public void Start(WindowInfo window)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VideoCaptureService));

            EnsureDevice(window);

            if (_device == null || _winrtDevice == null)
                throw new InvalidOperationException("Could not create a Direct3D device for capture.");

            Stop();

            var item = CaptureHelper.CreateItemForWindow(window.Hwnd);
            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException("The selected window has no captureable surface (is it minimized?).");

            var pool = Direct3D11CaptureFramePool.Create(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                3,
                size);

            var session = pool.CreateCaptureSession(item);

            // Windows 11 draws a yellow border around captured windows — keep it off.
            try { session.IsBorderRequired = false; } catch { /* API not present */ }

            // We composite our own cursor at an exact mapped position instead.
            bool cursorOffBeforeStart = false;
            bool cursorSettingSupported = true;
            try
            {
                session.IsCursorCaptureEnabled = false;
                cursorOffBeforeStart = !session.IsCursorCaptureEnabled;
            }
            catch (Exception ex)
            {
                cursorSettingSupported = false;
                AppLog.Write($"capture: IsCursorCaptureEnabled not supported: {ex.Message}");
            }

            item.Closed += OnItemClosed;
            pool.FrameArrived += OnFrameArrived;

            lock (_gate)
            {
                _item = item;
                _framePool = pool;
                _session = session;
                _lastSize = size;
            }

            SourceSize = size;
            SourceSizeChanged?.Invoke(size.Width, size.Height);
            session.StartCapture();

            // Re-assert after the session is live — some drivers accept the flag before
            // Start() and then ignore it, which is what leaves a stale compositor cursor
            // (at the wrong place for a stretched fullscreen game) in the shared picture.
            bool cursorOffAfterStart = cursorOffBeforeStart;
            if (cursorSettingSupported)
            {
                try
                {
                    if (session.IsCursorCaptureEnabled)
                    {
                        session.IsCursorCaptureEnabled = false;
                        cursorOffAfterStart = !session.IsCursorCaptureEnabled;
                    }
                }
                catch { /* keep the value from before Start() */ }
            }

            AppLog.Write(
                $"capture: {size.Width}x{size.Height} on {_device!.AdapterName}, own cursor overlay replaces the compositor cursor " +
                $"(disabled={cursorOffAfterStart}, supported={cursorSettingSupported})");
        }

        public void Stop()
        {
            GraphicsCaptureSession? session;
            Direct3D11CaptureFramePool? pool;
            GraphicsCaptureItem? item;

            lock (_gate)
            {
                session = _session; pool = _framePool; item = _item;
                _session = null; _framePool = null; _item = null;
            }

            if (session == null) return;

            try
            {
                if (item != null) item.Closed -= OnItemClosed;
                if (pool != null) pool.FrameArrived -= OnFrameArrived;
                session.Dispose();
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Stop capture: {ex.Message}");
            }
            finally
            {
                pool?.Dispose();
            }
        }

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            // The game window went away (closed, alt-tabbed to exclusive fullscreen, restarted).
            Stop();
            SourceClosed?.Invoke();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_gate)
            {
                if (_disposed || _session == null) return;

                Direct3D11CaptureFrame? frame = null;
                try
                {
                    frame = sender.TryGetNextFrame();
                    if (frame == null) return;

                    var content = frame.ContentSize;
                    if (content.Width > 0 && content.Height > 0 &&
                        (content.Width != _lastSize.Width || content.Height != _lastSize.Height))
                    {
                        _lastSize = content;
                        sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, content);
                        SourceSizeChanged?.Invoke(content.Width, content.Height);
                    }

                    var texture = ResolveTexture(frame);
                    if (texture == 0) return;

                    try
                    {
                        Interlocked.Increment(ref _framesReceived);
                        FrameArrived?.Invoke(texture, content.Width, content.Height);
                    }
                    finally
                    {
                        Marshal.Release(texture);
                    }
                }
                catch (Exception ex)
                {
                    Error?.Invoke($"Frame error: {ex.Message}");
                }
                finally
                {
                    frame?.Dispose();
                }
            }
        }

        private static nint ResolveTexture(Direct3D11CaptureFrame frame)
        {
            // Some C#/WinRT projections throw on surface.As<T>(), so go through the
            // low-level IObjectReference path documented for CsWinRT instead.
            var surface = frame.Surface;
            var surfaceRef = ((IWinRTObject)surface).NativeObject;
            var access = surfaceRef.AsInterface<IDirect3DDxgiInterfaceAccess>();
            var iidTexture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); // ID3D11Texture2D
            return access.GetInterface(ref iidTexture2D);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            _winrtDevice = null;
            _device?.Dispose();
            _device = null;
        }

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComVisible(true)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            nint GetInterface([In] ref Guid iid);
        }
    }
}
