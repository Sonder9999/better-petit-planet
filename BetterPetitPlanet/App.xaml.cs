using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using BetterPetitPlanet.GameTask.Music.Service;
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
            services.AddSingleton<TaskTriggerDispatcher>();
            services.AddSingleton<GameTaskManager>();

            // AutoPick services
            services.AddSingleton<IOcrEngine, DirectMlOcrEngine>();
            services.AddSingleton<AutoPickTrigger>();

            // Music services
            services.AddSingleton<InstrumentCoordinateService>();
            services.AddSingleton<InstrumentDetector>();
            services.AddSingleton<InstrumentDetectorTrigger>();
            services.AddSingleton<KeyInputTransports>();
            services.AddSingleton<PetitScoreParser>();
            services.AddSingleton<MusicPlaybackService>();
            services.AddSingleton<MusicLibraryService>();

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
        })
        .Build();

    public static T GetService<T>() where T : class
    {
        return _host.Services.GetRequiredService<T>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        await _host.StartAsync();

        bool isAdmin = IsAdministrator();
        if (isAdmin)
        {
            Log.Information("程序已以管理员权限运行 (UIPI 保护已正常绕过)");
        }
        else
        {
            Log.Warning("程序当前未以管理员权限运行。如果游戏星布谷地以管理员权限启动，Windows UIPI 将拦截所有的键鼠模拟输入！");
        }

        // Register Triggers into dispatcher
        var dispatcher = _host.Services.GetRequiredService<TaskTriggerDispatcher>();
        var autoPickTrigger = _host.Services.GetRequiredService<AutoPickTrigger>();
        dispatcher.RegisterTrigger(autoPickTrigger);

        var instrumentTrigger = _host.Services.GetRequiredService<InstrumentDetectorTrigger>();
        dispatcher.RegisterTrigger(instrumentTrigger);

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
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
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
            Log.CloseAndFlush();
            base.OnExit(e);
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
