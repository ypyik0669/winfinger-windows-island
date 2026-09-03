using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>chat.json 的根对象：留一个 version 字段，以后换布局时好迁移。</summary>
public sealed class ChatArchive
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("sessions")] public List<ChatSession> Sessions { get; set; } = new();
}

/// <summary>
/// AI 对话仓库：内存里按最近活跃倒序，落盘走原子写 + 去抖 + 串行任务链（同 ClipboardStore）。
/// 流式期间不逐块写盘，由 ChatService 每隔几秒打一次检查点，崩溃最多丢几秒的字。
/// </summary>
public sealed class ChatStore
{
    /// <summary>会话数上限，超出丢最旧的。</summary>
    public const int MaxSessions = 30;

    /// <summary>单会话消息条数上限，超出从最早的开始丢。</summary>
    public const int MaxMessagesPerSession = 200;

    /// <summary>单条消息落盘的字符上限：剪贴板里几十万字的文本不能整篇塞进对话。</summary>
    public const int MaxMessageChars = 20000;

    /// <summary>每次请求带上的历史字符预算默认值。</summary>
    public const int DefaultContextChars = 6000;

    /// <summary>每次请求带上的历史条数默认上限。</summary>
    public const int DefaultContextTurns = 20;

    public ObservableCollection<ChatSession> Sessions { get; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DispatcherTimer _saveTimer;

    /// <summary>串行化落盘任务链：避免旧快照后写覆盖新快照。</summary>
    private Task _flushChain = Task.CompletedTask;

    public ChatStore()
    {
        // 构造在 UI 线程（AppViewModel 字段初始化），DispatcherTimer 可安全创建
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = FlushAsync();
        };
        Load();
    }

    // ── 会话 ──

    public ChatSession Create(string systemPrompt)
    {
        var session = new ChatSession { SystemPrompt = systemPrompt };
        Sessions.Insert(0, session);
        TrimSessions();
        Save();
        return session;
    }

    public void Remove(ChatSession session)
    {
        Sessions.Remove(session);
        Save();
    }

    public void Clear()
    {
        Sessions.Clear();
        Save();
    }

    public void Rename(ChatSession session, string title)
    {
        string trimmed = title.Trim();
        session.Title = trimmed.Length == 0 ? ChatSession.DefaultTitle : trimmed;
        session.TitleIsCustom = true;
        Save();
    }

    // ── 消息 ──

    /// <summary>追加用户消息；首条消息顺便定标题。</summary>
    public ChatMessage AppendUser(ChatSession session, string text, string? source = null)
    {
        string stored = TrimForStorage(text, out bool truncated);
        var message = new ChatMessage
        {
            Role = ChatMessage.RoleUser,
            Content = stored,
            Source = source,
            Truncated = truncated
        };
        Add(session, message);
        if (!session.TitleIsCustom && session.Title == ChatSession.DefaultTitle)
            session.Title = DeriveTitle(stored);
        Save();
        return message;
    }

    /// <summary>先占位一条空的助手消息（partial），第一个字节到达前就落盘，问题不会丢。</summary>
    public ChatMessage BeginAssistant(ChatSession session)
    {
        var message = new ChatMessage { Role = ChatMessage.RoleAssistant, IsPartial = true };
        Add(session, message);
        Save();
        return message;
    }

    /// <summary>已经完整的一条助手消息（「继续追问」把抽屉结果搬过来时用）。</summary>
    public ChatMessage AppendAssistant(ChatSession session, string text)
    {
        var message = new ChatMessage
        {
            Role = ChatMessage.RoleAssistant,
            Content = TrimForStorage(text, out bool truncated),
            Truncated = truncated
        };
        Add(session, message);
        Save();
        return message;
    }

    /// <summary>流式过程中的检查点：只更新内容并排一次去抖落盘，不动 partial 标记。</summary>
    public void Checkpoint(ChatSession session, ChatMessage message, string text)
    {
        message.Content = TrimForStorage(text, out _);
        session.Touch();
        Save();
    }

    /// <summary>一轮结束：写回最终文本、清掉 partial、记下错误或「用户停止」。</summary>
    public void CompleteAssistant(ChatSession session, ChatMessage message, string text, string? error, bool stopped)
    {
        message.Content = TrimForStorage(text, out _);
        message.Error = error;
        message.Stopped = stopped;
        message.IsPartial = false;
        session.IsStreaming = false;
        Touch(session);
        Save();
    }

    private void Add(ChatSession session, ChatMessage message)
    {
        session.Messages.Add(message);
        TrimMessages(session);
        Touch(session);
    }

    /// <summary>刷新活跃时间并把会话移到列表最前（不落盘，调用方决定何时 Save）。</summary>
    public void Touch(ChatSession session)
    {
        session.Touch();
        int index = Sessions.IndexOf(session);
        if (index > 0) Sessions.Move(index, 0);
    }

    // ── 纯函数（可单测，不需要 Dispatcher） ──

    /// <summary>会话标题：第一条用户消息的首行，去掉 markdown 噪声，截到 20 字。</summary>
    public static string DeriveTitle(string text)
    {
        string line = text.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        line = line.TrimStart('#', '>', '-', '*', '+', ' ').Replace("`", "").Replace("**", "").Trim();
        if (line.Length == 0) return ChatSession.DefaultTitle;
        return line.Length <= 20 ? line : line[..20] + "…";
    }

    /// <summary>落盘前的单条消息截断。</summary>
    public static string TrimForStorage(string text, out bool truncated)
    {
        truncated = text.Length > MaxMessageChars;
        return truncated ? text[..MaxMessageChars] + "\n…（已截断）" : text;
    }

    /// <summary>
    /// 组装这次请求要发的消息：system 常在；从最新往回收，超预算就整条丢弃（不切半条）；
    /// 半截 / 失败的助手回复不进上下文；保证第一条是 user（不少网关拒绝 assistant 开头）。
    /// 最新那条用户消息一定发得出去——单条超预算时头尾各留一半、中间省略。
    /// </summary>
    public static List<ChatTurn> BuildContext(ChatSession session, string systemPrompt,
        int charBudget = DefaultContextChars, int maxTurns = DefaultContextTurns)
    {
        int budget = charBudget > 0 ? charBudget : DefaultContextChars;
        int turns = maxTurns > 0 ? maxTurns : DefaultContextTurns;

        var picked = new List<ChatTurn>();
        int used = 0;
        var messages = session.Messages;
        for (int i = messages.Count - 1; i >= 0 && picked.Count < turns; i--)
        {
            var message = messages[i];
            if (message.Content.Length == 0) continue;
            if (message.Role == ChatMessage.RoleAssistant && (message.IsPartial || message.HasError)) continue;

            string body = message.Content;
            if (picked.Count == 0)
            {
                if (body.Length > budget) body = ClipMiddle(body, budget);
            }
            else if (used + body.Length > budget) break;

            used += body.Length;
            picked.Add(new ChatTurn(message.Role, body));
        }

        picked.Reverse();
        while (picked.Count > 0 && picked[0].Role != ChatMessage.RoleUser) picked.RemoveAt(0);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            picked.Insert(0, new ChatTurn(ChatMessage.RoleSystem, systemPrompt));
        return picked;
    }

    /// <summary>头尾各留一半，中间插省略标记：比砍掉开头更能同时保住任务描述和结尾的问题。</summary>
    public static string ClipMiddle(string text, int budget)
    {
        if (budget < 8 || text.Length <= budget) return text;
        int half = budget / 2;
        return text[..half] + $"\n…（中间省略 {text.Length - budget} 字）…\n" + text[^half..];
    }

    // ── 持久化 ──

    private void TrimSessions()
    {
        while (Sessions.Count > MaxSessions) Sessions.RemoveAt(Sessions.Count - 1);
    }

    private static void TrimMessages(ChatSession session)
    {
        while (session.Messages.Count > MaxMessagesPerSession) session.Messages.RemoveAt(0);
    }

    /// <summary>400ms 去抖。</summary>
    public void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即落盘（App.OnExit），最多等 3s 以免退出卡住。</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        FlushAsync().Wait(TimeSpan.FromSeconds(3));
    }

    private void Load()
    {
        if (!File.Exists(StoragePaths.ChatJson)) return;
        try
        {
            var archive = JsonSerializer.Deserialize<ChatArchive>(
                File.ReadAllText(StoragePaths.ChatJson), JsonOptions);
            if (archive?.Sessions is not { Count: > 0 } sessions) return;
            foreach (var session in sessions.OrderByDescending(s => s.UpdatedAt))
            {
                // 上次是被强杀的：空的半截回复留着没意义，标记出来让界面能给「重试」
                foreach (var message in session.Messages)
                    if (message.IsPartial && message.Content.Length == 0 && !message.HasError)
                        message.Error = "已中断";
                Sessions.Add(session);
            }
        }
        catch (Exception ex) when (AtomicJson.IsCorruptionError(ex))
        {
            // 文件存在但解析失败：改名保留现场，避免后续 Save 把损坏内容悄悄覆盖
            TryMarkCorrupt(StoragePaths.ChatJson);
        }
        catch
        {
            // 瞬时 I/O 失败：文件本身健康，不动它，本次会话退回空列表
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

    private Task FlushAsync()
    {
        // 深拷贝再交给后台线程：流式输出随时在改 Content，直接序列化实时对象会写出半截甚至抛异常
        var snapshot = new ChatArchive { Sessions = Sessions.Select(Snapshot).ToList() };
        _flushChain = _flushChain.ContinueWith(_ => WriteToDisk(snapshot),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return _flushChain;
    }

    private static ChatSession Snapshot(ChatSession session)
    {
        var copy = new ChatSession
        {
            Id = session.Id,
            CreatedAt = session.CreatedAt,
            Title = session.Title,
            TitleIsCustom = session.TitleIsCustom,
            SystemPrompt = session.SystemPrompt,
            UpdatedAt = session.UpdatedAt
        };
        foreach (var message in session.Messages.ToList())
            copy.Messages.Add(new ChatMessage
            {
                Id = message.Id,
                Role = message.Role,
                CreatedAt = message.CreatedAt,
                Content = message.Content,
                IsPartial = message.IsPartial,
                Stopped = message.Stopped,
                Error = message.Error,
                Source = message.Source,
                Truncated = message.Truncated
            });
        return copy;
    }

    private static void WriteToDisk(ChatArchive archive)
    {
        try
        {
            StoragePaths.EnsureCreated();
            AtomicJson.Write(StoragePaths.ChatJson, archive, JsonOptions);
        }
        catch
        {
            // best effort
        }
    }
}
