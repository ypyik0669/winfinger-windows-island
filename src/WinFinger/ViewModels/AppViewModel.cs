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
    Pomodoro
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

    /// <summary>剪贴板条目动作扩展点：OCR / AI 等能力在这里注册自己的菜单项。</summary>
    public ObservableCollection<IEntryActionProvider> EntryActionProviders { get; } = new();

    /// <summary>Raised by the window layer when Ctrl+N is pressed while the notes page is showing.</summary>
    public event Action? NewNoteRequested;

    public AppViewModel()
    {
        ClipboardMonitor = new ClipboardMonitorService(ClipboardStore, ForegroundApp, SettingsStore);
        Paste = new PasteService(ClipboardMonitor, ClipboardStore, FocusRestore, this, Notifications);

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

    public void Start()
    {
        StoragePaths.EnsureCreated();
        Theme.Start(AppearanceStyle);
        Metrics.Start();
        ForegroundApp.Start();
        Media.Start();
        Lyrics.Start(Media);
    }

    public void Stop()
    {
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
}
