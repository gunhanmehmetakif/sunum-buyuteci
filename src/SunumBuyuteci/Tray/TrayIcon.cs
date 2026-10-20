using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SunumBuyuteci.Tray;

/// <summary>Notification-area icon with the context menu; raises events, holds no app logic.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly CultureInfo Tr = new("tr-TR");

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _lock, _edge, _center, _clip;
    private Icon? _iconImage;

    public event Action? ZoomInRequested, ZoomOutRequested, ResetRequested, ToggleLockRequested,
        ToggleZoomRequested, ChangeScreenRequested, ShortcutsRequested, ExitRequested;
    public event Action<FollowMode>? FollowModeChanged;
    public event Action<bool>? ClipCursorChanged;

    public TrayIcon()
    {
        _lock = Item("Çerçeveyi kilitle  (Ctrl+Alt+K)", () => ToggleLockRequested?.Invoke());
        _edge = Item("Kenara gelince kaydır", () => FollowModeChanged?.Invoke(FollowMode.Edge));
        _center = Item("Fareyi ortada tut", () => FollowModeChanged?.Invoke(FollowMode.Center));
        _clip = Item("Fareyi bu ekranda tut", () => ClipCursorChanged?.Invoke(!_clip!.Checked));

        var follow = new ToolStripMenuItem("Takip modu");
        follow.DropDownItems.AddRange(new ToolStripItem[] { _edge, _center });

        var menu = new ContextMenuStrip();
        menu.Items.Add(Item("Yakınlaştır  (Ctrl+Alt+↑)", () => ZoomInRequested?.Invoke()));
        menu.Items.Add(Item("Uzaklaştır  (Ctrl+Alt+↓)", () => ZoomOutRequested?.Invoke()));
        menu.Items.Add(Item("Tam ekran (1x)  (Ctrl+Alt+0)", () => ResetRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_lock);
        menu.Items.Add(follow);
        menu.Items.Add(_clip);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Ekranı değiştir…", () => ChangeScreenRequested?.Invoke()));
        menu.Items.Add(Item("Kısayollar", () => ShortcutsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Çıkış  (Ctrl+Alt+Q)", () => ExitRequested?.Invoke()));

        _iconImage = CreateIcon();
        _icon = new NotifyIcon { Icon = _iconImage, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => ToggleZoomRequested?.Invoke();
        SetZoom(1.0);
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    public void SetZoom(double zoom) =>
        _icon.Text = "Sunum Büyüteci – " + zoom.ToString("0.##", Tr) + "x";

    public void SetState(bool locked, FollowMode mode, bool clipCursor)
    {
        _lock.Checked = locked;
        _edge.Checked = mode == FollowMode.Edge;
        _center.Checked = mode == FollowMode.Center;
        _clip.Checked = clipCursor;
    }

    public void ShowBalloon(string text, bool warning = false)
    {
        _icon.BalloonTipTitle = "Sunum Büyüteci";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    /// <summary>Magnifier glass on a blue rounded square; same drawing as app.ico.</summary>
    private static Icon CreateIcon()
    {
        const int size = 32, d = 14;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(size - d, 0, d, d, 270, 90);
            path.AddArc(size - d, size - d, d, d, 0, 90);
            path.AddArc(0, size - d, d, d, 90, 90);
            path.CloseFigure();
            using var bg = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB));
            g.FillPath(bg, path);
            using var ring = new Pen(Color.White, 3);
            g.DrawEllipse(ring, 5.5f, 5.5f, 15, 15);
            using var handle = new Pen(Color.White, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(handle, 18.5f, 18.5f, 26, 26);
        }
        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconImage?.Dispose();
        _iconImage = null;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
