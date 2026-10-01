using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameCapture;
using Serilog;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class HomePageViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly GameProcessDetector? _processDetector;
    private readonly GameTaskManager? _taskManager;
    private readonly BetterPetitPlanet.Core.Web.OfficialCoverService? _coverService;

    [ObservableProperty]
    private string _officialBackgroundUrl = "https://planet.mihoyo.com/_nuxt/img/bg-1.6a2cfc2.jpg";

    [ObservableProperty]
    private string _officialForegroundUrl = "https://planet.mihoyo.com/_nuxt/img/character-foreground.0888a19.png";

    [ObservableProperty]
    private string _officialStarrySkyUrl = "https://planet.mihoyo.com/_nuxt/img/starry-sky@2x.c86a4f8.png";

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _captureButtonText = "启动";

    [ObservableProperty]
    private string _captureStatusDescription = "截图器启动后才能使用各项功能，点击展开启动相关配置。";

    [ObservableProperty]
    private bool _autoStartGame;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _captureMode = "WindowsGraphicsCapture";

    [ObservableProperty]
    private int _triggerInterval = 50;

    [ObservableProperty]
    private string _inferenceDevice = "DirectML GPU";

    [ObservableProperty]
    private string _gameExecutablePath = string.Empty;

    [ObservableProperty]
    private string _gameStartArgs = string.Empty;

    [ObservableProperty]
    private bool _autoEnterGameEnabled;

    public List<string> AvailableCaptureModes { get; } = ["WindowsGraphicsCapture", "PrintWindow"];
    public List<string> AvailableInferenceDevices { get; } = ["DirectML GPU", "CPU"];

    public HomePageViewModel(
        IConfigService configService,
        GameProcessDetector? processDetector = null,
        GameTaskManager? taskManager = null,
        BetterPetitPlanet.Core.Web.OfficialCoverService? coverService = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _taskManager = taskManager;
        _coverService = coverService;

        _autoStartGame = _configService.Config.General.AutoStartGame;
        _gameExecutablePath = _configService.Config.General.GameExecutablePath;

        var savedMode = _configService.Config.General.CaptureMode;
        if (savedMode.StartsWith("PrintWindow", StringComparison.OrdinalIgnoreCase) || savedMode.StartsWith("BitBlt", StringComparison.OrdinalIgnoreCase))
        {
            _captureMode = "PrintWindow";
        }
        else
        {
            _captureMode = "WindowsGraphicsCapture";
        }
        _configService.Config.General.CaptureMode = _captureMode;

        _triggerInterval = _configService.Config.AutoPick.CooldownMs;

        if (_taskManager != null)
        {
            _taskManager.StateChanged += OnCaptureStateChanged;
        }
        if (_processDetector != null)
        {
            CheckGameStatus();
        }

        // 异步查询官网最新主视觉资源（不下载落盘，直接通过远程连接流式更新）
        _ = LoadLiveOfficialCoverAsync();
    }

    private async Task LoadLiveOfficialCoverAsync()
    {
        if (_coverService == null) return;
        try
        {
            var visuals = await _coverService.FetchLatestCoverVisualsAsync();
            if (!string.IsNullOrEmpty(visuals.BackgroundUrl))
            {
                OfficialBackgroundUrl = visuals.BackgroundUrl;
            }
            if (!string.IsNullOrEmpty(visuals.ForegroundUrl))
            {
                OfficialForegroundUrl = visuals.ForegroundUrl;
            }
            if (!string.IsNullOrEmpty(visuals.StarrySkyUrl))
            {
                OfficialStarrySkyUrl = visuals.StarrySkyUrl;
            }
        }
        catch
        {
            // 网络异常时保持官方预设远端链接
        }
    }

    private void OnCaptureStateChanged(bool running)
    {
        IsCapturing = running;
        CaptureButtonText = running ? "停止" : "启动";
        CaptureStatusDescription = running
            ? "截图器正在运行中，实时监控游戏画面..."
            : "截图器启动后才能使用各项功能，点击展开启动相关配置。";
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (_taskManager == null) return;

        if (_taskManager.IsRunning)
        {
            _taskManager.Stop();
        }
        else
        {
            if (_processDetector != null && !_processDetector.IsGameRunning())
            {
                if (AutoStartGame)
                {
                    LaunchGame();
                    System.Threading.Thread.Sleep(1000);
                }

                if (!_processDetector.IsGameRunning())
                {
                    Log.Warning("未检测到星布谷地游戏运行");
                    System.Windows.MessageBox.Show(
                        "未检测到星布谷地游戏（PetitPlanet.exe）正在运行，无法启动截图器。\n\n请先启动游戏，或在下方配置游戏路径后开启“自动拉起游戏”。",
                        "星布谷地 - 截图器启动提示",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
            }

            bool started = _taskManager.Start();
            if (!started)
            {
                Log.Warning("截图器启动失败");
                System.Windows.MessageBox.Show(
                    "截图器启动失败。\n\n请确认星布谷地游戏窗口处于非最小化状态，且本软件具备相应系统权限。",
                    "星布谷地 - 截图器启动提示",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void CheckGameStatus()
    {
        IsGameRunning = _processDetector?.IsGameRunning() ?? false;
    }

    [RelayCommand]
    private void LaunchGame()
    {
        var exePath = GameExecutablePath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = GameStartArgs,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "拉起游戏客户端失败");
            }
        }
    }

    [RelayCommand]
    private void BrowseGamePath()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择星布谷地游戏主程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            FileName = "PetitPlanet.exe"
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            GameExecutablePath = dialog.FileName;
            _configService.Config.General.GameExecutablePath = dialog.FileName;
            _configService.Save();
        }
    }

    [RelayCommand]
    private void StartCaptureTest()
    {
        Log.Information("准备执行图像捕获测试");
        var hwnd = _processDetector?.FindMainWindowHandle() ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            Log.Warning("执行图像捕获测试失败：未检测到游戏窗口");
            System.Windows.MessageBox.Show(
                "未检测到星布谷地游戏窗口（PetitPlanet.exe）。\n\n请先启动游戏并确保游戏窗口可见，然后再执行图像捕获测试。",
                "星布谷地 - 图像捕获测试",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var captureMode = CaptureMode.ToCaptureMode();

        var testWin = new BetterPetitPlanet.View.CaptureTestWindow();
        testWin.Owner = System.Windows.Application.Current?.MainWindow;
        testWin.StartCapture(hwnd, captureMode);
        testWin.Show();
        Log.Information("图像捕获测试窗口已成功启动");
    }

    [RelayCommand]
    private void OpenChildSessionWindow()
    {
        Log.Information("打开桌面分身 / 后台独立会话窗口");
    }

    [RelayCommand]
    private void GoToWikiUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignored
        }
    }

    partial void OnAutoStartGameChanged(bool value)
    {
        _configService.Config.General.AutoStartGame = value;
        _configService.Save();
    }

    partial void OnCaptureModeChanged(string value)
    {
        _configService.Config.General.CaptureMode = value;
        _configService.Save();
    }

    partial void OnTriggerIntervalChanged(int value)
    {
        _configService.Config.AutoPick.CooldownMs = value;
        _configService.Save();
    }
}
