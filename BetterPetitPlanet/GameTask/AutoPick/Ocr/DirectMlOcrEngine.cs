using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick.Ocr;

public sealed class DirectMlOcrEngine : IOcrEngine, IDisposable
{
    private readonly ILogger<DirectMlOcrEngine>? _logger;
    private readonly object _inferenceLock = new();
    private InferenceSession? _session;
    private List<string> _characterList = [];
    private bool _isDisposed;

    public string Name => "DirectML";
    public bool IsAvailable => _session != null;

    public DirectMlOcrEngine(ILogger<DirectMlOcrEngine>? logger = null)
    {
        _logger = logger;
        InitializeEngine();
    }

    private void InitializeEngine()
    {
        var modelCandidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "Ocr", "ch_PP-OCRv3_rec_infer.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Model", "ch_PP-OCRv3_rec_infer.onnx"),
            Path.Combine(AppContext.BaseDirectory, "assets", "models", "ocr", "ch_PP-OCRv3_rec_infer.onnx"),
            Path.Combine(AppContext.BaseDirectory, "assets", "models", "ocr", "recognition.onnx"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3", "Lib", "site-packages", "rapidocr_onnxruntime", "models", "ch_PP-OCRv3_rec_infer.onnx")
        };

        string? modelPath = null;
        foreach (var path in modelCandidatePaths)
        {
            if (File.Exists(path))
            {
                modelPath = path;
                break;
            }
        }

        if (modelPath == null)
        {
            _logger?.LogWarning("ONNX OCR model not found in default search paths. DirectML OCR disabled.");
            return;
        }

        try
        {
            var options = new SessionOptions();
            try
            {
                // Try DirectML GPU acceleration (Device 0)
                options.AppendExecutionProvider_DML(0);
                _logger?.LogInformation("DirectML Execution Provider initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DirectML execution provider failed, falling back to CPU.");
            }

            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _session = new InferenceSession(modelPath, options);

            // Extract embedded character dict metadata if present
            LoadCharacterDict();
            _logger?.LogInformation("DirectMlOcrEngine loaded model from: {Path}", modelPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize ONNX InferenceSession for OCR");
            _session = null;
        }
    }

    private void LoadCharacterDict()
    {
        if (_session == null) return;

        try
        {
            var metadata = _session.ModelMetadata.CustomMetadataMap;
            if (metadata.TryGetValue("character", out var charText))
            {
                _characterList = [.. charText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)];
                _logger?.LogInformation("Loaded {Count} characters from model metadata.", _characterList.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load character dict from metadata");
        }
    }

    public string Recognize(Mat roiMat)
    {
        if (_session == null || roiMat == null || roiMat.Empty() || _isDisposed)
        {
            return string.Empty;
        }

        lock (_inferenceLock)
        {
            if (_session == null || roiMat.Empty() || _isDisposed)
            {
                return string.Empty;
            }

            try
            {
                // Normalize image to [1, 3, 48, targetWidth]
                const int targetHeight = 48;
                int targetWidth = (int)Math.Max(32, Math.Round((double)roiMat.Cols * targetHeight / roiMat.Rows));
                targetWidth = Math.Min(targetWidth, 640);

                using var resized = new Mat();
                Cv2.Resize(roiMat, resized, new Size(targetWidth, targetHeight));

                using var rgb = new Mat();
                if (resized.Channels() == 4)
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGRA2RGB);
                }
                else if (resized.Channels() == 3)
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
                }
                else
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.GRAY2RGB);
                }

                // Convert to float tensor and normalize: (pixel / 255.0 - 0.5) / 0.5
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
                for (int c = 0; c < 3; c++)
                {
                    for (int h = 0; h < targetHeight; h++)
                    {
                        for (int w = 0; w < targetWidth; w++)
                        {
                            var pixel = rgb.At<Vec3b>(h, w)[c];
                            inputTensor[0, c, h, w] = (float)((pixel / 255.0 - 0.5) / 0.5);
                        }
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("x", inputTensor)
                };

                using var results = _session.Run(inputs);
                var output = results[0].AsTensor<float>();

                return DecodeCtc(output);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OCR inference error");
                return string.Empty;
            }
        }
    }

    private string DecodeCtc(Tensor<float> outputTensor)
    {
        if (_characterList.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        int timeSteps = outputTensor.Dimensions[1];
        int numClasses = outputTensor.Dimensions[2];

        int lastClassIndex = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            int maxIdx = 0;
            float maxScore = float.MinValue;

            for (int c = 0; c < numClasses; c++)
            {
                float score = outputTensor[0, t, c];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxIdx = c;
                }
            }

            // 0 is blank in CTC
            if (maxIdx > 0 && maxIdx != lastClassIndex)
            {
                int charIdx = maxIdx - 1;
                if (charIdx >= 0 && charIdx < _characterList.Count)
                {
                    sb.Append(_characterList[charIdx]);
                }
            }

            lastClassIndex = maxIdx;
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_inferenceLock)
        {
            if (!_isDisposed)
            {
                _session?.Dispose();
                _session = null;
                _isDisposed = true;
            }
        }
        GC.SuppressFinalize(this);
    }
}
