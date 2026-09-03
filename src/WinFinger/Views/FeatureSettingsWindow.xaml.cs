using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinFinger.Controls;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views;

/// <summary>功能设置：快捷键、OCR、AI、动作目录。改一项存一项。</summary>
public partial class FeatureSettingsWindow : Window
{
    /// <summary>模型下拉的预设（可编辑，用户能直接输入别的）。</summary>
    private static readonly string[] ModelPresets =
    {
        "gpt-4o-mini", "gpt-4o", "deepseek-chat", "qwen-plus", "llama3"
    };

    /// <summary>翻译目标语言：显示名 → 设置值。</summary>
    private static readonly (string Label, string Value)[] TargetLanguages =
    {
        ("自动", "auto"), ("中文", "zh"), ("英文", "en"), ("日文", "ja")
    };

    private const int MinChatContextChars = 1000;
    private const int MaxChatContextChars = 40000;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 300;

    private readonly AppViewModel _model;
    private readonly IslandWindow _island;
    private CancellationTokenSource? _testCts;
    private bool _loading = true;
    private bool _closed;

    public FeatureSettingsWindow(AppViewModel model, IslandWindow island)
    {
        _model = model;
        _island = island;
        InitializeComponent();
        _model.Actions.Changed += OnActionsChanged;
        Closed += OnWindowClosed;
        LoadFromSettings(); // 结尾会把 _loading 放掉
    }

    private AppSettings S => _model.SettingsStore.Settings;

    // ── 载入 ──

    private void LoadFromSettings()
    {
        _loading = true;

        ClipboardHotkeyBox.Gesture = S.ClipboardHotkey ?? "";
        ScreenshotHotkeyBox.Gesture = S.HotkeyScreenshot ?? "";
        ScreenshotOcrHotkeyBox.Gesture = S.HotkeyScreenshotOcr ?? "";
        PasteAfterSelectCheck.IsChecked = S.PasteAfterSelect;

        OcrAutoCheck.IsChecked = S.OcrAutoOnNewImage;
        BuildOcrLanguages();
        BuildAiControls();
        RefreshActionInfo();

        _loading = false;
    }

    private void BuildOcrLanguages()
    {
        OcrLanguageCombo.Items.Clear();
        OcrLanguageCombo.Items.Add(new ComboBoxItem { Content = "自动", Tag = "auto" });
        foreach (var tag in _model.Ocr.AvailableLanguages)
            OcrLanguageCombo.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });
        SelectByTag(OcrLanguageCombo, string.IsNullOrWhiteSpace(S.OcrLanguage) ? "auto" : S.OcrLanguage);

        bool available = _model.Ocr.IsAvailable;
        OcrUnavailableText.Text = OcrService.UnavailableMessage;
        OcrUnavailableText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        OcrLanguageSettingsButton.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildAiControls()
    {
        BaseUrlBox.Text = S.AiBaseUrl ?? "";

        ModelCombo.Items.Clear();
        foreach (var preset in ModelPresets) ModelCombo.Items.Add(preset);
        ModelCombo.Text = S.AiModel ?? "";

        TargetLanguageCombo.Items.Clear();
        foreach (var (label, value) in TargetLanguages)
            TargetLanguageCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        SelectByTag(TargetLanguageCombo, string.IsNullOrWhiteSpace(S.AiTargetLanguage) ? "auto" : S.AiTargetLanguage);

        TimeoutBox.Text = S.AiTimeoutSeconds.ToString();
        ChatModelBox.Text = S.ChatModel;
        ChatPromptBox.Text = S.ChatSystemPrompt;
        ChatContextBox.Text = S.ChatContextChars.ToString();
        RefreshApiKeyUi();
    }

    /// <summary>Key 永远不回显，只用文案说明"已保存"。</summary>
    private void RefreshApiKeyUi()
    {
        bool hasKey = !string.IsNullOrWhiteSpace(S.AiApiKeyProtected);
        ApiKeyLabel.Text = hasKey ? "API Key（已保存 ••••，留空不修改）" : "API Key";
        ClearKeyButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Clear();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem { Tag: string t } && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "auto";

    // ── 快捷键 ──

    private void OnClipboardHotkeyChanged(object? sender, EventArgs e) =>
        CommitHotkey(HotkeyService.HotkeyClipboard, ClipboardHotkeyBox, ClipboardHotkeyError,
            () => S.ClipboardHotkey, g => S.ClipboardHotkey = g);

    private void OnScreenshotHotkeyChanged(object? sender, EventArgs e) =>
        CommitHotkey(HotkeyService.HotkeyScreenshot, ScreenshotHotkeyBox, ScreenshotHotkeyError,
            () => S.HotkeyScreenshot, g => S.HotkeyScreenshot = g);

    private void OnScreenshotOcrHotkeyChanged(object? sender, EventArgs e) =>
        CommitHotkey(HotkeyService.HotkeyScreenshotOcr, ScreenshotOcrHotkeyBox, ScreenshotOcrHotkeyError,
            () => S.HotkeyScreenshotOcr, g => S.HotkeyScreenshotOcr = g);

    /// <summary>
    /// 先拿新手势去注册，成功了才写设置并落盘——
    /// 注册不上的手势绝不能进 settings.json，否则下次启动就没热键了。
    /// 失败时捕获框回滚到旧值、旧绑定保持有效，并亮红字提示。
    /// </summary>
    private void CommitHotkey(int id, HotkeyCaptureBox box, TextBlock error, Func<string> read, Action<string> assign)
    {
        if (_loading) return;
        string previous = read() ?? "";
        string candidate = box.Gesture ?? "";
        if (string.Equals(previous, candidate, StringComparison.Ordinal))
        {
            error.Visibility = Visibility.Collapsed;
            return;
        }

        if (_island.ApplyHotkey(id, candidate))
        {
            assign(candidate);
            _model.SettingsStore.Save();
            error.Visibility = Visibility.Collapsed;
            return;
        }

        // 注册失败：设置文件没动过，界面也退回旧手势
        error.Visibility = Visibility.Visible;
        _loading = true;
        box.Gesture = previous;
        _loading = false;
    }

    private void OnPasteAfterSelectChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.PasteAfterSelect = PasteAfterSelectCheck.IsChecked == true;
        _model.SettingsStore.Save();
    }

    // ── OCR ──

    private void OnOcrAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.OcrAutoOnNewImage = OcrAutoCheck.IsChecked == true;
        _model.SettingsStore.Save();
    }

    private void OnOcrLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.OcrLanguage = TagOf(OcrLanguageCombo);
        _model.SettingsStore.Save();
    }

    private void OnOpenLanguageSettings(object sender, RoutedEventArgs e) =>
        TryShellExecute(OcrService.LanguageSettingsUri);

    // ── AI ──

    private void OnEnterCommits(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is UIElement element)
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); // 触发 LostFocus 提交
    }

    private void OnBaseUrlCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var text = BaseUrlBox.Text.Trim();
        if (string.Equals(text, S.AiBaseUrl, StringComparison.Ordinal)) return;
        S.AiBaseUrl = text;
        _model.SettingsStore.Save();
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedItem is not string model) return;
        CommitModel(model);
    }

    private void OnModelCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CommitModel(ModelCombo.Text);
    }

    private void CommitModel(string model)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0 || string.Equals(model, S.AiModel, StringComparison.Ordinal)) return;
        S.AiModel = model;
        _model.SettingsStore.Save();
    }

    /// <summary>
    /// 从当前接口地址拉一遍模型列表填进下拉框。换成自建/中转接口时，预设里那几个 OpenAI 名字
    /// 基本都不存在，不拉一次就只能靠猜——猜错的表现是每次动作都报「模型或地址不存在」。
    /// </summary>
    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        FetchModelsButton.IsEnabled = false;
        string original = (string)FetchModelsButton.Content;
        FetchModelsButton.Content = "获取中…";
        try
        {
            var models = await _model.Ai.ListModelsAsync(CancellationToken.None);
            if (models.Count == 0)
            {
                _model.Notifications.Post("🤖", "没能获取模型列表，检查接口地址和 Key");
                return;
            }

            string current = ModelCombo.Text;
            ModelCombo.Items.Clear();
            foreach (string id in models) ModelCombo.Items.Add(id);
            ModelCombo.Text = current; // 别把用户已经填的名字冲掉
            _model.Notifications.Post("🤖", $"取到 {models.Count} 个模型");
        }
        finally
        {
            FetchModelsButton.Content = original;
            FetchModelsButton.IsEnabled = true;
        }
    }

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.AiTargetLanguage = TagOf(TargetLanguageCombo);
        _model.SettingsStore.Save();
    }

    private void OnTimeoutCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(TimeoutBox.Text.Trim(), out int seconds))
            S.AiTimeoutSeconds = Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);
        TimeoutBox.Text = S.AiTimeoutSeconds.ToString();
        _model.SettingsStore.Save();
    }

    private void OnChatModelCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ChatModel = ChatModelBox.Text.Trim();
        _model.SettingsStore.Save();
    }

    private void OnChatPromptCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ChatSystemPrompt = ChatPromptBox.Text.Trim();
        _model.SettingsStore.Save();
    }

    private void OnChatContextCommit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(ChatContextBox.Text.Trim(), out int chars))
            S.ChatContextChars = Math.Clamp(chars, MinChatContextChars, MaxChatContextChars);
        ChatContextBox.Text = S.ChatContextChars.ToString();
        _model.SettingsStore.Save();
    }

    private void OnApiKeyCommit(object sender, RoutedEventArgs e) => SaveApiKey();

    private void OnSaveApiKey(object sender, RoutedEventArgs e) => SaveApiKey();

    /// <summary>留空 = 不改动已保存的 Key（避免误清空）。</summary>
    private void SaveApiKey()
    {
        if (_loading) return;
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key)) return;
        _model.SettingsStore.SetAiApiKey(key.Trim()); // 内部 Save()
        RefreshApiKeyUi();
        ShowTestResult("API Key 已保存", ok: true);
    }

    private void OnClearApiKey(object sender, RoutedEventArgs e)
    {
        _model.SettingsStore.SetAiApiKey(null);
        RefreshApiKeyUi();
        ShowTestResult("API Key 已清除", ok: true);
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _testCts = cts;

        TestButton.IsEnabled = false;
        ShowTestResult("测试中…", ok: true);
        try
        {
            var (ok, message) = await _model.Ai.TestAsync(cts.Token);
            if (_closed) return;
            ShowTestResult(ok ? "✓ " + message : message, ok);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ShowTestResult("测试超时或已取消", ok: false);
        }
        catch (Exception ex)
        {
            if (!_closed) ShowTestResult(ex.Message, ok: false);
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts))
            {
                _testCts = null;
                cts.Dispose();
            }
            if (!_closed) TestButton.IsEnabled = true;
        }
    }

    private void ShowTestResult(string message, bool ok)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Brush.Green" : "Brush.Danger");
        TestResultText.Visibility = Visibility.Visible;
    }

    // ── 动作 ──

    private void OnActionsChanged() => Dispatcher.BeginInvoke(RefreshActionInfo);

    private void RefreshActionInfo()
    {
        ActionCountText.Text = $"已加载 {_model.Actions.All.Count} 个动作";
        var error = _model.Actions.LastError;
        ActionErrorText.Text = error ?? "";
        ActionErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRevealActions(object sender, RoutedEventArgs e)
    {
        var path = _model.Actions.ActionsPath;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                TryShellExecute(Path.GetDirectoryName(path) ?? path);
        }
        catch
        {
            // 资源管理器打不开就算了
        }
    }

    private void OnReloadActions(object sender, RoutedEventArgs e)
    {
        _model.Actions.Reload();
        RefreshActionInfo();
    }

    private void OnRestoreActions(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "将用内置默认内容覆盖 actions.json，你的自定义动作会丢失。确定继续吗？", "恢复默认动作",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        _model.Actions.RestoreDefaults();
        RefreshActionInfo();
    }

    // ── 窗口 ──

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _model.Actions.Changed -= OnActionsChanged;
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (TextBox or PasswordBox or ComboBox or ComboBoxItem or Button or CheckBox))
            try { DragMove(); } catch { }
    }

    private static void TryShellExecute(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 目标不可用（例如系统设置被策略禁用）时静默
        }
    }
}
