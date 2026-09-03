using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    // ── utools 剪贴板管理升级 ──
    public string ClipboardHotkey { get; set; } = "Ctrl+Shift+V";
    public bool PasteAfterSelect { get; set; } = true;
    public int MaxTextLength { get; set; } = 524288;

    // ── OCR / 截图 / AI ──
    /// <summary>截图热键（避开系统的 Win+Shift+S）。</summary>
    public string HotkeyScreenshot { get; set; } = "Ctrl+Shift+A";
    /// <summary>截图并 OCR 的热键。</summary>
    public string HotkeyScreenshotOcr { get; set; } = "Ctrl+Shift+T";
    /// <summary>新图片进入历史时自动 OCR。</summary>
    public bool OcrAutoOnNewImage { get; set; }
    public string OcrLanguage { get; set; } = "auto";
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>DPAPI(CurrentUser) 加密后再 base64 的 API Key，请用 GetAiApiKey/SetAiApiKey 存取。</summary>
    public string AiApiKeyProtected { get; set; } = "";
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiTargetLanguage { get; set; } = "auto";
    /// <summary>单轮动作是整段超时；多轮对话里这个值改成「多久没收到新数据算断」。</summary>
    public int AiTimeoutSeconds { get; set; } = 60;
    /// <summary>AI 对话页的系统提示词，留空用内置的。建会话时快照进会话，改它不影响旧对话。</summary>
    public string ChatSystemPrompt { get; set; } = "";
    /// <summary>每次请求带上的历史字符预算，超出的旧消息整条丢弃。</summary>
    public int ChatContextChars { get; set; } = 6000;
    /// <summary>对话页专用模型，留空跟随 AiModel。</summary>
    public string ChatModel { get; set; } = "";
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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save()
    {
        try
        {
            StoragePaths.EnsureCreated();
            AtomicJson.Write(StoragePaths.SettingsJson, Settings, JsonOptions);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>取出明文 API Key；未设置或解密失败（换机/换用户）返回 null，不抛异常。</summary>
    public string? GetAiApiKey()
    {
        var protectedKey = Settings.AiApiKeyProtected;
        if (string.IsNullOrWhiteSpace(protectedKey)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入 API Key（DPAPI CurrentUser 加密 + base64）；null/空表示清除。加密失败时同样清除，避免留下脏值。</summary>
    public void SetAiApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            Settings.AiApiKeyProtected = "";
        }
        else
        {
            try
            {
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
                Settings.AiApiKeyProtected = Convert.ToBase64String(cipher);
            }
            catch
            {
                Settings.AiApiKeyProtected = "";
            }
        }
        Save();
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
        if (!File.Exists(StoragePaths.SettingsJson)) return;
        try
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StoragePaths.SettingsJson), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (AtomicJson.IsCorruptionError(ex))
        {
            // 文件存在但解析失败：先改名保留现场，避免后续 Save 把损坏内容悄悄覆盖
            TryMarkCorrupt(StoragePaths.SettingsJson);
            Settings = new AppSettings();
        }
        catch
        {
            // 瞬时 I/O 失败（文件被杀软/备份占用等）：文件本身健康，不动它，本次会话退回默认设置
            Settings = new AppSettings();
        }
    }

    private static void TryMarkCorrupt(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
