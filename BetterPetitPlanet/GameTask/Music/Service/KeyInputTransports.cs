using System;
using System.Drawing;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class KeyInputTransports
{
    private readonly InstrumentCoordinateService _coordinateService;
    private readonly IConfigService? _configService;

    public KeyInputTransports(InstrumentCoordinateService? coordinateService = null, IConfigService? configService = null)
    {
        _coordinateService = coordinateService ?? new InstrumentCoordinateService();
        _configService = configService;
    }

    public void SendKey(string keyName, int holdDurationMs, IntPtr targetHwnd, MusicInputMode inputMode)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return;
        }

        if (inputMode == MusicInputMode.Mouse && targetHwnd != IntPtr.Zero)
        {
            User32.GetClientRect(targetHwnd, out var clientRect);
            int cw = clientRect.Width;
            int ch = clientRect.Height;

            if (_coordinateService.TryGetNoteCoordinate(keyName, cw, ch, out int cx, out int cy))
            {
                bool restoreMouse = _configService?.Config.Music.RestoreMouseAfterClick ?? false;
                _ = SendMouseClickAsync(targetHwnd, cx, cy, holdDurationMs, restoreMouse);
                return;
            }
        }

        // 默认与回退：前台 SendInput 键盘按键
        _ = HardwareInputSimulator.SendKeyPressAsync(keyName, holdDurationMs);
    }

    /// <summary>
    /// 发送鼠标音符点击：将光标移动至目标音符并点击。
    /// 若 restoreMouse 为 true，则在点击后将光标瞬移回原位；若为 false（默认），光标停留在游戏内连续演奏。
    /// </summary>
    public static async Task SendMouseClickAsync(IntPtr targetHwnd, int clientX, int clientY, int holdDurationMs, bool restoreMouse)
    {
        if (targetHwnd == IntPtr.Zero) return;

        POINT origPos = default;
        if (restoreMouse)
        {
            User32.GetCursorPos(out origPos);
        }

        POINT screenPt = new POINT(clientX, clientY);
        User32.ClientToScreen(targetHwnd, ref screenPt);

        User32.SetCursorPos(screenPt.X, screenPt.Y);
        await Task.Delay(6);

        int lparam = (clientY << 16) | (clientX & 0xFFFF);
        User32.PostMessage(targetHwnd, (uint)User32.WindowMessage.WM_LBUTTONDOWN, (IntPtr)1, (IntPtr)lparam);

        int hold = Math.Max(holdDurationMs, 40);
        await Task.Delay(hold);
        User32.PostMessage(targetHwnd, (uint)User32.WindowMessage.WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lparam);

        if (restoreMouse)
        {
            User32.SetCursorPos(origPos.X, origPos.Y);
        }
    }
}
