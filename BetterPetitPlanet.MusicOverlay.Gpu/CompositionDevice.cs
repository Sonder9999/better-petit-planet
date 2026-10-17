using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using System.Runtime.InteropServices;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DirectComposition.DComp;
using static Vortice.DXGI.DXGI;
using AlphaMode = Vortice.DXGI.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace BetterPetitPlanet.MusicOverlay.Gpu;

internal sealed class CompositionDevice : IDisposable
{
    private ID3D11Device? _d3d;
    private ID3D11DeviceContext? _immediate;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2d;
    private ID2D1DeviceContext? _context;
    private ID2D1Bitmap1? _targetBitmap;
    private IDCompositionDevice? _composition;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private int _width, _height;
    private float _dpi;
    private nint _frameLatencyHandle;
    internal ID2D1DeviceContext Context => _context!;
    internal nint FrameLatencyHandle => _frameLatencyHandle;
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    internal CompositionDevice(nint hwnd, int width, int height, float dpi)
    {
        try
        {
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out _d3d, out _immediate).CheckError();
            using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
            _factory = CreateDXGIFactory2<IDXGIFactory2>(false);
            _swapChain = _factory.CreateSwapChainForComposition(_d3d, new SwapChainDescription1
            {
                Width = (uint)width, Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false, SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.FrameLatencyWaitableObject
            });
            _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            _swapChain2.MaximumFrameLatency = 1;
            _frameLatencyHandle = _swapChain2.FrameLatencyWaitableObject;
            if (_frameLatencyHandle == 0) throw new InvalidOperationException("DXGI returned no frame latency wait handle.");
            _d2dFactory = D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
            _d2d = _d2dFactory.CreateDevice(dxgi);
            _context = _d2d.CreateDeviceContext(DeviceContextOptions.None);
            _composition = DCompositionCreateDevice<IDCompositionDevice>(dxgi);
            _composition.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
            _composition.CreateVisual(out _visual).CheckError();
            _visual.SetContent(_swapChain).CheckError();
            _target.SetRoot(_visual).CheckError();
            _composition.Commit().CheckError();
            Resize(width, height, dpi);
        }
        catch { Dispose(); throw; }
    }

    internal void Resize(int width, int height, float dpi)
    {
        if (_width == width && _height == height && _dpi == dpi) return;
        Context.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        if (_width != 0 && (_width != width || _height != height))
            _swapChain!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm,
                SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _targetBitmap = Context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = dpi, DpiY = dpi,
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw
        });
        Context.Target = _targetBitmap;
        Context.SetDpi(dpi, dpi);
        _width = width; _height = height; _dpi = dpi;
    }

    internal void Begin()
    {
        Context.BeginDraw();
        Context.Clear(new Color4(0, 0, 0, 0));
    }
    internal void End() => Context.EndDraw().CheckError();
    internal void Present() => _swapChain!.Present(1, PresentFlags.None).CheckError();

    public void Dispose()
    {
        if (_context != null) _context.Target = null;
        _targetBitmap?.Dispose(); _targetBitmap = null;
        _visual?.Dispose(); _visual = null;
        _target?.Dispose(); _target = null;
        _composition?.Dispose(); _composition = null;
        _context?.Dispose(); _context = null;
        _d2d?.Dispose(); _d2d = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        // This object is disposed only on its owning render thread, after all waits have returned.
        if (_frameLatencyHandle != 0) { CloseHandle(_frameLatencyHandle); _frameLatencyHandle = 0; }
        _swapChain2?.Dispose(); _swapChain2 = null;
        _swapChain?.Dispose(); _swapChain = null;
        _factory?.Dispose(); _factory = null;
        _immediate?.Dispose(); _immediate = null;
        _d3d?.Dispose(); _d3d = null;
    }
}
