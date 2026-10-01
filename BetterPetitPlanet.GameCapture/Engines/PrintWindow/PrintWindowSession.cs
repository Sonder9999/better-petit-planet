using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameCapture.Engines.PrintWindow;

public class PrintWindowSession : IDisposable
{
    private readonly HWND _hWnd;
    private readonly object _lockObject = new();

    private Gdi32.SafeHBITMAP _hBitmap;
    private IntPtr _bitsPtr;
    private readonly int _stride;
    private Gdi32.SafeHDC _hdcDest;
    private User32.SafeReleaseHDC _hdcSrc;
    private HGDIOBJ _oldBitmap;
    private readonly int _bufferSize;
    private readonly ConcurrentStack<IntPtr> _bufferPool = [];

    public int Width { get; }
    public int Height { get; }

    public bool Invalid => _hWnd.IsNull || _hdcSrc.IsInvalid || _hdcDest.IsInvalid || _hBitmap.IsInvalid || _bitsPtr == 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(HWND hwnd, HDC hdcBlt, uint nFlags);

    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public PrintWindowSession(HWND hWnd, int w, int h)
    {
        if (hWnd.IsNull) throw new ArgumentException("hWnd is invalid", nameof(hWnd));
        if (w <= 0 || h <= 0) throw new ArgumentException("Invalid width or height", nameof(w));

        _hWnd = hWnd;
        Width = w;
        Height = h;

        lock (_lockObject)
        {
            try
            {
                _hdcSrc = User32.GetDC(_hWnd);
                _hdcDest = Gdi32.CreateCompatibleDC(_hdcSrc.IsInvalid ? IntPtr.Zero : _hdcSrc);
                if (_hdcDest.IsInvalid)
                {
                    Debug.Fail("Failed to create CompatibleDC");
                    throw new InvalidOperationException($"Failed to create CompatibleDC for {_hWnd}");
                }

                var bmi = new Gdi32.BITMAPINFO
                {
                    bmiHeader = new Gdi32.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(),
                        biWidth = Width,
                        biHeight = -Height, // Top-down image
                        biPlanes = 1,
                        biBitCount = 24,
                        biCompression = Gdi32.BitmapCompressionMode.BI_RGB,
                        biSizeImage = 0
                    }
                };
                _hBitmap = Gdi32.CreateDIBSection(_hdcDest, bmi, Gdi32.DIBColorMode.DIB_RGB_COLORS, out _bitsPtr, IntPtr.Zero);

                if (_hBitmap.IsInvalid || _bitsPtr == 0)
                {
                    if (!_hBitmap.IsInvalid) Gdi32.DeleteObject(_hBitmap);
                    _hdcDest.Dispose();
                    _hdcSrc.Dispose();
                    Debug.Fail("Failed to create DIBSection");
                    throw new InvalidOperationException($"Failed to create DIBSection for {_hWnd}");
                }

                _stride = (Width * 3 + 3) & ~3;
                _bufferSize = _stride * Height;
                _oldBitmap = Gdi32.SelectObject(_hdcDest, _hBitmap);
            }
            catch
            {
                ReleaseResources();
                throw;
            }
            finally
            {
                Gdi32.GdiFlush();
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            ReleaseResources();
        }
        GC.SuppressFinalize(this);
    }

    public unsafe Mat? GetImage()
    {
        lock (_lockObject)
        {
            if (User32.IsIconic(_hWnd)) return null;

            // 优先尝试 PW_CLIENTONLY | PW_RENDERFULLCONTENT (3)
            var success = PrintWindow(_hWnd, _hdcDest, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            if (!success)
            {
                // 回退到 PW_RENDERFULLCONTENT (2)
                success = PrintWindow(_hWnd, _hdcDest, PW_RENDERFULLCONTENT);
            }
            if (!success)
            {
                // 回退到传统 PW_CLIENTONLY (1)
                success = PrintWindow(_hWnd, _hdcDest, PW_CLIENTONLY);
            }
            if (!success)
            {
                // PrintWindow 全部落空时，每次重新获取实时窗口 DC 进行 GDI BitBlt
                using var currentDc = User32.GetDC(_hWnd);
                if (!currentDc.IsInvalid)
                {
                    success = Gdi32.BitBlt(_hdcDest, 0, 0, Width, Height,
                        currentDc, 0, 0, Gdi32.RasterOperationMode.SRCCOPY);
                }
            }
            if (!success || !Gdi32.GdiFlush()) return null;

            var buffer = AcquireBuffer();
            var src = (byte*)_bitsPtr.ToPointer();
            var dest = (byte*)buffer.ToPointer();
            var size = _bufferSize;
            var longSize = size / 8;
            var remaining = size % 8;

            var src64 = (long*)src;
            var dest64 = (long*)dest;
            for (var i = 0; i < longSize; i++)
            {
                dest64[i] = src64[i];
            }

            var src8 = (byte*)(src64 + longSize);
            var dest8 = (byte*)(dest64 + longSize);
            for (var i = 0; i < remaining; i++)
            {
                dest8[i] = src8[i];
            }

            var step = (long)_stride;
            return PrintWindowMat.FromPixelData(this, Height, Width, MatType.CV_8UC3, buffer, step);
        }
    }

    public IntPtr AcquireBuffer()
    {
        return _bufferPool.TryPop(out var buffer) ? buffer : Marshal.AllocHGlobal(_bufferSize);
    }

    public void ReleaseBuffer(IntPtr buffer)
    {
        _bufferPool.Push(buffer);
    }

    private void ReleaseResources()
    {
        if (_bitsPtr != 0)
        {
            _bitsPtr = 0;
        }

        if (!_hdcDest.IsInvalid && !_oldBitmap.IsNull)
        {
            Gdi32.SelectObject(_hdcDest, _oldBitmap);
            _oldBitmap = default;
        }

        if (!_hBitmap.IsInvalid)
        {
            _hBitmap.Dispose();
        }

        if (!_hdcDest.IsInvalid)
        {
            _hdcDest.Dispose();
        }

        if (!_hdcSrc.IsInvalid)
        {
            _hdcSrc.Dispose();
        }

        while (_bufferPool.TryPop(out var buffer))
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
