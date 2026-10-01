using OpenCvSharp;

namespace BetterPetitPlanet.GameCapture.Graphics.Helpers;

public sealed class WgcBgrMat : Mat
{
    private readonly Mat _owner;
    private readonly Action<Mat> _recycler;
    private int _recycled;

    private WgcBgrMat(Mat owner, Action<Mat> recycler) : base(owner)
    {
        _owner = owner;
        _recycler = recycler;
    }

    public static Mat CreateFrom(Mat owner, Action<Mat> recycler)
    {
        return new WgcBgrMat(owner, recycler);
    }

    protected override void DisposeUnmanaged()
    {
        base.DisposeUnmanaged();
        if (Interlocked.Exchange(ref _recycled, 1) == 0)
        {
            _recycler?.Invoke(_owner);
        }
    }
}
