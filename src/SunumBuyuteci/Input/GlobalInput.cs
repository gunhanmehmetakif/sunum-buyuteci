using System.Runtime.InteropServices;
using System.Windows.Threading;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Input;

/// <summary>
/// Global Ctrl+Alt hotkeys via low-level hooks. RegisterHotKey is avoided because Turkish keyboards
/// send AltGr as LCtrl+RAlt (AltGr+Q types "@"); here the left Alt must be down and the right Alt must not.
/// Must be created on the UI thread.
/// </summary>
public sealed class GlobalInput : IDisposable
{
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_MOUSEWHEEL = 0x20A;
    private const int VK_CONTROL = 0x11, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int VK_NUMPAD0 = 0x60, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D,
        VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD, VK_UP = 0x26, VK_DOWN = 0x28, VK_0 = 0x30, VK_K = 0x4B, VK_Q = 0x51,
        VK_L = 0x4C, VK_G = 0x47;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly HashSet<int> _swallowedKeys = new();
    private IntPtr _keyboardHook, _mouseHook;

    public event Action? ZoomIn, ZoomOut, ResetZoom, ToggleLock, NextPointer, TogglePointerHidden, ExitRequested;

    public GlobalInput()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
    }

    /// <summary>False if either hook could not be installed.</summary>
    public bool IsActive => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    private static bool ComboHeld() =>
        IsDown(VK_CONTROL) && IsDown(VK_LMENU) && !IsDown(VK_RMENU);

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var action = ComboHeld() ? Map(vk) : null;
                if (action != null)
                {
                    bool repeat = !_swallowedKeys.Add(vk);
                    if (!repeat || IsRepeatable(vk)) _dispatcher.BeginInvoke(action);
                    return (IntPtr)1;
                }
            }
            else if ((msg is WM_KEYUP or WM_SYSKEYUP) && _swallowedKeys.Remove(vk))
            {
                return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL && ComboHeld())
        {
            short delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
            if (delta != 0)
            {
                bool up = delta > 0;
                _dispatcher.BeginInvoke(() => (up ? ZoomIn : ZoomOut)?.Invoke());
                return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool IsRepeatable(int vk) =>
        vk is VK_ADD or VK_OEM_PLUS or VK_UP or VK_SUBTRACT or VK_OEM_MINUS or VK_DOWN;

    private Action? Map(int vk) => vk switch
    {
        VK_ADD or VK_OEM_PLUS or VK_UP => () => ZoomIn?.Invoke(),
        VK_SUBTRACT or VK_OEM_MINUS or VK_DOWN => () => ZoomOut?.Invoke(),
        VK_0 or VK_NUMPAD0 => () => ResetZoom?.Invoke(),
        VK_K => () => ToggleLock?.Invoke(),
        VK_L => () => NextPointer?.Invoke(),
        VK_G => () => TogglePointerHidden?.Invoke(),
        VK_Q => () => ExitRequested?.Invoke(),
        _ => null,
    };

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
