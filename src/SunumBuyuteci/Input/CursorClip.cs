using System.Runtime.InteropServices;
using System.Windows.Threading;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Input;

/// <summary>Keeps the mouse cursor inside the primary monitor; always releases the clip on exit.</summary>
public sealed class CursorClip : IDisposable
{
    private readonly RECT _bounds;
    private readonly DispatcherTimer _timer;
    private bool _active;

    public CursorClip(RECT bounds)
    {
        _bounds = bounds;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Apply();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Release();
    }

    public void Start()
    {
        _active = true;
        Apply();
        _timer.Start();
    }

    /// <summary>Releases the clip immediately; safe to call from crash handlers.</summary>
    public void Release()
    {
        _active = false;
        ClipCursor(IntPtr.Zero);
    }

    private void Apply()
    {
        if (!_active) return;
        var r = _bounds;
        ClipCursor(ref r);
    }

    public void Dispose()
    {
        _timer.Stop();
        Release();
    }

    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursor(IntPtr rect);
}
