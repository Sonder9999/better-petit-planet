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

    private int _pickCount;

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
        AutoPickStateTracker? stateTracker = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _ocrEngine = ocrEngine;
        _logger = logger;
        _itemNameRecognition = itemNameRecognition;
        _inputChannel = inputChannel ?? new AutoPickInputChannel(logger, () => MusicSuppressed);
        _stateTracker = stateTracker ?? new AutoPickStateTracker();

        _isEnabled = _configService.Config.AutoPick.Enabled;
    }

    public void OnCapture(CaptureContent content)
    {
        if (MusicSuppressed || !IsEnabled || !content.IsValid)
        {
            return;
        }

        try
        {
            // Foreground check
            if (!_configService.Config.General.RunInBackground && !_processDetector.IsGameForegroundCached())
            {
                return;
            }

            var config = _configService.Config.AutoPick;

            // 1. 若原子按键通道正处于物理按下或执行中，直接跳过当前帧
            if (_inputChannel.IsBusy)
            {
                return;
            }

            // Calculate ROI based on actual captured frame dimensions for resolution & DPI invariance
            int frameWidth = content.Width;
            int frameHeight = content.Height;
            var roiRect = PetitRoiCalculator.Calculate(frameWidth, frameHeight);
            if (roiRect.Width <= 0 || roiRect.Height <= 0)
            {
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
                return;
            }

            // 2. 视觉极速预筛选（< 0.05ms）：过滤无提示空帧，避免无脑执行重量级 OCR
            bool hasVisualPrompt = true;
            if (config.FastPrescreenEnabled)
            {
                hasVisualPrompt = AutoPickPrescreener.CanContainPrompt(
                    roiFrame.Frame,
                    config.PickKey,
                    config.PrescreenMinScore > 0 ? config.PrescreenMinScore : 0.70);
            }

            // 3. 密集花田连采状态机更新（波形消失下降沿重新上膛机制）
            bool cooldownReady = _inputChannel.IsCooldownReady(config.CooldownMs);
            if (!hasVisualPrompt)
            {
                _stateTracker.ShouldTrigger(hasPrompt: false, cooldownReady);
                return;
            }

            if (!_stateTracker.ShouldTrigger(hasPrompt: true, cooldownReady))
            {
                return;
            }

            // 4. 执行 DirectML OCR 识读文字
            var recognizedText = _ocrEngine.Recognize(roiFrame.Frame).Trim();

            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                return;
            }

            _logger?.LogDebug("AutoPick OCR detected text: '{Text}'", recognizedText);

            // 5. 黑名单初筛 (Blacklist check: 命中即忽略不触发)
            var blacklist = (config.Blacklist != null && config.Blacklist.Count > 0)
                ? config.Blacklist
                : ["拾取雪球"];

            if (blacklist.Any(b =>
                !string.IsNullOrWhiteSpace(b) &&
                recognizedText.Contains(b.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogDebug("AutoPick OCR candidate '{Text}' hit blacklist, ignored.", recognizedText);
                return;
            }

            // 6. 白名单匹配 (Whitelist check: 未被黑名单拦截且命中白名单)
            var whitelist = (config.Whitelist != null && config.Whitelist.Count > 0)
                ? config.Whitelist
                : (config.Keywords != null && config.Keywords.Count > 0 ? config.Keywords : ["拾取"]);

            bool matched = whitelist.Any(w =>
                !string.IsNullOrWhiteSpace(w) &&
                recognizedText.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase))
                || (recognizedText.Contains("拾") && recognizedText.Contains("取") && whitelist.Contains("拾取"));

            if (!matched)
            {
                _logger?.LogDebug("AutoPick OCR candidate '{Text}' did not match pickup keywords", recognizedText);
                return;
            }

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
                _logger?.LogInformation("AutoPick triggered: '{Text}'. Pick count: {Count}", recognizedText, _pickCount);

                // Optional diagnostics only: key injection is already queued above.
                if (content.Frame != null)
                {
                    _itemNameRecognition?.TryQueue(content.Frame);
                }
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
