using System;
using System.Collections.Generic;
using System.IO;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using BetterPetitPlanet.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 竖笛等乐器演奏界面自动识别器。
/// 基于 OCR 文字识别与原生小块标注图像模板双引擎对比，高精准低开销识别游戏是否处于乐器弹奏状态。
/// </summary>
public sealed class InstrumentDetector : IDisposable
{
    private readonly IConfigService? _configService;
    private readonly IOcrEngine? _ocrEngine;
    private readonly bool _ownsOcrEngine;
    private readonly ILogger<InstrumentDetector>? _logger;
    private readonly object _syncLock = new();

    private readonly Dictionary<string, Mat> _targetTemplates = [];
    private Mat? _legacyTemplateMat;
    private bool _isInitialized;

    private DateTime _lastDetectionTime = DateTime.MinValue;

    public const double DefaultThreshold = 0.65;

    public bool IsTemplateLoaded => _targetTemplates.Count > 0 || (_legacyTemplateMat != null && !_legacyTemplateMat.Empty());

    /// <summary>
    /// 最新的全局检测结果（全局单例状态源，确保各窗口与主界面绝对同步）
    /// </summary>
    public InstrumentDetectionResult? LatestResult { get; private set; }

    /// <summary>
    /// 全局状态同步更新事件
    /// </summary>
    public event Action<InstrumentDetectionResult>? DetectionUpdated;

    public InstrumentDetector(
        IConfigService? configService = null,
        IOcrEngine? ocrEngine = null,
        ILogger<InstrumentDetector>? logger = null)
    {
        _configService = configService;
        _logger = logger;

        if (ocrEngine != null)
        {
            _ocrEngine = ocrEngine;
            _ownsOcrEngine = false;
        }
        else
        {
            try
            {
                _ocrEngine = new DirectMlOcrEngine();
                _ownsOcrEngine = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to instantiate default DirectMlOcrEngine in InstrumentDetector");
                _ownsOcrEngine = false;
            }
        }

        InitializeTemplates();
    }

    private void InitializeTemplates(string instrumentName = "Recorder")
    {
        lock (_syncLock)
        {
            if (_isInitialized) return;

            var baseDirs = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", instrumentName, "targets"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", instrumentName),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", "Recorder", "targets"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", "targets"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments"),
                Path.Combine(@"D:\Coding\Game\petit_planet\better-petit-planet\better_petit_planet\BetterPetitPlanet\Assets\Instruments", instrumentName, "targets"),
                Path.Combine(@"D:\Coding\Game\petit_planet\better-petit-planet\better_petit_planet\BetterPetitPlanet\Assets\Instruments", instrumentName),
                @"D:\Coding\Game\petit_planet\better-petit-planet\better_petit_planet\test_crops",
            };

            // 加载 21 个原生小块目标模板 (Numbers, Syllables, Keys)
            var allTargets = new List<InstrumentTarget>();
            allTargets.AddRange(InstrumentTargets.Numbers);
            allTargets.AddRange(InstrumentTargets.Syllables);
            allTargets.AddRange(InstrumentTargets.Keys);

            foreach (var target in allTargets)
            {
                string fileName = $"{target.Name}.png";
                foreach (var dir in baseDirs)
                {
                    string filePath = Path.Combine(dir, fileName);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var mat = Cv2.ImRead(filePath, ImreadModes.Color);
                            if (!mat.Empty())
                            {
                                _targetTemplates[target.Name] = mat;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to load target template: {File}", filePath);
                        }
                    }
                }
            }

            _logger?.LogInformation("InstrumentDetector loaded {Count}/{Total} small target templates for {Instrument}.",
                _targetTemplates.Count, allTargets.Count, instrumentName);

            // 兼容性大底模板
            var legacyCandidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", instrumentName, "template.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", "Recorder", "template.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Instruments", "recorder_template.png"),
                Path.Combine(@"D:\Coding\Game\petit_planet\better-petit-planet\better_petit_planet\BetterPetitPlanet\Assets\Instruments", instrumentName, "template.png"),
                @"D:\Coding\Game\petit_planet\petit_music\music_recorder.png"
            };

            foreach (var path in legacyCandidates)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var mat = Cv2.ImRead(path, ImreadModes.Color);
                        if (!mat.Empty())
                        {
                            _legacyTemplateMat = mat;
                            break;
                        }
                    }
                    catch { }
                }
            }

            _isInitialized = true;
        }
    }

    /// <summary>
    /// 竖笛界面检测核心方法：纯 OCR 识别 7 个数字与 7 个唱名。
    /// 根据指令目前已禁用图像模板对比，纯由实时 OCR 识别判定，毫秒级响应。
    /// </summary>
    public InstrumentDetectionResult DetectFull(Mat? frame)
    {
        var result = new InstrumentDetectionResult();
        if (frame == null || frame.Empty())
        {
            result.StatusSummary = "竖笛: 未检测 (画面为空)";
            UpdateLatestResult(result);
            return result;
        }

        lock (_syncLock)
        {
            try
            {
                // 1. OCR 识别（重点检测 7 个数字 1~7 及 唱名 do~ti，共 14 项原生小目标）
                DetectOcr(frame, out double ocrConfidence, out int ocrHits, out int ocrTotal);
                result.OcrConfidence = ocrConfidence;
                result.OcrHits = ocrHits;
                result.OcrTotal = ocrTotal;

                // 2. 图像识别对比按要求暂时禁用，仅保留指标为 0
                result.TemplateConfidence = 0.0;
                result.TemplateHits = 0;
                result.TemplateTotal = 0;

                // 3. 判定标准：OCR 识别命中率 >= 35% 即判定为已就绪 (就绪时实测 90%~100%，未拿出时 0%)
                result.IsDetected = ocrConfidence >= 0.35;
                result.CombinedConfidence = ocrConfidence;
                result.Details = $"OCR: {ocrConfidence:P0} ({ocrHits}/{ocrTotal})";

                string stateText = result.IsDetected ? "已就绪" : "未拿出";
                result.StatusSummary = $"竖笛: {stateText} (OCR: {ocrConfidence:P0})";

                _lastDetectionTime = DateTime.Now;
                UpdateLatestResult(result);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception during pure OCR instrument detection");
                result.StatusSummary = "竖笛: 检测异常";
                UpdateLatestResult(result);
                return result;
            }
        }
    }

    /// <summary>
    /// 先 OCR 识别逻辑：提取数字与唱名区域进行文字识别对比 (优化分级识别，降低开销)
    /// </summary>
    private void DetectOcr(Mat frame, out double confidence, out int hits, out int total)
    {
        hits = 0;
        total = InstrumentTargets.Numbers.Length;

        if (_ocrEngine == null || !_ocrEngine.IsAvailable)
        {
            confidence = 0.0;
            return;
        }

        double scaleX = frame.Width / (double)InstrumentTargets.BaseWidth;
        double scaleY = frame.Height / (double)InstrumentTargets.BaseHeight;

        // 1. 优先检测 7 个数字 (1~7)
        int numberHits = 0;
        foreach (var target in InstrumentTargets.Numbers)
        {
            if (TryCropTarget(frame, target, scaleX, scaleY, out var cropMat))
            {
                using (cropMat)
                {
                    string text = _ocrEngine.Recognize(cropMat).Trim();
                    if (text.Equals(target.ExpectedText, StringComparison.OrdinalIgnoreCase) ||
                        text.Contains(target.ExpectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        numberHits++;
                    }
                }
            }
        }

        // 若数字命中已达 3 个以上，已可明确判定为竖笛已拿出，无需额外耗费开销去识别唱名
        if (numberHits >= 3)
        {
            hits = numberHits;
            total = InstrumentTargets.Numbers.Length;
            confidence = (double)numberHits / total;
            return;
        }

        // 若数字命中为 0，基本判定未拿出竖笛，无需识别唱名
        if (numberHits == 0)
        {
            hits = 0;
            total = InstrumentTargets.Numbers.Length;
            confidence = 0.0;
            return;
        }

        // 2. 若处于边缘状态 (1~2 个数字)，进一步识别唱名进行双重验证
        int syllableHits = 0;
        foreach (var target in InstrumentTargets.Syllables)
        {
            if (TryCropTarget(frame, target, scaleX, scaleY, out var cropMat))
            {
                using (cropMat)
                {
                    string text = _ocrEngine.Recognize(cropMat).Trim().ToLowerInvariant();
                    if (text.Equals(target.ExpectedText, StringComparison.OrdinalIgnoreCase) ||
                        text.Contains(target.ExpectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        syllableHits++;
                    }
                }
            }
        }

        hits = numberHits + syllableHits;
        total = InstrumentTargets.Numbers.Length + InstrumentTargets.Syllables.Length;
        confidence = total > 0 ? (double)hits / total : 0.0;
    }

    /// <summary>
    /// 同时小块图像对比逻辑：将 7 个按键数字及唱名与原生标注模板分别做相关性匹配
    /// </summary>
    private void DetectSmallTemplates(Mat frame, out double confidence, out int hits, out int total)
    {
        hits = 0;
        total = 0;
        double totalScore = 0.0;

        double scaleX = frame.Width / (double)InstrumentTargets.BaseWidth;
        double scaleY = frame.Height / (double)InstrumentTargets.BaseHeight;

        // 对比 Numbers
        foreach (var target in InstrumentTargets.Numbers)
        {
            if (_targetTemplates.TryGetValue(target.Name, out var tmplMat))
            {
                total++;
                if (TryCropTarget(frame, target, scaleX, scaleY, out var cropMat))
                {
                    using (cropMat)
                    {
                        double score = MatchSmallCrop(cropMat, tmplMat);
                        totalScore += Math.Max(0.0, score);
                        if (score >= 0.70)
                        {
                            hits++;
                        }
                    }
                }
            }
        }

        // 对比 Syllables
        foreach (var target in InstrumentTargets.Syllables)
        {
            if (_targetTemplates.TryGetValue(target.Name, out var tmplMat))
            {
                total++;
                if (TryCropTarget(frame, target, scaleX, scaleY, out var cropMat))
                {
                    using (cropMat)
                    {
                        double score = MatchSmallCrop(cropMat, tmplMat);
                        totalScore += Math.Max(0.0, score);
                        if (score >= 0.70)
                        {
                            hits++;
                        }
                    }
                }
            }
        }

        // 如果小模板未加载，回退到历史整块匹配
        if (total == 0 && _legacyTemplateMat != null && !_legacyTemplateMat.Empty())
        {
            total = 1;
            int roiX = (int)Math.Round(500.0 * scaleX);
            int roiY = (int)Math.Round(1100.0 * scaleY);
            int roiW = (int)Math.Round(1500.0 * scaleX);
            int roiH = (int)Math.Round(250.0 * scaleY);

            roiX = Math.Clamp(roiX, 0, frame.Width - 10);
            roiY = Math.Clamp(roiY, 0, frame.Height - 10);
            roiW = Math.Clamp(roiW, 10, frame.Width - roiX);
            roiH = Math.Clamp(roiH, 10, frame.Height - roiY);

            using var roi = new Mat(frame, new Rect(roiX, roiY, roiW, roiH));
            using var scaledRoi = new Mat();
            Cv2.Resize(roi, scaledRoi, new Size(750, 125));

            using var scaledTmpl = new Mat();
            Cv2.Resize(_legacyTemplateMat, scaledTmpl, new Size(750, 125));

            using var matchRes = new Mat();
            Cv2.MatchTemplate(scaledRoi, scaledTmpl, matchRes, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(matchRes, out _, out double maxVal, out _, out _);

            confidence = double.IsNaN(maxVal) ? 0.0 : maxVal;
            if (confidence >= DefaultThreshold) hits = 1;
            return;
        }

        confidence = total > 0 ? totalScore / total : 0.0;
    }

    private static double MatchSmallCrop(Mat crop, Mat tmpl)
    {
        if (crop.Empty() || tmpl.Empty()) return 0.0;

        using var resizedCrop = new Mat();
        if (crop.Size() != tmpl.Size())
        {
            Cv2.Resize(crop, resizedCrop, tmpl.Size());
        }
        else
        {
            crop.CopyTo(resizedCrop);
        }

        using var matchResult = new Mat();
        Cv2.MatchTemplate(resizedCrop, tmpl, matchResult, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(matchResult, out _, out double maxVal, out _, out _);
        return double.IsNaN(maxVal) ? 0.0 : maxVal;
    }

    private static bool TryCropTarget(Mat frame, InstrumentTarget target, double scaleX, double scaleY, out Mat cropMat)
    {
        cropMat = null!;
        int x = (int)Math.Round(target.X * scaleX);
        int y = (int)Math.Round(target.Y * scaleY);
        int w = (int)Math.Round(target.Width * scaleX);
        int h = (int)Math.Round(target.Height * scaleY);

        if (x < 0 || y < 0 || x + w > frame.Width || y + h > frame.Height || w <= 0 || h <= 0)
        {
            return false;
        }

        cropMat = new Mat(frame, new Rect(x, y, w, h));
        return true;
    }

    private void UpdateLatestResult(InstrumentDetectionResult result)
    {
        LatestResult = result;
        DetectionUpdated?.Invoke(result);
    }

    /// <summary>
    /// 兼容旧版调用签名的 Detect 方法
    /// </summary>
    public bool Detect(Mat? frame, out double confidence, double threshold = DefaultThreshold)
    {
        var result = DetectFull(frame);
        confidence = result.CombinedConfidence;
        return result.IsDetected;
    }

    /// <summary>
    /// 从指定游戏窗口抓取一帧并检测竖笛演奏界面。
    /// 若已有连续流在驱动更新（如截图器后台运行或 CaptureTestWindow 运行中），直接复用最新状态杜绝会话冲突。
    /// </summary>
    public bool CheckGameWindow(IntPtr hwnd, out double confidence)
    {
        // 若近期（3 秒内）已有连续捕获帧产出判定，直接复用该状态
        if (LatestResult != null && (DateTime.Now - _lastDetectionTime).TotalSeconds < 3.0)
        {
            confidence = LatestResult.CombinedConfidence;
            return LatestResult.IsDetected;
        }

        confidence = 0.0;
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return false;

        try
        {
            var captureMode = _configService?.Config.General.CaptureMode.ToCaptureMode() ?? CaptureModes.WindowsGraphicsCapture;
            using var capture = GameCaptureFactory.Create(captureMode);
            capture.Start(hwnd);
            using var frame = capture.Capture();
            capture.Stop();

            if (frame != null && !frame.Frame.Empty())
            {
                var res = DetectFull(frame.Frame);
                confidence = res.CombinedConfidence;
                return res.IsDetected;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to capture frame for instrument check");
        }

        return false;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            foreach (var kv in _targetTemplates)
            {
                kv.Value.Dispose();
            }
            _targetTemplates.Clear();

            _legacyTemplateMat?.Dispose();
            _legacyTemplateMat = null;

            if (_ownsOcrEngine && _ocrEngine is IDisposable disp)
            {
                disp.Dispose();
            }
        }
    }
}
