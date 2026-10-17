using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Service;
using Serilog;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.ViewModel.Pages;

public sealed class SongItemViewModel : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Instrument { get; set; } = string.Empty;
    public string Duration { get; set; } = "00:00";
    public double Bpm { get; set; } = 120.0;
    public string FilePath { get; set; } = string.Empty;
    public PerformanceTimeline Timeline { get; set; } = null!;
}

public partial class MusicPageViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly MusicLibraryService _libraryService;
    private readonly MusicPlaybackService _playbackService;
    private readonly GameProcessDetector? _processDetector;
    private readonly InstrumentDetector? _instrumentDetector;
    private readonly GameTaskManager? _taskManager;
    private readonly MusicSessionCoordinator? _coordinator;
    private readonly DispatcherTimer? _instrumentCheckTimer;

    [ObservableProperty]
    private string _songsDirectory = string.Empty;

    [ObservableProperty]
    private string _selectedInstrument = "竖笛";

    [ObservableProperty]
    private string _selectedFormat = "全部格式";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _currentSongTitle = "等待播放";

    [ObservableProperty]
    private string _currentSongArtist = "未选择乐曲";

    [ObservableProperty]
    private string _currentSongSubtitle = "PetitMusic JSON · --";

    [ObservableProperty]
    private double _currentPosition;

    [ObservableProperty]
    private double _totalDuration = 100.0;

    [ObservableProperty]
    private int _currentNoteIndex;

    [ObservableProperty]
    private int _totalNotes = 1;

    public int MaximumNoteIndex => Math.Max(0, TotalNotes - 1);

    partial void OnTotalNotesChanged(int value) => OnPropertyChanged(nameof(MaximumNoteIndex));

    [ObservableProperty]
    private string _currentTimeText = "00:00";

    [ObservableProperty]
    private string _totalTimeText = "00:00";

    [ObservableProperty]
    private bool _isMouseInput;

    public bool AutoFocusGameOnPlayback
    {
        get => _configService.Config.Music.AutoFocusGameOnPlayback;
        set
        {
            _configService.Config.Music.AutoFocusGameOnPlayback = value;
            _configService.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private SongItemViewModel? _selectedSong;

    [ObservableProperty]
    private SymbolRegular _playPauseSymbol = SymbolRegular.Play24;

    [ObservableProperty]
    private MusicPlaybackMode _playbackMode = MusicPlaybackMode.PlayOnce;

    [ObservableProperty]
    private SymbolRegular _playbackModeSymbol = SymbolRegular.ArrowRight24;

    [ObservableProperty]
    private string _playbackModeToolTip = "播放模式：单曲播放 (弹完停止)";

    public string MusicFolderDisplayText => string.IsNullOrWhiteSpace(SongsDirectory) ? "尚未选择目录" : SongsDirectory;
    public string InputModeDisplayText => IsMouseInput ? "鼠标" : "键盘";

    public ObservableCollection<SongSectionMarker> SectionMarkers { get; } = [];

    [ObservableProperty]
    private SongSectionMarker? _currentSection;

    public bool HasSections => SectionMarkers.Count > 0;

    public ObservableCollection<SongItemViewModel> Songs { get; } = [];
    private readonly ObservableCollection<SongItemViewModel> _allSongs = [];

    public ObservableCollection<string> AvailableInstruments { get; } = new(
        BetterPetitPlanet.GameTask.Music.Instrument.InstrumentRegistry.Default
            .GetAllProfiles()
            .Select(p => p.DisplayName)
            .Append("全部乐器"));
    public ObservableCollection<string> AvailableFormats { get; } = ["全部格式", "Petit JSON", "MIDI"];

    [ObservableProperty]
    private string _instrumentStatusText = "检测中...";

    [ObservableProperty]
    private string _instrumentStatusColor = "#888888";

    public MusicPageViewModel(
        IConfigService configService,
        MusicLibraryService libraryService,
        MusicPlaybackService playbackService,
        GameProcessDetector? processDetector = null,
        InstrumentDetector? instrumentDetector = null,
        GameTaskManager? taskManager = null,
        MusicSessionCoordinator? coordinator = null)
    {
        _configService = configService;
        _libraryService = libraryService;
        _playbackService = playbackService;
        _processDetector = processDetector;
        _instrumentDetector = instrumentDetector;
        _taskManager = taskManager;
        _coordinator = coordinator;

        _songsDirectory = _configService.Config.Music.SongsDirectory;
        _selectedInstrument = _configService.Config.Music.SelectedInstrument;
        if (string.IsNullOrWhiteSpace(_selectedInstrument) || _selectedInstrument == "全部乐器")
        {
            _selectedInstrument = "竖笛";
            _configService.Config.Music.SelectedInstrument = "竖笛";
            _configService.Save();
        }
        _selectedFormat = _configService.Config.Music.SelectedFormat;
        _isMouseInput = _configService.Config.Music.InputMode == MusicInputMode.Mouse;
        _playbackMode = _configService.Config.Music.PlaybackMode;
        UpdatePlaybackModeDisplay(_playbackMode);

        var foundDir = MusicLibraryService.FindSongsDirectory(_songsDirectory);
        if (!string.IsNullOrEmpty(foundDir))
        {
            _songsDirectory = foundDir;
            if (_configService.Config.Music.SongsDirectory != foundDir)
            {
                _configService.Config.Music.SongsDirectory = foundDir;
                _configService.Save();
            }
        }

        _playbackService.StateChanged += OnPlaybackStateChanged;
        _playbackService.NotePositionChanged += OnNotePositionChanged;
        _playbackService.PlaybackCompleted += OnPlaybackCompleted;
        _playbackService.NextTrackRequested += () => Application.Current?.Dispatcher.InvokeAsync(NextTrack);
        _playbackService.PreviousTrackRequested += () => Application.Current?.Dispatcher.InvokeAsync(PreviousTrack);

        if (_instrumentDetector != null)
        {
            _instrumentDetector.DetectionUpdated += OnInstrumentDetectionUpdated;
        }

        if (_coordinator != null)
        {
            _coordinator.Changed += () =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    InstrumentStatusText = _coordinator.Status;
                    InstrumentStatusColor = _coordinator.Ready ? "#107C41" : "#D83B01";
                });
            };
        }

        if (_processDetector != null && _instrumentDetector != null)
        {
            _instrumentCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _instrumentCheckTimer.Tick += (s, e) => CheckInstrumentStatus();
            _instrumentCheckTimer.Start();
            CheckInstrumentStatus();
        }

        _ = RefreshSongsAsync();
    }

    private DateTime _lastContinuousDetectionTime = DateTime.MinValue;

    private void OnInstrumentDetectionUpdated(InstrumentDetectionResult result)
    {
        long diagnosticPosted = MusicOverlayDiagnostics.Timestamp();
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrUiInvokeTotalMs);
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            MusicOverlayDiagnostics.Elapsed(MusicOverlayDiagnostics.Metric.OcrUiInvokeQueueMs, diagnosticPosted);
            using var execution = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrUiInvokeExecuteMs);
            _lastContinuousDetectionTime = DateTime.Now;
            InstrumentStatusText = result.StatusSummary;
            InstrumentStatusColor = result.IsDetected ? "#107C41" : "#D83B01";
        });
    }

    private void CheckInstrumentStatus()
    {
        if (_processDetector == null) return;
        var hwnd = _processDetector.FindMainWindowHandleCached();
        if (hwnd == IntPtr.Zero)
        {
            InstrumentStatusText = "游戏未运行";
            InstrumentStatusColor = "#888888";
            return;
        }

        if (_instrumentDetector == null)
        {
            InstrumentStatusText = "检测器未初始化";
            InstrumentStatusColor = "#888888";
            return;
        }

        // 若截图器未启动且游戏在运行，自动启动截图器以持续提供实时 OCR 画面流
        if (_taskManager != null && !_taskManager.IsRunning && _processDetector.IsGameRunningCached())
        {
            _taskManager.StartWithCachedTarget();
        }

        // 若截图器正在运行，由触发流实时驱动检测，直接依赖现有触发流，不再调用 CheckGameWindow
        if (_taskManager != null && _taskManager.IsRunning)
        {
            if (_coordinator != null)
            {
                InstrumentStatusText = _coordinator.Status;
                InstrumentStatusColor = _coordinator.Ready ? "#107C41" : "#D83B01";
            }
            else if (_instrumentDetector.LatestResult != null)
            {
                InstrumentStatusText = _instrumentDetector.LatestResult.StatusSummary;
                InstrumentStatusColor = _instrumentDetector.LatestResult.IsDetected ? "#107C41" : "#D83B01";
            }
            return;
        }

        // 若近期（3 秒内）已有连续流（如 CaptureTestWindow）产出结果，直接复用保证绝对同步
        if ((DateTime.Now - _lastContinuousDetectionTime).TotalSeconds < 3.0 && _instrumentDetector.LatestResult != null)
        {
            InstrumentStatusText = _instrumentDetector.LatestResult.StatusSummary;
            InstrumentStatusColor = _instrumentDetector.LatestResult.IsDetected ? "#107C41" : "#D83B01";
            return;
        }

        bool isOpen = _instrumentDetector.CheckGameWindow(hwnd, out _);
        if (_instrumentDetector.LatestResult != null)
        {
            InstrumentStatusText = _instrumentDetector.LatestResult.StatusSummary;
            InstrumentStatusColor = _instrumentDetector.LatestResult.IsDetected ? "#107C41" : "#D83B01";
        }
        else
        {
            string? currentInst = !string.IsNullOrWhiteSpace(_instrumentDetector.LatestResult?.DisplayName)
                ? _instrumentDetector.LatestResult.DisplayName
                : (!string.IsNullOrWhiteSpace(SelectedInstrument) && SelectedInstrument != "全部乐器" ? SelectedInstrument : null);

            if (isOpen)
            {
                InstrumentStatusText = $"{currentInst ?? "乐器"}: 已就绪";
                InstrumentStatusColor = "#107C41";
            }
            else
            {
                InstrumentStatusText = currentInst != null ? $"{currentInst}: 未拿出" : "未检测到乐器";
                InstrumentStatusColor = "#D83B01";
            }
        }
    }

    private bool _isSeeking;

    public double PlaybackSpeed
    {
        get => _playbackService.PlaybackSpeed;
        set
        {
            _playbackService.SetSpeed(value);
            OnPropertyChanged();
        }
    }

    public bool AutoPauseOnFocusLost
    {
        get => _configService.Config.Music.AutoPauseOnFocusLost;
        set
        {
            _configService.Config.Music.AutoPauseOnFocusLost = value;
            _configService.Save();
            OnPropertyChanged();
        }
    }

    public bool AutomaticPlaybackEnabled
    {
        get => _configService.Config.Music.AutomaticPlaybackEnabled;
        set
        {
            if (_configService.Config.Music.AutomaticPlaybackEnabled != value)
            {
                _configService.Config.Music.AutomaticPlaybackEnabled = value;
                if (_coordinator != null)
                {
                    _ = _coordinator.PrepareSessionAsync();
                }
                else
                {
                    _playbackService.SetAutomaticPlayback(value);
                    _configService.Save();
                }
                OnPropertyChanged();
            }
        }
    }

    public int PreparationMs
    {
        get => _configService.Config.Music.PreparationMs;
        set
        {
            if (_configService.Config.Music.PreparationMs != value)
            {
                _configService.Config.Music.PreparationMs = Math.Clamp(value, 0, 10000);
                if (_coordinator != null)
                {
                    _ = _coordinator.PrepareSessionAsync();
                }
                else
                {
                    _playbackService.ConfigurePreparation(_configService.Config.Music.PreparationMs);
                    _configService.Save();
                }
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void SetPresetSpeed(string speedStr)
    {
        if (double.TryParse(speedStr, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            PlaybackSpeed = speed;
        }
    }

    private void TryAutoFocusGame()
    {
        if (AutoFocusGameOnPlayback && _processDetector != null && !_processDetector.IsGameForegroundCached())
        {
            _processDetector.ActivateGameWindow();
        }
    }

    [RelayCommand]
    private void BeginSeek()
    {
        _isSeeking = true;
    }

    [RelayCommand]
    private void Scrubbing(double noteIndexVal)
    {
        _isSeeking = true;
        int noteIndex = (int)Math.Round(noteIndexVal);
        CurrentNoteIndex = Math.Clamp(noteIndex, 0, MaximumNoteIndex);

        var timeline = _playbackService.CurrentTimeline;
        if (timeline != null && timeline.Events.Count > 0)
        {
            int idx = Math.Clamp(CurrentNoteIndex, 0, timeline.Events.Count - 1);
            var noteTimeMs = timeline.Events[idx].TimeMs;
            CurrentPosition = noteTimeMs / 1000.0;
            CurrentTimeText = TimeSpan.FromMilliseconds(noteTimeMs).ToString(@"mm\:ss");
        }
        UpdateActiveSection(CurrentNoteIndex);
    }

    [RelayCommand]
    private void Seek(double noteIndexVal)
    {
        // 1. 若当前尚未载入曲目但已选中歌曲，先载入曲目
        if (_playbackService.CurrentTimeline == null && SelectedSong?.Timeline != null)
        {
            _playbackService.LoadTimeline(SelectedSong.Timeline);
            SetupSongDisplay(SelectedSong.Timeline);
        }

        int noteIndex = (int)Math.Round(noteIndexVal);
        noteIndex = Math.Clamp(noteIndex, 0, MaximumNoteIndex);
        CurrentNoteIndex = noteIndex;

        var timeline = _playbackService.CurrentTimeline;
        if (timeline != null && timeline.Events.Count > 0)
        {
            int idx = Math.Clamp(noteIndex, 0, timeline.Events.Count - 1);
            var noteTimeMs = timeline.Events[idx].TimeMs;
            CurrentPosition = noteTimeMs / 1000.0;
            CurrentTimeText = TimeSpan.FromMilliseconds(noteTimeMs).ToString(@"mm\:ss");
        }
        UpdateActiveSection(noteIndex);

        if (_coordinator != null)
        {
            _ = _coordinator.SeekNoteAsync(noteIndex);
            _isSeeking = false;
            return;
        }

        _playbackService.SeekNote(noteIndex);
        _isSeeking = false;

        // 2. 真正的音乐播放器体验：点击进度条任意位置直接开始/继续播放
        if (_playbackService.CurrentTimeline != null && _playbackService.State != PlayerState.Playing)
        {
            if (_processDetector != null && _instrumentDetector != null && _instrumentDetector.IsTemplateLoaded)
            {
                var hwnd = _processDetector.FindMainWindowHandle();
                if (hwnd != IntPtr.Zero && !ConfirmContinuePlayingIfNotReady(hwnd, SelectedSong?.Timeline?.Metadata?.Instrument))
                {
                    return;
                }
            }

            _playbackService.Play();
        }

        // 3. 激活游戏窗口确保按键发送与演奏立即响应
        TryAutoFocusGame();
    }

    [RelayCommand]
    private void SeekSection(SongSectionMarker? section)
    {
        if (section == null) return;
        Log.Information("跳转至段落：{Section} (音符 #{Idx}, 时间 {Time})", section.Name, section.NoteIndex + 1, section.DisplayTime);
        Seek(section.NoteIndex);
    }

    public void UpdateSectionMarkers(PerformanceTimeline? timeline)
    {
        SectionMarkers.Clear();
        if (timeline?.Sections != null)
        {
            foreach (var sec in timeline.Sections)
            {
                SectionMarkers.Add(sec);
            }
        }
        OnPropertyChanged(nameof(HasSections));
        UpdateActiveSection(CurrentNoteIndex);
    }

    private void UpdateActiveSection(int currentNote)
    {
        if (SectionMarkers.Count == 0)
        {
            CurrentSection = null;
            return;
        }

        var active = SectionMarkers.LastOrDefault(s => s.NoteIndex <= currentNote) ?? SectionMarkers[0];
        foreach (var s in SectionMarkers)
        {
            s.IsActive = (s == active);
        }
        CurrentSection = active;
    }

    private void OnPlaybackStateChanged(PlayerState state)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsPlaying = state == PlayerState.Playing;
            PlayPauseSymbol = IsPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        });
    }

    private void OnNotePositionChanged(int currentNote, int totalNotes, double currentSec, double totalSec)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            TotalNotes = Math.Max(1, totalNotes);
            TotalDuration = Math.Max(1.0, totalSec);
            TotalTimeText = TimeSpan.FromSeconds(totalSec).ToString(@"mm\:ss");

            if (!_isSeeking)
            {
                CurrentNoteIndex = Math.Clamp(currentNote, 0, MaximumNoteIndex);
                CurrentPosition = currentSec;
                CurrentTimeText = TimeSpan.FromSeconds(currentSec).ToString(@"mm\:ss");
                UpdateActiveSection(CurrentNoteIndex);
            }
        });
    }

    private void OnPlaybackCompleted()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            switch (PlaybackMode)
            {
                case MusicPlaybackMode.PlayOnce:
                    Stop();
                    Log.Information("当前歌曲已演奏完毕，单曲播放模式自动停止");
                    break;
                case MusicPlaybackMode.SingleLoop:
                    _playbackService.Seek(0);
                    _playbackService.Play();
                    Log.Information("当前歌曲演奏完毕，单曲循环重新开始");
                    break;
                case MusicPlaybackMode.Shuffle:
                    PlayRandomTrack();
                    break;
                case MusicPlaybackMode.ListLoop:
                default:
                    NextTrack();
                    break;
            }
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(SongsDirectory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SongsDirectory,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void SelectDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择曲谱文件夹",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SongsDirectory = dialog.SelectedPath;
            OnPropertyChanged(nameof(MusicFolderDisplayText));
            _configService.Config.Music.SongsDirectory = SongsDirectory;
            _configService.Save();
            _ = RefreshSongsAsync();
        }
    }

    [RelayCommand]
    private void DeleteMusicFolder()
    {
        SongsDirectory = string.Empty;
        OnPropertyChanged(nameof(MusicFolderDisplayText));
        _configService.Config.Music.SongsDirectory = string.Empty;
        _configService.Save();
        Songs.Clear();
        _allSongs.Clear();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Log.Information("打开演奏高级设置窗口");
        var win = new BetterPetitPlanet.View.Windows.MusicSettingsWindow(this);
        win.Owner = Application.Current?.MainWindow;
        win.ShowDialog();
    }

    [RelayCommand]
    public void CycleInstrument()
    {
        var instruments = AvailableInstruments;
        int currentIndex = instruments.IndexOf(SelectedInstrument);
        int nextIndex = (currentIndex + 1) % instruments.Count;
        SelectedInstrument = instruments[nextIndex];
        Log.Information("切换乐器筛选为: {Instrument}", SelectedInstrument);
    }

    [RelayCommand]
    private void CyclePlaybackMode()
    {
        // 循环顺序：单曲播放(弹完停止) -> 列表循环 -> 单曲循环 -> 随机播放
        PlaybackMode = PlaybackMode switch
        {
            MusicPlaybackMode.PlayOnce => MusicPlaybackMode.ListLoop,
            MusicPlaybackMode.ListLoop => MusicPlaybackMode.SingleLoop,
            MusicPlaybackMode.SingleLoop => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.Shuffle => MusicPlaybackMode.PlayOnce,
            _ => MusicPlaybackMode.PlayOnce
        };
        Log.Information("切换播放模式为: {Mode} ({ToolTip})", PlaybackMode, PlaybackModeToolTip);
    }

    partial void OnPlaybackModeChanged(MusicPlaybackMode value)
    {
        _configService.Config.Music.PlaybackMode = value;
        _configService.Save();
        UpdatePlaybackModeDisplay(value);
    }

    private void UpdatePlaybackModeDisplay(MusicPlaybackMode mode)
    {
        (PlaybackModeSymbol, PlaybackModeToolTip) = mode switch
        {
            MusicPlaybackMode.PlayOnce => (SymbolRegular.ArrowRight24, "播放模式：单曲播放 (弹完停止)"),
            MusicPlaybackMode.ListLoop => (SymbolRegular.ArrowRepeatAll24, "播放模式：列表循环"),
            MusicPlaybackMode.SingleLoop => (SymbolRegular.ArrowRepeat124, "播放模式：单曲循环"),
            MusicPlaybackMode.Shuffle => (SymbolRegular.ArrowShuffle24, "播放模式：随机播放"),
            _ => (SymbolRegular.ArrowRight24, "播放模式：单曲播放 (弹完停止)")
        };
    }

    [RelayCommand]
    private async Task RefreshSongsAsync()
    {
        IsRefreshing = true;
        try
        {
            _allSongs.Clear();
            Songs.Clear();

            if (string.IsNullOrWhiteSpace(SongsDirectory) || !Directory.Exists(SongsDirectory))
            {
                return;
            }

            var scores = await _libraryService.ScanAndLoadScoresAsync(SongsDirectory, SelectedInstrument);

            foreach (var timeline in scores)
            {
                var durationSpan = TimeSpan.FromMilliseconds(timeline.TotalDurationMs);
                var item = new SongItemViewModel
                {
                    Title = timeline.Metadata.Title,
                    Artist = string.IsNullOrWhiteSpace(timeline.Metadata.Artist) ? "未知艺术家" : timeline.Metadata.Artist,
                    Instrument = timeline.Metadata.Instrument,
                    Duration = durationSpan.ToString(@"mm\:ss"),
                    Bpm = timeline.Metadata.Bpm,
                    FilePath = timeline.FilePath,
                    Timeline = timeline
                };
                _allSongs.Add(item);
            }

            ApplyFilter();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ApplyFilter()
    {
        Songs.Clear();
        var query = _allSongs.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(s =>
                s.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                s.Artist.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var s in query)
        {
            Songs.Add(s);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void SetupSongDisplay(PerformanceTimeline timeline)
    {
        CurrentSongTitle = timeline.Metadata.Title;
        CurrentSongArtist = string.IsNullOrWhiteSpace(timeline.Metadata.Artist) ? "未知艺术家" : timeline.Metadata.Artist;
        CurrentSongSubtitle = $"PetitMusic JSON · {timeline.Metadata.Instrument}";
        TotalNotes = Math.Max(1, timeline.Events.Count);
        CurrentNoteIndex = 0;
        CurrentPosition = 0;
        CurrentTimeText = "00:00";
        TotalDuration = Math.Max(1.0, timeline.TotalDurationMs / 1000.0);
        TotalTimeText = TimeSpan.FromMilliseconds(timeline.TotalDurationMs).ToString(@"mm\:ss");
        UpdateSectionMarkers(timeline);
    }

    private async Task StartTimelineAsync()
    {
        try
        {
            if (_coordinator != null)
            {
                await _coordinator.PrepareSessionAsync();
                await _coordinator.StartTimelineAsync();
                InstrumentStatusText = _coordinator.Status;
            }
            else
            {
                _playbackService.Play();
            }
        }
        catch (Exception ex)
        {
            InstrumentStatusText = ex.Message;
            Log.Warning(ex, "Music page timeline start failed");
        }
    }

    [RelayCommand]
    private async Task PlaySelected(SongItemViewModel? song)
    {
        var target = song ?? SelectedSong;
        if (target?.Timeline != null)
        {
            if (_coordinator != null)
            {
                _coordinator.LoadTimeline(target.Timeline);
                SetupSongDisplay(target.Timeline);
                await StartTimelineAsync();
                return;
            }

            if (_processDetector != null && _instrumentDetector != null && _instrumentDetector.IsTemplateLoaded)
            {
                var hwnd = _processDetector.FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (!ConfirmContinuePlayingIfNotReady(hwnd, target.Timeline.Metadata?.Instrument))
                    {
                        return;
                    }
                }
            }

            _playbackService.LoadTimeline(target.Timeline);
            SetupSongDisplay(target.Timeline);
            _playbackService.Play();
            TryAutoFocusGame();
        }
    }

    private bool ConfirmContinuePlayingIfNotReady(IntPtr hwnd, string? targetInstrument = null)
    {
        if (_instrumentDetector == null || !_instrumentDetector.IsTemplateLoaded)
        {
            return true;
        }

        bool isOpen = _instrumentDetector.CheckGameWindow(hwnd, out _);
        var latest = _instrumentDetector.LatestResult;

        // 画面有乐器且与目标曲谱乐器一致时方可直接放行；若乐器不匹配，继续弹窗提示
        if (isOpen && latest != null && (string.IsNullOrWhiteSpace(targetInstrument) ||
            MusicReadinessGate.MatchesInstrument(targetInstrument, latest.InstrumentId, latest.DisplayName)))
        {
            return true;
        }

        string instrumentName = !string.IsNullOrWhiteSpace(targetInstrument)
            ? targetInstrument
            : (!string.IsNullOrWhiteSpace(SelectedSong?.Timeline?.Metadata?.Instrument)
                ? SelectedSong.Timeline.Metadata.Instrument
                : (!string.IsNullOrWhiteSpace(SelectedInstrument) && SelectedInstrument != "全部乐器" ? SelectedInstrument : "乐器"));

        // 弹窗提示：右边是确定按钮，继续演奏在最左边按钮
        var dialog = new BetterPetitPlanet.View.Windows.InstrumentNotReadyDialog(instrumentName)
        {
            Owner = Application.Current?.MainWindow
        };
        bool? dialogResult = dialog.ShowDialog();
        return dialogResult == true;
    }

    partial void OnSelectedSongChanged(SongItemViewModel? value)
    {
        if (value?.Timeline != null)
        {
            if (_coordinator != null) _coordinator.LoadTimeline(value.Timeline);
            else _playbackService.LoadTimeline(value.Timeline);
            SetupSongDisplay(value.Timeline);
        }
    }

    [RelayCommand]
    private async Task TogglePlay()
    {
        if (_playbackService.CurrentTimeline == null && SelectedSong != null)
        {
            if (_coordinator != null) _coordinator.LoadTimeline(SelectedSong.Timeline);
            else _playbackService.LoadTimeline(SelectedSong.Timeline);
            SetupSongDisplay(SelectedSong.Timeline);
        }

        if (_coordinator != null)
        {
            await _coordinator.ToggleTimelineAsync();
            return;
        }

        if (_playbackService.State != PlayerState.Playing)
        {
            if (_processDetector != null && _instrumentDetector != null && _instrumentDetector.IsTemplateLoaded)
            {
                var hwnd = _processDetector.FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (!ConfirmContinuePlayingIfNotReady(hwnd, SelectedSong?.Timeline?.Metadata?.Instrument))
                    {
                        return;
                    }
                }
            }
            TryAutoFocusGame();
        }

        _playbackService.TogglePlayPause();
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        var prev = _libraryService.GetPreviousSong();
        if (prev != null)
        {
            if (_coordinator != null)
            {
                _coordinator.LoadTimeline(prev);
                SetupSongDisplay(prev);
                _ = StartTimelineAsync();
            }
            else
            {
                _playbackService.LoadTimeline(prev);
                SetupSongDisplay(prev);
                _playbackService.Play();
                TryAutoFocusGame();
            }
        }
    }

    [RelayCommand]
    private void NextTrack()
    {
        var next = PlaybackMode == MusicPlaybackMode.Shuffle
            ? _libraryService.GetRandomSong()
            : _libraryService.GetNextSong();

        if (next != null)
        {
            if (_coordinator != null)
            {
                _coordinator.LoadTimeline(next);
                SetupSongDisplay(next);
                _ = StartTimelineAsync();
            }
            else
            {
                _playbackService.LoadTimeline(next);
                SetupSongDisplay(next);
                _playbackService.Play();
                TryAutoFocusGame();
            }
        }
    }

    private void PlayRandomTrack()
    {
        var randomSong = _libraryService.GetRandomSong();
        if (randomSong != null)
        {
            if (_coordinator != null)
            {
                _coordinator.LoadTimeline(randomSong);
                SetupSongDisplay(randomSong);
                _ = StartTimelineAsync();
            }
            else
            {
                _playbackService.LoadTimeline(randomSong);
                SetupSongDisplay(randomSong);
                _playbackService.Play();
                TryAutoFocusGame();
            }
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (_coordinator != null) _coordinator.StopTimeline();
        else _playbackService.Stop();
        CurrentPosition = 0;
        CurrentNoteIndex = 0;
        CurrentTimeText = "00:00";
    }

    partial void OnIsMouseInputChanged(bool value)
    {
        _configService.Config.Music.InputMode = value ? MusicInputMode.Mouse : MusicInputMode.Keyboard;
        _configService.Save();
        OnPropertyChanged(nameof(InputModeDisplayText));
    }

    partial void OnSelectedInstrumentChanged(string value)
    {
        _configService.Config.Music.SelectedInstrument = value;
        _configService.Save();
        _ = RefreshSongsAsync();
    }
}
