using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace NetAnalyzer;

/// <summary>
/// Registers the app to launch at logon.
///
/// This used to be a per-user Run key entry, and cannot be any more: the app requires
/// administrator, and Windows will not elevate a Run entry at logon — it is skipped, silently,
/// so the menu would read as on while nothing started. A scheduled task registered to run with
/// highest privileges is the only way an elevated app starts at logon without a prompt.
///
/// The task itself is the source of truth, exactly as the registry entry was: nothing is
/// duplicated into settings.json, so the menu always reflects what Windows will really do.
/// A leftover Run entry from an earlier version is removed on sight, otherwise a user who
/// upgrades keeps a dead entry that quietly does nothing.
/// </summary>
public static class StartupRegistration
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "j0kers Network Analyzer";
    private const string TaskName = "j0kers Network Analyzer";

    /// <summary>The exe the task is registered to launch, or null when there is no task.</summary>
    private static string? RegisteredPath
    {
        get
        {
            var xml = RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE");
            if (xml is null)
                return null;

            // <Command>C:\path\NetAnalyzer.exe</Command>
            var open = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
            if (open < 0)
                return null;
            open += "<Command>".Length;
            var close = xml.IndexOf("</Command>", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                return null;

            var path = xml[open..close].Trim().Trim('"');
            return path.Length == 0 ? null : path;
        }
    }

    public static bool IsEnabled
    {
        get
        {
            // A task naming an exe that is no longer there is not enabled in any useful sense:
            // the launch fails silently, so treating it as on leaves the menu ticked while
            // nothing happens at logon.
            var path = RegisteredPath;
            return path is not null && File.Exists(path);
        }
    }

    /// <summary>
    /// Re-points an existing task at the running exe once it has moved, and clears any Run entry
    /// left by a version that predates the task. Called at startup.
    /// </summary>
    public static void Repair()
    {
        // A Run entry means an earlier version had launch-at-logon switched ON. Removing it
        // without registering the task would turn the setting off behind the user's back on the
        // first elevated run, so the entry is migrated rather than merely cleared.
        var hadLegacy = RemoveLegacyRunEntry();

        var registered = RegisteredPath;
        if (registered is null)
        {
            if (hadLegacy)
                SetEnabled(true);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) ||
            string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
            return;

        SetEnabled(true);
    }

    /// <summary>Enables or disables launch-at-logon. Returns the resulting state.</summary>
    public static bool SetEnabled(bool enable)
    {
        if (!enable)
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            return IsEnabled;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        // /RL HIGHEST is the whole point: without it the task starts the app unelevated, the
        // manifest refuses, and logon start fails. /F replaces an existing task, which is also
        // how a moved exe is healed.
        var created = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F") is not null;

        return created && IsEnabled;
    }

    /// <summary>
    /// Removes the Run entry earlier versions used, which cannot work with elevation. Returns
    /// whether one was there, which is how the caller knows the setting used to be on.
    /// </summary>
    private static bool RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is null)
                return false;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                    or IOException)
        {
            // Nothing to do if the registry refuses; the task is what matters now.
        }
        return false;
    }

    /// <summary>
    /// Runs schtasks and returns its output, or null when it failed. Used rather than the Task
    /// Scheduler COM API to keep the single-file publish free of another dependency, and it is
    /// never allowed to block startup.
    /// </summary>
    private static string? RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
