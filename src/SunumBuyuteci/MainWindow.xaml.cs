using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SunumBuyuteci.Display;
using SunumBuyuteci.Native;

namespace SunumBuyuteci;

public partial class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));

    private readonly Settings _settings = Settings.Load();
    private readonly List<NumberOverlayWindow> _overlays = new();
    private readonly Dictionary<MonitorInfo, Border> _cards = new();
    private readonly DispatcherTimer _refreshDebounce;
    private MonitorInfo? _selected;
    private bool _started;

    /// <summary>Raised on Başlat with the chosen monitor and the overlay window handles to exclude from capture.</summary>
    public event Action<MonitorInfo, IReadOnlyList<IntPtr>>? StartRequested;

    public MainWindow()
    {
        InitializeComponent();
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _refreshDebounce.Tick += (_, _) => { _refreshDebounce.Stop(); Refresh(); };

        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);
        Loaded += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            CloseOverlays();
            if (!_started) Application.Current.Shutdown();
        };
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DISPLAYCHANGE && !_started)
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }
        return IntPtr.Zero;
    }

    private void Refresh()
    {
        if (_started) return;
        var monitors = MonitorService.GetMonitors();
        var targets = monitors.Where(m => !m.IsPrimary).ToList();

        CloseOverlays();
        foreach (var m in monitors)
        {
            var overlay = new NumberOverlayWindow(m);
            overlay.Show();
            _overlays.Add(overlay);
        }

        CardsPanel.Children.Clear();
        _cards.Clear();
        foreach (var m in targets) CardsPanel.Children.Add(BuildCard(m));

        bool none = targets.Count == 0;
        EmptyPanel.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        InstructionText.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = none ? Visibility.Collapsed : Visibility.Visible;

        Select(targets.FirstOrDefault(m => m.DeviceName == _settings.LastMonitorDevice)
               ?? (targets.Count == 1 ? targets[0] : null));
    }

    private Border BuildCard(MonitorInfo m)
    {
        var number = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Background = Accent,
            Child = new TextBlock
            {
                Text = m.Number.ToString(), Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = $"{m.Number} – {m.FriendlyName}", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
        });
        text.Children.Add(new TextBlock
        {
            Text = m.Resolution, FontSize = 14, Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(number);
        row.Children.Add(text);

        var card = new Border
        {
            Background = Brushes.White, BorderBrush = Idle, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand, Child = row,
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(m);
            if (e.ClickCount == 2) Start();
        };
        _cards[m] = card;
        return card;
    }

    private void Select(MonitorInfo? monitor)
    {
        _selected = monitor;
        foreach (var (m, card) in _cards) card.BorderBrush = m == monitor ? Accent : Idle;
        StartButton.IsEnabled = monitor != null;
    }

    private void Start()
    {
        if (_selected == null || _started) return;
        _started = true;
        _settings.LastMonitorDevice = _selected.DeviceName;
        _settings.Save();

        var handles = _overlays.Select(o => o.Handle).ToList();
        foreach (var o in _overlays) o.Hide();
        Hide();
        StartRequested?.Invoke(_selected, handles);
    }

    private void CloseOverlays()
    {
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => Start();

    private void ShortcutsLink_Click(object sender, RoutedEventArgs e) =>
        new Tray.ShortcutsWindow { Owner = this }.ShowDialog();

    private void ExtendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show("Ekran modu değiştirilemedi. Windows + P ile 'Uzat'ı seçin.", "Sunum Büyüteci",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
