using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using BetterPetitPlanet.GameTask.Music.Instrument;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View;
using BetterPetitPlanet.View.Pages;
using BetterPetitPlanet.ViewModel;
using BetterPetitPlanet.ViewModel.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.DependencyInjection;

namespace BetterPetitPlanet;

public partial class App : Application
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging =>
        {
            var logFolder = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(logFolder);
            var logFile = Path.Combine(logFolder, "better_petit_planet.log");

            var loggerConfiguration = new LoggerConfiguration()
                .WriteTo.File(
                    logFile,
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning);

            Log.Logger = loggerConfiguration.CreateLogger();

            logging.ClearProviders();
            logging.AddSerilog(Log.Logger);
        })
        .ConfigureServices((context, services) =>
        {
            // Core & Process
            services.AddSingleton<IConfigService, ConfigService>();
            services.AddSingleton<GameProcessDetector>();
            services.AddSingleton<BetterPetitPlanet.Core.Web.OfficialCoverService>();
            services.AddSingleton<TaskTriggerDispatcher>();
            services.AddSingleton<GameTaskManager>();

            // AutoPick services
            services.AddSingleton<IOcrEngine, DirectMlOcrEngine>();
            services.AddSingleton(sp =>
            {
                var configService = sp.GetRequiredService<IConfigService>();
                return new ItemNameRecognitionService(configService, () =>
                {
                    // Lazy background initialization; never share the pickup OCR session lock.
                    var detector = new DirectMlDetEngine(configService.Config.AutoPick.ItemNameRecognition.Detection);
                    try { return new ItemNameRecognizer(detector, new DirectMlOcrEngine(),
                        Math.Clamp(configService.Config.AutoPick.ItemNameRecognition.MinimumTextLength, 1, 64)); }
                    catch { detector.Dispose(); throw; }
                }, sp.GetRequiredService<ILogger<AutoPickTrigger>>());
            });
            services.AddSingleton<AutoPickTrigger>();

            // Music services
            services.AddSingleton<IInstrumentRegistry, InstrumentRegistry>();
            services.AddSingleton<InstrumentCoordinateService>();
            services.AddSingleton<InstrumentDetector>();
            services.AddSingleton<InstrumentDetectorTrigger>();
            services.AddSingleton<KeyInputTransports>();
            services.AddSingleton<PetitScoreParser>();
            services.AddSingleton(sp =>
            {
                var timing = sp.GetRequiredService<IConfigService>().Config.Music.PlaybackTiming;
                return new MusicPlaybackOptions
                {
                    SchedulerIntervalMs = timing.SchedulerIntervalMs,
                    SnapshotIntervalMs = timing.SnapshotIntervalMs,
                    ReleaseRetryIntervalMs = timing.ReleaseRetryIntervalMs,
                    ReleaseRetryAttempts = timing.ReleaseRetryAttempts,
                    NotificationCapacity = timing.NotificationCapacity
                };
            });
            services.AddSingleton(sp =>
            {
                var overlay = sp.GetRequiredService<IConfigService>().Config.MusicOverlay;
                return MusicOverlayConfigMigration.ToReadinessOptions(overlay);
            });
            services.AddSingleton<MusicPlaybackService>();
            services.AddSingleton<MusicLibraryService>();
            services.AddSingleton<MusicCaptureObserver>();
            services.AddSingleton<MusicSessionCoordinator>();
            services.AddSingleton<MusicSkinRepository>();
            services.AddSingleton<MusicOverlayPresenter>();

            // Hotkey service
            services.AddSingleton<HotkeyService>();

            // Navigation
            services.AddNavigationViewPageProvider();
            services.AddSingleton<INavigationService, NavigationService>();

            // Views & ViewModels
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<HomePage>();
            services.AddSingleton<HomePageViewModel>();

            services.AddSingleton<TriggerSettingsPage>();
            services.AddSingleton<TriggerSettingsPageViewModel>();

            services.AddSingleton<MusicPage>();
            services.AddSingleton<MusicPageViewModel>();

            services.AddSingleton<HotkeyPage>();
            services.AddSingleton<HotkeyPageViewModel>();

            services.AddSingleton<SettingsPage>();
            services.AddSingleton<SettingsPageViewModel>();

            services.AddTransient<BetterPetitPlanet.ViewModel.Windows.MusicOverlaySettingsViewModel>();
        })
        .Build();

    public static T GetService<T>() where T : class
    {
        return _host.Services.GetRequiredService<T>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MusicOverlayDiagnostics.Start(System.Windows.Media.RenderCapability.Tier >> 16);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Log.Information("应用程序正在启动...");

            await _host.StartAsync();
            await _host.Services.GetRequiredService<IConfigService>().InitializeAsync();

            bool isAdmin = IsAdministrator();
            if (isAdmin)
            {
                Log.Information("程序已以管理员权限运行 (UIPI 保护已正常绕过)");
            }
            else
            {
                Log.Warning("程序当前未以管理员权限运行。如果游戏星布谷地以管理员权限启动，Windows UIPI 将拦截所有的键鼠模拟输入！");
            }

            var detector = _host.Services.GetRequiredService<GameProcessDetector>();
            HardwareInputSimulator.ForegroundGuard = detector.IsGameForegroundCached;

            // Register Triggers into dispatcher
            var dispatcher = _host.Services.GetRequiredService<TaskTriggerDispatcher>();
            var autoPickTrigger = _host.Services.GetRequiredService<AutoPickTrigger>();
            dispatcher.RegisterTrigger(autoPickTrigger);

            var instrumentTrigger = _host.Services.GetRequiredService<InstrumentDetectorTrigger>();
            dispatcher.RegisterTrigger(instrumentTrigger);

            var captureObserver = _host.Services.GetRequiredService<MusicCaptureObserver>();
            dispatcher.RegisterTrigger(captureObserver);

            var coordinator = _host.Services.GetRequiredService<MusicSessionCoordinator>();
            coordinator.Start();

            var overlayPresenter = _host.Services.GetRequiredService<MusicOverlayPresenter>();
            overlayPresenter.Start();

            // Pre-resolve ViewModels that listen to background trigger/hotkey events
            _host.Services.GetRequiredService<TriggerSettingsPageViewModel>();

            // Initialize Global Hotkeys
            var hotkeyService = _host.Services.GetRequiredService<HotkeyService>();
            hotkeyService.Initialize();

            // If AutoPick is enabled, start GameTaskManager
            if (autoPickTrigger.IsEnabled)
            {
                var taskManager = _host.Services.GetRequiredService<GameTaskManager>();
                taskManager.Start();
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            mainWindow.Navigate(typeof(HomePage));
            Log.Information("主窗口已呈现，应用程序就绪");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用程序在启动阶段遇到致命异常并中止");
            try
            {
                MessageBox.Show(
                    $"应用程序启动失败: {ex.Message}\n\n详细日志请查看: data/logs/better_petit_planet.log",
                    "BetterPetitPlanet 启动错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // UI failed to show message box
            }
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var overlayPresenter = _host.Services.GetService<MusicOverlayPresenter>();
            overlayPresenter?.Dispose();

            var coordinator = _host.Services.GetService<MusicSessionCoordinator>();
            coordinator?.Dispose();

            var hotkeyService = _host.Services.GetService<HotkeyService>();
            hotkeyService?.Dispose();

            var taskManager = _host.Services.GetService<GameTaskManager>();
            taskManager?.Dispose();

            var playbackService = _host.Services.GetService<MusicPlaybackService>();
            playbackService?.Dispose();

            await _host.StopAsync();
            _host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exception while exiting application");
        }
        finally
        {
            MusicOverlayDiagnostics.Shutdown();
            Log.CloseAndFlush();
            base.OnExit(e);
            Environment.Exit(0);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI dispatcher exception occurred");
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Fatal domain unhandled exception occurred");
        }
        else
        {
            Log.Fatal("Fatal domain unhandled exception occurred: {ExceptionObject}", e.ExceptionObject);
        }
        Log.CloseAndFlush();
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception occurred");
        e.SetObserved();
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
