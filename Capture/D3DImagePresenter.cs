using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using StretchCordNG.Interop;
using StretchCordNG.Services;

namespace StretchCordNG.Capture
{
    /// <summary>
    /// Presents captured frames through a <see cref="D3DImage"/> without a single CPU copy.
    ///
    /// Every frame runs one GPU instruction: <c>CopyResource</c> from the capture texture
    /// into a shared presentation texture, and WPF composites that texture directly during
    /// its render pass. The previous generation copied each frame to a staging texture,
    /// mapped it on the CPU, marshalled the pixels into a managed array and uploaded them
    /// again through a WriteableBitmap — roughly two full-frame copies plus a GPU stall per
    /// frame (about a gigabyte per second at 1080p60). None of that exists here.
    ///
    /// D3DImage only accepts D3D9Ex surfaces, so the presentation texture is a D3D11
    /// texture created with <c>D3D11_RESOURCE_MISC_SHARED</c> whose shared handle is opened
    /// by a D3D9Ex device on the same adapter (see <see cref="D3D9Bridge"/>).
    /// </summary>
    public sealed unsafe class D3DImagePresenter : IDisposable
    {
        private readonly VideoCaptureService _capture;
        private readonly Dispatcher _dispatcher;
        private readonly object _gpuGate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private D3DImage _image;
        private D3D9Bridge _d3d9 = new();
        private D3DCaptureDevice? _boundDevice;
        private ID3D11Texture2D* _backbuffer;
        private IDirect3DSurface9* _surface9;
        private nint _presentedSurface;
        private int _bbWidth;
        private int _bbHeight;

        private int _busy;
        private int _disposed;
        private double _nextPresentMs;
        private double _fpsWindowStartMs;
        private int _fpsFrames;
        private long _arrivals;
        private long _admitted;
        private long _dropLimiter;
        private long _dropBusy;
        private double _lastDiagMs;

        public D3DImagePresenter(VideoCaptureService capture, Dispatcher dispatcher)
        {
            _capture = capture;
            _dispatcher = dispatcher;
            _image = new D3DImage();
            _capture.FrameArrived += OnFrameArrived;
        }

        /// <summary>Raised on the UI thread after each presented frame.</summary>
        public event Action<D3DImage>? FramePresented;

        public D3DImage Image => _image;
        public int MaxFps { get; set; }
        public double FramesPerSecond { get; private set; }
        public long DroppedFrames { get; private set; }
        public int Width => _bbWidth;
        public int Height => _bbHeight;
        public long AdmittedFrames => Interlocked.Read(ref _admitted);
        public long ArrivedFrames => Interlocked.Read(ref _arrivals);

        /// <summary>
        /// Frees the shared presentation texture while nothing is streaming. The next frame
        /// recreates it, so this is how the app avoids holding per-source GPU/shared memory
        /// between runs. Called when the stream stops.
        /// </summary>
        public void ReleasePresentationSurface()
        {
            if (_disposed != 0) return;

            lock (_gpuGate)
            {
                ReleaseBackbufferLocked();
            }
        }

        private void OnFrameArrived(nint texture, int width, int height)
        {
            if (_disposed != 0) return;
            Interlocked.Increment(ref _arrivals);

            // The frame-delivery thread is a streaming worker: keep it on the efficiency
            // cores so the game's performance cores are never spent on frame submission.
            CpuTopology.EnsureThreadPlacement();

            // Rate limit before doing any GPU work.
            //
            // Presentation runs on a fixed grid: a frame is admitted once it lands in
            // its own slot, with half a period of early tolerance. Half a period is the
            // widest tolerance that still cannot admit two frames into one slot, so the
            // long-run rate is exactly MaxFps, while the normal ±2 ms jitter of
            // compositor arrivals never costs a frame. Falling a whole period behind
            // resyncs the grid instead of bursting to catch up.
            double nowMs = _clock.Elapsed.TotalMilliseconds;
            if (MaxFps > 0)
            {
                double periodMs = 1000.0 / MaxFps;

                if (_nextPresentMs <= 0)
                {
                    _nextPresentMs = nowMs;
                }
                else if (nowMs < _nextPresentMs - periodMs / 2)
                {
                    DroppedFrames++;
                    _dropLimiter++;
                    Diagnose(nowMs);
                    return;
                }

                _nextPresentMs += periodMs;
                if (_nextPresentMs < nowMs) _nextPresentMs = nowMs + periodMs;
            }

            // At most one frame may be waiting for the UI thread.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                DroppedFrames++;
                _dropBusy++;
                Diagnose(nowMs);
                return;
            }

            try
            {
                var deviceHolder = _capture.Device;
                if (deviceHolder == null || deviceHolder.Device == null) { _busy = 0; return; }

                lock (_gpuGate)
                {
                    if (_disposed != 0) { _busy = 0; return; }

                    if (!ReferenceEquals(_boundDevice, deviceHolder))
                    {
                        // The capture moved to a different adapter — the shared surface lives
                        // on the old device and cannot be reused.
                        ReleaseBackbufferLocked();
                        _d3d9.Dispose();
                        _d3d9.Initialize(deviceHolder.VendorId, deviceHolder.DeviceId);
                        _boundDevice = deviceHolder;
                    }

                    EnsureBackbufferLocked(deviceHolder, width, height);
                    if (_backbuffer == null || _surface9 == null) { _busy = 0; return; }

                    var context = deviceHolder.Context;
                    context->CopyResource((ID3D11Resource*)_backbuffer, (ID3D11Resource*)texture);
                    context->Flush();
                }

                _fpsFrames++;
                Interlocked.Increment(ref _admitted);
                Diagnose(nowMs);
                if (nowMs - _fpsWindowStartMs >= 1000)
                {
                    FramesPerSecond = _fpsFrames * 1000.0 / (nowMs - _fpsWindowStartMs);
                    _fpsWindowStartMs = nowMs;
                    _fpsFrames = 0;
                }

                var queued = _dispatcher.InvokeAsync(() =>
                {
                    Present();
                    Interlocked.Exchange(ref _busy, 0);
                }, DispatcherPriority.Render);

                // If the dispatcher never runs the callback (shutdown), release the gate here.
                _ = queued.Task.ContinueWith(t =>
                {
                    if (t.IsCanceled || t.IsFaulted)
                        Interlocked.Exchange(ref _busy, 0);
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _busy, 0);
                Services.AppLog.Write($"present: {ex.Message}");
            }
        }

        /// <summary>
        /// Every few seconds records how many frames arrived, how many were admitted and
        /// why the rest were dropped.
        /// </summary>
        private void Diagnose(double nowMs)
        {
            if (nowMs - _lastDiagMs < 5000) return;
            _lastDiagMs = nowMs;
            Services.AppLog.Write(
                $"video: fps={FramesPerSecond:0.0} cap={MaxFps} " +
                $"arrivals={Interlocked.Read(ref _arrivals)} admitted={Interlocked.Read(ref _admitted)} " +
                $"dropLimiter={_dropLimiter} dropBusy={_dropBusy}");
        }

        /// <summary>Composites the GPU texture into WPF (UI thread only).</summary>
        private void Present()
        {
            if (_disposed != 0) return;

            // Held while touching the surface pointer so a device swap cannot release the
            // surface between the check here and the SetBackBuffer call.
            lock (_gpuGate)
            {
                if (_surface9 == null) return;

                nint surface = (nint)_surface9;
                int width = _bbWidth;
                int height = _bbHeight;

                _image.Lock();
                try
                {
                    if (surface != _presentedSurface)
                    {
                        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface, enableSoftwareFallback: false);
                        _presentedSurface = surface;
                    }

                    _image.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    _image.Unlock();
                }

                FramePresented?.Invoke(_image);
            }
        }

        private void EnsureBackbufferLocked(D3DCaptureDevice deviceHolder, int width, int height)
        {
            if (_backbuffer != null && _bbWidth == width && _bbHeight == height) return;

            ReleaseBackbufferLocked();

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                // D3DImage composites through a shared D3D9Ex surface (see D3D9Bridge); the
                // render-target binding is what WPF reads during composition.
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
                CPUAccessFlags = 0,
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
            };

            ID3D11Texture2D* tex = null;
            deviceHolder.Device->CreateTexture2D(&desc, null, &tex);

            IDirect3DSurface9* surface = null;
            try
            {
                surface = _d3d9.CreateSharedSurfaceFrom(tex, (uint)width, (uint)height);
            }
            catch
            {
                _ = tex->Release();
                throw;
            }

            _backbuffer = tex;
            _surface9 = surface;
            _bbWidth = width;
            _bbHeight = height;
        }

        private void ReleaseBackbufferLocked()
        {
            if (_backbuffer == null && _surface9 == null)
            {
                _bbWidth = 0;
                _bbHeight = 0;
                return;
            }

            // WPF may still reference the D3D9 surface until the next SetBackBuffer runs on
            // the UI thread, so release it after clearing the back buffer there.
            var staleSurface = _surface9;
            var staleTexture = _backbuffer;
            _surface9 = null;
            _backbuffer = null;
            _presentedSurface = 0;
            _bbWidth = 0;
            _bbHeight = 0;

            void ClearAndRelease()
            {
                _image.Lock();
                try { _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0); }
                finally { _image.Unlock(); }
                if (staleSurface != null) _ = staleSurface->Release();
                if (staleTexture != null) _ = staleTexture->Release();
            }

            if (_dispatcher.CheckAccess())
            {
                ClearAndRelease();
            }
            else
            {
                _ = _dispatcher.InvokeAsync(ClearAndRelease).Task;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _capture.FrameArrived -= OnFrameArrived;

            lock (_gpuGate)
            {
                ReleaseBackbufferLocked();
                _boundDevice = null;
                _d3d9.Dispose();
            }
        }
    }
}
