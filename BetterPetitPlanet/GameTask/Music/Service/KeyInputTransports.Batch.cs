using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed partial class KeyInputTransports
{
    private readonly Dictionary<string, (IntPtr Target, int X, int Y, POINT? Restore)> _mouseOwned = [];

    public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
    {
        if (commands.Count == 0) return [];
        if (mode == MusicInputMode.Mouse) return commands.Select(command => SubmitMouse(command, target)).ToArray();
        // Releases deliberately bypass the foreground check: only the scheduler's owned keys reach this method.
        var allowed = commands.Select((command, index) => (command, index))
            .Where(item => !item.command.IsDown || _keyboardNative.IsTargetForeground(target)).ToArray();
        var results = commands.Select(command => new MusicInputResult(command, false, "游戏未处于前台，已阻止自动输入。")).ToArray();
        if (allowed.Length == 0) return results;
        var submission = _keyboardNative.Send(allowed.Select(item => item.command).ToArray());
        for (int i = 0; i < allowed.Length; i++)
        {
            bool success = i < submission.SubmittedCount;
            results[allowed[i].index] = new(allowed[i].command, success, success ? null :
                $"SendInput failed ({submission.ErrorCode}); submitted {submission.SubmittedCount}/{allowed.Length}.");
        }
        return results;
    }

    private static bool IsTargetForeground(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        var foreground = User32.GetForegroundWindow();
        return (IntPtr)foreground == target || (IntPtr)User32.GetAncestor(foreground, User32.GetAncestorFlag.GA_ROOT) == target;
    }

    private MusicInputResult SubmitMouse(MusicKeyCommand command, IntPtr target)
    {
        string key = command.Note.Key;
        if (command.IsDown)
        {
            if (!IsTargetForeground(target) || !User32.GetClientRect(target, out var rect) ||
                !_coordinateService.TryGetNoteCoordinate(key, rect.Width, rect.Height, out int x, out int y))
                return new(command, false, "鼠标输入目标不可用。");
            if (_mouseOwned.Count > 0) return new(command, false, "鼠标无法执行重叠音符，请切换键盘输入。");
            POINT? original = null;
            if (_configService?.Config.Music.RestoreMouseAfterClick == true && User32.GetCursorPos(out var point)) original = point;
            var screen = new POINT(x, y);
            if (!User32.ClientToScreen(target, ref screen) || !User32.SetCursorPos(screen.X, screen.Y))
                return new(command, false, "无法定位鼠标音符。");
            if (!User32.PostMessage(target, (uint)User32.WindowMessage.WM_LBUTTONDOWN, (IntPtr)1, (IntPtr)((y << 16) | (x & 0xffff))))
                return new(command, false, "鼠标按下提交失败。");
            _mouseOwned[key] = (target, x, y, original);
            return new(command, true);
        }
        if (!_mouseOwned.TryGetValue(key, out var owned)) return new(command, false, "鼠标按键不属于此会话。");
        if (!User32.PostMessage(owned.Target, (uint)User32.WindowMessage.WM_LBUTTONUP, IntPtr.Zero, (IntPtr)((owned.Y << 16) | (owned.X & 0xffff))))
            return new(command, false, "鼠标释放提交失败。");
        _mouseOwned.Remove(key);
        if (owned.Restore is { } restore && IsTargetForeground(owned.Target)) User32.SetCursorPos(restore.X, restore.Y);
        return new(command, true);
    }
}
