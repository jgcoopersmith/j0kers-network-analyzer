using Microsoft.Win32;
using System.IO;

namespace NetAnalyzer;

/// <summary>
/// Registers the app to launch at logon via the per-user Run key. The registry entry itself is
/// the source of truth — nothing is duplicated into settings.json, so the checkbox always
/// reflects what Windows will actually do, even if the user edits it via Task Manager.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "j0kers Network Analyzer";

    /// <summary>Path Windows would need to launch this running instance.</summary>
    private static string? LaunchCommand
    {
        get
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\"";
        }
    }

    /// <summary>The exe path recorded in the Run key, unquoted, or null when there is no entry.</summary>
    private static string? RegisteredPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string value)
                    return null;

                var path = value.Trim().Trim('"');
                return path.Length == 0 ? null : path;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            // An entry naming an exe that is no longer there is not enabled in any useful sense:
            // Windows fails that launch silently, so treating it as on leaves the menu ticked
            // while nothing happens at logon.
            var path = RegisteredPath;
            return path is not null && File.Exists(path);
        }
    }

    /// <summary>
    /// Re-points an existing entry at the running exe once it has moved. The path is otherwise
    /// only written when the menu item is toggled, so moving the exe breaks launch-at-logon for
    /// good while the menu still reports it as on.
    /// </summary>
    public static void Repair()
    {
        var registered = RegisteredPath;
        if (registered is null)
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) ||
            string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
            return;

        SetEnabled(true);
    }

    /// <summary>Enables or disables launch-at-logon. Returns the resulting state.</summary>
    public static bool SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var command = LaunchCommand;
                if (command is null)
                    return false;
                // Re-registering on toggle also heals a stale path after the exe has moved.
                key.SetValue(ValueName, command);
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                    or System.IO.IOException)
        {
            // Registry write refused (locked-down account or policy); report the actual state.
            return IsEnabled;
        }
    }
}
