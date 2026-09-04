using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

/// <summary>
/// AI 对话页。流式驱动在 <see cref="Services.ChatService"/> 里，页面只负责显示——
/// 所以切到别的标签页或收起面板时生成不会中断（和剪贴板页的结果抽屉相反，那里是刻意掐掉的）。
/// </summary>
public partial class ChatPage : UserControl, IIslandPage
{
    /// <summary>离底部这么近就继续自动跟随，否则用户正在往回看，别把他拽走。</summary>
    private const double StickToBottomSlack = 24;

    private AppViewModel? _model;
    private ChatSession? _current;

    /// <summary>服务端模型列表缓存：菜单每次都拉一遍太慢，第一次打开时取一次。</summary>
    private IReadOnlyList<string> _models = Array.Empty<string>();
    private bool _modelsLoading;

    /// <summary>已经拉过一次列表（哪怕失败）：失败也别每次点都再等 15 秒。</summary>
    private bool _modelsTried;

    /// <summary>正在展开的模型菜单：列表回来时只刷新它，菜单已经关了就别再弹一次。</summary>
    private ContextMenu? _modelMenu;
    private bool _stickToBottom = true;

    /// <summary>ShowSession 的重入闸：SelectedItem 赋值会同步触发 SelectionChanged。</summary>
    private bool _switching;

    public ChatPage()
    {
        InitializeComponent();

        SessionList.SelectionChanged += (_, _) => ShowSession(SessionList.SelectedItem as ChatSession);
        NewButton.Click += (_, _) => CreateSession();
        SendButton.Click += (_, _) => SendOrStop();
        ConfigureButton.Click += (_, _) => _model?.RequestOpenFeatureSettings();
        ModelButton.Click += (_, _) => _model?.RequestOpenFeatureSettings();
        ModelChip.Click += (_, _) => ShowModelMenu();

        Composer.TextChanged += (_, _) =>
        {
            ComposerPlaceholder.Visibility = Composer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Composer.PreviewKeyDown += OnComposerKey;

        TitleEditor.LostFocus += (_, _) => CommitRename();
        TitleEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.ImeProcessed) return;
            if (e.Key == Key.Enter) { e.Handled = true; CommitRename(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); }
        };
        SessionTitle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) BeginRename();
        };

        Transcript.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0)
                _stickToBottom = Transcript.ScrollableHeight - Transcript.VerticalOffset <= StickToBottomSlack;
            else if (_stickToBottom)
                Transcript.ScrollToEnd();
        };
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        SessionList.ItemsSource = model.Chat.Sessions;
        model.Chat.Sessions.CollectionChanged += (_, _) => RefreshListState();
        model.ChatStream.StateChanged += OnStreamStateChanged;
        model.ChatPrefillRequested += OnPrefillRequested;
        model.NewChatRequested += () => CreateSession();
        // 用户可能就停在本页时去功能设置里填了 Key：设置一存就把输入框放开，
        // 不然要切走再切回来（OnShown 只在换页时才跑）才恢复
        model.SettingsStore.Changed += () =>
        {
            RefreshComposerState();
            RefreshModelChip();
        };

        // 代码块复制按钮走应用自己的剪贴板服务（会被自写回抑制识别，不会重复记录）
        Controls.MarkdownView.CopyHandler = text =>
        {
            model.ClipboardMonitor.CopyText(text);
            model.Notifications.Post("📋", "已复制代码");
        };

        RefreshListState();
        ShowSession(model.Chat.Sessions.FirstOrDefault());
        RefreshComposerState();
    }

    public void OnShown()
    {
        // 用户可能刚在功能设置里填了 Key，回到本页要立刻反映出来
        RefreshComposerState();
        RefreshModelChip();
        if (_current is null) ShowSession(_model?.Chat.Sessions.FirstOrDefault());
    }

    public void OnExpanded() => FocusComposer();

    public bool HandleEscape()
    {
        if (TitleEditor.Visibility == Visibility.Visible)
        {
            CancelRename();
            return true;
        }
        // 只停当前会话的流：Esc 不该把另一个会话正在跑的回答掐掉
        if (CurrentIsStreaming())
        {
            _model!.ChatStream.StopActive();
            return true;
        }
        if (Composer.Text.Length > 0)
        {
            Composer.Clear();
            return true;
        }
        return false;
    }

    // ── 会话 ──

    private ChatSession EnsureSession()
    {
        if (_current is not null) return _current;
        return CreateSession();
    }

    private ChatSession CreateSession()
    {
        var model = _model!;
        var session = model.Chat.Create(model.ChatPrompt());
        ShowSession(session);
        FocusComposer();
        return session;
    }

    private void ShowSession(ChatSession? session)
    {
        // 下面那行 SelectedItem 赋值会同步回调 SelectionChanged → 又进这里一次；
        // 不挡住的话消息事件会被订阅两次，旧会话上还会漏下一个
        if (_switching) return;
        _switching = true;
        try { SwitchTo(session); }
        finally { _switching = false; }
    }

    private void SwitchTo(ChatSession? session)
    {
        if (_current is { } previous) previous.Messages.CollectionChanged -= OnMessagesChanged;
        _current = session;
        SessionList.SelectedItem = session;
        MessageList.ItemsSource = session?.Messages;
        SessionTitle.Text = session?.Title ?? ChatSession.DefaultTitle;
        RefreshModelChip();
        if (session is not null)
        {
            session.Messages.CollectionChanged += OnMessagesChanged;
            _stickToBottom = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Transcript.ScrollToEnd());
        }
        RefreshListState();
        RefreshComposerState();
        RefreshStreamState();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshListState();
        if (_stickToBottom) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Transcript.ScrollToEnd());
    }

    private void RefreshListState()
    {
        bool hasSessions = _model?.Chat.Sessions.Count > 0;
        ListEmptyHint.Visibility = hasSessions ? Visibility.Collapsed : Visibility.Visible;
        if (_model?.Chat.LoadFailed == true) ListEmptyHint.Text = "历史读取失败，已留备份";

        bool hasMessages = _current?.Messages.Count > 0;
        EmptyPane.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
        Transcript.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
        if (_current is { } session) SessionTitle.Text = session.Title;
    }

    private void OnContextRename(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatSession session)
        {
            ShowSession(session);
            BeginRename();
        }
    }

    private void OnContextDelete(object sender, RoutedEventArgs e)
    {
        if (_model is null || (sender as FrameworkElement)?.DataContext is not ChatSession session) return;
        if (ReferenceEquals(_model.ChatStream.ActiveSession, session)) _model.ChatStream.StopActive();
        _model.Chat.Remove(session);
        // 删掉的是选中项时 Selector 会自己把 SelectedItem 清空（→ ShowSession(null)），
        // 所以这里比的是「现在还有没有选中会话」，而不是「删的是不是当前会话」
        if (_current is null) ShowSession(_model.Chat.Sessions.FirstOrDefault());
    }

    private void BeginRename()
    {
        if (_current is null) return;
        TitleEditor.Text = _current.Title;
        TitleEditor.Visibility = Visibility.Visible;
        SessionTitle.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TitleEditor.Focus();
            TitleEditor.SelectAll();
        });
    }

    private void CommitRename()
    {
        if (TitleEditor.Visibility != Visibility.Visible) return;
        if (_model is not null && _current is { } session) _model.Chat.Rename(session, TitleEditor.Text);
        CancelRename();
    }

    private void CancelRename()
    {
        TitleEditor.Visibility = Visibility.Collapsed;
        SessionTitle.Visibility = Visibility.Visible;
        if (_current is { } session) SessionTitle.Text = session.Title;
        FocusComposer();
    }

    // ── 发送 ──

    private void OnComposerKey(object sender, KeyEventArgs e)
    {
        // 中文输入法用 Enter 上屏候选词，没有这个判断就会把半句话发出去
        if (e.Key == Key.ImeProcessed) return;
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return; // Shift+Enter 换行

        e.Handled = true;
        SendOrStop();
    }

    private void SendOrStop()
    {
        if (_model is null) return;
        // 只有「正在看的这个会话」在生成时按钮才是停止；别的会话在跑不该吃掉这次回车
        if (CurrentIsStreaming())
        {
            _model.ChatStream.StopActive();
            return;
        }

        string text = Composer.Text.Trim();
        if (text.Length == 0) return;
        // 同时只跑一条流：在别的会话正在生成时发送，会把那边掐掉。掐可以，但得说一声
        if (_model.ChatStream.IsStreaming && !CurrentIsStreaming())
            _model.Notifications.Post("🤖", "已停止另一个会话的生成");
        var session = EnsureSession();
        Composer.Clear();
        _stickToBottom = true;
        // SendAsync 内部吞掉所有异常（错误写进消息里），所以这里可以安全地丢弃任务
        _ = _model.ChatStream.SendAsync(session, text);
    }

    /// <summary>会话由 AppViewModel 选好（右键发送 / 继续追问都走这里），页面只负责显示与聚焦。</summary>
    private void OnPrefillRequested(ChatSession session, string text, string? source)
    {
        ShowSession(session);
        // 只填进输入框，不自动发送：用户通常还要在粘来的内容前面补一句要求。
        // 「继续追问」传空串，正好把上一次残留的草稿清掉。
        Composer.Text = text;
        Composer.CaretIndex = Composer.Text.Length;
        _ = source;
        FocusComposer();
    }

    private void OnStreamStateChanged() => RefreshStreamState();

    /// <summary>
    /// 按钮 / 状态文字 / 自动滚动都只反映当前显示的会话：同时只跑一条流，
    /// 但用户可能正看着别的会话，别把那边的进度写在这里，也别把这里的滚动位置拽走。
    /// </summary>
    private void RefreshStreamState()
    {
        bool streaming = CurrentIsStreaming();
        SendButton.Content = streaming ? "\uE71A" : "\uE724";
        SendButton.ToolTip = streaming ? "停止生成（Esc）" : "发送（Enter）";
        StatusText.Text = streaming
            ? $"生成中… {_current?.Messages.LastOrDefault()?.Content.Length ?? 0} 字符"
            : _model?.ChatStream.IsStreaming == true ? "其他会话正在生成…" : "";
        if (streaming && _stickToBottom) Transcript.ScrollToEnd();
    }

    /// <summary>当前显示的会话是不是正在生成（同时只可能有一条流）。</summary>
    private bool CurrentIsStreaming() =>
        _current is not null && ReferenceEquals(_model?.ChatStream.ActiveSession, _current);

    private void RefreshComposerState()
    {
        if (_model is null) return;
        bool configured = _model.Ai.IsConfigured;
        ConfigureButton.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = configured ? "问点什么" : "未配置 AI";
        EmptySubtitle.Text = configured
            ? "内容会用你自己配置的接口发送"
            : "在托盘 → 功能设置 里填入 API Key 和接口地址";
        SendButton.IsEnabled = configured;
        Composer.IsEnabled = configured;
    }

    // ── 模型选择 ──

    /// <summary>当前会话实际会用的模型：会话 > 对话页设置 > 全局设置。</summary>
    private string CurrentModel()
    {
        var cfg = _model?.SettingsStore.Settings;
        if (_current is { Model: { Length: > 0 } sessionModel }) return sessionModel;
        if (cfg is null) return "";
        return string.IsNullOrWhiteSpace(cfg.ChatModel) ? cfg.AiModel : cfg.ChatModel;
    }

    private void RefreshModelChip()
    {
        string model = CurrentModel();
        ModelChip.Content = model.Length == 0 ? "选择模型" : model;
    }

    private void ShowModelMenu()
    {
        if (_model is null) return;
        // 先把菜单弹出来（哪怕只有「跟随设置」），列表回来再原地重建：
        // 之前是等列表到了才弹，接口慢的时候点了像没反应，等它到了菜单又弹在别的页面上
        BuildAndOpenModelMenu();
        if (!_modelsTried && !_modelsLoading) _ = LoadModelsAsync();
    }

    private void BuildAndOpenModelMenu()
    {
        if (_model is null) return;

        // 重建时先把上一个关掉，别叠两个弹出层
        if (_modelMenu is { IsOpen: true } previous) previous.IsOpen = false;

        var menu = new ContextMenu { PlacementTarget = ModelChip, Placement = PlacementMode.Bottom };
        var cfg = _model.SettingsStore.Settings;
        string fallback = string.IsNullOrWhiteSpace(cfg.ChatModel) ? cfg.AiModel : cfg.ChatModel;

        AddModelItem(menu, $"跟随设置（{fallback}）", null, string.IsNullOrEmpty(_current?.Model));
        if (_models.Count > 0)
        {
            menu.Items.Add(new Separator());
            // 只勾本会话明确选过的那个；跟随设置时勾的是上面那行，别两行同时打勾
            foreach (string id in _models) AddModelItem(menu, id, id, id == _current?.Model);
        }
        menu.Items.Add(new Separator());
        var refresh = new MenuItem
        {
            Header = _modelsLoading ? "正在获取模型列表…" : _models.Count == 0 ? "获取模型列表" : "刷新模型列表",
            IsEnabled = !_modelsLoading
        };
        refresh.Click += (_, _) =>
        {
            _models = Array.Empty<string>();
            _modelsTried = false;
            _ = LoadModelsAsync();
        };
        menu.Items.Add(refresh);

        menu.Closed += (_, _) => { if (ReferenceEquals(_modelMenu, menu)) _modelMenu = null; };
        _modelMenu = menu;
        menu.IsOpen = true;
    }

    private void AddModelItem(ContextMenu menu, string header, string? model, bool isCurrent)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isCurrent };
        item.Click += (_, _) =>
        {
            if (_model is null) return;
            // 还没有会话时先建一个，否则选了模型没处落，点了跟没点一样
            var session = EnsureSession();
            session.Model = model;
            _model.Chat.Save();
            // 同时记成对话页的默认模型：不然「新建对话」又退回全局模型，
            // 而全局那个是给翻译/总结用的，多半不是这个接口上存在的模型
            if (model is { Length: > 0 })
            {
                _model.SettingsStore.Settings.ChatModel = model;
                _model.SettingsStore.Save();
            }
            RefreshModelChip();
        };
        menu.Items.Add(item);
    }

    private async Task LoadModelsAsync()
    {
        if (_model is null || _modelsLoading) return;
        _modelsLoading = true;
        if (_modelMenu is { IsOpen: true }) BuildAndOpenModelMenu(); // 让菜单显示「正在获取…」
        try
        {
            _models = await _model.Ai.ListModelsAsync(CancellationToken.None);
        }
        finally
        {
            _modelsLoading = false;
            _modelsTried = true;
        }

        if (_models.Count == 0) _model.Notifications.Post("🤖", "没能获取模型列表，检查接口地址和 Key");
        // 只在菜单还开着时重建；用户早就点开别的页面了就别再弹一个出来
        if (_modelMenu is { IsOpen: true }) BuildAndOpenModelMenu();
    }

    private void FocusComposer() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        // 岛是 WS_EX_NOACTIVATE 窗口，同步 Focus() 会静默失败
        Composer.Focus();
        Composer.CaretIndex = Composer.Text.Length;
    });

    private void OnCopyMessage(object sender, RoutedEventArgs e)
    {
        if (_model is null || (sender as FrameworkElement)?.DataContext is not ChatMessage message) return;
        if (message.Content.Length == 0) return;
        _model.ClipboardMonitor.CopyText(message.Content);
        _model.Notifications.Post("📋", "已复制");
    }
}
