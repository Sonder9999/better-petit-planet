using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using Vanara.PInvoke;

namespace BetterPetitPlanet.HotkeyCapture;

public sealed class HotkeyHook : IHotkeyHook
{
    public event EventHandler<KeyPressedEventArgs>? KeyPressed = null;

    private readonly Window window = new();
    private int currentId;
    private readonly Dictionary<(User32.HotKeyModifiers Modifier, Keys Key), int> registeredHotkeys = [];

    private class Window : NativeWindow, IDisposable
    {
        public event EventHandler<KeyPressedEventArgs>? KeyPressed = null;

        public Window()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == (int)User32.WindowMessage.WM_HOTKEY)
            {
                Keys key = (Keys)(((int)m.LParam >> 16) & 0xFFFF);
                User32.HotKeyModifiers modifier = (User32.HotKeyModifiers)((int)m.LParam & 0xFFFF);

                KeyPressed?.Invoke(this, new KeyPressedEventArgs(modifier, key));
            }
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    public HotkeyHook()
    {
        window.KeyPressed += (sender, args) =>
        {
            KeyPressed?.Invoke(this, args);
        };
    }

    public void RegisterHotKey(User32.HotKeyModifiers modifier, Keys key)
    {
        var binding = (modifier, key);
        if (registeredHotkeys.ContainsKey(binding))
        {
            throw new InvalidOperationException("Hotkey is already registered by this hook");
        }

        var id = currentId + 1;
        if (!User32.RegisterHotKey(window!.Handle, id, modifier, (uint)key))
        {
            if (Marshal.GetLastWin32Error() == SystemErrorCodes.ERROR_HOTKEY_ALREADY_REGISTERED)
            {
                throw new InvalidOperationException("Hotkey already registered");
            }
            else
            {
                throw new InvalidOperationException("Hotkey registration failed");
            }
        }

        currentId = id;
        registeredHotkeys.Add(binding, id);
    }

    public HotkeyUnregisterResult UnregisterHotKey(User32.HotKeyModifiers modifier, Keys key)
    {
        var binding = (modifier, key);
        if (!registeredHotkeys.TryGetValue(binding, out var id)) return HotkeyUnregisterResult.NotRegistered;
        if (!User32.UnregisterHotKey(window!.Handle, id)) return HotkeyUnregisterResult.Unknown;

        registeredHotkeys.Remove(binding);
        return HotkeyUnregisterResult.Unregistered;
    }

    public void UnregisterHotKey()
    {
        foreach (var (binding, id) in registeredHotkeys.ToArray())
        {
            if (User32.UnregisterHotKey(window!.Handle, id))
            {
                registeredHotkeys.Remove(binding);
            }
        }
    }

    public void Dispose()
    {
        UnregisterHotKey();
        window?.Dispose();
    }
}
