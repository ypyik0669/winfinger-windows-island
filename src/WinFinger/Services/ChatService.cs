using System.Text;
using System.Windows.Threading;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>
/// AI 对话的流式驱动。放在服务层而不是页面里，是为了让「切标签页 / 收起面板」不打断生成——
/// 页面随时可能被藏起来（ExpandedPanelView 只换 PageHost.Content），而请求得继续跑完。
/// 同时只跑一条流：一个 HttpClient、一个「停止」按钮，账也好算。
/// </summary>
public sealed class ChatService
{
    /// <summary>界面批量刷新间隔：逐个 token 通知会把 markdown 渲染压垮。</summary>
    private const int FlushMs = 50;

    /// <summary>流式过程中的落盘检查点间隔：崩溃最多丢这么久的字。</summary>
    private const int CheckpointMs = 5000;

    private readonly AiService _ai;
    private readonly ChatStore _store;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flushTimer;
    private readonly CancellationTokenSource _appCts = new();

    private StreamRun? _run;
    private DateTime _lastCheckpoint;

    public ChatService(AiService ai, ChatStore store, SettingsService settings)
    {
        _ai = ai;
        _store = store;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(FlushMs) };
        _flushTimer.Tick += (_, _) => Flush();
    }

    /// <summary>正在生成的会话；null = 空闲。只在 UI 线程读写。</summary>
    public ChatSession? ActiveSession => _run?.Session;

    public bool IsStreaming => _run is not null;

    /// <summary>生成开始 / 结束 / 每次批量刷新后触发，页面据此更新按钮与状态文字。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 发一轮。永不抛异常：错误写进那条助手消息的 Error 里。必须在 UI 线程调用。
    /// </summary>
    public async Task SendAsync(ChatSession session, string userText, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;

        // 单一在飞流：先把上一条收干净（半截文本保留）
        StopActive();

        if (!_ai.IsConfigured)
        {
            _store.AppendUser(session, userText, source);
            var refused = _store.BeginAssistant(session);
            _store.CompleteAssistant(session, refused, "", "未配置 AI，请在托盘 → 功能设置 中填写 API Key", stopped: false);
            StateChanged?.Invoke();
            return;
        }

        _store.AppendUser(session, userText, source);
        var reply = _store.BeginAssistant(session);
        var run = new StreamRun(session, reply, CancellationTokenSource.CreateLinkedTokenSource(_appCts.Token));
        _run = run;
        session.IsStreaming = true;
        _lastCheckpoint = DateTime.UtcNow;
        _flushTimer.Start();
        StateChanged?.Invoke();

        var cfg = _settings.Settings;
        string systemPrompt = string.IsNullOrWhiteSpace(session.SystemPrompt)
            ? (string.IsNullOrWhiteSpace(cfg.ChatSystemPrompt) ? AiService.ChatSystemPrompt : cfg.ChatSystemPrompt)
            : session.SystemPrompt;
        var turns = ChatStore.BuildContext(session, systemPrompt, cfg.ChatContextChars);
        // 优先级：本会话选的模型 > 对话页专用模型 > 全局模型
        string? model = !string.IsNullOrWhiteSpace(session.Model) ? session.Model
            : string.IsNullOrWhiteSpace(cfg.ChatModel) ? null : cfg.ChatModel;

        string? error = null;
        try
        {
            // ConfigureAwait(false)：网络循环留在线程池，不跟 UI 线程（玻璃取景等）抢时间；
            // 每个 chunk 只进缓冲区，界面由 50ms 的 flush 定时器主动拉。
            await foreach (string chunk in _ai.StreamChatAsync(turns, model, run.Cts.Token).ConfigureAwait(false))
                run.Append(chunk);
        }
        catch (OperationCanceledException)
        {
            error = null; // 用户点了停止 / 应用退出，不算错误
        }
        catch (AiException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = $"AI 请求失败：{ex.Message}";
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() => Settle(run, error, stopped: run.StopRequested));
            run.Cts.Dispose();
        }
    }

    /// <summary>
    /// 用户点「停止」：取消并立刻把已收到的文本写回去，不等 socket 解开——
    /// TLS 读有时要几百毫秒才响应取消，界面不该跟着卡。Settle 是幂等的，晚到的收尾不会写坏。
    /// </summary>
    public void StopActive()
    {
        if (_run is not { } run) return;
        run.StopRequested = true;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
        Settle(run, null, stopped: true);
    }

    /// <summary>应用退出：取消在飞流并把半截文本落到内存对象上（落盘由 ChatStore.SaveNow 负责）。</summary>
    public void Stop()
    {
        StopActive();
        try { _appCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _flushTimer.Stop();
    }

    /// <summary>把缓冲区里的文本刷进消息（UI 线程），顺便按间隔打检查点。</summary>
    private void Flush()
    {
        if (_run is not { } run)
        {
            _flushTimer.Stop();
            return;
        }

        string text = run.Snapshot();
        // 界面上也按落盘口径截断：否则超过上限后这里写完整版、5s 一次的检查点写截断版，
        // 气泡里的「（已截断）」会一闪一闪，markdown 还得整篇重解析两次
        if (text.Length != run.FlushedLength)
        {
            run.FlushedLength = text.Length;
            run.Message.Content = ChatStore.TrimForStorage(text, out _);
            StateChanged?.Invoke();
        }

        if ((DateTime.UtcNow - _lastCheckpoint).TotalMilliseconds >= CheckpointMs && text.Length > 0)
        {
            _lastCheckpoint = DateTime.UtcNow;
            _store.Checkpoint(run.Session, run.Message, text);
        }
    }

    /// <summary>
    /// 收尾：把文本写回消息、清 partial、结束 UI 状态。幂等——StopActive 先调一次、
    /// 网络晚几百毫秒解开时 finally 里再调一次，第二次会被 Settled 挡掉（但仍合并更长的文本）。
    /// </summary>
    private void Settle(StreamRun run, string? error, bool stopped)
    {
        string text = run.Snapshot();
        if (run.Settled)
        {
            // 迟到的收尾：只在拿到了更多文本时补写。走 Checkpoint 而不是直接改 Content，
            // 这样超长时照样截断、侧栏预览也跟着刷新
            if (text.Length > run.FlushedLength)
            {
                run.FlushedLength = text.Length;
                _store.Checkpoint(run.Session, run.Message, text);
            }
            return;
        }

        run.Settled = true;
        run.FlushedLength = text.Length;
        _store.CompleteAssistant(run.Session, run.Message, text,
            error ?? (text.Length == 0 && !stopped ? "AI 没有返回内容" : null), stopped);

        if (ReferenceEquals(_run, run))
        {
            _run = null;
            _flushTimer.Stop();
        }
        StateChanged?.Invoke();
    }

    /// <summary>一次生成的状态。每次发送都新建一个，避免「停止后立刻重发」把旧缓冲区清掉丢字。</summary>
    private sealed class StreamRun
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        public StreamRun(ChatSession session, ChatMessage message, CancellationTokenSource cts)
        {
            Session = session;
            Message = message;
            Cts = cts;
        }

        public ChatSession Session { get; }
        public ChatMessage Message { get; }
        public CancellationTokenSource Cts { get; }
        public bool Settled { get; set; }
        public bool StopRequested { get; set; }

        /// <summary>最近一次刷进消息的文本长度（消息里的文本可能被截断过，不能拿它当基准）。</summary>
        public int FlushedLength { get; set; }

        public void Append(string chunk)
        {
            lock (_gate) _text.Append(chunk);
        }

        public string Snapshot()
        {
            lock (_gate) return _text.ToString();
        }
    }
}
