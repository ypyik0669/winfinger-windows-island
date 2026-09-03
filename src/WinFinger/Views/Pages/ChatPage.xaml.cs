using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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
    private bool _stickToBottom = true;

    public ChatPage()
    {
        InitializeComponent();

        SessionList.SelectionChanged += (_, _) => ShowSession(SessionList.SelectedItem as ChatSession);
        NewButton.Click += (_, _) => CreateSession();
        SendButton.Click += (_, _) => SendOrStop();
        ConfigureButton.Click += (_, _) => _model?.RequestOpenFeatureSettings();
        ModelButton.Click += (_, _) => _model?.RequestOpenFeatureSettings();

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
        if (_model?.ChatStream.IsStreaming == true)
        {
            _model.ChatStream.StopActive();
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
        if (_current is { } previous) previous.Messages.CollectionChanged -= OnMessagesChanged;
        _current = session;
        SessionList.SelectedItem = session;
        MessageList.ItemsSource = session?.Messages;
        SessionTitle.Text = session?.Title ?? ChatSession.DefaultTitle;
        if (session is not null)
        {
            session.Messages.CollectionChanged += OnMessagesChanged;
            _stickToBottom = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Transcript.ScrollToEnd());
        }
        RefreshListState();
        RefreshComposerState();
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
        if (ReferenceEquals(_current, session)) ShowSession(_model.Chat.Sessions.FirstOrDefault());
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
        if (_model.ChatStream.IsStreaming)
        {
            _model.ChatStream.StopActive();
            return;
        }

        string text = Composer.Text.Trim();
        if (text.Length == 0) return;
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

    private void OnStreamStateChanged()
    {
        bool streaming = _model?.ChatStream.IsStreaming == true;
        SendButton.Content = streaming ? "\uE71A" : "\uE724";
        SendButton.ToolTip = streaming ? "停止生成（Esc）" : "发送（Enter）";
        StatusText.Text = streaming && _model?.ChatStream.ActiveSession is { } active
            ? $"生成中… {active.Messages.LastOrDefault()?.Content.Length ?? 0} 字符"
            : "";
        if (_stickToBottom) Transcript.ScrollToEnd();
    }

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
