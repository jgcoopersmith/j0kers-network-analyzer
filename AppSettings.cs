using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetAnalyzer;

/// <summary>Per-interface state worth remembering between runs.</summary>
public sealed class InterfaceSetting
{
    /// <summary>Adapter GUID from the OS — stable across restarts, unlike the display name.</summary>
    public string Id { get; set; } = "";

    /// <summary>Stored only to make the settings file readable by a human.</summary>
    public string Name { get; set; } = "";

    public bool ShowActivity { get; set; } = true;
}

/// <summary>Everything the app restores on startup. Serialized as JSON.</summary>
public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 500;
    public bool UseBits { get; set; }
    public bool ShowInactive { get; set; }
    public bool ShowLoopback { get; set; }

    /// <summary>Defaults on: the WFP/QoS pseudo-adapters are noise for almost every user.</summary>
    public bool HideFilterAdapters { get; set; } = true;

    /// <summary>"Bars", "Stream" or "Widget".</summary>
    public string ViewMode { get; set; } = "";

    /// <summary>Superseded by <see cref="ViewMode"/>; still read so older files keep working.</summary>
    public bool StreamView { get; set; }
    public double StreamSpeed { get; set; } = 60;
    public bool AlwaysOnTop { get; set; }
    public double WindowOpacity { get; set; } = 1.0;
    public bool MinimizeOnClose { get; set; }
    public bool MinimizeToTray { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    /// <summary>Reopen minimized if that is how it was left.</summary>
    public bool WindowMinimized { get; set; }

    /// <summary>Reopen straight into the notification area if it was sitting there.</summary>
    public bool HiddenInTray { get; set; }

    /// <summary>Interfaces in display order; the order of this list is the saved ordering.</summary>
    public List<InterfaceSetting> Interfaces { get; set; } = new();
}

/// <summary>Loads and saves <see cref="AppSettings"/> under the user's roaming profile.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // WindowLeft/Top use NaN to mean "no saved position". System.Text.Json throws on
        // non-finite doubles unless named literals are allowed, which would turn a missing
        // position into a crash on save.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "j0kers Network Analyzer",
        "settings.json");

    /// <summary>Returns defaults if the file is missing, unreadable or corrupt — never throws.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException
                                    or UnauthorizedAccessException or ArgumentException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes settings, swallowing IO failures: losing preferences must not break the app.</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            // Write beside the target and swap, so an interrupted write can't truncate the file.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException
                                    or UnauthorizedAccessException or ArgumentException)
        {
            // Preferences are a convenience; failing to store them must never take down the app.
        }
    }
}
