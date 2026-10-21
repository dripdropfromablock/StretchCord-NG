using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Dxgi;

namespace StretchCordNG.Interop
{
    /// <summary>
    /// A headless Direct3D 9Ex device that bridges D3D11 render output into WPF.
    ///
    /// WPF's <see cref="System.Windows.Interop.D3DImage"/> only accepts D3D9Ex surfaces,
    /// so D3D11 content reaches it through shared memory: the D3D11 texture is created
    /// with <c>D3D11_RESOURCE_MISC_SHARED</c>, its shared handle is opened by this D3D9Ex
    /// device as a render target, and that surface is what WPF composites. This is the
    /// officially documented interop path for Direct3D 11 content in WPF.
    ///
    /// The D3D9Ex device is created on the same adapter as the D3D11 capture device —
    /// shared surfaces cannot cross GPUs.
    /// </summary>
    internal sealed unsafe class D3D9Bridge : IDisposable
    {
        private const uint D3D_SDK_VERSION = 32;
        private const uint D3DCREATE_FPU_PRESERVE = 0x00000002;
        private const uint D3DCREATE_MULTITHREADED = 0x00000004;
        private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x00000040;
        private const uint D3DCREATE_NOWINDOWCHANGES = 0x00000800;

        private IDirect3DDevice9* _device;

        public IDirect3DDevice9* Device => _device;

        /// <summary>
        /// Creates the D3D9Ex device, preferring the adapter whose PCI ids match the D3D11
        /// device's adapter (D3D9 cannot match by LUID). Pass 0, 0 to use the default.
        /// </summary>
        public void Initialize(uint vendorId, uint deviceId)
        {
            IDirect3D9Ex* d3d9 = null;
            var hr = PInvoke.Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9);
            if (hr.Failed || d3d9 == null)
                hr.ThrowOnFailure();

            try
            {
                uint ordinal = 0; // D3DADAPTER_DEFAULT

                if (vendorId != 0 || deviceId != 0)
                {
                    uint count = d3d9->GetAdapterCount();
                    for (uint i = 0; i < count; i++)
                    {
                        D3DADAPTER_IDENTIFIER9 id = default;
                        d3d9->GetAdapterIdentifier(i, 0, &id);
                        if (id.VendorId == vendorId && id.DeviceId == deviceId)
                        {
                            ordinal = i;
                            break;
                        }
                    }
                }

                var pp = new D3DPRESENT_PARAMETERS
                {
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
                    BackBufferCount = 1,
                    MultiSampleType = D3DMULTISAMPLE_TYPE.D3DMULTISAMPLE_NONE,
                    SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_COPY,
                    hDeviceWindow = PInvoke.GetDesktopWindow(),
                    Windowed = true,
                    EnableAutoDepthStencil = false,
                    Flags = 0,
                    FullScreen_RefreshRateInHz = 0,
                    PresentationInterval = 0,
                };

                IDirect3DDevice9* device = null;
                d3d9->CreateDevice(
                    ordinal,
                    D3DDEVTYPE.D3DDEVTYPE_HAL,
                    PInvoke.GetDesktopWindow(),
                    D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED |
                        D3DCREATE_FPU_PRESERVE | D3DCREATE_NOWINDOWCHANGES,
                    &pp, &device);
                if (device == null)
                    throw new InvalidOperationException("D3D9Ex device creation returned no device.");

                _device = device;
            }
            finally
            {
                _ = d3d9->Release();
            }
        }

        /// <summary>
        /// Opens the D3D11 texture's shared memory as a D3D9 render-target surface.
        /// The texture must have been created with <c>D3D11_RESOURCE_MISC_SHARED</c>.
        /// </summary>
        public IDirect3DSurface9* CreateSharedSurfaceFrom(ID3D11Texture2D* d3d11Texture, uint width, uint height)
        {
            if (_device == null) throw new InvalidOperationException("D3D9 bridge not initialized.");

            void* resourcePtr = null;
            var iid = IidOf<IDXGIResource>();
            var hr = d3d11Texture->QueryInterface(&iid, &resourcePtr);
            hr.ThrowOnFailure();

            HANDLE sharedHandle;
            var resource = (IDXGIResource*)resourcePtr;
            try
            {
                resource->GetSharedHandle(&sharedHandle);
            }
            finally
            {
                _ = resource->Release();
            }

            IDirect3DSurface9* surface = null;
            _device->CreateRenderTarget(
                width, height,
                D3DFORMAT.D3DFMT_A8R8G8B8,
                D3DMULTISAMPLE_TYPE.D3DMULTISAMPLE_NONE,
                0,
                false,
                &surface, &sharedHandle);
            if (surface == null)
                throw new InvalidOperationException("D3D9 could not open the shared D3D11 surface.");

            return surface;
        }

        public void Dispose()
        {
            if (_device != null)
            {
                _ = _device->Release();
                _device = null;
            }
        }

        /// <summary>Reads the IID constant from the generated interface metadata.</summary>
        private static Guid IidOf<T>() where T : unmanaged, IComIID => T.Guid;
    }
}
