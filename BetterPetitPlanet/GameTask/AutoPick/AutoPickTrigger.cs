using System;
using System.Diagnostics;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public sealed class AutoPickTrigger : ITaskTrigger, IDisposable
{
    private readonly ILogger<AutoPickTrigger>? _logger;
    private readonly IConfigService _configService;
    private readonly GameProcessDetector _processDetector;
    private readonly IOcrEngine _ocrEngine;
    private readonly ItemNameRecognitionService? _itemNameRecognition;
    private readonly IAutoPickInputChannel _inputChannel;
    private readonly AutoPickStateTracker _stateTracker;
    private readonly GameTaskManager? _taskManager;

    private int _pickCount;
    private long _totalCaptureFrames;
    private bool _lastForegroundState = true;

    private volatile bool _musicSuppressed;
    public bool MusicSuppressed
    {
        get => _musicSuppressed;
        set
        {
            _musicSuppressed = value;
            if (value)
            {
                _inputChannel.ForceReleaseAll();
                _stateTracker.Reset();
                _logger?.LogInformation("AutoPick: 音乐演奏活跃，自动拾取已静音让行");
            }
        }
    }

    public string Name => "自动拾取";
    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (!value)
                {
                    _inputChannel.ForceReleaseAll();
                    _stateTracker.Reset();
                    _taskManager?.ReleaseSession("AutoPick");
                    _logger?.LogInformation("自动拾取功能已关闭");
                }
                else
                {
                    _taskManager?.AcquireSession("AutoPick");
                    var cfg = _configService.Config.AutoPick;
                    _logger?.LogInformation(
                        "自动拾取功能已开启: 触发键='{Key}', 间隔={Cooldown}ms, 按下时长={PressMs}ms, 预筛选={Prescreen}, OCR引擎='{Ocr}'",
                        cfg.PickKey, cfg.CooldownMs, cfg.PressDurationMs, cfg.FastPrescreenEnabled ? "开启" : "关闭", cfg.OcrEngine);
                }
                StateChanged?.Invoke(_isEnabled);
            }
        }
    }
    public int Priority => 30;

    public event Action<bool>? StateChanged;

    public AutoPickTrigger(
        IConfigService configService,
        GameProcessDetector processDetector,
        IOcrEngine ocrEngine,
        ILogger<AutoPickTrigger>? logger = null,
        ItemNameRecognitionService? itemNameRecognition = null,
        IAutoPickInputChannel? inputChannel = null,
        AutoPickStateTracker? stateTracker = null,
        GameTaskManager? taskManager = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _ocrEngine = ocrEngine;
        _logger = logger;
        _itemNameRecognition = itemNameRecognition;
        _inputChannel = inputChannel ?? new AutoPickInputChannel(logger, () => MusicSuppressed);
        _stateTracker = stateTracker ?? new AutoPickStateTracker();
        _taskManager = taskManager;

        _isEnabled = _configService.Config.AutoPick.Enabled;
        if (_isEnabled)
        {
            _taskManager?.AcquireSession("AutoPick");
            var cfg = _configService.Config.AutoPick;
            _logger?.LogInformation(
                "AutoPickTrigger 初始化就绪 (已启用): 快捷键='{Key}', 间隔={Cooldown}ms, 预筛选={Prescreen}",
                cfg.PickKey, cfg.CooldownMs, cfg.FastPrescreenEnabled ? "开启" : "关闭");
        }
    }

    public void OnCapture(CaptureContent content)
    {
        if (MusicSuppressed || !IsEnabled || !content.IsValid)
        {
            return;
        }

        try
        {
            _totalCaptureFrames++;

            // Foreground check
            bool isForeground = _processDetector.IsGameForegroundCached();
            if (!_configService.Config.General.RunInBackground)
            {
                if (!isForeground)
                {
                    if (_lastForegroundState)
                    {
                        _lastForegroundState = false;
                        _logger?.LogInformation("AutoPick [帧 #{Frame}]: 游戏窗口失焦/转入后台，自动拾取暂停检测 (当前配置: 仅前台生效)", _totalCaptureFrames);
                    }
                    return;
                }
                else if (!_lastForegroundState)
                {
                    _lastForegroundState = true;
                    _logger?.LogInformation("AutoPick [帧 #{Frame}]: 游戏窗口已恢复前台，恢复自动拾取画面检测", _totalCaptureFrames);
                }
            }

            var config = _configService.Config.AutoPick;

            // 周期性待机心跳 (约每 120 帧 / 5 秒打印一次，确保控制台始终清晰可知运行状态)
            if (_totalCaptureFrames == 1 || _totalCaptureFrames % 120 == 0)
            {
                _logger?.LogInformation(
                    "AutoPick 运行心跳 [帧 #{Frame}]: 监控中, 状态=就绪待机, 累计拾取={PickCount} 次 (前台={Foreground}, 快捷键='{Key}', 预筛选={Prescreen})",
                    _totalCaptureFrames, _pickCount, isForeground, config.PickKey, config.FastPrescreenEnabled);
            }

            // 1. 若原子按键通道正处于物理按下或执行中，直接跳过当前帧
            if (_inputChannel.IsBusy)
            {
                _logger?.LogDebug("AutoPick [帧 #{Frame}]: 按键通道正在执行中，跳过当前帧", _totalCaptureFrames);
                return;
            }

            // Calculate ROI based on actual captured frame dimensions for resolution & DPI invariance
            int frameWidth = content.Width;
            int frameHeight = content.Height;
            var roiRect = PetitRoiCalculator.Calculate(frameWidth, frameHeight);
            if (roiRect.Width <= 0 || roiRect.Height <= 0)
            {
                _logger?.LogWarning("AutoPick [帧 #{Frame}]: 计算 ROI 区域异常 ({Width}x{Height})", _totalCaptureFrames, frameWidth, frameHeight);
                return;
            }

            // Clamp ROI to image boundary
            int x = Math.Clamp(roiRect.X, 0, frameWidth - 1);
            int y = Math.Clamp(roiRect.Y, 0, frameHeight - 1);
            int w = Math.Clamp(roiRect.Width, 1, frameWidth - x);
            int h = Math.Clamp(roiRect.Height, 1, frameHeight - y);
            var clampedRect = new Rect(x, y, w, h);

            using var roiFrame = content.GetRoi(clampedRect);
            if (roiFrame == null || roiFrame.Frame.Empty())
            {
                _logger?.LogWarning("AutoPick [帧 #{Frame}]: 读取 ROI 帧失败或数据为空", _totalCaptureFrames);
                return;
            }

            // 2. 视觉极速预筛选（< 0.05ms）：过滤无提示空帧，避免无脑执行重量级 OCR
            bool hasVisualPrompt = true;
            double prescreenScore = 1.0;
            string prescreenReason = "预筛选未启用";

            if (config.FastPrescreenEnabled)
            {
                var prescreenResult = AutoPickPrescreener.Evaluate(
                    roiFrame.Frame,
                    config.PickKey,
                    config.PrescreenMinScore > 0 ? config.PrescreenMinScore : 0.60);

                hasVisualPrompt = prescreenResult.CanContainPrompt;
                prescreenScore = prescreenResult.Score;
                prescreenReason = prescreenResult.Reason;
            }

            // 3. 密集花田连采状态机更新（波形消失下降沿重新上膛机制）
            bool cooldownReady = _inputChannel.IsCooldownReady(config.CooldownMs);
            if (!hasVisualPrompt)
            {
                _stateTracker.ShouldTrigger(hasPrompt: false, cooldownReady);
                _logger?.LogDebug("AutoPick [帧 #{Frame}]: 预筛选未发现按键提示 (得分: {Score:F2}, 原因: {Reason})，跳过 OCR",
                    _totalCaptureFrames, prescreenScore, prescreenReason);
                return;
            }

            if (!_stateTracker.ShouldTrigger(hasPrompt: true, cooldownReady))
            {
                _logger?.LogDebug("AutoPick [帧 #{Frame}]: 检出提示但状态机未就绪 (Armed: {Armed}, CooldownReady: {Cooldown})，暂缓触发",
                    _totalCaptureFrames, _stateTracker.IsArmed, cooldownReady);
                return;
            }

            _logger?.LogInformation(
                "AutoPick [帧 #{Frame}]: 预筛选检出可交互提示 (得分={Score:F2}, 原因={Reason}), 提交 OCR 识读...",
                _totalCaptureFrames, prescreenScore, prescreenReason);

            // 4. 执行 DirectML OCR 识读文字 (优先避开左侧按键图标以优化 CTC 解码长宽比)
            var swOcr = Stopwatch.StartNew();
            int textStartX = Math.Clamp((int)(roiFrame.Frame.Cols * 0.10), 0, roiFrame.Frame.Cols - 10);
            using var textMat = new Mat(roiFrame.Frame, new Rect(textStartX, 0, roiFrame.Frame.Cols - textStartX, roiFrame.Frame.Rows));
            var recognizedText = _ocrEngine.Recognize(textMat).Trim();
            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                recognizedText = _ocrEngine.Recognize(roiFrame.Frame).Trim();
            }
            swOcr.Stop();

            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                _logger?.LogInformation("AutoPick [帧 #{Frame}]: OCR 识读未检出文字 (耗时: {Elapsed:F1}ms)",
                    _totalCaptureFrames, swOcr.Elapsed.TotalMilliseconds);
                return;
            }

            _logger?.LogInformation("AutoPick [帧 #{Frame}]: OCR 识读结果: '{Text}' (耗时: {Elapsed:F1}ms)",
                _totalCaptureFrames, recognizedText, swOcr.Elapsed.TotalMilliseconds);

            // 5. 黑名单初筛 (Blacklist check: 命中即忽略不触发)
            var blacklist = (config.Blacklist != null && config.Blacklist.Count > 0)
                ? config.Blacklist
                : ["拾取雪球"];

            var hitBlacklist = blacklist.FirstOrDefault(b =>
                !string.IsNullOrWhiteSpace(b) &&
                recognizedText.Contains(b.Trim(), StringComparison.OrdinalIgnoreCase));

            if (hitBlacklist != null)
            {
                _logger?.LogWarning("AutoPick [帧 #{Frame}]: 识别文本 '{Text}' 命中黑名单关键词 '{BlacklistWord}'，拦截拾取",
                    _totalCaptureFrames, recognizedText, hitBlacklist);
                return;
            }

            // 6. 白名单匹配 (Whitelist check: 未被黑名单拦截且命中白名单)
            var whitelist = (config.Whitelist != null && config.Whitelist.Count > 0)
                ? config.Whitelist
                : (config.Keywords != null && config.Keywords.Count > 0 ? config.Keywords : ["拾取"]);

            var matchedKeyword = whitelist.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w) &&
                recognizedText.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase));

            bool matched = matchedKeyword != null
                || (recognizedText.Contains("拾") && recognizedText.Contains("取") && whitelist.Contains("拾取"));

            if (!matched)
            {
                _logger?.LogInformation("AutoPick [帧 #{Frame}]: 识别文本 '{Text}' 未匹配白名单关键词，跳过按键",
                    _totalCaptureFrames, recognizedText);
                return;
            }

            _logger?.LogInformation(
                "AutoPick [帧 #{Frame}]: 识别文本 '{Text}' 成功命中白名单关键词 '{Keyword}'，准备发送快捷键 '{Key}'",
                _totalCaptureFrames, recognizedText, matchedKeyword ?? "拾取", config.PickKey);

            // 7. 原子串行按键注入（保证 KeyDown -> Hold -> KeyUp 严格时序与松开后冷却起算）
            bool triggered = _inputChannel.TryTrigger(
                config.PickKey,
                config.PressDurationMs,
                config.CooldownMs,
                content.Hwnd,
                _configService.Config.General.RunInBackground);

            if (triggered)
            {
                _pickCount++;
                _stateTracker.RecordTrigger();
                _logger?.LogInformation(
                    "AutoPick [帧 #{Frame}]: 成功触发拾取按键 '{Key}' (持续: {PressMs}ms, 冷却: {CooldownMs}ms)! 累计拾取: {Count} 次",
                    _totalCaptureFrames, config.PickKey, config.PressDurationMs, config.CooldownMs, _pickCount);

                // Optional diagnostics only: key injection is already queued above.
                if (content.Frame != null)
                {
                    _itemNameRecognition?.TryQueue(content.Frame);
                }
            }
            else
            {
                _logger?.LogWarning("AutoPick [帧 #{Frame}]: 按键通道忙碌或处于冷却中，本次触发未被接受", _totalCaptureFrames);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception in AutoPickTrigger.OnCapture");
        }
    }

    public void Dispose()
    {
        _inputChannel.Dispose();
        _stateTracker.Reset();
    }
}
