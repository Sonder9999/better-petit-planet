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
    public bool IsEnabled { get; set; }
    public int Priority => 30;

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

        IsEnabled = _configService.Config.AutoPick.Enabled;
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

            // Check if keyword is matched (based on pick.xml target "拾取")
            bool matched = config.Keywords.Any(k => recognizedText.Contains(k, StringComparison.OrdinalIgnoreCase))
                           || recognizedText.Contains("拾取", StringComparison.OrdinalIgnoreCase)
                           || (recognizedText.Contains("拾") && recognizedText.Contains("取"));
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
