using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SunumBuyuteci.Display;
using SunumBuyuteci.Magnifier;
using SunumBuyuteci.Native;

namespace SunumBuyuteci;

/// <summary>Small control panel (zoom, lock, full screen, settings menu); raises events, holds no app logic.</summary>
public partial class ControlWindow : Window
{
    private const int DefaultTopMarginPx = 20;
    private const uint SWP_NOZORDER = 0x4;
    private static readonly CultureInfo Tr = new("tr-TR");

    private readonly Settings _settings;
    private readonly DispatcherTimer _saveDebounce;
    private readonly MenuItem _edge, _center, _clip, _topmost, _follow;
    private readonly ContextMenu _menu;
    private readonly HashSet<IntPtr> _popupHandles = new();
    private bool _locked, _menuOpen, _positioned, _closing;

    public event Action? ZoomInRequested, ZoomOutRequested, ToggleLockRequested, FullScreenRequested,
        ChangeScreenRequested, ShortcutsRequested, ExitRequested, ExcludedChanged;
    public event Action<FollowMode>? FollowModeChanged;
    public event Action<bool>? ClipCursorChanged, TopmostChanged;

    public ControlWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Topmost = settings.ControlTopmost;

        _edge = Item("Kenara gelince kaydır", () => FollowModeChanged?.Invoke(FollowMode.Edge));
        _center = Item("Fareyi ortada tut", () => FollowModeChanged?.Invoke(FollowMode.Center));
        _clip = Item("Fareyi bu ekranda tut", () => ClipCursorChanged?.Invoke(!_clip!.IsChecked));
        _topmost = Item("Hep üstte", () => TopmostChanged?.Invoke(!_topmost!.IsChecked));
        _follow = new MenuItem { Header = "Takip modu" };
        _follow.Items.Add(_edge);
        _follow.Items.Add(_center);
        _follow.SubmenuOpened += (_, _) => Dispatcher.BeginInvoke(RefreshPopupHandles, DispatcherPriority.Loaded);

        _menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        _menu.Items.Add(_follow);
        _menu.Items.Add(_clip);
        _menu.Items.Add(_topmost);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Ekranı değiştir…", () => ChangeScreenRequested?.Invoke()));
        _menu.Items.Add(Item("Kısayollar", () => ShortcutsRequested?.Invoke()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Çıkış", () => ExitRequested?.Invoke()));
        _menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            Dispatcher.BeginInvoke(RefreshPopupHandles, DispatcherPriority.Loaded);
        };
        _menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _popupHandles.Clear();
            ExcludedChanged?.Invoke();
        };

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SavePosition(); };

        Loaded += (_, _) => PlaceWindow();
        LocationChanged += (_, _) =>
        {
            if (!_positioned || _closing) return;
            _saveDebounce.Stop();
            _saveDebounce.Start();
        };
        Closed += (_, _) =>
        {
            SavePosition();
            if (!_closing) ExitRequested?.Invoke();
        };
        SetState(1.0, false, FollowMode.Edge, true, false);
    }

    private static MenuItem Item(string text, Action onClick)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>The window handle plus any open menu popups; all must be hidden from the mirror.</summary>
    public IEnumerable<IntPtr> ExcludedHandles
    {
        get
        {
            var list = new List<IntPtr>(_popupHandles);
            var h = Handle;
            if (h != IntPtr.Zero) list.Add(h);
            return list;
        }
    }

    /// <summary>True while the cursor is over the window or a menu is open; the view must not follow the mouse then.</summary>
    public bool SuspendsFollow
    {
        get
        {
            if (!IsVisible) return false;
            if (_menuOpen) return true;
            if (WindowState == WindowState.Minimized) return false;
            var h = Handle;
            return h != IntPtr.Zero && NativeMethods.GetCursorPos(out var p) && NativeMethods.GetWindowRect(h, out var r) &&
                   p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }
    }

    public void SetState(double zoom, bool locked, FollowMode mode, bool clipCursor, bool waiting)
    {
        _locked = locked;
        ZoomLabel.Text = waiting ? "Bekleniyor…" : Math.Round(zoom * 100).ToString("0", Tr) + "%";
        ZoomLabel.FontSize = waiting ? 14 : 18;
        ZoomOutButton.IsEnabled = !waiting && zoom > ViewController.ZoomSteps[0] + 0.001;
        ZoomInButton.IsEnabled = !waiting && zoom < ViewController.ZoomSteps[^1] - 0.001;
        LockButton.IsEnabled = FullButton.IsEnabled = !waiting;
        LockButton.IsChecked = locked;
        LockGlyph.Text = locked ? "" : "";
        _edge.IsChecked = mode == FollowMode.Edge;
        _center.IsChecked = mode == FollowMode.Center;
        _clip.IsChecked = clipCursor;
        _topmost.IsChecked = _settings.ControlTopmost;
        Topmost = _settings.ControlTopmost;
    }

    private void RefreshPopupHandles()
    {
        _popupHandles.Clear();
        AddPopupHandle(_menu);
        foreach (var item in _follow.Items) AddPopupHandle(item as FrameworkElement);
        ExcludedChanged?.Invoke();
    }

    private void AddPopupHandle(FrameworkElement? element)
    {
        if (element == null || !element.IsLoaded) return;
        if (PresentationSource.FromVisual(element) is HwndSource src && src.Handle != IntPtr.Zero)
            _popupHandles.Add(src.Handle);
    }

    /// <summary>Restores the saved position (physical pixels) if still on a monitor, else top center of the primary screen.</summary>
    private void PlaceWindow()
    {
        var h = Handle;
        if (h == IntPtr.Zero || !NativeMethods.GetWindowRect(h, out var r)) { _positioned = true; return; }
        var monitors = MonitorService.GetMonitors();
        int x, y;
        if (_settings.ControlLeft is int sl && _settings.ControlTop is int st && IsOnScreen(monitors, sl, st, r.Width))
        {
            x = sl;
            y = st;
        }
        else
        {
            var pb = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First()).Bounds;
            x = pb.Left + (pb.Width - r.Width) / 2;
            y = pb.Top + DefaultTopMarginPx;
        }
        NativeMethods.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | SWP_NOZORDER);
        _positioned = true;
    }

    /// <summary>The title-bar strip must intersect a monitor, otherwise the window could not be dragged back.</summary>
    private static bool IsOnScreen(IEnumerable<MonitorInfo> monitors, int x, int y, int width)
    {
        const int Margin = 40;
        return monitors.Any(m => x + width - Margin > m.Bounds.Left && x + Margin < m.Bounds.Right &&
                                 y >= m.Bounds.Top - 4 && y + Margin < m.Bounds.Bottom);
    }

    private void SavePosition()
    {
        if (!_positioned || WindowState == WindowState.Minimized) return;
        var h = Handle;
        if (h == IntPtr.Zero || !NativeMethods.GetWindowRect(h, out var r)) return;
        if (_settings.ControlLeft == r.Left && _settings.ControlTop == r.Top) return;
        _settings.ControlLeft = r.Left;
        _settings.ControlTop = r.Top;
        _settings.Save();
    }

    /// <summary>Closes without raising <see cref="ExitRequested"/> (the app is already shutting down).</summary>
    public void CloseForExit()
    {
        _closing = true;
        _saveDebounce.Stop();
        SavePosition();
        Close();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomInRequested?.Invoke();
    private void Full_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        ToggleLockRequested?.Invoke();
        LockButton.IsChecked = _locked;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _menu.PlacementTarget = SettingsButton;
        _menu.IsOpen = true;
    }
}
