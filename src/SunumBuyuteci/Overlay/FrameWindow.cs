using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SunumBuyuteci.Magnifier;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Overlay;

/// <summary>
/// Native click-through window shaped (SetWindowRgn) as a thin frame just outside the visible
/// source rectangle on the primary screen. All coordinates are physical pixels.
/// </summary>
public sealed class FrameWindow : IDisposable
{
    private const string ClassName = "SunumBuyuteciFrame";
    private const int Thickness = 3;
    private const double HideBelowZoom = 1.01;
    private const uint WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84;
    private const int HTTRANSPARENT = -1;
    private const int RGN_DIFF = 4;
    private static readonly uint FollowColor = Rgb(0x2F, 0x80, 0xED);
    private static readonly uint LockedColor = Rgb(0xF2, 0x99, 0x4A);

    private readonly ViewController _view;
    private readonly MagnifierOutput _output;
    private readonly RECT _screen;
    private readonly WndProc _wndProc; // field keeps the delegate alive
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd, _hInstance, _brush;
    private bool _classRegistered, _visible;
    private RECT _lastRect;
    private bool _lastLocked;

    public FrameWindow(ViewController view, MagnifierOutput output)
    {
        _view = view;
        _output = output;
        _screen = output.FullSource;
        _wndProc = WindowProc;
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_visible) NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
    }

    public IntPtr Handle => _hwnd;

    public void Start()
    {
        _hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = _hInstance,
            lpszClassName = ClassName,
        };
        if (NativeMethods.RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT,
            ClassName, "Sunum Büyüteci Çerçeve", NativeMethods.WS_POPUP,
            _screen.Left, _screen.Top, 1, 1, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);

        _lastLocked = !_view.IsLocked; // force the first brush creation
        _view.RectChanged += _ => Update();
        _view.Changed += _ => Update();
        _topmostTimer.Start();
        Update();
    }

    private void Update()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_output.ZoomLevel < HideBelowZoom)
        {
            if (_visible)
            {
                NativeMethods.ShowWindow(_hwnd, 0);
                _visible = false;
            }
            return;
        }

        var r = _view.CurrentRect;
        bool locked = _view.IsLocked;
        bool rectSame = _visible && r.Left == _lastRect.Left && r.Top == _lastRect.Top &&
                        r.Right == _lastRect.Right && r.Bottom == _lastRect.Bottom;
        if (rectSame && locked == _lastLocked) return;

        if (locked != _lastLocked)
        {
            if (_brush != IntPtr.Zero) DeleteObject(_brush);
            _brush = CreateSolidBrush(locked ? LockedColor : FollowColor);
            _lastLocked = locked;
        }

        // Outer window = rect inflated by the border, clipped to the screen; region = outer minus the rect.
        int ol = Math.Max(_screen.Left, r.Left - Thickness), ot = Math.Max(_screen.Top, r.Top - Thickness);
        int or = Math.Min(_screen.Right, r.Right + Thickness), ob = Math.Min(_screen.Bottom, r.Bottom + Thickness);
        var outer = CreateRectRgn(0, 0, or - ol, ob - ot);
        var inner = CreateRectRgn(r.Left - ol, r.Top - ot, r.Right - ol, r.Bottom - ot);
        CombineRgn(outer, outer, inner, RGN_DIFF);
        DeleteObject(inner);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, ol, ot, or - ol, ob - ot,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        SetWindowRgn(_hwnd, outer, true); // the system owns the region afterwards
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        _visible = true;
        _lastRect = r;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_ERASEBKGND)
        {
            if (_brush != IntPtr.Zero && GetClientRect(hWnd, out var rc)) FillRect(wParam, ref rc, _brush);
            return new IntPtr(1);
        }
        if (msg == WM_NCHITTEST) return new IntPtr(HTTRANSPARENT);
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _topmostTimer.Stop();
        if (_hwnd != IntPtr.Zero) { NativeMethods.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_classRegistered) { NativeMethods.UnregisterClass(ClassName, _hInstance); _classRegistered = false; }
        if (_brush != IntPtr.Zero) { DeleteObject(_brush); _brush = IntPtr.Zero; }
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16)); // COLORREF

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
}
