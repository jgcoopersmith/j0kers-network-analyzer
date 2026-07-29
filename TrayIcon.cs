using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace NetAnalyzer;

/// <summary>
/// Notification-area presence for the app. WPF has no tray primitive, so this wraps the WinForms
/// <see cref="Forms.NotifyIcon"/> and keeps the interop confined to one file.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _drawnIcon;
    private nint _iconHandle;
    private bool _greeted;

    /// <summary>Raised on double-click or the tray menu's Show item.</summary>
    public event Action? RestoreRequested;

    /// <summary>Raised by the tray menu's Exit item.</summary>
    public event Action? ExitRequested;

    public TrayIcon(string tooltip)
    {
        _drawnIcon = BuildIcon();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Text = tooltip,
            Icon = _drawnIcon,
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.DoubleClick += (_, _) => RestoreRequested?.Invoke();
    }

    public void Show()
    {
        _icon.Visible = true;

        // Explain where the window went, but only the first time each session.
        if (_greeted)
            return;
        _greeted = true;
        _icon.ShowBalloonTip(2500, "j0kers Network Analyzer",
            "Still monitoring. Double-click the tray icon to bring it back.", Forms.ToolTipIcon.Info);
    }

    public void Hide() => _icon.Visible = false;

    /// <summary>
    /// Prefers the app icon so the tray matches the taskbar, picking the frame sized for the
    /// notification area. Falls back to drawing one if the file is missing from the output.
    /// </summary>
    private Icon BuildIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path))
                return new Icon(path, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
        }

        return DrawFallbackIcon();
    }

    /// <summary>Three rising bars, used only if the icon file cannot be loaded.</summary>
    private Icon DrawFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);

            void Bar(int x, int top, Color color)
            {
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, x, top, 7, 32 - top);
            }

            Bar(2, 18, Color.FromArgb(0x3A, 0xD1, 0x7E));
            Bar(12, 9, Color.FromArgb(0xE8, 0xC4, 0x4A));
            Bar(22, 2, Color.FromArgb(0xF2, 0x4E, 0x4E));
        }

        // GetHicon hands back an unmanaged handle that Icon.FromHandle does not own,
        // so hold it and release it explicitly in Dispose.
        _iconHandle = bmp.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
        if (_iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
