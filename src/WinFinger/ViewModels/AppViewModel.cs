using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinger.Models;
using WinFinger.Services;

namespace WinFinger.ViewModels;

public enum AppPage
{
    Clipboard,
    Media,
    Notes,
    Shortcuts,
    Pomodoro,
    Chat
}

public static class AppPageInfo
{
    /// <summary>mac AppPage.title.</summary>
    public static string Title(this AppPage page) => page switch
    {
        AppPage.Clipboard => "剪贴板",
        AppPage.Media => "音乐",
        AppPage.Notes => "便利贴",
        AppPage.Shortcuts => "快捷键",
        AppPage.Pomodoro => "番茄钟",
        AppPage.Chat => "对话",
        _ => ""
    };

    /// <summary>Segoe Fluent / MDL2 glyph standing in for the mac SF symbol.</summary>
    public static string Glyph(this AppPage page) => page switch
    {
        AppPage.Clipboard => "\uE77F", // doc.on.clipboard
        AppPage.Media => "\uE8D6",     // music.note
        AppPage.Notes => "\uE70B",     // note.text
        AppPage.Shortcuts => "\uE765", // command
        AppPage.Pomodoro => "\uE823",  // timer
        AppPage.Chat => "\uE8BD",      // message
        _ => ""
    };
}

/// <summary>Central application state (counterpart of mac's AppModel).</summary>
public sealed partial class AppViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private AppPage _selectedPage = AppPage.Clipboard;

    /// <summary>"black" (纯黑) or "glass" (Liquid Glass) — mac MacFingerAppearance.</summary>
    [ObservableProperty] private string _appearanceStyle = "glass";
    /// <summary>"top" or "floating" — mac MacFingerDockMode.</summary>
    [ObservableProperty] private string _dockMode = "top";
    /// <summary>Locked panel: clicking outside doesn't collapse — mac isExpandedPinned.</summary>
    [ObservableProperty] private bool _isExpandedPinned;
    /// <summary>User-chosen expanded width (0 = default) — mac expandedUserSize.</summary>
    [ObservableProperty] private double _expandedUserWidth;
    /// <summary>True while the island is being dragged (lightweight glass, paused ambience).</summary>
    [ObservableProperty] private bool _isDraggingPanel;

    public MetricsService Metrics { get; } = new();
    public ClipboardStore ClipboardStore { get; } = new();
    public ClipboardMonitorService ClipboardMonitor { get; }
    public NoteStore Notes { get; } = new();
    public ChatStore Chat { get; } = new();
    public ShortcutCatalogService ShortcutCatalog { get; } = new();
    public ForegroundAppService ForegroundApp { get; } = new();
    public MediaService Media { get; } = new();
    public LyricsService Lyrics { get; } = new();
    public AudioVisualizerService Visualizer { get; } = new();
    public PomodoroService Pomodoro { get; } = new();
    public NotificationService Notifications { get; } = new();
    public SettingsService SettingsStore { get; } = new();
    public ThemeService Theme { get; } = new();
    public FocusRestoreService FocusRestore { get; } = new();
    public HotkeyService Hotkeys { get; } = new();
    public PasteService Paste { get; }
    public OcrService Ocr { get; } = new();
    public ScreenshotService Screenshot { get; }
    public AiService Ai { get; }
    public ChatService ChatStream { get; }
    public ActionCatalogService Actions { get; }

    /// <summary>动作执行器（结果抽屉挂上来之后才有）。</summary>
    public ActionExecutor? Executor { get; private set; }

    /// <summary>自动 OCR 串行队列（一次只跑一张图，避免 CPU 抖动）。</summary>
    private readonly SemaphoreSlim _autoOcrGate = new(1, 1);
    private readonly CancellationTokenSource _autoOcrCts = new();
    private volatile bool _autoOcrStopped;

    /// <summary>剪贴板条目动作扩展点：OCR / AI 等能力在这里注册自己的菜单项。</summary>
    public ObservableCollection<IEntryActionProvider> EntryActionProviders { get; } = new();

    /// <summary>Raised by the window layer when Ctrl+N is pressed while the notes page is showing.</summary>
    public event Action? NewNoteRequested;

    /// <summary>Ctrl+N 在对话页 = 新建对话。</summary>
    public event Action? NewChatRequested;

    /// <summary>让对话页显示某个会话，并把文本填进输入框（不自动发送）。</summary>
    public event Action<Models.ChatSession, string, string?>? ChatPrefillRequested;

    /// <summary>请求打开"功能设置"窗口（Task 14 接管；未接管时只提示）。</summary>
    public event Action? FeatureSettingsRequested;

    public AppViewModel()
    {
        ClipboardMonitor = new ClipboardMonitorService(ClipboardStore, ForegroundApp, SettingsStore);
        Paste = new PasteService(ClipboardMonitor, ClipboardStore, FocusRestore, this, Notifications);
        Ai = new AiService(SettingsStore);
        ChatStream = new ChatService(Ai, Chat, SettingsStore);
        EntryActionProviders.Add(new ChatEntryActionProvider(this));
        Actions = new ActionCatalogService(Notifications);
        Screenshot = new ScreenshotService(this);
        ActionCatalogService.Current = Actions;

        var settings = SettingsStore.Settings;
        ClipboardMonitor.IsPaused = settings.ClipboardPaused;
        Pomodoro.FocusMinutes = settings.PomodoroFocusMinutes;
        Pomodoro.BreakMinutes = settings.PomodoroBreakMinutes;
        Pomodoro.CompletedFocusCount = Math.Max(0, settings.PomodoroCompletedFocusCount);
        _appearanceStyle = settings.AppearanceStyle == "black" ? "black" : "glass";
        _dockMode = settings.DockMode == "floating" ? "floating" : "top";
        _isExpandedPinned = settings.IsExpandedPinned;
        _expandedUserWidth = settings.ExpandedUserWidth;

        ClipboardMonitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipboardMonitorService.IsPaused))
            {
                settings.ClipboardPaused = ClipboardMonitor.IsPaused;
                SettingsStore.Save();
            }
        };
        Pomodoro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PomodoroService.FocusMinutes) or nameof(PomodoroService.BreakMinutes))
            {
                settings.PomodoroFocusMinutes = Pomodoro.FocusMinutes;
                settings.PomodoroBreakMinutes = Pomodoro.BreakMinutes;
                SettingsStore.Save();
            }
            else if (e.PropertyName == nameof(PomodoroService.CompletedFocusCount))
            {
                settings.PomodoroCompletedFocusCount = Pomodoro.CompletedFocusCount;
                SettingsStore.Save();
            }
        };
        Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaService.IsPlaying))
            {
                if (Media.IsPlaying) Visualizer.Start();
                else Visualizer.Stop();
            }
        };
        Pomodoro.PhaseCompleted += phase =>
        {
            Notifications.Post("🍅", phase == Services.PomodoroPhase.Focus ? "专注结束，休息一下" : "休息结束，开始专注");
            System.Media.SystemSounds.Asterisk.Play();
        };
        ClipboardMonitor.Captured += entry =>
        {
            if (IsExpanded) return;
            var preview = entry.Kind switch
            {
                Models.ClipboardEntryKind.Image => "已记录图片",
                Models.ClipboardEntryKind.File => entry.DisplayTitle,
                _ => Truncate(entry.Text ?? "", 24)
            };
            Notifications.Post("📋", preview);
        };
        ClipboardMonitor.Captured += entry =>
        {
            // 自动 OCR 默认关闭（CPU / 隐私），开启后新图片串行排队识别
            if (!SettingsStore.Settings.OcrAutoOnNewImage) return;
            if (entry.Kind != Models.ClipboardEntryKind.Image) return;
            if (entry.OcrText is not null) return;
            _ = QueueAutoOcrAsync(entry);
        };
    }

    private async Task QueueAutoOcrAsync(Models.ClipboardEntry entry)
    {
        CancellationToken token;
        try
        {
            if (_autoOcrStopped) return;
            token = _autoOcrCts.Token;
            await _autoOcrGate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return; // 已退出
        }
        try
        {
            await Task.Delay(200, token); // 连续复制时防抖
            if (entry.OcrText is not null) return;
            await Ocr.RecognizeEntryAsync(entry, ClipboardStore, SettingsStore.Settings.OcrLanguage, token);
        }
        catch (OperationCanceledException)
        {
            // 退出中
        }
        catch
        {
            // 自动识别失败静默忽略，用户仍可手动触发
        }
        finally
        {
            try
            {
                _autoOcrGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Stop() 已释放，无需归还
            }
        }
    }

    partial void OnAppearanceStyleChanged(string value)
    {
        SettingsStore.Settings.AppearanceStyle = value;
        SettingsStore.Save();
        Theme.SetAppearanceStyle(value);
    }

    partial void OnDockModeChanged(string value)
    {
        SettingsStore.Settings.DockMode = value;
        SettingsStore.Save();
    }

    partial void OnIsExpandedPinnedChanged(bool value)
    {
        SettingsStore.Settings.IsExpandedPinned = value;
        SettingsStore.Save();
    }

    partial void OnExpandedUserWidthChanged(double value)
    {
        SettingsStore.Settings.ExpandedUserWidth = value;
        SettingsStore.Save();
    }

    /// <summary>结果抽屉就绪后挂上来，动作才能把结果显示出去。</summary>
    public ActionExecutor AttachPresenter(IResultPresenter presenter)
    {
        Executor = new ActionExecutor(this, presenter);
        return Executor;
    }

    /// <summary>没有订阅者（功能设置窗口还没做）时退化成一条提示。</summary>
    public void RequestOpenFeatureSettings()
    {
        if (FeatureSettingsRequested is { } handler) handler();
        else Notifications.Post("⚙️", "未配置 AI，请在托盘 → 功能设置 中填写 API Key");
    }

    public void Start()
    {
        StoragePaths.EnsureCreated();
        Actions.Start();
        Theme.Start(AppearanceStyle);
        Metrics.Start();
        ForegroundApp.Start();
        Media.Start();
        Lyrics.Start(Media);
    }

    public void Stop()
    {
        ChatStream.Stop();
        _autoOcrStopped = true;
        _autoOcrCts.Cancel();
        _autoOcrCts.Dispose();
        _autoOcrGate.Dispose();
        Actions.Stop();
        Theme.Stop();
        Metrics.Stop();
        ForegroundApp.Stop();
        ClipboardMonitor.Detach();
        Visualizer.Stop();
        Lyrics.Stop();
        Media.Stop();
        Pomodoro.Pause();
    }

    private static string Truncate(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>本次收起是否把焦点还给展开前的前台窗口（点击外部 / 粘贴流程会置 false）。</summary>
    public bool RestoreFocusOnCollapse { get; set; } = true;

    public void Collapse() => IsExpanded = false;

    /// <summary>收起但不还原焦点：用户已经点到别的窗口，或调用方自己接管前台切换。</summary>
    public void CollapseWithoutFocusRestore()
    {
        RestoreFocusOnCollapse = false;
        IsExpanded = false;
    }

    public void ToggleExpandedPinned() => IsExpandedPinned = !IsExpandedPinned;

    public void Select(AppPage page)
    {
        SelectedPage = page;
        IsExpanded = true;
    }

    public void RequestNewNote() => NewNoteRequested?.Invoke();

    public void RequestNewChat() => NewChatRequested?.Invoke();

    /// <summary>抽屉里的 AI 结果「继续追问」：把原文与结果作为一轮历史放进新会话，输入框留空。</summary>
    public void ContinueInChat(string result, Models.ClipboardEntry? source)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        var session = Chat.Create(ChatPrompt());
        string? origin = source?.Text ?? source?.OcrText;
        if (!string.IsNullOrWhiteSpace(origin)) Chat.AppendUser(session, origin!, "来自剪贴板");
        Chat.AppendAssistant(session, result);
        Select(AppPage.Chat);
        ChatPrefillRequested?.Invoke(session, "", null);
    }

    /// <summary>剪贴板条目送进对话页：切页并把文本填进输入框，等用户补一句要求再发。</summary>
    public void SendToChat(string text, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // 最新的会话还是空的就复用它，避免右键几次攒出一堆空会话
        var session = Chat.Sessions.FirstOrDefault() is { Messages.Count: 0 } empty
            ? empty
            : Chat.Create(ChatPrompt());
        Select(AppPage.Chat);
        ChatPrefillRequested?.Invoke(session, text, source);
    }

    /// <summary>建会话时用的系统提示词：设置里留空就用内置的。</summary>
    public string ChatPrompt() => string.IsNullOrWhiteSpace(SettingsStore.Settings.ChatSystemPrompt)
        ? AiService.ChatSystemPrompt
        : SettingsStore.Settings.ChatSystemPrompt;
}
