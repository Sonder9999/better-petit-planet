using System;
using System.IO;

namespace BetterPetitPlanet.Core.Config;

public sealed class TextDetectionConfig
{
    public string ModelPath { get; set; } = "Assets/Models/Ocr/ch_PP-OCRv3_det_infer.onnx";
    public int MaxSideLength { get; set; } = 960;
    public float BinaryThreshold { get; set; } = 0.3f;
    public float BoxThreshold { get; set; } = 0.5f;
    public float UnclipRatio { get; set; } = 1.5f;
    public int DeviceId { get; set; }
    public bool EnableCpuFallback { get; set; } = true;

    internal TextDetectionConfig ValidatedCopy()
    {
        if (MaxSideLength < 32 || MaxSideLength > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxSideLength));
        if (!float.IsFinite(BinaryThreshold) || BinaryThreshold < 0 || BinaryThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(BinaryThreshold));
        if (!float.IsFinite(BoxThreshold) || BoxThreshold < 0 || BoxThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(BoxThreshold));
        if (!float.IsFinite(UnclipRatio) || UnclipRatio <= 0 || UnclipRatio > 10)
            throw new ArgumentOutOfRangeException(nameof(UnclipRatio));
        if (DeviceId < 0)
            throw new ArgumentOutOfRangeException(nameof(DeviceId));
        if (string.IsNullOrWhiteSpace(ModelPath) || Path.IsPathRooted(ModelPath)
            || ModelPath.Split('/', '\\').AsSpan().Contains(".."))
            throw new ArgumentException("The detector model path must be relative to the application directory.", nameof(ModelPath));
        return (TextDetectionConfig)MemberwiseClone();
    }
}
