using System.Collections.Generic;

namespace BetterPetitPlanet.Core.Config;

public sealed class AppConfig
{
    public MusicOverlayConfig MusicOverlay { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
    public AutoPickConfig AutoPick { get; set; } = new();
    public MusicConfig Music { get; set; } = new();
    public HotkeyConfig Hotkey { get; set; } = new();
}

public sealed class GeneralConfig
{
    public string ProcessName { get; set; } = "PetitPlanet.exe";
    public string GameExecutablePath { get; set; } = string.Empty;
    public bool AutoStartGame { get; set; } = false;
    public string CaptureMode { get; set; } = "WindowsGraphicsCapture";
    public bool RunInBackground { get; set; } = false;
}

public sealed class AutoPickConfig
{
    private ItemNameRecognitionConfig _itemNameRecognition = new();
    public ItemNameRecognitionConfig ItemNameRecognition
    {
        get => _itemNameRecognition;
        set => _itemNameRecognition = value ?? new();
    }
    public bool Enabled { get; set; } = false;
    public string OcrEngine { get; set; } = "DirectML";
    public string PickKey { get; set; } = "F";
    public int PressDurationMs { get; set; } = 40;
    public int CooldownMs { get; set; } = 50;
    public double MinConfidence { get; set; } = 0.50;
    public List<string> Whitelist { get; set; } = ["拾取"];
    public List<string> Blacklist { get; set; } = ["拾取雪球"];
    public bool FastPrescreenEnabled { get; set; } = true;
    public double PrescreenMinScore { get; set; } = 0.70;

    [Newtonsoft.Json.JsonIgnore]
    public List<string> Keywords
    {
        get => Whitelist;
        set => Whitelist = value;
    }
}

public enum MusicInputMode
{
    Keyboard = 0,
    Mouse = 1
}

public enum MusicPlaybackMode
{
    PlayOnce = 0,    // 弹完停止（单曲播放完毕自动停止）
    ListLoop = 1,    // 列表循环（顺序播放）
    SingleLoop = 2,  // 单曲循环
    Shuffle = 3      // 随机播放
}

public sealed class MusicConfig
{
    public MusicPlaybackTimingConfig PlaybackTiming { get; set; } = new();
    public bool AutomaticPlaybackEnabled { get; set; } = true;
    public int PreparationMs { get; set; } = 3000;
    public string SongsDirectory { get; set; } = string.Empty;
    public string SelectedInstrument { get; set; } = "竖笛";
    public string SelectedFormat { get; set; } = "全部格式";
    public double PlaybackSpeed { get; set; } = 1.0;
    public MusicInputMode InputMode { get; set; } = MusicInputMode.Keyboard;
    /// <summary>
    /// 仅在代码配置中生效：鼠标输入模式下点击音符后是否恢复光标到原位置。
    /// 默认 false：光标留在游戏窗口内连续点击音符；若为 true 则在点击后瞬移回原位。
    /// </summary>
    public bool RestoreMouseAfterClick { get; set; } = false;
    public bool AutoPauseOnFocusLost { get; set; } = false;
    public bool AutoFocusGameOnPlayback { get; set; } = true;
    public MusicPlaybackMode PlaybackMode { get; set; } = MusicPlaybackMode.PlayOnce;
}

public sealed class HotkeyConfig
{
    public string ToggleMusicTimelineHotkey { get; set; } = "Ctrl+F9";
    public string ToggleMusicOverlayHotkey { get; set; } = "Ctrl+F12";
    public string ToggleAutoPickHotkey { get; set; } = "F8";
    public string ToggleMusicHotkey { get; set; } = "F9";
    public string PreviousSongHotkey { get; set; } = "F10";
    public string NextSongHotkey { get; set; } = "F11";
}
