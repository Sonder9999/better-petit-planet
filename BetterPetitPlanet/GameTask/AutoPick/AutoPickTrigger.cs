using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public sealed class AutoPickTrigger : ITaskTrigger
{
    private readonly ILogger<AutoPickTrigger>? _logger;
    private readonly IConfigService _configService;
    private readonly GameProcessDetector _processDetector;
    private readonly IOcrEngine _ocrEngine;

    private long _lastPickTimestamp;
    private int _pickCount;

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
        ILogger<AutoPickTrigger>? logger = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _ocrEngine = ocrEngine;
        _logger = logger;

        _isEnabled = _configService.Config.AutoPick.Enabled;
    }

    public void OnCapture(CaptureContent content)
    {
        if (!IsEnabled || content.Frame == null || content.Frame.Empty())
        {
            return;
        }

        try
        {
            // Foreground check
            if (!_configService.Config.General.RunInBackground && !_processDetector.IsGameForeground())
            {
                return;
            }

            // Calculate ROI based on actual captured frame dimensions for resolution & DPI invariance
            int frameWidth = content.Frame.Cols;
            int frameHeight = content.Frame.Rows;
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

            using var roiMat = new Mat(content.Frame, new Rect(x, y, w, h));
            var recognizedText = _ocrEngine.Recognize(roiMat).Trim();

            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                return;
            }

            _logger?.LogDebug("AutoPick OCR detected text: '{Text}'", recognizedText);

            var config = _configService.Config.AutoPick;

            // 1. 黑名单初筛 (Blacklist check: 命中即忽略不触发)
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

            // 2. 白名单匹配 (Whitelist check: 未被黑名单拦截且命中白名单)
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

            // Check cooldown
            long now = Stopwatch.GetTimestamp();
            double elapsedMs = (double)(now - _lastPickTimestamp) * 1000 / Stopwatch.Frequency;
            if (elapsedMs < config.CooldownMs)
            {
                return;
            }

            _lastPickTimestamp = now;
            _pickCount++;

            _logger?.LogInformation("AutoPick triggered: '{Text}'. Pick count: {Count}", recognizedText, _pickCount);

            // Inject key press with duration using hardware scancode
            InjectKeyPress(config.PickKey, config.PressDurationMs, content.Hwnd);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception in AutoPickTrigger.OnCapture");
        }
    }

    private void InjectKeyPress(string keyName, int holdDurationMs, IntPtr hwnd)
    {
        Task.Run(async () =>
        {
            try
            {
                if (_configService.Config.General.RunInBackground && hwnd != IntPtr.Zero)
                {
                    await HardwareInputSimulator.SendBackgroundKeyPressAsync(hwnd, keyName, holdDurationMs);
                }
                else
                {
                    await HardwareInputSimulator.SendKeyPressAsync(keyName, holdDurationMs);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to inject key press for AutoPick");
            }
        });
    }
}
