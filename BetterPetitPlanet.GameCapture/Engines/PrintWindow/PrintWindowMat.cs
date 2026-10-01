using System;
using OpenCvSharp;
using OpenCvSharp.Internal;

namespace BetterPetitPlanet.GameCapture.Engines.PrintWindow;

public class PrintWindowMat : Mat
{
    private readonly PrintWindowSession _session;
    private readonly IntPtr _data;

    private PrintWindowMat(IntPtr ptr, PrintWindowSession session, IntPtr data)
    {
        if (ptr == IntPtr.Zero)
            throw new OpenCvSharpException("Native object address is NULL");
        this.ptr = ptr;
        _session = session;
        _data = data;
    }

    public static Mat FromPixelData(PrintWindowSession session, int rows, int cols, MatType type, IntPtr data, long step = 0)
    {
        NativeMethods.HandleException(
            NativeMethods.core_Mat_new8(rows, cols, type, data, new IntPtr(step), out var ptr));
        return new PrintWindowMat(ptr, session, data);
    }

    protected override void DisposeUnmanaged()
    {
        base.DisposeUnmanaged();
        _session.ReleaseBuffer(_data);
    }
}
