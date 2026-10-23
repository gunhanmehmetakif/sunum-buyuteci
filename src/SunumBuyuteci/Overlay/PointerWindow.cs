using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SunumBuyuteci.Magnifier;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Overlay;

/// <summary>Display names and size factors of the pointer styles.</summary>
public static class PointerInfo
{
    public static readonly PointerStyle[] Styles = Enum.GetValues<PointerStyle>();
    public static readonly PointerSize[] Sizes = Enum.GetValues<PointerSize>();

    public static string Name(PointerStyle style) => style switch
    {
        PointerStyle.Normal => "Normal",
        PointerStyle.Hand => "El",
        PointerStyle.BigArrow => "Büyük ok",
        PointerStyle.Laser => "Lazer",
        PointerStyle.Ring => "Halka",
        PointerStyle.Crosshair => "Nişangah",
        PointerStyle.Hidden => "Gizli",
        _ => style.ToString(),
    };

    public static string Name(PointerSize size) => size switch
    {
        PointerSize.Small => "Küçük",
        PointerSize.Medium => "Orta",
        PointerSize.Large => "Büyük",
        _ => size.ToString(),
    };

    /// <summary>Icon height relative to the host (presentation screen) height.</summary>
    public static double HeightFactor(PointerSize size) => size switch
    {
        PointerSize.Small => 0.035,
        PointerSize.Large => 0.07,
        _ => 0.05,
    };
}

/// <summary>
/// Native click-through layered window on the host monitor that draws the pointer icon at the mirrored
/// cursor position (per-pixel alpha). The icon bitmap is rendered with WPF only when style or size change.
/// All coordinates are physical pixels.
/// </summary>
public sealed class PointerWindow : IDisposable
{
    private const string ClassName = "SunumBuyuteciPointer";
    private const double Canvas = 128; // icon design space; the glyph is ~100 units tall, the rest is shadow/glow margin
    private const double GlyphUnits = 100;
    private const int HTTRANSPARENT = -1;
    private const int TopmostEveryTicks = 60;

    private static readonly Brush Dark = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x30)));
    private static readonly Dictionary<(PointerStyle, int), BitmapSource> PreviewCache = new();

    private readonly MagnifierOutput _output;
    private readonly Func<bool> _suppress;
    private readonly WndProc _wndProc; // field keeps the delegate alive
    private IntPtr _hwnd, _hInstance, _memDc, _dib, _oldBitmap;
    private bool _classRegistered, _visible, _shownOnce, _dirty = true;
    private PointerStyle _style;
    private PointerSize _size;
    private int _width, _height, _hotX, _hotY, _x, _y, _ticks;

    public PointerWindow(MagnifierOutput output, Func<bool> suppress, PointerStyle style, PointerSize size)
    {
        _output = output;
        _suppress = suppress;
        _style = style;
        _size = size;
        _wndProc = WindowProc;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Raised the first time the window is shown; the mirror's filter list only honors it from then on.</summary>
    public event Action? Shown;

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
            ClassName, "Sunum Büyüteci İşaretçi", NativeMethods.WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        _output.Tick += OnTick;
    }

    public void SetStyle(PointerStyle style)
    {
        if (style == _style) return;
        _style = style;
        _dirty = true;
        if (!IsDrawn) Hide();
    }

    public void SetSize(PointerSize size)
    {
        if (size == _size) return;
        _size = size;
        _dirty = true;
    }

    private bool IsDrawn => _style is not (PointerStyle.Normal or PointerStyle.Hidden);

    private void OnTick()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!IsDrawn || _suppress() || !NativeMethods.GetCursorPos(out var cursor) ||
            !_output.TryMapToHost(cursor, out var host))
        {
            Hide();
            return;
        }

        bool resized = false;
        if (_dirty)
        {
            Rebuild();
            resized = true;
        }
        int x = host.X - _hotX, y = host.Y - _hotY;
        if (_visible && !resized && x == _x && y == _y)
        {
            if (++_ticks >= TopmostEveryTicks) Raise();
            return;
        }
        _ticks = 0;
        _x = x;
        _y = y;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _visible = true;
        if (!_shownOnce)
        {
            _shownOnce = true;
            Shown?.Invoke();
        }
    }

    private void Raise() => NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    private void Hide()
    {
        if (!_visible) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _visible = false;
    }

    /// <summary>Renders the current style/size into a premultiplied BGRA DIB and hands it to the layered window.</summary>
    private void Rebuild()
    {
        _dirty = false;
        int height = Math.Max(12, (int)Math.Round(_output.HostHeight * PointerInfo.HeightFactor(_size)));
        int side = (int)Math.Ceiling(height * Canvas / GlyphUnits);
        var bmp = Render(_style, side, out var hot);
        int stride = side * 4;
        var pixels = new byte[stride * side];
        bmp.CopyPixels(pixels, stride, 0);

        FreeBitmap();
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = side, biHeight = -side,
                biPlanes = 1, biBitCount = 32,
            };
            _dib = NativeMethods.CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);

            _width = _height = side;
            _hotX = (int)Math.Round(hot.X);
            _hotY = (int)Math.Round(hot.Y);
            var dst = new POINT { X = _x, Y = _y };
            var src = new POINT();
            var size = new SIZE { cx = side, cy = side };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            NativeMethods.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void FreeBitmap()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldBitmap);
            NativeMethods.DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        _memDc = _dib = _oldBitmap = IntPtr.Zero;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        msg == NativeMethods.WM_NCHITTEST ? (IntPtr)HTTRANSPARENT : NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    public void Dispose()
    {
        _output.Tick -= OnTick;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        FreeBitmap();
        if (_classRegistered)
        {
            NativeMethods.UnregisterClass(ClassName, _hInstance);
            _classRegistered = false;
        }
    }

    /// <summary>Small icon for menus and buttons; same drawing as the overlay. UI thread only.</summary>
    public static ImageSource RenderPreview(PointerStyle style, int pixels = 48)
    {
        if (!PreviewCache.TryGetValue((style, pixels), out var bmp))
            PreviewCache[(style, pixels)] = bmp = Render(style, pixels, out _, preview: true);
        return bmp;
    }

    private static BitmapSource Render(PointerStyle style, int side, out Point hotspot, bool preview = false)
    {
        double s = side / Canvas;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(s, s));
            Draw(dc, style, preview);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        var h = Hotspot(style);
        hotspot = new Point(h.X * s, h.Y * s);
        return rtb;
    }

    private static Point Hotspot(PointerStyle style) => style switch
    {
        PointerStyle.Hand => new Point(54.5, 10),
        PointerStyle.BigArrow or PointerStyle.Normal => new Point(ArrowOrigin.X, ArrowOrigin.Y),
        _ => new Point(Canvas / 2, Canvas / 2),
    };

    private static readonly Point ArrowOrigin = new(18, 14);

    private static void Draw(DrawingContext dc, PointerStyle style, bool preview)
    {
        switch (style)
        {
            case PointerStyle.Hand: DrawHand(dc); break;
            case PointerStyle.BigArrow: DrawArrow(dc, Brushes.White, Dark); break;
            case PointerStyle.Normal: DrawArrow(dc, Brushes.Black, Brushes.White); break;
            case PointerStyle.Laser: DrawLaser(dc); break;
            case PointerStyle.Ring: DrawRing(dc); break;
            case PointerStyle.Crosshair: DrawCrosshair(dc); break;
            case PointerStyle.Hidden: DrawHidden(dc); break;
        }
    }

    private static Pen OutlinePen(Brush brush, double width) =>
        Frozen(new Pen(brush, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    /// <summary>Soft drop shadow: a few translucent, progressively thinner strokes plus a fill, offset down-right.</summary>
    private static void DrawShadow(DrawingContext dc, Geometry shape)
    {
        dc.PushTransform(new TranslateTransform(2.5, 3.5));
        var black = Color.FromArgb(0x16, 0, 0, 0);
        foreach (double w in new[] { 11.0, 8.0, 5.0 })
            dc.DrawGeometry(null, OutlinePen(new SolidColorBrush(black), w), shape);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, shape);
        dc.Pop();
    }

    private static RectangleGeometry RoundRect(double x, double y, double w, double h, double r) =>
        new(new Rect(x, y, w, h), r, r);

    private static void DrawHand(DrawingContext dc)
    {
        var thumb = RoundRect(-9, -36, 18, 50, 9);
        thumb.Transform = new TransformGroup
        {
            Children = { new RotateTransform(-32), new TranslateTransform(47, 94) },
        };
        Geometry hand = RoundRect(46, 10, 17, 70, 8.5);
        foreach (var part in new Geometry[]
        {
            RoundRect(62, 34, 17, 52, 8.5),
            RoundRect(78, 41, 17, 46, 8.5),
            RoundRect(94, 50, 16, 38, 8),
            RoundRect(46, 58, 64, 54, 16),
            thumb,
        })
            hand = Geometry.Combine(hand, part, GeometryCombineMode.Union, null);
        hand.Freeze();

        DrawShadow(dc, hand);
        dc.DrawGeometry(Brushes.White, OutlinePen(Dark, 4.5), hand);
        // Redraw the fill over the inner half of the stroke so the outline reads as an even border, then the finger creases.
        dc.DrawGeometry(Brushes.White, null, hand);
        var crease = OutlinePen(new SolidColorBrush(Color.FromArgb(0xB0, 0x1B, 0x23, 0x30)), 2);
        dc.DrawLine(crease, new Point(62.5, 66), new Point(62.5, 82));
        dc.DrawLine(crease, new Point(78.5, 70), new Point(78.5, 84));
        dc.DrawLine(crease, new Point(94.5, 72), new Point(94.5, 85));
        dc.DrawLine(crease, new Point(50, 84), new Point(38, 78));
    }

    private static Geometry ArrowGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
        foreach (var p in new[] { new Point(0, 74), new Point(17, 58), new Point(30, 88), new Point(43, 82), new Point(30, 53), new Point(53, 53) })
            figure.Segments.Add(new LineSegment(p, true));
        var geo = new PathGeometry(new[] { figure });
        geo.Transform = new TransformGroup { Children = { new ScaleTransform(1.12, 1.12), new TranslateTransform(ArrowOrigin.X, ArrowOrigin.Y) } };
        var flat = geo.GetFlattenedPathGeometry();
        flat.Freeze();
        return flat;
    }

    private static void DrawArrow(DrawingContext dc, Brush fill, Brush outline)
    {
        var arrow = ArrowGeometry();
        DrawShadow(dc, arrow);
        dc.DrawGeometry(fill, OutlinePen(outline, 5), arrow);
        dc.DrawGeometry(fill, null, arrow);
    }

    private static void DrawLaser(DrawingContext dc)
    {
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xF4, 0xF0), 0.0),
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0x30, 0x30), 0.16),
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0x00, 0x00), 0.30),
                new GradientStop(Color.FromArgb(0xB0, 0xFF, 0x00, 0x00), 0.42),
                new GradientStop(Color.FromArgb(0x50, 0xFF, 0x10, 0x10), 0.65),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0x10, 0x10), 1.0),
            },
        };
        glow.Freeze();
        dc.DrawEllipse(glow, null, new Point(Canvas / 2, Canvas / 2), 62, 62);
    }

    private static void DrawRing(DrawingContext dc)
    {
        var disc = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x70, 0xFF, 0xD8, 0x00), 0.0),
                new GradientStop(Color.FromArgb(0x78, 0xFF, 0xD8, 0x00), 0.72),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xD8, 0x00), 1.0),
            },
        };
        disc.Freeze();
        dc.DrawEllipse(disc, null, new Point(Canvas / 2, Canvas / 2), 56, 56);
    }

    private static void DrawCrosshair(DrawingContext dc)
    {
        const double c = Canvas / 2;
        var lines = new[]
        {
            (new Point(c, 10), new Point(c, 46)), (new Point(c, 82), new Point(c, 118)),
            (new Point(10, c), new Point(46, c)), (new Point(82, c), new Point(118, c)),
        };
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 9);
        dc.PushTransform(new TranslateTransform(2, 3));
        foreach (var (a, b) in lines) dc.DrawLine(shadowPen, a, b);
        dc.DrawEllipse(null, shadowPen, new Point(c, c), 22, 22);
        dc.Pop();

        var darkPen = new Pen(Dark, 8);
        var whitePen = new Pen(Brushes.White, 4);
        foreach (var (a, b) in lines) dc.DrawLine(darkPen, a, b);
        dc.DrawEllipse(null, darkPen, new Point(c, c), 22, 22);
        foreach (var (a, b) in lines) dc.DrawLine(whitePen, a, b);
        dc.DrawEllipse(null, whitePen, new Point(c, c), 22, 22);
        dc.DrawEllipse(Dark, null, new Point(c, c), 5, 5);
        dc.DrawEllipse(Brushes.White, null, new Point(c, c), 3, 3);
    }

    /// <summary>Menu glyph only: a dashed circle with a slash.</summary>
    private static void DrawHidden(DrawingContext dc)
    {
        var gray = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        var dashed = new Pen(gray, 7) { DashStyle = new DashStyle(new[] { 1.2, 1.2 }, 0), DashCap = PenLineCap.Round };
        dc.DrawEllipse(null, dashed, new Point(Canvas / 2, Canvas / 2), 44, 44);
        dc.DrawLine(new Pen(gray, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            new Point(26, 102), new Point(102, 26));
    }

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
