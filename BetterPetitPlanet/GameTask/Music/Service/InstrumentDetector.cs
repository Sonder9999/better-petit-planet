using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameCapture;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using BetterPetitPlanet.GameTask.Music.Instrument;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 多乐器（竖笛、吉他等）演奏界面自动识别器。
/// 基于分阶段短路算法（Stage 1 按键初筛 -> Stage 2 乐器专属视觉特征验证 -> Stage 3 OCR 兜底复核），
/// 实现低开销、高精度的乐器就绪判定。
/// </summary>
public sealed class InstrumentDetector : IDisposable
{
    private readonly IConfigService? _configService;
    private readonly IOcrEngine? _ocrEngine;
    private readonly bool _ownsOcrEngine;
    private readonly ILogger<InstrumentDetector>? _logger;
    private readonly IInstrumentRegistry _registry;
    private readonly object _syncLock = new();

    private readonly Dictionary<string, Mat> _keyTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Mat>> _exclusiveTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _targetTemplates = new(StringComparer.OrdinalIgnoreCase);
    private Mat? _legacyTemplateMat;
    private bool _isInitialized;

    private long _lastEffectiveSequence;
    private DateTime _lastDetectionTime = DateTime.MinValue;

    public const double DefaultThreshold = 0.65;
    public const double KeyAnchorThreshold = 0.58;
    public const double FeatureThreshold = 0.58;
    public const double FeatureMarginThreshold = 0.15;

    public bool IsTemplateLoaded =>
        _keyTemplates.Count > 0 ||
        _targetTemplates.Count > 0 ||
        (_legacyTemplateMat != null && !_legacyTemplateMat.Empty());

    /// <summary>
    /// 最新的全局检测结果（全局单例状态源，确保各窗口与主界面绝对同步）
    /// </summary>
    public InstrumentDetectionResult? LatestResult { get; private set; }

    /// <summary>
    /// 全局状态同步更新事件（保证在同步锁外安全派发，绝不引发死锁）
    /// </summary>
    public event Action<InstrumentDetectionResult>? DetectionUpdated;

    public InstrumentDetector(
        IConfigService? configService = null,
        IOcrEngine? ocrEngine = null,
        ILogger<InstrumentDetector>? logger = null,
        IInstrumentRegistry? registry = null)
    {
        _configService = configService;
        _logger = logger;
        _registry = registry ?? InstrumentRegistry.Default;

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

    private void InitializeTemplates()
    {
        lock (_syncLock)
        {
            if (_isInitialized) return;

            // 1. 加载 common 按键锚点模板 (key_A, key_L 等)
            var commonAnchors = InstrumentRegistry.CreateCommonKeyAnchors();
            foreach (var anchor in commonAnchors)
            {
                var filePath = _registry.ResolveTargetFilePath("common", anchor.TemplateFileName) ??
                               _registry.ResolveTargetFilePath("Recorder", anchor.TemplateFileName);
                if (filePath != null && File.Exists(filePath))
                {
                    try
                    {
                        var mat = Cv2.ImRead(filePath, ImreadModes.Color);
                        if (!mat.Empty())
                        {
                            _keyTemplates[anchor.Name] = mat;
                            _targetTemplates[anchor.Name] = mat;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to load common key template: {Path}", filePath);
                    }
                }
            }

            // 2. 加载各已注册乐器的专属特征模板 (Guitar 和 Recorder)
            var profiles = _registry.GetAllProfiles();
            foreach (var profile in profiles)
            {
                var instrumentDict = new Dictionary<string, Mat>(StringComparer.OrdinalIgnoreCase);
                _exclusiveTemplates[profile.Id] = instrumentDict;

                foreach (var target in profile.ExclusiveTargets)
                {
                    var filePath = _registry.ResolveTargetFilePath(profile.Id, target.TemplateFileName);
                    if (filePath != null && File.Exists(filePath))
                    {
                        try
                        {
                            var mat = Cv2.ImRead(filePath, ImreadModes.Color);
                            if (!mat.Empty())
                            {
                                instrumentDict[target.Name] = mat;
                                _targetTemplates[target.Name] = mat;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to load exclusive template for {Instrument}: {Path}", profile.Id, filePath);
                        }
                    }
                }

                // 大底模板（主要针对 Recorder 保留兼容）
                if (profile.Id.Equals("Recorder", StringComparison.OrdinalIgnoreCase) && _legacyTemplateMat == null)
                {
                    var tmplPath = _registry.ResolveTemplatePath(profile.Id);
                    if (tmplPath != null && File.Exists(tmplPath))
                    {
                        try
                        {
                            var mat = Cv2.ImRead(tmplPath, ImreadModes.Color);
                            if (!mat.Empty())
                            {
                                _legacyTemplateMat = mat;
                            }
                        }
                        catch { }
                    }
                }
            }

            _logger?.LogInformation(
                "InstrumentDetector initialized. Key templates: {Keys}, Exclusive: {ExclusiveInstruments}, Total: {Total}",
                _keyTemplates.Count, _exclusiveTemplates.Count, _targetTemplates.Count);

            _isInitialized = true;
        }
    }

    /// <summary>
    /// 多乐器界面分阶段检测核心方法。
    /// 支持全帧画面或 ROI 子图识别，通过锁外通知彻底消除死锁隐患。
    /// </summary>
    public InstrumentDetectionResult DetectFull(
        Mat? frame,
        int roiOffsetX = 0,
        int roiOffsetY = 0,
        int fullWidth = 0,
        int fullHeight = 0,
        long sequence = 0)
    {
        InstrumentDetectionResult result;
        bool shouldNotify = false;

        lock (_syncLock)
        {
            result = DetectAlgorithm(frame, roiOffsetX, roiOffsetY, fullWidth, fullHeight);

            // 乱序防护：若显式提供了 sequence (> 0)，且早于当前已生效序号，则安全丢弃状态更新
            if (sequence > 0 && sequence < _lastEffectiveSequence)
            {
                _logger?.LogDebug("Discarding out-of-order detection in InstrumentDetector: sequence {Seq} < {Latest}", sequence, _lastEffectiveSequence);
                return result;
            }

            if (sequence > 0)
            {
                _lastEffectiveSequence = sequence;
            }

            LatestResult = result;
            _lastDetectionTime = DateTime.UtcNow;
            shouldNotify = true;
        }

        // 致命隐患修复：DetectionUpdated 事件通知移到锁外执行，杜绝外部订阅者导致双向死锁
        if (shouldNotify)
        {
            try
            {
                using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrNotifyMs);
                DetectionUpdated?.Invoke(result);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception in DetectionUpdated event handlers");
            }
        }

        return result;
    }

    private InstrumentDetectionResult DetectAlgorithm(
        Mat? frame,
        int roiOffsetX,
        int roiOffsetY,
        int fullWidth,
        int fullHeight)
    {
        if (frame == null || frame.Empty())
        {
            return new InstrumentDetectionResult
            {
                IsDetected = false,
                State = InstrumentDetectionState.None,
                Stage = "None",
                OcrInvoked = false,
                StatusSummary = "未检测到乐器",
                Message = "画面为空"
            };
        }

        // 尺寸与 ROI 换算：若未指定 fullWidth/fullHeight，根据当前帧纵横比自动推断是否已是 ROI 子图
        if (fullWidth <= 0 || fullHeight <= 0)
        {
            // 2560x1440 下 ROI 外接矩形约 1550x250 (纵横比 6.2:1)；全画幅纵横比通常为 16:9 (1.78:1)
            if (frame.Width / (double)frame.Height >= 3.5)
            {
                double roiScale = frame.Width / 1550.0;
                fullWidth = (int)Math.Round(2560.0 * roiScale);
                fullHeight = (int)Math.Round(1440.0 * roiScale);
                roiOffsetX = (int)Math.Round(500.0 * roiScale);
                roiOffsetY = (int)Math.Round(1100.0 * roiScale);
            }
            else
            {
                fullWidth = frame.Width;
                fullHeight = frame.Height;
                roiOffsetX = 0;
                roiOffsetY = 0;
            }
        }

        double scaleX = fullWidth / 2560.0;
        double scaleY = fullHeight / 1440.0;

        // 若模板因故未加载，自动退化至纯 OCR 识别（向下兼容）
        if (!IsTemplateLoaded)
        {
            return DetectPureOcrFallback(frame, scaleX, scaleY, roiOffsetX, roiOffsetY);
        }

        // =========================================================================
        // Stage 1: 按键快速初筛短路 (KeyAnchor Fast Filter)
        // =========================================================================
        double scoreA = MatchKeyTemplate(frame, "key_A", 574, 1272, 48, 38, scaleX, scaleY, roiOffsetX, roiOffsetY);
        double scoreL = MatchKeyTemplate(frame, "key_L", 1936, 1272, 48, 38, scaleX, scaleY, roiOffsetX, roiOffsetY);
        double keyAvg = (scoreA + scoreL) / 2.0;
        double keyMax = Math.Max(scoreA, scoreL);

        // Stage 2 候选乐器评估 (配置驱动多乐器特征比对)
        var profiles = _registry.GetAllProfiles().ToList();
        var candidateResults = new List<(InstrumentProfile Profile, double AvgScore, int Hits, int Total, string Detail)>();

        foreach (var profile in profiles)
        {
            var anchors = profile.VerificationAnchors.Count > 0
                ? profile.VerificationAnchors
                : (profile.ExclusiveTargets.Count >= 2
                    ? new[] { profile.ExclusiveTargets[0], profile.ExclusiveTargets[profile.ExclusiveTargets.Count - 1] }
                    : profile.ExclusiveTargets);

            if (anchors.Count == 0) continue;

            double sumScore = 0.0;
            int hits = 0;
            var details = new List<string>();

            foreach (var anchor in anchors)
            {
                double score = MatchInstrumentTemplate(
                    frame,
                    profile.Id,
                    anchor.Name,
                    anchor.Bounds.X,
                    anchor.Bounds.Y,
                    anchor.Bounds.Width,
                    anchor.Bounds.Height,
                    scaleX,
                    scaleY,
                    roiOffsetX,
                    roiOffsetY);

                sumScore += score;
                if (score >= FeatureThreshold) hits++;
                details.Add($"{anchor.Name}={score:P0}");
            }

            double avgScore = sumScore / anchors.Count;
            candidateResults.Add((profile, avgScore, hits, anchors.Count, string.Join(", ", details)));
        }

        var sortedCandidates = candidateResults.OrderByDescending(c => c.AvgScore).ToList();
        double maxCandidateScore = sortedCandidates.Count > 0 ? sortedCandidates[0].AvgScore : 0.0;

        // Stage 1 门禁快速短路判定：
        // 彻底消除单一/双点半透明按键一票否决权！
        // 仅当按键无显著匹配（keyMax < 0.40 且 keyAvg < 0.35）且所有乐器专属特征亦无匹配（maxCandidateScore < 0.35）时，
        // 确认为非乐器画面（如野外跑动、探索、过场黑屏等），安全短路以避免不必要的全量计算与 OCR。
        // 若按键虽受草地/夜间光照干扰（如 scoreL 跌至 34%），但 keyA 或乐器专属高对比度特征显著存在，绝不提前否决！
        if (keyMax < 0.40 && keyAvg < 0.35 && maxCandidateScore < 0.35)
        {
            double earlyConf = Math.Max(0.0, Math.Max(keyAvg, maxCandidateScore));
            return new InstrumentDetectionResult
            {
                IsDetected = false,
                State = InstrumentDetectionState.None,
                Stage = "KeyAnchor",
                OcrInvoked = false,
                TemplateConfidence = earlyConf,
                CombinedConfidence = earlyConf,
                StatusSummary = "未检测到乐器",
                Details = $"未匹配到按键或乐器特征 (key_A={scoreA:P0}, key_L={scoreL:P0}, 特征最高={maxCandidateScore:P0})"
            };
        }

        // =========================================================================
        // Stage 2: 乐器专属视觉特征验证 (Feature Verification - 配置驱动多乐器评估)
        // =========================================================================
        if (sortedCandidates.Count > 0 && sortedCandidates[0].AvgScore >= FeatureThreshold)
        {
            var best = sortedCandidates[0];
            double secondScore = sortedCandidates.Count > 1 ? sortedCandidates[1].AvgScore : 0.0;

            if (best.AvgScore - secondScore >= FeatureMarginThreshold)
            {
                return new InstrumentDetectionResult
                {
                    IsDetected = true,
                    State = InstrumentDetectionState.Confirmed,
                    InstrumentId = best.Profile.Id,
                    DisplayName = best.Profile.DisplayName,
                    Stage = "FeatureVerification",
                    OcrInvoked = false,
                    TemplateConfidence = best.AvgScore,
                    TemplateHits = best.Hits,
                    TemplateTotal = best.Total,
                    CombinedConfidence = best.AvgScore,
                    StatusSummary = $"{best.Profile.DisplayName}: 已就绪 (模板: {best.AvgScore:P0})",
                    Details = $"{best.Profile.DisplayName}特征确认 ({best.Detail}, 均值={best.AvgScore:P0}; 次优均值={secondScore:P0})"
                };
            }
        }

        // =========================================================================
        // Stage 3: OCR 兜底复核 (OCR Fallback - 仅当模板匹配存疑/接近时介入)
        // =========================================================================
        double maxTmplScore = sortedCandidates.Count > 0 ? sortedCandidates[0].AvgScore : 0.0;
        string profilesSummary = string.Join("; ", sortedCandidates.Select(c => $"{c.Profile.DisplayName}均值={c.AvgScore:P0}"));

        if (_ocrEngine == null || !_ocrEngine.IsAvailable)
        {
            return new InstrumentDetectionResult
            {
                IsDetected = false,
                State = InstrumentDetectionState.Ambiguous,
                InstrumentId = null,
                DisplayName = null,
                Stage = "OcrFallback",
                OcrInvoked = false,
                TemplateConfidence = maxTmplScore,
                CombinedConfidence = maxTmplScore,
                StatusSummary = "乐器识别存疑",
                Details = $"模板匹配存疑且 OCR 引擎不可用 ({profilesSummary})",
                Message = "OCR engine unavailable for fallback verification"
            };
        }

        return ExecuteOcrFallback(frame, scaleX, scaleY, roiOffsetX, roiOffsetY, profiles, sortedCandidates);
    }

    private InstrumentDetectionResult ExecuteOcrFallback(
        Mat frame,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY,
        IReadOnlyList<InstrumentProfile> profiles,
        IReadOnlyList<(InstrumentProfile Profile, double AvgScore, int Hits, int Total, string Detail)> tmplCandidates)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrRecognitionMs);

        // 配置驱动多乐器 OCR 区域裁剪与识别（针对各乐器 ExpectedText 专属区域精确定位）
        var profileOcrHits = new Dictionary<string, (int Hits, int Total, List<string> Matches)>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            // 获取该乐器专属具有 ExpectedText 的目标（竖笛为数字 1-7，吉他为和弦 C-G7 等）
            var targets = profile.ExclusiveTargets
                .Where(t => !string.IsNullOrEmpty(t.ExpectedText) && t.Category != AnchorCategory.Syllable)
                .ToList();

            if (targets.Count == 0)
            {
                targets = profile.ExclusiveTargets.Where(t => !string.IsNullOrEmpty(t.ExpectedText)).Take(7).ToList();
            }

            int hits = 0;
            var matchedList = new List<string>();
            const int pad = 4;

            foreach (var target in targets)
            {
                int baseX = Math.Max(0, target.Bounds.X - pad);
                int baseY = Math.Max(0, target.Bounds.Y - pad);
                int baseW = target.Bounds.Width + 2 * pad;
                int baseH = target.Bounds.Height + 2 * pad;

                if (TryCropRegion(frame, baseX, baseY, baseW, baseH, scaleX, scaleY, roiOffsetX, roiOffsetY, out var cropMat))
                {
                    using (cropMat)
                    {
                        string text = _ocrEngine!.Recognize(cropMat).Trim();
                        if (string.IsNullOrEmpty(text)) continue;

                        bool matched = string.Equals(text, target.ExpectedText, StringComparison.OrdinalIgnoreCase) ||
                                       text.Contains(target.ExpectedText, StringComparison.OrdinalIgnoreCase);

                        if (matched)
                        {
                            hits++;
                            matchedList.Add($"{target.ExpectedText}->{text}");
                        }
                    }
                }
            }

            profileOcrHits[profile.Id] = (hits, targets.Count, matchedList);
        }

        var rankedProfiles = profiles
            .Select(p =>
            {
                var info = profileOcrHits.TryGetValue(p.Id, out var h) ? h : (Hits: 0, Total: 0, Matches: new List<string>());
                return (Profile: p, Hits: info.Hits, Total: info.Total, Matches: info.Matches);
            })
            .OrderByDescending(p => p.Hits)
            .ToList();

        if (rankedProfiles.Count > 0 && rankedProfiles[0].Hits >= 2)
        {
            var best = rankedProfiles[0];
            int secondHits = rankedProfiles.Count > 1 ? rankedProfiles[1].Hits : 0;

            if (best.Hits > secondHits)
            {
                double ocrConf = best.Total > 0 ? (double)best.Hits / best.Total : 1.0;
                double tmplScore = tmplCandidates.FirstOrDefault(c => c.Profile.Id.Equals(best.Profile.Id, StringComparison.OrdinalIgnoreCase)).AvgScore;

                return new InstrumentDetectionResult
                {
                    IsDetected = true,
                    State = InstrumentDetectionState.Confirmed,
                    InstrumentId = best.Profile.Id,
                    DisplayName = best.Profile.DisplayName,
                    Stage = "OcrFallback",
                    OcrInvoked = true,
                    OcrConfidence = ocrConf,
                    OcrHits = best.Hits,
                    OcrTotal = best.Total,
                    TemplateConfidence = tmplScore,
                    CombinedConfidence = ocrConf,
                    StatusSummary = $"{best.Profile.DisplayName}: 已就绪 (OCR: {ocrConf:P0})",
                    Details = $"OCR 复核判定为{best.Profile.DisplayName} (命中数={best.Hits}/{best.Total}, 次优命中数={secondHits}, 识别={string.Join(", ", best.Matches)})"
                };
            }
        }

        string ocrSummary = string.Join(", ", rankedProfiles.Select(p => $"{p.Profile.DisplayName}命中={p.Hits}/{p.Total}"));
        return new InstrumentDetectionResult
        {
            IsDetected = false,
            State = InstrumentDetectionState.Ambiguous,
            InstrumentId = null,
            DisplayName = null,
            Stage = "OcrFallback",
            OcrInvoked = true,
            OcrConfidence = 0.0,
            CombinedConfidence = 0.0,
            StatusSummary = "乐器识别存疑",
            Details = $"OCR 识别无法明确区分乐器 ({ocrSummary})",
            Message = "无法区分乐器"
        };
    }

    private InstrumentDetectionResult DetectPureOcrFallback(
        Mat frame,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY)
    {
        if (_ocrEngine == null || !_ocrEngine.IsAvailable)
        {
            return new InstrumentDetectionResult
            {
                IsDetected = false,
                State = InstrumentDetectionState.None,
                Stage = "OcrFallback",
                OcrInvoked = false,
                StatusSummary = "未检测到乐器",
                Message = "OCR engine unavailable and templates not loaded"
            };
        }

        int numberHits = 0;
        foreach (var target in InstrumentTargets.Numbers)
        {
            if (TryCropRegion(frame, target.X, target.Y, target.Width, target.Height, scaleX, scaleY, roiOffsetX, roiOffsetY, out var cropMat))
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

        int total = InstrumentTargets.Numbers.Length;
        double ocrConf = total > 0 ? (double)numberHits / total : 0.0;
        bool isDetected = ocrConf >= 0.35;

        return new InstrumentDetectionResult
        {
            IsDetected = isDetected,
            State = isDetected ? InstrumentDetectionState.Confirmed : InstrumentDetectionState.None,
            InstrumentId = isDetected ? "Recorder" : null,
            DisplayName = isDetected ? "竖笛" : null,
            Stage = "OcrFallback",
            OcrInvoked = true,
            OcrConfidence = ocrConf,
            OcrHits = numberHits,
            OcrTotal = total,
            NumberConfidence = ocrConf,
            NumberHits = numberHits,
            CombinedConfidence = ocrConf,
            Details = $"Pure OCR: {ocrConf:P0} ({numberHits}/{total})",
            StatusSummary = isDetected ? $"竖笛: 已就绪 (OCR: {ocrConf:P0})" : "未检测到乐器"
        };
    }

    private double MatchKeyTemplate(
        Mat frame,
        string anchorName,
        int baseX,
        int baseY,
        int baseW,
        int baseH,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY)
    {
        if (_keyTemplates.TryGetValue(anchorName, out var tmplMat))
        {
            return MatchTemplateInFrame(frame, tmplMat, baseX, baseY, baseW, baseH, scaleX, scaleY, roiOffsetX, roiOffsetY);
        }

        if (_targetTemplates.TryGetValue(anchorName, out tmplMat))
        {
            return MatchTemplateInFrame(frame, tmplMat, baseX, baseY, baseW, baseH, scaleX, scaleY, roiOffsetX, roiOffsetY);
        }

        return 0.0;
    }

    private double MatchInstrumentTemplate(
        Mat frame,
        string instrumentId,
        string targetName,
        int baseX,
        int baseY,
        int baseW,
        int baseH,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY)
    {
        if (_exclusiveTemplates.TryGetValue(instrumentId, out var dict) && dict.TryGetValue(targetName, out var tmplMat))
        {
            return MatchTemplateInFrame(frame, tmplMat, baseX, baseY, baseW, baseH, scaleX, scaleY, roiOffsetX, roiOffsetY);
        }

        if (_targetTemplates.TryGetValue(targetName, out tmplMat))
        {
            return MatchTemplateInFrame(frame, tmplMat, baseX, baseY, baseW, baseH, scaleX, scaleY, roiOffsetX, roiOffsetY);
        }

        return 0.0;
    }

    private double MatchTemplateInFrame(
        Mat frame,
        Mat tmplMat,
        int baseX,
        int baseY,
        int baseW,
        int baseH,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY)
    {
        if (frame.Empty() || tmplMat.Empty()) return 0.0;

        // 换算当前分辨率下的坐标
        double scaledX = baseX * scaleX;
        double scaledY = baseY * scaleY;
        double scaledW = baseW * scaleX;
        double scaledH = baseH * scaleY;

        // 转换为相对于当前帧（可能为 ROI 子图）的坐标
        double frameTargetX = scaledX - roiOffsetX;
        double frameTargetY = scaledY - roiOffsetY;

        // 模板缩放后的尺寸
        int tmplScaledW = Math.Max(1, (int)Math.Round(scaledW));
        int tmplScaledH = Math.Max(1, (int)Math.Round(scaledH));

        // 搜索区域适当增加 padding，允许微小 UI 抖动或尺寸舍入偏移
        int padX = Math.Max(6, (int)Math.Round(12 * scaleX));
        int padY = Math.Max(6, (int)Math.Round(10 * scaleY));

        int searchX = (int)Math.Round(frameTargetX - padX);
        int searchY = (int)Math.Round(frameTargetY - padY);
        int searchW = tmplScaledW + 2 * padX;
        int searchH = tmplScaledH + 2 * padY;

        // 越界安全裁剪
        if (searchX < 0)
        {
            searchW += searchX;
            searchX = 0;
        }
        if (searchY < 0)
        {
            searchH += searchY;
            searchY = 0;
        }
        if (searchX + searchW > frame.Width)
        {
            searchW = frame.Width - searchX;
        }
        if (searchY + searchH > frame.Height)
        {
            searchH = frame.Height - searchY;
        }

        if (searchW < tmplScaledW || searchH < tmplScaledH || searchW <= 0 || searchH <= 0)
        {
            return 0.0;
        }

        try
        {
            using var searchMat = new Mat(frame, new Rect(searchX, searchY, searchW, searchH));
            using var scaledTmpl = new Mat();

            if (tmplMat.Width != tmplScaledW || tmplMat.Height != tmplScaledH)
            {
                Cv2.Resize(tmplMat, scaledTmpl, new Size(tmplScaledW, tmplScaledH));
            }
            else
            {
                tmplMat.CopyTo(scaledTmpl);
            }

            using var matchResult = new Mat();
            Cv2.MatchTemplate(searchMat, scaledTmpl, matchResult, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(matchResult, out _, out double maxScore, out _, out _);
            return double.IsNaN(maxScore) ? 0.0 : maxScore;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Exception in MatchTemplateInFrame");
            return 0.0;
        }
    }

    private static bool TryCropRegion(
        Mat frame,
        int baseX,
        int baseY,
        int baseW,
        int baseH,
        double scaleX,
        double scaleY,
        int roiOffsetX,
        int roiOffsetY,
        out Mat cropMat)
    {
        cropMat = null!;
        int x = (int)Math.Round(baseX * scaleX) - roiOffsetX;
        int y = (int)Math.Round(baseY * scaleY) - roiOffsetY;
        int w = (int)Math.Round(baseW * scaleX);
        int h = (int)Math.Round(baseH * scaleY);

        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > frame.Width) { w = frame.Width - x; }
        if (y + h > frame.Height) { h = frame.Height - y; }

        if (w <= 0 || h <= 0 || x < 0 || y < 0)
        {
            return false;
        }

        cropMat = new Mat(frame, new Rect(x, y, w, h));
        return true;
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
    /// 从指定游戏窗口抓取一帧并检测乐器演奏界面。
    /// 若已有连续流在驱动更新（如截图器后台运行或 CaptureTestWindow 运行中），直接复用最新状态杜绝会话冲突。
    /// </summary>
    public bool CheckGameWindow(IntPtr hwnd, out double confidence)
    {
        if (LatestResult != null && (DateTime.UtcNow - _lastDetectionTime).TotalSeconds < 3.0)
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
            foreach (var kv in _keyTemplates)
            {
                kv.Value.Dispose();
            }
            _keyTemplates.Clear();

            foreach (var instrumentKv in _exclusiveTemplates)
            {
                foreach (var kv in instrumentKv.Value)
                {
                    kv.Value.Dispose();
                }
                instrumentKv.Value.Clear();
            }
            _exclusiveTemplates.Clear();

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
