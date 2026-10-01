using Vanara.PInvoke;

namespace BetterPetitPlanet.HotkeyCapture;

public class KeyPressedEventArgs : EventArgs
{
    public User32.HotKeyModifiers Modifier { get; }
    public Keys Key { get; }

    public KeyPressedEventArgs(User32.HotKeyModifiers modifier, Keys key)
    {
        Modifier = modifier;
        Key = key;
    }
}
