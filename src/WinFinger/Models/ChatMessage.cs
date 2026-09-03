using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WinFinger.Models;

/// <summary>
/// 对话里的一条消息。Content 在流式输出期间会被整段替换（每 50ms 一次），所以必须能通知界面；
/// 逐个 token 通知会把 markdown 渲染压垮，批量刷新由 ChatService 负责。
/// </summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";
    public const string RoleSystem = "system";

    private string _content = "";
    private bool _isPartial;
    private bool _stopped;
    private string? _error;

    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>"user" 或 "assistant"。system 提示词存在会话上，不作为消息落盘。</summary>
    [JsonPropertyName("role")] public string Role { get; init; } = RoleUser;

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; } = DateTime.Now;

    [JsonPropertyName("content")]
    public string Content
    {
        get => _content;
        set => Set(ref _content, value, nameof(Content));
    }

    /// <summary>
    /// 这条回复还没写完：流式进行中，或者上次进程被杀时停在半截。
    /// 载入后仍为 true 表示是被中断的历史，界面上打「已中断」标记。
    /// </summary>
    [JsonPropertyName("partial")]
    public bool IsPartial
    {
        get => _isPartial;
        set => Set(ref _isPartial, value, nameof(IsPartial));
    }

    /// <summary>用户主动点了停止：保留半截文本，但不算错误。</summary>
    [JsonPropertyName("stopped")]
    public bool Stopped
    {
        get => _stopped;
        set => Set(ref _stopped, value, nameof(Stopped));
    }

    /// <summary>失败原因（已本地化）。落盘保留，方便用户回看为什么断了。</summary>
    [JsonPropertyName("error")]
    public string? Error
    {
        get => _error;
        set
        {
            Set(ref _error, value, nameof(Error));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
    }

    /// <summary>内容来源标注（如「剪贴板 · 3 条」），只用于界面上的小标签。</summary>
    [JsonPropertyName("source")] public string? Source { get; init; }

    /// <summary>入库时被截断过（超过 ChatStore.MaxMessageChars）。</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    [JsonIgnore] public bool IsUser => Role == RoleUser;
    [JsonIgnore] public bool HasError => !string.IsNullOrEmpty(_error);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
