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
    private string _currentTimeText = "00:00";

    [ObservableProperty]
    private string _totalTimeText = "00:00";

    [ObservableProperty]
    private bool _isMouseInput;

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

    public ObservableCollection<SongItemViewModel> Songs { get; } = [];
    private readonly ObservableCollection<SongItemViewModel> _allSongs = [];

    public ObservableCollection<string> AvailableInstruments { get; } = ["竖笛", "吉他", "全部乐器"];
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
        GameTaskManager? taskManager = null)
    {
        _configService = configService;
        _libraryService = libraryService;
        _playbackService = playbackService;
        _processDetector = processDetector;
        _instrumentDetector = instrumentDetector;
        _taskManager = taskManager;

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
        _playbackService.PositionChanged += OnPositionChanged;
        _playbackService.PlaybackCompleted += OnPlaybackCompleted;
        _playbackService.NextTrackRequested += () => Application.Current?.Dispatcher.Invoke(NextTrack);
        _playbackService.PreviousTrackRequested += () => Application.Current?.Dispatcher.Invoke(PreviousTrack);
        _playbackService.SwitchInstrumentRequested += () => Application.Current?.Dispatcher.Invoke(CycleInstrument);

        if (_instrumentDetector != null)
        {
            _instrumentDetector.DetectionUpdated += OnInstrumentDetectionUpdated;
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
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _lastContinuousDetectionTime = DateTime.Now;
            InstrumentStatusText = result.StatusSummary;
            InstrumentStatusColor = result.IsDetected ? "#107C41" : "#D83B01";
        });
    }

    private void CheckInstrumentStatus()
    {
        if (_processDetector == null) return;
        var hwnd = _processDetector.FindMainWindowHandle();
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
        if (_taskManager != null && !_taskManager.IsRunning && _processDetector.IsGameRunning())
        {
            _taskManager.Start();
        }

        // 若截图器正在运行，由 InstrumentDetectorTrigger 实时驱动 OCR，无需单次捕获
        if (_taskManager != null && _taskManager.IsRunning)
        {
            if (_instrumentDetector.LatestResult != null)
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
            InstrumentStatusText = isOpen ? "竖笛: 已就绪" : "竖笛: 未拿出";
            InstrumentStatusColor = isOpen ? "#107C41" : "#D83B01";
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

    [RelayCommand]
    private void SetPresetSpeed(string speedStr)
    {
        if (double.TryParse(speedStr, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            PlaybackSpeed = speed;
        }
    }

    [RelayCommand]
    private void BeginSeek()
    {
        _isSeeking = true;
    }

    [RelayCommand]
    private void Seek(double seconds)
    {
        _playbackService.Seek(seconds);
        _isSeeking = false;
    }

    private void OnPlaybackStateChanged(PlayerState state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsPlaying = state == PlayerState.Playing;
            PlayPauseSymbol = IsPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        });
    }

    private void OnPositionChanged(double currentSec, double totalSec)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isSeeking)
            {
                CurrentPosition = currentSec;
                CurrentTimeText = TimeSpan.FromSeconds(currentSec).ToString(@"mm\:ss");
            }
            TotalDuration = Math.Max(1.0, totalSec);
            TotalTimeText = TimeSpan.FromSeconds(totalSec).ToString(@"mm\:ss");
        });
    }

    private void OnPlaybackCompleted()
    {
        Application.Current?.Dispatcher.Invoke(() =>
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

    [RelayCommand]
    private void PlaySelected(SongItemViewModel? song)
    {
        var target = song ?? SelectedSong;
        if (target?.Timeline != null)
        {
            if (_processDetector != null && _instrumentDetector != null && _instrumentDetector.IsTemplateLoaded)
            {
                var hwnd = _processDetector.FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (!ConfirmContinuePlayingIfNotReady(hwnd))
                    {
                        return;
                    }
                }
            }

            _playbackService.LoadTimeline(target.Timeline);
            CurrentSongTitle = target.Title;
            CurrentSongArtist = target.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {target.Instrument}";
            _playbackService.Play();
        }
    }

    private bool ConfirmContinuePlayingIfNotReady(IntPtr hwnd)
    {
        if (_instrumentDetector == null || !_instrumentDetector.IsTemplateLoaded)
        {
            return true;
        }

        bool isOpen = _instrumentDetector.CheckGameWindow(hwnd, out _);
        if (isOpen)
        {
            return true;
        }

        // 弹窗提示：右边是确定按钮，继续演奏在最左边按钮
        var dialog = new BetterPetitPlanet.View.Windows.InstrumentNotReadyDialog
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
            _playbackService.LoadTimeline(value.Timeline);
            CurrentSongTitle = value.Title;
            CurrentSongArtist = value.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {value.Instrument}";
        }
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_playbackService.CurrentTimeline == null && SelectedSong != null)
        {
            _playbackService.LoadTimeline(SelectedSong.Timeline);
            CurrentSongTitle = SelectedSong.Title;
            CurrentSongArtist = SelectedSong.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {SelectedSong.Instrument}";
        }

        if (_playbackService.State != PlayerState.Playing)
        {
            if (_processDetector != null && _instrumentDetector != null && _instrumentDetector.IsTemplateLoaded)
            {
                var hwnd = _processDetector.FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (!ConfirmContinuePlayingIfNotReady(hwnd))
                    {
                        return;
                    }
                }
            }
        }

        _playbackService.TogglePlayPause();
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        var prev = _libraryService.GetPreviousSong();
        if (prev != null)
        {
            _playbackService.LoadTimeline(prev);
            CurrentSongTitle = prev.Metadata.Title;
            CurrentSongArtist = prev.Metadata.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {prev.Metadata.Instrument}";
            _playbackService.Play();
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
            _playbackService.LoadTimeline(next);
            CurrentSongTitle = next.Metadata.Title;
            CurrentSongArtist = next.Metadata.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {next.Metadata.Instrument}";
            _playbackService.Play();
        }
    }

    private void PlayRandomTrack()
    {
        var randomSong = _libraryService.GetRandomSong();
        if (randomSong != null)
        {
            _playbackService.LoadTimeline(randomSong);
            CurrentSongTitle = randomSong.Metadata.Title;
            CurrentSongArtist = randomSong.Metadata.Artist;
            CurrentSongSubtitle = $"PetitMusic JSON · {randomSong.Metadata.Instrument}";
            _playbackService.Play();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _playbackService.Stop();
        CurrentPosition = 0;
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
