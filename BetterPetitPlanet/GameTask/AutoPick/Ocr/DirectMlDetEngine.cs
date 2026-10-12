using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick.Ocr;

public sealed class DirectMlDetEngine : ITextDetectionEngine
{
    private readonly object _inferenceLock = new();
    private readonly TextDetectionConfig _config;
    private readonly DbNetPostProcessor _postProcessor;
    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private float[] _inputBuffer = [];
    private float[] _probabilityBuffer = [];
    private bool _disposed;

    /// <summary>Construct on the auxiliary worker; model loading is synchronous.</summary>
    public DirectMlDetEngine(TextDetectionConfig? config = null)
    {
        _config = (config ?? new TextDetectionConfig()).ValidatedCopy();
        _postProcessor = new DbNetPostProcessor(_config);
        Initialize();
    }

    public bool IsAvailable
    {
        get { lock (_inferenceLock) return !_disposed && _session != null; }
    }

    private void Initialize()
    {
        string modelPath = Path.Combine(AppContext.BaseDirectory, _config.ModelPath);
        if (!File.Exists(modelPath)) return;
        InferenceSession? session = null;
        try
        {
            try
            {
                session = CreateSession(modelPath, useDirectMl: true);
            }
            catch (Exception) when (_config.EnableCpuFallback)
            {
                session = CreateSession(modelPath, useDirectMl: false);
            }
            _inputName = session.InputMetadata.Keys.Single();
            var input = session.InputMetadata[_inputName];
            var output = session.OutputMetadata.Values.First();
            if (input.ElementType != typeof(float) || input.Dimensions.Length != 4
                || output.ElementType != typeof(float) || output.Dimensions.Length != 4)
                throw new InvalidDataException("The model is not a DBNet float image detector.");
            _session = session;
        }
        catch (Exception)
        {
            session?.Dispose();
        }
    }

    private InferenceSession CreateSession(string modelPath, bool useDirectMl)
    {
        using var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL
        };
        if (useDirectMl) options.AppendExecutionProvider_DML(_config.DeviceId);
        return new InferenceSession(modelPath, options);
    }

    public IReadOnlyList<Rect> Detect(Mat image)
    {
        lock (_inferenceLock)
        {
            if (_disposed || _session == null || image == null || image.IsDisposed || image.Empty())
                return [];
            try
            {
                var size = GetInputSize(image.Size(), _config.MaxSideLength);
                var tensor = Preprocess(image, size);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
                using var results = _session.Run(inputs);
                var output = results.First().AsTensor<float>();
                if (output.Dimensions.Length != 4 || output.Dimensions[0] != 1 || output.Dimensions[1] != 1)
                    return [];
                int height = output.Dimensions[2], width = output.Dimensions[3];
                int count = checked(height * width);
                if (_probabilityBuffer.Length < count) _probabilityBuffer = new float[count];
                if (output is DenseTensor<float> dense)
                    dense.Buffer.Span.CopyTo(_probabilityBuffer);
                else
                {
                    int index = 0;
                    foreach (float value in output) _probabilityBuffer[index++] = value;
                }
                using var map = Mat.FromPixelData(height, width, MatType.CV_32FC1, _probabilityBuffer);
                return _postProcessor.Process(map, image.Size());
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    private DenseTensor<float> Preprocess(Mat image, Size size)
    {
        using var bgr = new Mat();
        if (image.Type() == MatType.CV_8UC4)
            Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR);
        else if (image.Type() == MatType.CV_8UC1)
            Cv2.CvtColor(image, bgr, ColorConversionCodes.GRAY2BGR);
        else if (image.Type() == MatType.CV_8UC3)
            image.CopyTo(bgr);
        else
            throw new ArgumentException("Text detection expects an 8-bit BGR, BGRA, or gray image.", nameof(image));
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, size, interpolation: InterpolationFlags.Linear);
        int plane = checked(size.Width * size.Height);
        int count = checked(plane * 3);
        if (_inputBuffer.Length < count) _inputBuffer = new float[count];
        var pixels = resized.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < size.Height; y++)
        for (int x = 0; x < size.Width; x++)
        {
            var pixel = pixels[y, x];
            int index = y * size.Width + x;
            // PP-OCRv3 detection normalizes BGR directly; do not swap red and blue.
            _inputBuffer[index] = (pixel.Item0 / 255f - 0.485f) / 0.229f;
            _inputBuffer[plane + index] = (pixel.Item1 / 255f - 0.456f) / 0.224f;
            _inputBuffer[plane * 2 + index] = (pixel.Item2 / 255f - 0.406f) / 0.225f;
        }
        return new DenseTensor<float>(_inputBuffer.AsMemory(0, count), new[] { 1, 3, size.Height, size.Width });
    }

    public static Size GetInputSize(Size originalSize, int maxSideLength)
    {
        if (originalSize.Width <= 0 || originalSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(originalSize));
        if (maxSideLength < 32 || maxSideLength > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxSideLength));
        const int stride = 32;
        int limit = maxSideLength / stride * stride;
        double ratio = Math.Min(1, (double)limit / Math.Max(originalSize.Width, originalSize.Height));
        int width = Math.Max(stride, (int)Math.Round((int)(originalSize.Width * ratio) / (double)stride) * stride);
        int height = Math.Max(stride, (int)Math.Round((int)(originalSize.Height * ratio) / (double)stride) * stride);
        return new Size(width, height);
    }
    public void Dispose()
    {
        lock (_inferenceLock)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
            _inputBuffer = [];
            _probabilityBuffer = [];
        }
        GC.SuppressFinalize(this);
    }
}
