using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Magnifier;

/// <summary>
/// Native topmost host window on the target monitor with a Magnification API child
/// that mirrors a rectangle of the primary screen. All coordinates are physical pixels.
/// </summary>
public sealed class MagnifierOutput : IDisposable
{
    private const string HostClassName = "SunumBuyuteciMirrorHost";

    private readonly RECT _hostBounds;
    private readonly RECT _fullSource;
    private readonly WndProc _wndProc = static (h, m, w, l) => NativeMethods.DefWindowProc(h, m, w, l); // field keeps the delegate alive
    private readonly HashSet<IntPtr> _excluded = new();
    private readonly DispatcherTimer _timer;
    private IntPtr _host, _magnifier, _hInstance;
    private bool _classRegistered;
    private RECT _source;
    private double _zoom = 1.0;

    /// <param name="hostBounds">Screen rectangle the output window covers (target monitor, or the test window).</param>
    /// <param name="fullSource">Full source area (primary monitor bounds) shown at 1x.</param>
    public MagnifierOutput(RECT hostBounds, RECT fullSource)
    {
        _hostBounds = hostBounds;
        _fullSource = fullSource;
        _source = fullSource;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            Tick?.Invoke();
            Refresh();
        };
    }

    /// <summary>Raised every timer tick, just before the source rect is pushed to the magnifier.</summary>
    public event Action? Tick;

    public IntPtr HostHandle => _host;
    public int HostWidth => _hostBounds.Width;
    public int HostHeight => _hostBounds.Height;
    public RECT FullSource => _fullSource;

    /// <summary>Current zoom factor relative to the full source area.</summary>
    public double ZoomLevel => _zoom;

    public void Start()
    {
        _hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = _hInstance,
            hbrBackground = NativeMethods.GetStockObject(NativeMethods.BLACK_BRUSH),
            lpszClassName = HostClassName,
        };
        if (NativeMethods.RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;

        _host = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT,
            HostClassName, "Sunum Büyüteci Çıkış", NativeMethods.WS_POPUP,
            _hostBounds.Left, _hostBounds.Top, _hostBounds.Width, _hostBounds.Height,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_host == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        NativeMethods.SetLayeredWindowAttributes(_host, 0, 255, NativeMethods.LWA_ALPHA);

        _magnifier = NativeMethods.CreateWindowEx(0, Magnification.WC_MAGNIFIER, "Magnifier",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | Magnification.MS_SHOWMAGNIFIEDCURSOR,
            0, 0, _hostBounds.Width, _hostBounds.Height, _host, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_magnifier == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        ApplyFilter();
        ApplyLayout();
        Refresh();
        NativeMethods.ShowWindow(_host, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(_host, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        _timer.Start();
    }

    /// <summary>Sets the screen rectangle to mirror; the child is letterboxed if its aspect differs from the host.</summary>
    public void SetSourceRect(RECT source)
    {
        if (source.Width <= 0 || source.Height <= 0) return;
        bool sizeChanged = source.Width != _source.Width || source.Height != _source.Height;
        _source = source;
        _zoom = (double)_fullSource.Width / source.Width;
        if (sizeChanged && _magnifier != IntPtr.Zero) ApplyLayout();
    }

    /// <summary>Convenience: zoom around a center point (screen pixels), clamped inside the full source.</summary>
    public void SetView(double zoom, double centerX, double centerY)
    {
        zoom = Math.Max(1.0, zoom);
        int w = (int)Math.Round(_fullSource.Width / zoom);
        int h = (int)Math.Round(_fullSource.Height / zoom);
        int left = (int)Math.Round(centerX - w / 2.0);
        int top = (int)Math.Round(centerY - h / 2.0);
        left = Math.Clamp(left, _fullSource.Left, _fullSource.Right - w);
        top = Math.Clamp(top, _fullSource.Top, _fullSource.Bottom - h);
        SetSourceRect(new RECT(left, top, left + w, top + h));
    }

    /// <summary>Windows that must not appear in the mirror (frame overlay, number overlays, ...).</summary>
    public void SetExcludedWindows(IEnumerable<IntPtr> windows)
    {
        _excluded.Clear();
        foreach (var w in windows) if (w != IntPtr.Zero) _excluded.Add(w);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_magnifier == IntPtr.Zero) return;
        var list = new HashSet<IntPtr>(_excluded) { _host }.ToArray();
        Magnification.MagSetWindowFilterList(_magnifier, Magnification.MW_FILTERMODE_EXCLUDE, list.Length, list);
    }

    /// <summary>Fits the source aspect inside the host (black bars) and sets scale = childWidth / sourceWidth.</summary>
    private void ApplyLayout()
    {
        int hw = _hostBounds.Width, hh = _hostBounds.Height;
        double fit = Math.Min((double)hw / _source.Width, (double)hh / _source.Height);
        int cw = Math.Max(1, (int)Math.Round(_source.Width * fit));
        int ch = Math.Max(1, (int)Math.Round(_source.Height * fit));
        NativeMethods.MoveWindow(_magnifier, (hw - cw) / 2, (hh - ch) / 2, cw, ch, true);
        var t = MAGTRANSFORM.Scale((float)cw / _source.Width);
        Magnification.MagSetWindowTransform(_magnifier, ref t);
    }

    private void Refresh()
    {
        if (_magnifier == IntPtr.Zero) return;
        Magnification.MagSetWindowSource(_magnifier, _source);
        NativeMethods.InvalidateRect(_magnifier, IntPtr.Zero, true);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_host != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_host); // also destroys the child
            _host = _magnifier = IntPtr.Zero;
        }
        if (_classRegistered)
        {
            NativeMethods.UnregisterClass(HostClassName, _hInstance);
            _classRegistered = false;
        }
    }
}
