using System;
using System.Collections.Generic;
using System.Windows.Forms;
using BetterPetitPlanet.Core.Config;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask;

public readonly record struct MusicHotkey(Keys Key, User32.HotKeyModifiers Modifiers)
{
    public static MusicHotkey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("快捷键不能为空");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        User32.HotKeyModifiers modifiers = User32.HotKeyModifiers.MOD_NONE;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => User32.HotKeyModifiers.MOD_CONTROL,
                "alt" => User32.HotKeyModifiers.MOD_ALT,
                "shift" => User32.HotKeyModifiers.MOD_SHIFT,
                "win" => User32.HotKeyModifiers.MOD_WIN,
                _ => throw new ArgumentException("不支持的快捷键修饰符：" + parts[i])
            };
            if ((modifiers & modifier) != 0) throw new ArgumentException("快捷键修饰符重复");
            modifiers |= modifier;
        }

        var keyText = parts[^1];
        if (keyText.Length == 0 || char.IsDigit(keyText[0]) || !Enum.TryParse<Keys>(keyText, true, out var key) ||
            key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu || (key & Keys.KeyCode) != key)
            throw new ArgumentException("快捷键按键无效：" + keyText);

        if (modifiers == 0 && key is Keys.A or Keys.S or Keys.D or Keys.F or Keys.J or Keys.K or Keys.L)
            throw new ArgumentException("快捷键不能占用七个琴键");

        return new(key, modifiers);
    }

    public static (string Name, string Text)[] Entries(HotkeyConfig config) =>
    [
        ("自动拾取", config.ToggleAutoPickHotkey),
        ("自动演奏", config.ToggleMusicHotkey),
        ("上一首", config.PreviousSongHotkey),
        ("下一首", config.NextSongHotkey),
        ("谱面启停", config.ToggleMusicTimelineHotkey),
        ("悬浮窗显示", config.ToggleMusicOverlayHotkey)
    ];

    public static void Validate(HotkeyConfig config)
    {
        var seen = new HashSet<MusicHotkey>();
        foreach (var entry in Entries(config))
        {
            if (!seen.Add(Parse(entry.Text)))
                throw new ArgumentException("快捷键重复：" + entry.Name);
        }
    }
}
