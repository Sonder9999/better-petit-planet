using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vanara.PInvoke;
using static System.Console;

namespace BetterPetitPlanet.GameCapture.Engines.PrintWindow;

public class PrintWindowCapture : IGameCapture
{
    public bool IsCapturing { get; private set; }
    private readonly Stopwatch _sizeCheckTimer = new();
    private readonly ReaderWriterLockSlim _lockSlim = new();
    private volatile nint _hWnd;
    private PrintWindowSession? _session;
    private RECT? _captureRect;
    private volatile bool _lastCaptureFailed;

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public GameCaptureFrame? Capture() => Capture(false);

    public void Start(nint hWnd, Dictionary<string, object>? settings = null)
    {
        if (settings != null && settings.TryGetValue("autoFixWin11BitBlt", out var value) && value is true)
        {
            PrintWindowRegistryHelper.SetDirectXUserGlobalSettings();
        }

        _lockSlim.EnterWriteLock();
        try
        {
            _hWnd = hWnd;
            if (_hWnd == IntPtr.Zero)
            {
                return;
            }

            _session?.Dispose();
            _session = null;
            IsCapturing = true;
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }

        CheckSession();
    }

    private void CheckSession()
    {
        if (_lockSlim.WaitingWriteCount > 0 || !_lockSlim.TryEnterWriteLock(TimeSpan.FromSeconds(0.5)))
        {
            return;
        }

        try
        {
            if (_session is not null && (_session.Invalid || _lastCaptureFailed))
            {
                _session.Dispose();
                _session = null;
            }

            if (!User32.GetClientRect(_hWnd, out var clientRect) || clientRect == default)
            {
                _session?.Dispose();
                _session = null;
                _captureRect = null;
                return;
            }

            var width = clientRect.right - clientRect.left;
            var height = clientRect.bottom - clientRect.top;
            DwmApi.DwmGetWindowAttribute<RECT>(_hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out var windowRect);
            var left = windowRect.Left;
            var top = windowRect.Top + windowRect.Height - clientRect.Height;
            var right = left + clientRect.Width;
            var bottom = top + clientRect.Height;
            _captureRect = new RECT(left, top, right, bottom);

            if (_session != null)
            {
                if (_session.Width == width && _session.Height == height)
                {
                    return;
                }

                _session.Dispose();
            }

            _session = new PrintWindowSession(_hWnd, width, height);
        }
        catch (Exception e)
        {
            Error.WriteLine("[PrintWindow]Failed to create session:{0}", e);
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }

    private GameCaptureFrame? Capture(bool recursive)
    {
        if (_hWnd == IntPtr.Zero)
        {
            return null;
        }

        if (!_sizeCheckTimer.IsRunning)
        {
            _sizeCheckTimer.Start();
        }

        if (_sizeCheckTimer.ElapsedMilliseconds > 1000 || _lastCaptureFailed || recursive)
        {
            _sizeCheckTimer.Restart();
            CheckSession();
        }

        _lockSlim.EnterReadLock();
        try
        {
            if (_session == null || _session.Invalid)
            {
                _lastCaptureFailed = true;
                if (!recursive)
                {
                    return Capture(true);
                }

                return null;
            }

            var mat = _session.GetImage();
            if (mat == null)
            {
                _lastCaptureFailed = true;
                if (!recursive)
                {
                    return Capture(true);
                }

                return null;
            }

            _lastCaptureFailed = false;
            return new GameCaptureFrame(mat, _captureRect);
        }
        catch (Exception e)
        {
            Error.WriteLine("[PrintWindow]Failed to capture image {0}", e);
            _lastCaptureFailed = true;
            if (!recursive)
            {
                return Capture(true);
            }

            return null;
        }
        finally
        {
            _lockSlim.ExitReadLock();
        }
    }

    public void Stop()
    {
        _lockSlim.EnterWriteLock();
        try
        {
            _sizeCheckTimer.Stop();
            _session?.Dispose();
            _session = null;
            _hWnd = IntPtr.Zero;
            IsCapturing = false;
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }
}
