using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterPetitPlanet.GameTask.Music.Model;

public enum SongSectionType
{
    Intro,      // 前奏
    Verse,      // 主歌
    Chorus,     // 高潮 / 副歌
    Bridge,     // 间奏 / 桥段 / 过渡
    Outro       // 尾奏
}

public sealed partial class SongSectionMarker : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public SongSectionType SectionType { get; set; } = SongSectionType.Verse;
    public int NoteIndex { get; set; }
    public int TimeMs { get; set; }
    public double NormalizedProgress { get; set; } // 0.0 ~ 1.0 (relative to TotalNotes - 1)
    
    [ObservableProperty]
    private bool _isActive;

    public bool IsClimax => SectionType == SongSectionType.Chorus;
    public string DisplayTime => TimeSpan.FromMilliseconds(TimeMs).ToString(@"mm\:ss");
    
    public string BadgeIcon => SectionType switch
    {
        SongSectionType.Intro => "🎵",
        SongSectionType.Chorus => "🔥",
        SongSectionType.Bridge => "✨",
        SongSectionType.Outro => "🏁",
        _ => "🎼"
    };

    public string ToolTipText => Name;
}
