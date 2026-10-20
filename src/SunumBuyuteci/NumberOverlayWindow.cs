using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SunumBuyuteci.Display;
using SunumBuyuteci.Native;

namespace SunumBuyuteci;

/// <summary>Big translucent monitor number; click-through, topmost, never activated.</summary>
public sealed class NumberOverlayWindow : Window
{
    private readonly RECT _target;
    private IntPtr _hwnd;

    public IntPtr Handle => _hwnd;

    public NumberOverlayWindow(MonitorInfo monitor)
    {
        // Square box of a third of the shorter side, centered on the monitor (physical pixels).
        int side = Math.Min(monitor.Width, monitor.Height) / 3;
        int x = monitor.Bounds.Left + (monitor.Width - side) / 2;
        int y = monitor.Bounds.Top + (monitor.Height - side) / 2;
        _target = new RECT(x, y, x + side, y + side);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = 100;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x14, 0x1F)),
            CornerRadius = new CornerRadius(24),
            Child = new Viewbox
            {
                Margin = new Thickness(12),
                Child = new TextBlock
                {
                    Text = monitor.Number.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 200,
                    FontWeight = FontWeights.SemiBold,
                },
            },
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThrough(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
            Place();
        };
    }

    private void Place() =>
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _target.Left, _target.Top,
            _target.Width, _target.Height, NativeMethods.SWP_NOACTIVATE);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF resizes on DPI change; re-apply the exact physical rectangle afterwards.
        if (msg == NativeMethods.WM_DPICHANGED) Dispatcher.BeginInvoke(Place);
        return IntPtr.Zero;
    }
}
