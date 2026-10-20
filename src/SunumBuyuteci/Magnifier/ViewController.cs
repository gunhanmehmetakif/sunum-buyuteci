using System.Diagnostics;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Magnifier;

public enum ViewChangeReason
{
    Zoom,
    Lock,
}

/// <summary>
/// Owns zoom level, lock state and mouse following; drives <see cref="MagnifierOutput"/> every tick.
/// All coordinates are physical pixels.
/// </summary>
public sealed class ViewController
{
    public static readonly double[] ZoomSteps = { 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };

    private const double ZoomAnimationMs = 150;
    private const double FollowTimeConstantMs = 60;
    private const double EdgeMarginFraction = 0.10;
    private const double AspectBlendRange = 0.25; // zoom range over which the view aspect moves from screen to host

    private readonly MagnifierOutput _output;
    private readonly RECT _primary;
    private readonly double _hostAspect;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _displayZoom, _animFrom;
    private double _animStartMs = -1;
    private double _anchorX, _anchorY, _anchorFx = 0.5, _anchorFy = 0.5;
    private double _left, _top, _lastMs;
    private RECT _rect;

    public ViewController(MagnifierOutput output, FollowMode followMode, double initialZoom = 1.0, (int X, int Y)? initialCenter = null)
    {
        _output = output;
        _primary = output.FullSource;
        _hostAspect = (double)output.HostWidth / output.HostHeight;
        FollowMode = followMode;
        TargetZoom = _displayZoom = Math.Clamp(initialZoom, 1.0, ZoomSteps[^1]);

        var (w, h) = ViewSize(_displayZoom);
        double cx = initialCenter?.X ?? (_primary.Left + _primary.Right) / 2.0;
        double cy = initialCenter?.Y ?? (_primary.Top + _primary.Bottom) / 2.0;
        _left = cx - w / 2;
        _top = cy - h / 2;
        IsLocked = initialCenter.HasValue;
        ClampAndApply(w, h);
        _output.Tick += OnTick;
    }

    public FollowMode FollowMode { get; set; }
    public double TargetZoom { get; private set; }
    public bool IsLocked { get; private set; }
    public RECT CurrentRect => _rect;

    /// <summary>When it returns true (e.g. cursor over the control window) mouse following and cursor anchoring pause.</summary>
    public Func<bool>? SuspendFollow { get; set; }

    /// <summary>Raised when zoom target or lock state changes (not on every view movement).</summary>
    public event Action<ViewChangeReason>? Changed;

    /// <summary>Raised whenever the integer source rect actually changes.</summary>
    public event Action<RECT>? RectChanged;

    public void ZoomIn()
    {
        foreach (var s in ZoomSteps)
            if (s > TargetZoom + 0.001) { SetZoom(s); return; }
    }

    public void ZoomOut()
    {
        for (int i = ZoomSteps.Length - 1; i >= 0; i--)
            if (ZoomSteps[i] < TargetZoom - 0.001) { SetZoom(ZoomSteps[i]); return; }
    }

    public void ResetZoom() => SetZoom(1.0);

    public void ToggleLock()
    {
        IsLocked = !IsLocked;
        Changed?.Invoke(ViewChangeReason.Lock);
    }

    public void SetZoom(double zoom)
    {
        if (Math.Abs(zoom - TargetZoom) < 0.001) return;
        TargetZoom = zoom;
        _animFrom = _displayZoom;
        _animStartMs = _clock.Elapsed.TotalMilliseconds;

        // Keep the point under the cursor fixed (view center when locked) while zooming.
        var (w, h) = ViewSize(_displayZoom);
        if (IsLocked || SuspendFollow?.Invoke() == true || !NativeMethods.GetCursorPos(out var p))
        {
            _anchorFx = _anchorFy = 0.5;
            _anchorX = _left + w / 2;
            _anchorY = _top + h / 2;
        }
        else
        {
            _anchorX = Math.Clamp(p.X, _left, _left + w);
            _anchorY = Math.Clamp(p.Y, _top, _top + h);
            _anchorFx = (_anchorX - _left) / w;
            _anchorFy = (_anchorY - _top) / h;
        }
        Changed?.Invoke(ViewChangeReason.Zoom);
    }

    /// <summary>View size at a zoom level. Aspect blends from the screen's to the host's so zoomed views have no black bars.</summary>
    private (double W, double H) ViewSize(double zoom)
    {
        double pw = _primary.Width, ph = _primary.Height;
        double w = pw / zoom;
        double t = Math.Clamp((zoom - 1.0) / AspectBlendRange, 0, 1);
        double aspect = pw / ph + (_hostAspect - pw / ph) * t;
        double h = w / aspect;
        if (h > ph) h = ph;
        return (w, h);
    }

    private void OnTick()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Clamp(now - _lastMs, 0, 100);
        _lastMs = now;

        bool animating = _animStartMs >= 0;
        if (animating)
        {
            double t = (now - _animStartMs) / ZoomAnimationMs;
            if (t >= 1) { t = 1; _animStartMs = -1; }
            double eased = 1 - Math.Pow(1 - t, 3);
            _displayZoom = t >= 1 ? TargetZoom : _animFrom + (TargetZoom - _animFrom) * eased;
        }

        var (w, h) = ViewSize(_displayZoom);
        if (animating)
        {
            _left = _anchorX - _anchorFx * w;
            _top = _anchorY - _anchorFy * h;
        }
        else if (!IsLocked && SuspendFollow?.Invoke() != true && NativeMethods.GetCursorPos(out var p))
        {
            Follow(p.X, p.Y, w, h, dt);
        }
        ClampAndApply(w, h);
    }

    private void Follow(double cx, double cy, double w, double h, double dt)
    {
        double wantLeft = _left, wantTop = _top;
        if (FollowMode == FollowMode.Center)
        {
            wantLeft = cx - w / 2;
            wantTop = cy - h / 2;
        }
        else
        {
            double mx = w * EdgeMarginFraction, my = h * EdgeMarginFraction;
            if (cx < _left + mx) wantLeft = cx - mx;
            else if (cx > _left + w - mx) wantLeft = cx - w + mx;
            if (cy < _top + my) wantTop = cy - my;
            else if (cy > _top + h - my) wantTop = cy - h + my;
        }
        wantLeft = Math.Clamp(wantLeft, _primary.Left, _primary.Right - w);
        wantTop = Math.Clamp(wantTop, _primary.Top, _primary.Bottom - h);

        double alpha = 1 - Math.Exp(-dt / FollowTimeConstantMs);
        _left = Approach(_left, wantLeft, alpha);
        _top = Approach(_top, wantTop, alpha);
    }

    private static double Approach(double current, double target, double alpha)
    {
        double d = target - current;
        return Math.Abs(d) < 0.25 ? target : current + d * alpha;
    }

    private void ClampAndApply(double w, double h)
    {
        _left = Math.Clamp(_left, _primary.Left, Math.Max(_primary.Left, _primary.Right - w));
        _top = Math.Clamp(_top, _primary.Top, Math.Max(_primary.Top, _primary.Bottom - h));

        int iw = Math.Min((int)Math.Round(w), _primary.Width);
        int ih = Math.Min((int)Math.Round(h), _primary.Height);
        int il = Math.Clamp((int)Math.Round(_left), _primary.Left, _primary.Right - iw);
        int it = Math.Clamp((int)Math.Round(_top), _primary.Top, _primary.Bottom - ih);
        var rect = new RECT(il, it, il + iw, it + ih);
        if (rect.Left == _rect.Left && rect.Top == _rect.Top && rect.Right == _rect.Right && rect.Bottom == _rect.Bottom) return;
        _rect = rect;
        _output.SetSourceRect(rect);
        RectChanged?.Invoke(rect);
    }
}
