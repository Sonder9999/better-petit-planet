using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicNativeSubmission(uint SubmittedCount, int ErrorCode);

/// <summary>Native boundary for music-owned commands; tests never touch desktop input.</summary>
public interface IMusicKeyboardNative
{
    bool IsTargetForeground(IntPtr target);
    MusicNativeSubmission Send(IReadOnlyList<MusicKeyCommand> commands);
}

internal sealed class MusicKeyboardNative : IMusicKeyboardNative
{
    public bool IsTargetForeground(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        var foreground = User32.GetForegroundWindow();
        return (IntPtr)foreground == target ||
            (IntPtr)User32.GetAncestor(foreground, User32.GetAncestorFlag.GA_ROOT) == target;
    }

    public MusicNativeSubmission Send(IReadOnlyList<MusicKeyCommand> commands)
    {
        var inputs = commands.Select(command => new User32.INPUT
        {
            type = User32.INPUTTYPE.INPUT_KEYBOARD,
            ki = new User32.KEYBDINPUT
            {
                wVk = HardwareInputSimulator.GetVirtualKey(command.Note.Key),
                wScan = HardwareInputSimulator.GetScancode(command.Note.Key),
                dwFlags = User32.KEYEVENTF.KEYEVENTF_SCANCODE |
                    (command.IsDown ? 0 : User32.KEYEVENTF.KEYEVENTF_KEYUP),
                dwExtraInfo = (IntPtr)HardwareInputSimulator.MusicInputMarker
            }
        }).ToArray();
        uint sent = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<User32.INPUT>());
        return new(sent, Marshal.GetLastWin32Error());
    }
}
