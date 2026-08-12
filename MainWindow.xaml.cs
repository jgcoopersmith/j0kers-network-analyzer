using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetAnalyzer;

public partial class MainWindow : Window
{
    private readonly NetworkMonitor _monitor = new();

    /// <summary>Set by File &gt; Exit so that path closes for real regardless of the minimize setting.</summary>
    private bool _exiting;

    /// <summary>Tracks whether the window is currently parked in the notification area.</summary>
    private bool _inTray;

    private readonly TrayIcon _tray = new("j0kers Network Analyzer");

    /// <summary>Coalesces bursts of preference changes into a single write.</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _monitor;

        // Captured from XAML before widget mode drops them to zero, so leaving widget mode
        // restores the real limits rather than a second copy of them hardcoded here.
        _chromeMinWidth = MinWidth;
        _chromeMinHeight = MinHeight;

        var settings = SettingsStore.Load();
        _monitor.ApplySettings(settings);
        RestoreWindowBounds(settings);

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };
        _monitor.SettingsChanged += () =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _monitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NetworkMonitor.Mode))
                ApplyViewMode();
            else if (e.PropertyName == nameof(NetworkMonitor.AlwaysOnTop))
                UpdateTopmost();
        };

        _tray.RestoreRequested += RestoreFromTray;
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };

        Loaded += (_, _) =>
        {
            _monitor.Start();
            ApplyViewMode();

            // Deferred to Loaded: hiding into the tray only works once the window exists.
            if (settings.HiddenInTray)
                HideToTray();
            else if (settings.WindowMinimized)
                WindowState = WindowState.Minimized;
        };
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        ShellContent.SizeChanged += (_, _) => UpdateShellClip();

        // Re-pin whenever something could have knocked us out of the topmost band: another app
        // taking activation (maximizing one does exactly this), our own handle being recreated,
        // or a restore from minimized.
        SourceInitialized += (_, _) => ReassertTopmost();
        Activated += (_, _) => ReassertTopmost();
        Deactivated += (_, _) => ReassertTopmost();
        Closed += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
            _monitor.Stop();
            _tray.Dispose();
        };
    }

    private void SaveSettings()
    {
        var settings = _monitor.CaptureSettings();

        // RestoreBounds holds the pre-maximize rectangle; Width/Height would report the
        // maximized size and the window would come back the wrong size next launch.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;

        // The two modes have independent sizes, so record whichever one is not on screen from
        // its remembered value rather than from the live window.
        var full = _inWidgetMode && !_normalBounds.IsEmpty ? _normalBounds.Size : bounds.Size;
        var widget = _inWidgetMode ? new Size(Width, Height) : _widgetSize;

        settings.WindowWidth = full.Width;
        settings.WindowHeight = full.Height;
        settings.WidgetWidth = widget.Width;
        settings.WidgetHeight = widget.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.HiddenInTray = _inTray;
        // In the tray the window is also technically minimized; only one of the two should stick.
        settings.WindowMinimized = !_inTray && WindowState == WindowState.Minimized;

        SettingsStore.Save(settings);
    }

    private void RestoreWindowBounds(AppSettings s)
    {
        if (s.WidgetWidth >= WidgetMinWidth && s.WidgetHeight >= WidgetMinHeight)
            _widgetSize = new Size(s.WidgetWidth, s.WidgetHeight);

        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            // Seed the pre-widget geometry too, so starting up in widget mode still knows
            // what size to return to.
            _normalBounds = new Rect(0, 0, s.WindowWidth, s.WindowHeight);
        }

        // Only honour a saved position that still leaves the window reachable — a monitor may
        // have been unplugged since. Requiring the whole window to fit would reject a window
        // legitimately parked against a screen edge, so this checks that enough of the top
        // edge is on screen to grab.
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) && IsReachable(s.WindowLeft, s.WindowTop, Width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private static Rect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>True when enough of the window's top edge is on a display to click and drag.</summary>
    private static bool IsReachable(double left, double top, double width)
    {
        var titleBar = new Rect(left, top, Math.Max(1, width), 32);
        titleBar.Intersect(VirtualScreen);
        return titleBar.Width >= 100 && titleBar.Height >= 20;
    }

    /// <summary>
    /// Pulls the window back into view. Leaving widget mode grows it from 330px wide, which can
    /// push most of it off screen if the widget was sitting near a right or bottom edge.
    /// </summary>
    private void EnsureOnScreen()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || IsReachable(Left, Top, Width))
            return;

        var screen = VirtualScreen;
        Left = Math.Clamp(Left, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        Top = Math.Clamp(Top, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_monitor.MinimizeOnClose)
            return;

        // Keep running instead of shutting down; polling carries on in the background either way.
        e.Cancel = true;
        if (_monitor.MinimizeToTray)
            HideToTray();
        else
            WindowState = WindowState.Minimized;
    }

    /// <summary>Catches an ordinary minimize so it lands in the tray when that option is on.</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _monitor.MinimizeToTray)
        {
            HideToTray();
            return;
        }

        // A borderless window maximizes over the whole screen, taskbar included; cap it to the
        // work area instead. MaximizedPrimaryScreenWidth/Height already include the fudge WPF
        // applies, so the window lands exactly on the work area of the display it is on.
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }

        UpdateShellClip();
        ReassertTopmost();
    }

    /// <summary>
    /// Rounds off the shell's contents. A Border does not clip what it contains, so without this
    /// the square-cornered strips inside — menu bar, footer, widget panel — paint across the
    /// rounded corners and the window reads as rounded and square at the same time. Squared off
    /// when maximized, matching the shell.
    /// </summary>
    private void UpdateShellClip()
    {
        var w = ShellContent.ActualWidth;
        var h = ShellContent.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            ShellContent.Clip = null;
            return;
        }

        // A shade under the shell's 8px radius, since this sits inside its 1px border.
        var radius = WindowState == WindowState.Maximized ? 0 : 7.0;
        ShellContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    private void HideToTray()
    {
        _inTray = true;
        _tray.Show();
        ShowInTaskbar = false;
        Hide();
        SaveSettings();
    }

    private void RestoreFromTray()
    {
        _inTray = false;
        Show();
        // Setting ShowInTaskbar recreates the window handle, so topmost has to be re-applied
        // after it rather than before.
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        UpdateTopmost();
        _tray.Hide();
        SaveSettings();
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Close();
    }

    private bool _paused;

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        // Tracked in a field rather than read back from the button caption, which would break
        // silently if the label ever changed.
        _paused = !_paused;

        if (_paused)
            _monitor.Stop();
        else
            _monitor.Start();

        PauseButton.Content = _paused ? "Resume" : "Pause";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => _monitor.ResetTotals();

    // ---- View modes ----

    /// <summary>Window geometry from before widget mode, restored on the way out.</summary>
    private Rect _normalBounds = Rect.Empty;

    /// <summary>Size limits for the full window, taken from XAML at construction.</summary>
    private readonly double _chromeMinWidth;
    private readonly double _chromeMinHeight;

    private void CycleView_Click(object sender, RoutedEventArgs e) => _monitor.CycleMode();

    /// <summary>Loads per-app usage for the hovered interface as its tooltip opens.</summary>
    private void Meter_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InterfaceMeter meter })
            _ = TopTalkers.Instance.RefreshAsync(meter.Id, meter.Name, _monitor.UseBits);
    }

    /// <summary>Refreshes the startup tick from the registry as the menu opens.</summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        => StartupMenuItem.IsChecked = StartupRegistration.IsEnabled;

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
        => StartupMenuItem.IsChecked = StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled);

    /// <summary>Opacity menu items carry their value in Tag, e.g. "0.65".</summary>
    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _monitor.WindowOpacity = value;
        }
    }

    private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _monitor.CycleMode();
            return;
        }

        // Widget mode has no title bar, so the panel itself is the drag handle. DragMove throws
        // if the button is no longer down by the time it runs — a fast click can beat it there,
        // and an unhandled exception here would take the app down over a stray click.
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyViewMode()
    {
        if (_monitor.Mode == ViewMode.Widget)
            EnterWidgetMode();
        else
            ExitWidgetMode();

        UpdateTopmost();
    }

    /// <summary>A widget floats by definition; otherwise the File menu option decides.</summary>
    private void UpdateTopmost()
    {
        Topmost = _monitor.Mode == ViewMode.Widget || _monitor.AlwaysOnTop;
        ReassertTopmost();
    }

    /// <summary>
    /// Pins the window to the topmost band directly. The WPF property alone is not reliable
    /// here: toggling ShowInTaskbar for the tray recreates the window handle, switching
    /// WindowStyle for widget mode restyles it, and another application taking activation can
    /// leave us behind it — each of which can drop the topmost flag even though the property
    /// still reads true. Never touches z-order when not pinned, since hoisting the window on
    /// every deactivation would be worse than the bug.
    /// </summary>
    private void ReassertTopmost()
    {
        if (!Topmost)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---- Custom title bar ----

    /// <summary>The banner is the title bar: drag to move, double-click to toggle maximize.</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// The Menu control swallows mouse-downs on its own strip, so the drag has to be caught on
    /// the way down. Clicks on an actual menu header are left alone to open their flyout.
    /// </summary>
    private void MenuBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<MenuItem>(e.OriginalSource as DependencyObject) is not null)
            return;

        TitleBar_MouseLeftButtonDown(sender, e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// Tracks widget mode directly. WindowStyle used to serve as this flag, but the window is
    /// now permanently borderless so that it can be transparent.
    /// </summary>
    private bool _inWidgetMode;

    /// <summary>Widget mode keeps its own size, remembered across switches and restarts.</summary>
    private Size _widgetSize = new(360, 300);

    private void EnterWidgetMode()
    {
        if (_inWidgetMode)
            return;
        _inWidgetMode = true;

        if (WindowState == WindowState.Normal)
            _normalBounds = new Rect(Left, Top, Width, Height);

        WindowState = WindowState.Normal;
        SizeToContent = SizeToContent.Manual;

        // Stays resizable in every direction: widening it lengthens the history each ribbon
        // shows, and heightening it gives the ribbons more room to resolve detail.
        ResizeMode = ResizeMode.CanResize;
        MinWidth = WidgetMinWidth;
        MinHeight = WidgetMinHeight;
        Width = Math.Max(WidgetMinWidth, _widgetSize.Width);
        Height = Math.Max(WidgetMinHeight, _widgetSize.Height);
    }

    private void ExitWidgetMode()
    {
        if (!_inWidgetMode)
            return;
        _inWidgetMode = false;

        // Carry whatever size the widget was left at back into the next visit.
        _widgetSize = new Size(Width, Height);

        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = _chromeMinWidth;
        MinHeight = _chromeMinHeight;

        if (!_normalBounds.IsEmpty)
        {
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
        }

        EnsureOnScreen();
    }

    private const double WidgetMinWidth = 240;
    private const double WidgetMinHeight = 120;

    // ---- Drag-and-drop reordering of the interface list ----

    private Point _dragOrigin;
    private InterfaceMeter? _dragItem;

    private void InterfaceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Let the activity checkbox handle its own clicks rather than starting a drag.
        if (FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null)
        {
            _dragItem = null;
            return;
        }

        _dragOrigin = e.GetPosition(null);
        _dragItem = MeterAt(e.OriginalSource as DependencyObject);
    }

    private void InterfaceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        // Wait for the system drag threshold so a click never turns into an accidental reorder.
        var delta = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(InterfaceList, item, DragDropEffects.Move);
        ClearDropHints();
    }

    private void InterfaceList_DragOver(object sender, DragEventArgs e)
    {
        var dragged = e.Data.GetData(typeof(InterfaceMeter)) as InterfaceMeter;
        var (target, above) = TargetAt(e);

        e.Effects = dragged is null || target is null || ReferenceEquals(target, dragged)
            ? DragDropEffects.None
            : DragDropEffects.Move;
        e.Handled = true;

        foreach (var m in _monitor.Interfaces)
        {
            m.DropHint = e.Effects == DragDropEffects.Move && ReferenceEquals(m, target)
                ? (above ? DropHint.Above : DropHint.Below)
                : DropHint.None;
        }
    }

    private void InterfaceList_DragLeave(object sender, DragEventArgs e) => ClearDropHints();

    private void InterfaceList_Drop(object sender, DragEventArgs e)
    {
        ClearDropHints();
        e.Handled = true;

        if (e.Data.GetData(typeof(InterfaceMeter)) is not InterfaceMeter dragged)
            return;

        var (target, above) = TargetAt(e);
        if (target is null || ReferenceEquals(target, dragged))
            return;

        var list = _monitor.Interfaces;
        var from = list.IndexOf(dragged);
        var to = list.IndexOf(target);
        if (from < 0 || to < 0)
            return;

        if (!above)
            to++;
        // Removing the dragged item first shifts everything below it up by one.
        if (from < to)
            to--;
        if (from != to)
            list.Move(from, to);
    }

    private void ClearDropHints()
    {
        foreach (var m in _monitor.Interfaces)
            m.DropHint = DropHint.None;
    }

    /// <summary>Resolves the card under the pointer and which half of it the pointer is in.</summary>
    private (InterfaceMeter? Target, bool Above) TargetAt(DragEventArgs e)
    {
        var hit = InterfaceList.InputHitTest(e.GetPosition(InterfaceList)) as DependencyObject;
        var container = FindMeterContainer(hit);
        if (container is null)
            return (null, false);

        var y = e.GetPosition(container).Y;
        return ((InterfaceMeter)container.DataContext, y < container.ActualHeight / 2);
    }

    private InterfaceMeter? MeterAt(DependencyObject? source)
        => FindMeterContainer(source)?.DataContext as InterfaceMeter;

    /// <summary>
    /// Resolves the generated item container for a visual inside the list. This is deliberately
    /// the container and not the nearest element bound to the meter — every child inherits that
    /// DataContext, so the nearest one is usually a text block whose bounds say nothing about
    /// where the card starts and ends.
    /// </summary>
    private FrameworkElement? FindMeterContainer(DependencyObject? node)
    {
        if (node is null)
            return null;
        return ItemsControl.ContainerFromElement(InterfaceList, node) as FrameworkElement;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Commits a typed interval on Enter instead of waiting for focus to leave.</summary>
    private void IntervalBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        IntervalBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        // Re-read so an out-of-range entry snaps back to the clamped value.
        IntervalBox.Text = _monitor.IntervalMs.ToString();
        IntervalBox.SelectAll();
    }
}
