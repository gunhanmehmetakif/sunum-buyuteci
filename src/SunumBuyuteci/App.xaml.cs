using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SunumBuyuteci.Display;
using SunumBuyuteci.Input;
using SunumBuyuteci.Magnifier;
using SunumBuyuteci.Native;
using SunumBuyuteci.Overlay;
using SunumBuyuteci.Tray;

namespace SunumBuyuteci;

public partial class App : Application
{
    private const int TestWidth = 960, TestHeight = 540, TestMargin = 24;
    private const string MutexName = "SunumBuyuteci.SingleInstance";
    private const string AppTitle = "Sunum Büyüteci";

    private MainWindow? _main;
    private GlobalInput? _input;
    private MagnifierOutput? _output;
    private ViewController? _view;
    private Settings _settings = new();
    private double _initialZoom = 1.0;
    private (int X, int Y)? _initialCenter;
    private bool _magInitialized;
    private bool _testWindow;
    private FrameWindow? _frame;
    private BadgeWindow? _badge;
    private CursorClip? _clip;
    private TrayIcon? _tray;
    private ShortcutsWindow? _shortcuts;
    private ControlWindow? _control;
    private IReadOnlyList<IntPtr> _selectionOverlays = Array.Empty<IntPtr>();
    private Mutex? _mutex;
    private bool _ownsMutex;
    private DispatcherTimer? _displayDebounce;
    private bool _balloonShown;
    private bool _fatal;

    // Presentation target; null while the selection window is shown.
    private string? _targetDevice;
    private RECT _hostBounds, _sourceBounds;
    private bool _waitingForTarget;

    // View state kept across restarts (display changes).
    private double _resumeZoom = 1.0;
    private (int X, int Y)? _resumeCenter;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Sunum Büyüteci zaten çalışıyor.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _magInitialized = Magnification.MagInitialize();
        if (!_magInitialized)
        {
            MessageBox.Show("Windows Büyüteç altyapısı başlatılamadı.", AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            FatalExit(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => FatalExit(args.ExceptionObject as Exception);

        _settings = Settings.Load();
        ParseDebugArgs(e.Args);
        _testWindow = e.Args.Contains("--test-window");

        _input = new GlobalInput();
        _input.ExitRequested += () => Shutdown();
        _input.ZoomIn += () => _view?.ZoomIn();
        _input.ZoomOut += () => _view?.ZoomOut();
        _input.ResetZoom += () => _view?.ResetZoom();
        _input.ToggleLock += () => _view?.ToggleLock();

        CreateTray();
        if (!_input.IsActive) _tray!.ShowBalloon("Kısayollar etkinleştirilemedi", warning: true);

        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _displayDebounce.Tick += (_, _) => { _displayDebounce.Stop(); OnDisplayChanged(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (_testWindow)
        {
            StartTestWindow();
            return;
        }

        ShowSelection();
    }

    private void CreateTray()
    {
        _tray = new TrayIcon();
        _tray.ZoomInRequested += () => _view?.ZoomIn();
        _tray.ZoomOutRequested += () => _view?.ZoomOut();
        _tray.ResetRequested += () => _view?.ResetZoom();
        _tray.ToggleLockRequested += () => _view?.ToggleLock();
        _tray.ToggleZoomRequested += ToggleZoom;
        _tray.FollowModeChanged += SetFollowMode;
        _tray.ClipCursorChanged += SetClipCursor;
        _tray.ChangeScreenRequested += ChangeScreen;
        _tray.ShortcutsRequested += ShowShortcuts;
        _tray.ExitRequested += () => Shutdown();
        UpdateTray();
    }

    private void SetFollowMode(FollowMode mode)
    {
        _settings.FollowMode = mode;
        _settings.Save();
        if (_view != null) _view.FollowMode = mode;
        UpdateTray();
    }

    private void SetClipCursor(bool enabled)
    {
        _settings.ClipCursor = enabled;
        _settings.Save();
        ApplyCursorClip();
        UpdateTray();
    }

    private void SetControlTopmost(bool enabled)
    {
        _settings.ControlTopmost = enabled;
        _settings.Save();
        UpdateTray();
    }

    /// <summary>Pushes the current state to the tray icon and the control window.</summary>
    private void UpdateTray()
    {
        _tray?.SetState(_view?.IsLocked ?? false, _settings.FollowMode, _settings.ClipCursor);
        _tray?.SetZoom(_view?.TargetZoom ?? 1.0);
        _control?.SetState(_view?.TargetZoom ?? 1.0, _view?.IsLocked ?? false, _settings.FollowMode,
            _settings.ClipCursor, _waitingForTarget);
    }

    private void ShowControl()
    {
        if (_control == null)
        {
            _control = new ControlWindow(_settings);
            _control.ZoomInRequested += () => _view?.ZoomIn();
            _control.ZoomOutRequested += () => _view?.ZoomOut();
            _control.ToggleLockRequested += () => _view?.ToggleLock();
            _control.FullScreenRequested += ToggleZoom;
            _control.FollowModeChanged += SetFollowMode;
            _control.ClipCursorChanged += SetClipCursor;
            _control.TopmostChanged += SetControlTopmost;
            _control.ChangeScreenRequested += ChangeScreen;
            _control.ShortcutsRequested += ShowShortcuts;
            _control.ExitRequested += () => Shutdown();
            _control.ExcludedChanged += ApplyExclusions;
        }
        _control.Show();
        UpdateTray();
    }

    /// <summary>Hides the mirror-excluded windows of this app from the presentation output.</summary>
    private void ApplyExclusions()
    {
        if (_output == null) return;
        var list = _selectionOverlays.Where(NativeMethods.IsWindow).ToList();
        if (_frame != null) list.Add(_frame.Handle);
        if (_badge != null) list.Add(_badge.Handle);
        if (_control != null) list.AddRange(_control.ExcludedHandles);
        _output.SetExcludedWindows(list);
    }

    private void ShowShortcuts()
    {
        if (_shortcuts != null) { _shortcuts.Activate(); return; }
        _shortcuts = new ShortcutsWindow();
        _shortcuts.Closed += (_, _) => _shortcuts = null;
        _shortcuts.Show();
    }

    private void ToggleZoom()
    {
        if (_view == null) return;
        if (_view.TargetZoom > 1.0 + 0.001) _view.ResetZoom();
        else _view.SetZoom(NearestStep(_settings.LastZoom));
    }

    private static double NearestStep(double zoom)
    {
        var steps = ViewController.ZoomSteps.Where(s => s > 1.0).ToArray();
        return steps.OrderBy(s => Math.Abs(s - zoom)).First();
    }

    private void ParseDebugArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--zoom" &&
                double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                _initialZoom = z;
            else if (args[i] == "--center")
            {
                var parts = args[i + 1].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
                    _initialCenter = (x, y);
            }
        }
    }

    private void ShowSelection()
    {
        if (_main != null && _main.IsVisible) { _main.Activate(); return; }
        _main = new MainWindow();
        _main.StartRequested += OnStartRequested;
        _main.Show();
    }

    private void OnStartRequested(MonitorInfo target, IReadOnlyList<IntPtr> overlayHandles)
    {
        _settings.LastMonitorDevice = target.DeviceName;
        _settings.Save();
        var primary = MonitorService.GetMonitors().First(m => m.IsPrimary);
        _targetDevice = target.DeviceName;
        _waitingForTarget = false;
        _resumeZoom = _initialZoom;
        _resumeCenter = _initialCenter;
        _selectionOverlays = overlayHandles.ToArray();
        StartOutput(target.Bounds, primary.Bounds);
    }

    /// <summary>"Ekranı değiştir": tear everything down and show the selection window again.</summary>
    private void ChangeScreen()
    {
        if (_testWindow) return;
        StopOutput();
        _targetDevice = null;
        _waitingForTarget = false;
        _resumeZoom = 1.0;
        _resumeCenter = null;
        _selectionOverlays = Array.Empty<IntPtr>();
        _control?.Hide();
        var old = _main;
        _main = null;
        old?.Close();
        UpdateTray();
        ShowSelection();
    }

    private void StartTestWindow()
    {
        var primary = MonitorService.GetMonitors().First(m => m.IsPrimary);
        var wa = primary.WorkArea;
        var bounds = new RECT(wa.Right - TestWidth - TestMargin, wa.Bottom - TestHeight - TestMargin,
            wa.Right - TestMargin, wa.Bottom - TestMargin);
        _resumeZoom = _initialZoom;
        _resumeCenter = _initialCenter;
        StartOutput(bounds, primary.Bounds);
    }

    private void StartOutput(RECT hostBounds, RECT source)
    {
        try
        {
            _hostBounds = hostBounds;
            _sourceBounds = source;
            _output = new MagnifierOutput(hostBounds, source);
            _view = new ViewController(_output, _settings.FollowMode, _resumeZoom, _resumeCenter);
            _view.Changed += OnViewChanged;
            _view.SuspendFollow = () => _control?.SuspendsFollow ?? false;
            _output.Start();
            _frame = new FrameWindow(_view, _output);
            _frame.Start();
            _badge = new BadgeWindow(_view, source);
            _badge.Show();

            ShowControl();
            ApplyExclusions();
            ApplyCursorClip();
            UpdateTray();

            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray?.ShowBalloon("Sunum Büyüteci çalışıyor. Ctrl+Alt+↑ ile yakınlaştırın.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Çıkış penceresi oluşturulamadı: " + ex.Message, AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnViewChanged(ViewChangeReason reason)
    {
        if (reason == ViewChangeReason.Zoom && _view != null && _view.TargetZoom > 1.0 + 0.001 &&
            Math.Abs(_settings.LastZoom - _view.TargetZoom) > 0.001)
        {
            _settings.LastZoom = _view.TargetZoom;
            _settings.Save();
        }
        UpdateTray();
    }

    /// <summary>Starts or stops the cursor clip to match the setting and the running state.</summary>
    private void ApplyCursorClip()
    {
        bool want = !_testWindow && _settings.ClipCursor && _output != null;
        if (!want)
        {
            _clip?.Dispose();
            _clip = null;
        }
        else if (_clip == null)
        {
            _clip = new CursorClip(_sourceBounds);
            _clip.Start();
        }
    }

    /// <summary>Disposes output, frame, badge and clip. Idempotent; remembers zoom and lock for a restart.</summary>
    private void StopOutput()
    {
        if (_view != null)
        {
            _resumeZoom = _view.TargetZoom;
            _resumeCenter = _view.IsLocked
                ? ((_view.CurrentRect.Left + _view.CurrentRect.Right) / 2, (_view.CurrentRect.Top + _view.CurrentRect.Bottom) / 2)
                : null;
        }
        _clip?.Dispose();
        _clip = null;
        _frame?.Dispose();
        _frame = null;
        _badge?.Close();
        _badge = null;
        _output?.Dispose();
        _output = null;
        _view = null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _displayDebounce?.Stop(); _displayDebounce?.Start(); });

    private void OnDisplayChanged()
    {
        if (_testWindow || _targetDevice == null) return;

        var monitors = MonitorService.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        var target = monitors.FirstOrDefault(m => m.DeviceName == _targetDevice && !m.IsPrimary);

        if (primary == null || target == null || monitors.Count < 2)
        {
            if (!_waitingForTarget)
            {
                StopOutput();
                _waitingForTarget = true;
                UpdateTray();
                _tray?.ShowBalloon("Sunum ekranı bağlantısı kesildi. Yeniden bağlanınca devam edecek.", warning: true);
            }
            return;
        }

        if (_waitingForTarget)
        {
            _waitingForTarget = false;
            StartOutput(target.Bounds, primary.Bounds);
            _tray?.ShowBalloon("Sunum ekranı yeniden bağlandı.");
        }
        else if (_output != null && (!SameRect(target.Bounds, _hostBounds) || !SameRect(primary.Bounds, _sourceBounds)))
        {
            StopOutput();
            StartOutput(target.Bounds, primary.Bounds);
        }
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Last-resort handler: release the cursor, unhook, tell the user, exit.</summary>
    private void FatalExit(Exception? ex)
    {
        if (_fatal) return;
        _fatal = true;
        try { _clip?.Release(); } catch (Exception) { }
        try { _input?.Dispose(); } catch (Exception) { }
        try { _tray?.Dispose(); } catch (Exception) { }
        try
        {
            MessageBox.Show("Beklenmeyen bir hata oluştu ve uygulama kapatılıyor." +
                (ex != null ? "\n\n" + ex.Message : ""), AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { }
        Environment.Exit(1);
    }

    private void Cleanup()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayDebounce?.Stop();
        StopOutput();
        _control?.CloseForExit();
        _control = null;
        _input?.Dispose();
        _tray?.Dispose();
        _tray = null;
        if (_magInitialized)
        {
            Magnification.MagUninitialize();
            _magInitialized = false;
        }
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }
}
