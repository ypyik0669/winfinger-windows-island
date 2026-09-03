using System.IO;

namespace WinFinger.Services;

/// <summary>Local data layout under %APPDATA%\WinFinger\ (mirrors mac's Application Support/MacFinger).</summary>
public static class StoragePaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinFinger");

    public static string ClipboardMedia { get; } = Path.Combine(Root, "ClipboardMedia");
    public static string ClipboardJson { get; } = Path.Combine(Root, "clipboard.json");
    public static string NotesJson { get; } = Path.Combine(Root, "notes.json");
    public static string SettingsJson { get; } = Path.Combine(Root, "settings.json");

    /// <summary>用户自定义动作目录（内置副本首次运行时写入）。</summary>
    public static string ActionsJson { get; } = Path.Combine(Root, "actions.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ClipboardMedia);
    }
}
