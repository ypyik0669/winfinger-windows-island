using System.Windows;
using Microsoft.Win32;

namespace WinFinger.Services;

/// <summary>
/// Palette switching (mac: MacFingerPalette + AppleInterfaceThemeChangedNotification).
/// "black" appearance is always dark; "glass" follows the system light/dark setting.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri DarkUri = new("Themes/Palette.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("Themes/Palette.Light.xaml", UriKind.Relative);

    private string _appearanceStyle = "glass";
    private bool _isDark = true;

    public bool IsDark => _isDark;

    /// <summary>Raised on the UI thread after the palette dictionary was swapped.</summary>
    public event Action<bool>? PaletteChanged;

    public void Start(string appearanceStyle)
    {
        _appearanceStyle = appearanceStyle;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply(force: true);
    }

    public void Stop()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    public void SetAppearanceStyle(string style)
    {
        _appearanceStyle = style;
        Apply(force: false);
    }

    /// <summary>Mirrors mac AppModel.systemUsesDarkInterface (AppleInterfaceStyle == "Dark").</summary>
    public static bool SystemUsesDarkInterface()
    {
        // dev hook: WINFINGER_FORCE_LIGHT=1 previews the light palette regardless of the system setting
        if (Environment.GetEnvironmentVariable("WINFINGER_FORCE_LIGHT") == "1") return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) return light == 0;
        }
        catch
        {
            // registry unavailable: assume dark
        }
        return true;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(force: false));
    }

    private void Apply(bool force)
    {
        bool dark = _appearanceStyle == "black" || SystemUsesDarkInterface();
        if (!force && dark == _isDark) return;
        _isDark = dark;

        var app = Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        // the palette is always the first merged dictionary (see App.xaml)
        if (merged.Count > 0 && merged[0].Source is { } src &&
            (src.OriginalString.Contains("Palette.Dark") || src.OriginalString.Contains("Palette.Light")))
            merged[0] = palette;
        else
            merged.Insert(0, palette);
        PaletteChanged?.Invoke(dark);
    }
}
