using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask;

public readonly record struct KeyIdentity(ushort Scancode, ushort Vk)
{
    public static KeyIdentity Normalize(ushort scancode, ushort vk)
    {
        if (scancode == 0 && vk != 0)
        {
            scancode = (ushort)(User32.MapVirtualKey(vk, User32.MAPVK.MAPVK_VK_TO_VSC) & 0xFF);
        }
        else if (scancode != 0 && vk == 0)
        {
            vk = (ushort)(User32.MapVirtualKey(scancode, User32.MAPVK.MAPVK_VSC_TO_VK) & 0xFFFF);
        }
        return new KeyIdentity(scancode, vk);
    }
}

public static class HardwareInputSimulator
{
    public static nuint MusicInputMarker => 0x4250504D;
    private static ILogger? _logger;

    /// <summary>
    /// 全局前台焦点硬守护委托：当且仅当返回 true 时才允许执行 SendInput 模拟按键按下，杜绝字符泄漏至本程序或其他应用
    /// </summary>
    public static Func<bool>? ForegroundGuard { get; set; }

    /// <summary>
    /// 物理按键守护委托：返回 true 表示用户本人正在物理按住该按键，安全释放机制将不会强行发送 KeyUp 打断用户物理操作
    /// </summary>
    public static Func<ushort, ushort, bool>? PhysicalKeyGuard { get; set; }

    /// <summary>
    /// 程序当前通过模拟器成功按下、尚未释放的按键集合（Held Keys Tracker）
    /// </summary>
    private static readonly ConcurrentDictionary<KeyIdentity, byte> _heldKeys = new();

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    public static bool IsKeyHeld(ushort scancode, ushort vk)
    {
        var key = KeyIdentity.Normalize(scancode, vk);
        if (_heldKeys.ContainsKey(key)) return true;
        foreach (var held in _heldKeys.Keys)
        {
            if (key.Scancode != 0 && held.Scancode == key.Scancode) return true;
            if (key.Vk != 0 && held.Vk == key.Vk) return true;
        }
        return false;
    }

    public static void RecordKeyDown(ushort scancode, ushort vk)
    {
        if (scancode == 0 && vk == 0) return;
        var key = KeyIdentity.Normalize(scancode, vk);
        _heldKeys.TryAdd(key, 0);
    }

    public static void RecordKeyUp(ushort scancode, ushort vk)
    {
        if (scancode == 0 && vk == 0) return;
        var key = KeyIdentity.Normalize(scancode, vk);
        TryRemoveHeldKey(key, out _);
    }

    private static bool TryRemoveHeldKey(KeyIdentity key, out KeyIdentity removedKey)
    {
        if (_heldKeys.TryRemove(key, out _))
        {
            removedKey = key;
            return true;
        }
        foreach (var k in _heldKeys.Keys)
        {
            if ((key.Scancode != 0 && k.Scancode == key.Scancode) ||
                (key.Vk != 0 && k.Vk == key.Vk))
            {
                if (_heldKeys.TryRemove(k, out _))
                {
                    removedKey = k;
                    return true;
                }
            }
        }
        removedKey = key;
        return false;
    }

    public static IReadOnlyCollection<KeyIdentity> GetHeldKeys() => _heldKeys.Keys.ToArray();

    public static void ClearHeldKeys() => _heldKeys.Clear();

    public static bool IsUserPhysicallyHolding(ushort scancode, ushort vk)
    {
        if (PhysicalKeyGuard != null && PhysicalKeyGuard(scancode, vk))
        {
            return true;
        }
        if (vk != 0 && IsModifierKey(vk))
        {
            if ((User32.GetAsyncKeyState((ushort)vk) & 0x8000) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsModifierKey(ushort vk) =>
        vk is (ushort)User32.VK.VK_MENU
           or (ushort)User32.VK.VK_LMENU
           or (ushort)User32.VK.VK_RMENU
           or (ushort)User32.VK.VK_CONTROL
           or (ushort)User32.VK.VK_LCONTROL
           or (ushort)User32.VK.VK_RCONTROL
           or (ushort)User32.VK.VK_SHIFT
           or (ushort)User32.VK.VK_LSHIFT
           or (ushort)User32.VK.VK_RSHIFT
           or (ushort)User32.VK.VK_LWIN
           or (ushort)User32.VK.VK_RWIN;

    private static readonly Dictionary<string, ushort> ScancodeTable = new(StringComparer.OrdinalIgnoreCase)
    {
        { "A", 0x1E },
        { "B", 0x30 },
        { "C", 0x2E },
        { "D", 0x20 },
        { "E", 0x12 },
        { "F", 0x21 },
        { "G", 0x22 },
        { "H", 0x23 },
        { "I", 0x17 },
        { "J", 0x24 },
        { "K", 0x25 },
        { "L", 0x26 },
        { "M", 0x32 },
        { "N", 0x31 },
        { "O", 0x18 },
        { "P", 0x19 },
        { "Q", 0x10 },
        { "R", 0x13 },
        { "S", 0x1F },
        { "T", 0x14 },
        { "U", 0x16 },
        { "V", 0x2F },
        { "W", 0x11 },
        { "X", 0x2D },
        { "Y", 0x15 },
        { "Z", 0x2C },
        { "SPACE", 0x39 },
        { "空格", 0x39 },
        { "ESC", 0x01 },
        { "ENTER", 0x1C },
        { "回车", 0x1C },
        { "TAB", 0x0F },
        { "1", 0x02 },
        { "2", 0x03 },
        { "3", 0x04 },
        { "4", 0x05 },
        { "5", 0x06 },
        { "6", 0x07 },
        { "7", 0x08 },
        { "8", 0x09 },
        { "9", 0x0A },
        { "0", 0x0B },
        { "F1", 0x3B },
        { "F2", 0x3C },
        { "F3", 0x3D },
        { "F4", 0x3E },
        { "F5", 0x3F },
        { "F6", 0x40 },
        { "F7", 0x41 },
        { "F8", 0x42 },
        { "F9", 0x43 },
        { "F10", 0x44 },
        { "F11", 0x57 },
        { "F12", 0x58 },
    };

    public static ushort GetScancode(string keyName)
    {
        var trimmed = keyName.Trim();
        if (ScancodeTable.TryGetValue(trimmed, out var scancode))
        {
            return scancode;
        }

        if (Enum.TryParse<User32.VK>("VK_" + trimmed.ToUpperInvariant(), true, out var vk))
        {
            return (ushort)(User32.MapVirtualKey((uint)vk, User32.MAPVK.MAPVK_VK_TO_VSC) & 0xFF);
        }

        return 0;
    }

    public static ushort GetVirtualKey(string keyName)
    {
        var trimmed = keyName.Trim();
        if (string.Equals(trimmed, "SPACE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "空格", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_SPACE;
        }
        if (string.Equals(trimmed, "ENTER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "回车", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_RETURN;
        }
        if (string.Equals(trimmed, "ESC", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_ESCAPE;
        }
        if (string.Equals(trimmed, "TAB", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_TAB;
        }

        if (Enum.TryParse<User32.VK>("VK_" + trimmed.ToUpperInvariant(), true, out var vk))
        {
            return (ushort)vk;
        }

        if (trimmed.Length == 1)
        {
            char c = char.ToUpperInvariant(trimmed[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return (ushort)c;
            }
        }

        return 0;
    }

    public static bool SendKeyDown(string keyName)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return false;
        return SendKey(scancode, vk, isKeyUp: false);
    }

    public static bool SendKeyUp(string keyName)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return false;
        return SendKey(scancode, vk, isKeyUp: true);
    }

    public static bool SendScancode(ushort scancode, bool isKeyUp)
    {
        return SendKey(scancode, 0, isKeyUp);
    }

    public static bool SendKey(ushort scancode, ushort vk, bool isKeyUp)
    {
        var normalizedKey = KeyIdentity.Normalize(scancode, vk);
        bool isForeground = ForegroundGuard == null || ForegroundGuard();

        if (!isKeyUp)
        {
            // 按键按下 (KeyDown)：必须受到前台守护限制，失焦时严禁发送新的按键按下
            if (!isForeground)
            {
                _logger?.LogWarning("HardwareInputSimulator.SendKey: 游戏未处于前台窗口，已拦截按键按下以防止误触 (scancode: 0x{Scancode:X2}, vk: 0x{Vk:X2})", scancode, vk);
                return false;
            }
        }
        else
        {
            // 按键释放 (KeyUp)：
            // 严格区分已有按键的释放操作：如果是本程序此前成功按下的键，即使此时游戏已经失焦，也必须放行释放，绝不能卡键！
            bool wasHeldBySimulator = IsKeyHeld(normalizedKey.Scancode, normalizedKey.Vk);

            if (!isForeground && !wasHeldBySimulator)
            {
                _logger?.LogWarning("HardwareInputSimulator.SendKey: 游戏未处于前台窗口且该按键非本程序持有，已拦截释放 (scancode: 0x{Scancode:X2}, vk: 0x{Vk:X2})", scancode, vk);
                return false;
            }

            // 如果用户当前正在物理按住该按键，绝对不能发送模拟释放打断用户的物理输入
            if (IsUserPhysicallyHolding(normalizedKey.Scancode, normalizedKey.Vk))
            {
                _logger?.LogInformation("HardwareInputSimulator.SendKey: 检测到用户正在物理按住按键 (0x{Scancode:X2}, 0x{Vk:X2})，跳过模拟释放以保护用户物理输入", scancode, vk);
                TryRemoveHeldKey(normalizedKey, out _);
                return true;
            }
        }

        var flags = User32.KEYEVENTF.KEYEVENTF_SCANCODE;
        if (isKeyUp)
        {
            flags |= User32.KEYEVENTF.KEYEVENTF_KEYUP;
        }

        var input = new User32.INPUT
        {
            type = User32.INPUTTYPE.INPUT_KEYBOARD,
            ki = new User32.KEYBDINPUT
            {
                wVk = normalizedKey.Vk,
                wScan = normalizedKey.Scancode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = (IntPtr)MusicInputMarker,
            }
        };

        var sent = User32.SendInput(1, new[] { input }, Marshal.SizeOf<User32.INPUT>());
        if (sent != 1)
        {
            int err = Marshal.GetLastWin32Error();
            _logger?.LogWarning("SendInput failed for scancode 0x{Scancode:X2} (VK 0x{Vk:X2}), sent={Sent}, win32Err={Err}", normalizedKey.Scancode, normalizedKey.Vk, sent, err);
            return false;
        }

        if (!isKeyUp)
        {
            _heldKeys.TryAdd(normalizedKey, 0);
        }
        else
        {
            TryRemoveHeldKey(normalizedKey, out _);
        }

        return true;
    }

    public static async Task SendKeyPressAsync(string keyName, int holdDurationMs)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return;

        if (ForegroundGuard != null && !ForegroundGuard())
        {
            _logger?.LogWarning("HardwareInputSimulator.SendKeyPressAsync: 游戏未处于前台窗口，已拦截按键发送以防止误触");
            return;
        }

        bool downSent = SendKey(scancode, vk, isKeyUp: false);
        if (!downSent) return;

        try
        {
            await Task.Delay(Math.Clamp(holdDurationMs, 20, 500));
        }
        finally
        {
            SendKey(scancode, vk, isKeyUp: true);
        }
    }

    public static async Task SendBackgroundKeyPressAsync(IntPtr hwnd, string keyName, int holdDurationMs)
    {
        if (hwnd == IntPtr.Zero) return;

        var scancode = GetScancode(keyName);
        uint vkCode = 0;
        if (Enum.TryParse<User32.VK>("VK_" + keyName.Trim().ToUpperInvariant(), true, out var vk))
        {
            vkCode = (uint)vk;
        }
        else
        {
            vkCode = User32.MapVirtualKey(scancode, User32.MAPVK.MAPVK_VSC_TO_VK);
        }

        var targetHwnd = new HWND(hwnd);
        var downLParam = (IntPtr)(1 | (scancode << 16));
        var upLParam = (IntPtr)(1 | (scancode << 16) | (1 << 30) | (1 << 31));

        try
        {
            User32.PostMessage(targetHwnd, User32.WindowMessage.WM_KEYDOWN, (IntPtr)vkCode, downLParam);
            await Task.Delay(Math.Clamp(holdDurationMs, 10, 500));
        }
        finally
        {
            User32.PostMessage(targetHwnd, User32.WindowMessage.WM_KEYUP, (IntPtr)vkCode, upLParam);
        }
    }

    /// <summary>
    /// 全局安全释放方法：在演奏停止、暂停、取消、失焦保护触发或异常时，主动将程序持有的所有按键全部释放（KeyUp）。
    /// 严格只释放本程序持有的按键，绝不盲目广播，且若用户当前物理按住则予以保护。
    /// </summary>
    public static int ReleaseAllHeldKeys()
    {
        var held = _heldKeys.Keys.ToArray();
        int releasedCount = 0;

        foreach (var key in held)
        {
            try
            {
                if (SendKey(key.Scancode, key.Vk, isKeyUp: true))
                {
                    releasedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HardwareInputSimulator.ReleaseAllHeldKeys: 释放按键异常 (0x{Scancode:X2}, 0x{Vk:X2})", key.Scancode, key.Vk);
                TryRemoveHeldKey(key, out _);
            }
        }

        // 附带清理任何可能残留处于逻辑卡键状态的修饰键（Alt, Ctrl, Shift）
        ReleaseStuckModifiers();

        if (releasedCount > 0)
        {
            _logger?.LogInformation("HardwareInputSimulator.ReleaseAllHeldKeys: 已成功安全释放 {Count} 个本程序持有的按键", releasedCount);
        }

        return releasedCount;
    }

    /// <summary>
    /// 检查修饰键（Alt, Ctrl, Shift）的系统逻辑状态与物理状态。
    /// 若物理按键并未被用户按下，但系统内部处于按下（卡键）状态，则发送一次 KeyUp 消除卡键。
    /// 绝对不会打断用户当前实际物理按住的按键。
    /// </summary>
    public static void ReleaseStuckModifiers()
    {
        ReleaseModifierIfStuck((ushort)User32.VK.VK_MENU, 0x38);       // Alt
        ReleaseModifierIfStuck((ushort)User32.VK.VK_CONTROL, 0x1D);    // Ctrl
        ReleaseModifierIfStuck((ushort)User32.VK.VK_SHIFT, 0x2A);      // Shift
    }

    private static void ReleaseModifierIfStuck(ushort vk, ushort scancode)
    {
        try
        {
            bool isPhysicalDown = (User32.GetAsyncKeyState((ushort)vk) & 0x8000) != 0;
            if (!isPhysicalDown)
            {
                short keyState = User32.GetKeyState((ushort)vk);
                if (keyState < 0)
                {
                    _logger?.LogWarning("HardwareInputSimulator: 检测到修饰键 (VK: 0x{Vk:X2}) 处于逻辑卡键状态，正在发送安全 KeyUp 释放", vk);
                    var input = new User32.INPUT
                    {
                        type = User32.INPUTTYPE.INPUT_KEYBOARD,
                        ki = new User32.KEYBDINPUT
                        {
                            wVk = vk,
                            wScan = scancode,
                            dwFlags = User32.KEYEVENTF.KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = (IntPtr)MusicInputMarker,
                        }
                    };
                    User32.SendInput(1, new[] { input }, Marshal.SizeOf<User32.INPUT>());
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "HardwareInputSimulator.ReleaseModifierIfStuck 检查修饰键 0x{Vk:X2} 失败，已忽略", vk);
        }
    }
}
