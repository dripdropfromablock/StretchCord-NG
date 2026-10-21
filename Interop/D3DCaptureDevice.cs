using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace StretchCordNG.Interop
{
    /// <summary>
    /// A Direct3D 11 device bound to the adapter that owns a specific monitor.
    ///
    /// Capture and presentation must run on the same GPU the game renders on: with the
    /// default adapter, a laptop running its game on the discrete GPU forces every frame
    /// across the PCIe bus before it can be presented. Matching the adapter to the source
    /// window's monitor keeps the whole pipeline on one GPU.
    /// </summary>
    internal sealed unsafe class D3DCaptureDevice : IDisposable
    {
        public const int D3D11_SDK_VERSION = 7;
        private const uint BGRA_SUPPORT = 0x20; // D3D11_CREATE_DEVICE_BGRA_SUPPORT
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        public static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

        private ID3D11Device* _device;
        private ID3D11DeviceContext* _context;
        private IDirect3DDevice? _winrtDevice;
        private string _adapterName = "default adapter";
        private uint _vendorId;
        private uint _deviceId;

        public ID3D11Device* Device => _device;
        public ID3D11DeviceContext* Context => _context;
        public IDirect3DDevice? WinrtDevice => _winrtDevice;
        public string AdapterName => _adapterName;

        /// <summary>PCI ids of the adapter this device was created on (0 when default).</summary>
        public uint VendorId => _vendorId;
        public uint DeviceId => _deviceId;

        /// <summary>
        /// Creates a hardware device, preferring the adapter that owns <paramref name="hMonitor"/>.
        /// Pass <see cref="nint.Zero"/> to accept the Windows default adapter.
        /// </summary>
        public static D3DCaptureDevice Create(nint hMonitor)
        {
            IDXGIFactory1* factory = null;
            IDXGIAdapter1* matched = null;
            var device = new D3DCaptureDevice();

            try
            {
                var hr = PInvoke.CreateDXGIFactory1(IidOf<IDXGIFactory1>(), out void* factoryPtr);
                hr.ThrowOnFailure();
                factory = (IDXGIFactory1*)factoryPtr;

                if (hMonitor != 0)
                    matched = FindAdapterForMonitor(factory, hMonitor);

                var flags = (D3D11_CREATE_DEVICE_FLAG)BGRA_SUPPORT;
                ID3D11Device* d = null;
                ID3D11DeviceContext* ctx = null;

                hr = PInvoke.D3D11CreateDevice(
                    (IDXGIAdapter*)matched,
                    matched != null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                    default(HMODULE),
                    flags,
                    default(ReadOnlySpan<D3D_FEATURE_LEVEL>),
                    D3D11_SDK_VERSION,
                    &d, null, &ctx);
                hr.ThrowOnFailure();

                device._device = d;
                device._context = ctx;

                if (matched != null)
                {
                    DXGI_ADAPTER_DESC1 desc = matched->GetDesc1();
                    device._adapterName = TrimToNull(desc.Description.AsSpan());
                    device._vendorId = desc.VendorId;
                    device._deviceId = desc.DeviceId;
                }

                // Windows.Graphics.Capture wants an IInspectable IDirect3DDevice wrapping the
                // same underlying DXGI device, so the capture frame pool allocates its
                // textures on this GPU.
                void* dxgiDevice = null;
                var iid = IidIdxgiDevice;
                hr = d->QueryInterface(&iid, &dxgiDevice);
                hr.ThrowOnFailure();

                try
                {
                    int createHr = CreateDirect3D11DeviceFromDXGIDevice((nint)dxgiDevice, out nint abi);
                    if (createHr < 0)
                        Marshal.ThrowExceptionForHR(createHr);

                    try
                    {
                        device._winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
                    }
                    finally
                    {
                        if (abi != 0) Marshal.Release(abi);
                    }
                }
                finally
                {
                    if (dxgiDevice != null) Marshal.Release((nint)dxgiDevice);
                }
            }
            catch
            {
                device.Dispose();
                if (matched != null) _ = matched->Release();
                if (factory != null) _ = factory->Release();
                throw;
            }

            if (matched != null) _ = matched->Release();
            if (factory != null) _ = factory->Release();

            Services.AppLog.Write($"d3d: device created on {device._adapterName}");
            return device;
        }

        /// <summary>Releases the device and every interface derived from it.</summary>
        public void Dispose()
        {
            _winrtDevice = null;

            if (_context != null)
            {
                _ = _context->Release();
                _context = null;
            }

            if (_device != null)
            {
                _ = _device->Release();
                _device = null;
            }
        }

        /// <summary>The DXGI adapter that renders to the given monitor, or null when none matches.</summary>
        private static IDXGIAdapter1* FindAdapterForMonitor(IDXGIFactory1* factory, nint hMonitor)
        {
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter = null;
                var hr = factory->EnumAdapters1(a, &adapter);
                if (hr.Failed || adapter == null) return null;

                for (uint o = 0; ; o++)
                {
                    IDXGIOutput* output = null;
                    hr = adapter->EnumOutputs(o, &output);
                    if (hr.Failed || output == null) break;

                    DXGI_OUTPUT_DESC desc = output->GetDesc();
                    _ = output->Release();

                    if ((nint)desc.Monitor.Value == hMonitor)
                        return adapter;
                }

                _ = adapter->Release();
            }
        }

        private static string TrimToNull(ReadOnlySpan<char> text)
        {
            int end = text.IndexOf('\0');
            return end >= 0 ? new string(text[..end]) : new string(text);
        }

        /// <summary>Reads the IID constant from the generated interface metadata.</summary>
        private static Guid IidOf<T>() where T : unmanaged, IComIID => T.Guid;

        [DllImport("d3d11.dll", SetLastError = false)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    }
}
