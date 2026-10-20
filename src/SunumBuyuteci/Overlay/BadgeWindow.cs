using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SunumBuyuteci.Magnifier;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Overlay;

/// <summary>Small rounded label at the bottom center of the primary screen, shown briefly after a zoom or lock change.</summary>
public sealed class BadgeWindow : Window
{
    private const int WidthDip = 160, HeightDip = 56, BottomMarginDip = 96; // clear of the taskbar and the control window
    private static readonly CultureInfo Tr = new("tr-TR");

    private readonly RECT _screen;
    private readonly ViewController _view;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _hideTimer;
    private IntPtr _hwnd;

    public IntPtr Handle => _hwnd;

    public BadgeWindow(ViewController view, RECT screen)
    {
        _view = view;
        _screen = screen;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = WidthDip;
        Height = HeightDip;
        Opacity = 0;

        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x14, 0x1F)),
            CornerRadius = new CornerRadius(14),
            Child = _text,
        };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThrough(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
            Place();
        };
        view.Changed += OnChanged;
    }

    private void OnChanged(ViewChangeReason reason)
    {
        _text.Text = reason == ViewChangeReason.Lock
            ? (_view.IsLocked ? "Kilitli" : "Takip")
            : FormatZoom(_view.TargetZoom);
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Place();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private static string FormatZoom(double zoom) => zoom.ToString("0.##", Tr) + "x";

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(WidthDip * scale), h = (int)Math.Round(HeightDip * scale);
        int x = _screen.Left + (_screen.Width - w) / 2;
        int y = _screen.Bottom - h - (int)Math.Round(BottomMarginDip * scale);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED) Dispatcher.BeginInvoke(Place);
        return IntPtr.Zero;
    }
}
