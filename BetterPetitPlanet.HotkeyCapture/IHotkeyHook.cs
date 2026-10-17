using System;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterPetitPlanet.HotkeyCapture;

public enum HotkeyUnregisterResult
{
    Unregistered,
    NotRegistered,
    Unknown
}

public interface IHotkeyHook : IDisposable
{
    event EventHandler<KeyPressedEventArgs>? KeyPressed;
    void RegisterHotKey(User32.HotKeyModifiers modifier, Keys key);
    HotkeyUnregisterResult UnregisterHotKey(User32.HotKeyModifiers modifier, Keys key);
    void UnregisterHotKey();
}
