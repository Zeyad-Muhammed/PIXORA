using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PIXORA.Recording;

/// <summary>Minimal COM interop needed to drive Windows.Graphics.Capture from a plain .NET process.</summary>
internal static class GraphicsInterop
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static bool IsCaptureSupported => GraphicsCaptureSession.IsSupported();

    public static IDirect3DDevice CreateDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;

        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out var d3d, out _, out var ctx);
        if (hr < 0) throw new RecordingException($"No compatible graphics device is available (0x{hr:X8}).");
        IntPtr dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            var iid = IidDxgiDevice;
            hr = Marshal.QueryInterface(d3d, in iid, out dxgi);
            if (hr < 0) throw new RecordingException($"Graphics device does not support DXGI (0x{hr:X8}).");
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable);
            if (hr < 0) throw new RecordingException($"Could not create the capture device (0x{hr:X8}).");
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (ctx != IntPtr.Zero) Marshal.Release(ctx);
            if (d3d != IntPtr.Zero) Marshal.Release(d3d);
        }
    }

    private static IGraphicsCaptureItemInterop GetInterop()
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(cls, cls.Length, out var hstr);
        if (hr < 0) throw new RecordingException("Windows runtime is unavailable.");
        try
        {
            var iid = IidGraphicsCaptureItemInterop;
            hr = RoGetActivationFactory(hstr, ref iid, out var factory);
            if (hr < 0) throw new RecordingException($"Screen capture is not supported on this system (0x{hr:X8}).");
            try { return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory); }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var iid = IidGraphicsCaptureItem;
        var ptr = GetInterop().CreateForMonitor(monitor, ref iid);
        try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var iid = IidGraphicsCaptureItem;
        var ptr = GetInterop().CreateForWindow(hwnd, ref iid);
        try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
}
