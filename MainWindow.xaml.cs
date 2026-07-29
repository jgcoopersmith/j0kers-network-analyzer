using System.Windows;
using System.Windows.Controls;
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

    private readonly TrayIcon _tray = new("j0kers Network Analyzer");

    /// <summary>Coalesces bursts of preference changes into a single write.</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _monitor;

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

        _tray.RestoreRequested += RestoreFromTray;
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };

        Loaded += (_, _) => _monitor.Start();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
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
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;

        SettingsStore.Save(settings);
    }

    private void RestoreWindowBounds(AppSettings s)
    {
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        // Only honour a saved position that still lands on a connected display — a monitor may
        // have been unplugged since, which would otherwise put the window out of reach.
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

            if (screen.Contains(new Rect(s.WindowLeft, s.WindowTop, Math.Min(Width, screen.Width), 40)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
        }

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
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
            HideToTray();
    }

    private void HideToTray()
    {
        _tray.Show();
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _tray.Hide();
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Close();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (PauseButton.Content as string == "Pause")
        {
            _monitor.Stop();
            PauseButton.Content = "Resume";
        }
        else
        {
            _monitor.Start();
            PauseButton.Content = "Pause";
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => _monitor.ResetTotals();

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
