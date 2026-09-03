using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WinFinger.Services;

public sealed class AppSettings
{
    public bool AutoStart { get; set; }
    public bool ClipboardPaused { get; set; }
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;
    public double IslandOffsetX { get; set; }
    public double IslandOffsetY { get; set; }
    public bool LiveGlassEnabled { get; set; } = true; // legacy toggle, superseded by BackgroundMode
    public string BackgroundMode { get; set; } = "glass"; // glass | color | image
    public string BackgroundColor { get; set; } = "#1A1A22";
    public string BackgroundImagePath { get; set; } = "";
    public double ImageDim { get; set; } = 0.3;
    public double GlassDarkness { get; set; } = 0.55;
    public double GlassSaturation { get; set; } = 1.6;
    public double GhostOpacity { get; set; } = 0.4;
    public bool GlintEnabled { get; set; } = true;
    public bool ChromaticEnabled { get; set; } = true;

    // ── parity with mac 1.1.0 ──
    /// <summary>"black" = 纯黑 (always dark), "glass" = Liquid Glass (follows system light/dark).</summary>
    public string AppearanceStyle { get; set; } = "glass";
    /// <summary>"top" = docked to the top edge, "floating" = free position.</summary>
    public string DockMode { get; set; } = "top";
    /// <summary>Window origin (DIP) used in floating mode.</summary>
    public double FloatingLeft { get; set; } = double.NaN;
    public double FloatingTop { get; set; } = double.NaN;
    /// <summary>Locked expanded panel: clicking outside doesn't collapse.</summary>
    public bool IsExpandedPinned { get; set; }
    /// <summary>User-resized expanded panel width (0 = default).</summary>
    public double ExpandedUserWidth { get; set; }
    /// <summary>Completed focus sessions, never reset.</summary>
    public int PomodoroCompletedFocusCount { get; set; }
}

/// <summary>settings.json persistence + the HKCU Run auto-start key.</summary>
public sealed class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinFinger";

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Load();
    }

    public void Save()
    {
        try
        {
            StoragePaths.EnsureCreated();
            File.WriteAllText(StoragePaths.SettingsJson,
                JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort
        }
    }

    public void SetAutoStart(bool enabled)
    {
        Settings.AutoStart = enabled;
        Save();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enabled)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // registry access denied; setting stays recorded in json
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StoragePaths.SettingsJson)) return;
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StoragePaths.SettingsJson)) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }
}
