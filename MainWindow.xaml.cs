using System.Windows;
using System.Windows.Input;

namespace NetAnalyzer;

public partial class MainWindow : Window
{
    private readonly NetworkMonitor _monitor = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _monitor;
        Loaded += (_, _) => _monitor.Start();
        Closed += (_, _) => _monitor.Stop();
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
