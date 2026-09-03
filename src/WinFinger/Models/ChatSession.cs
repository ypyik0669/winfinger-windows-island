using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WinFinger.Models;

/// <summary>一条对话线程：左侧列表一行对应一个。</summary>
public sealed class ChatSession : INotifyPropertyChanged
{
    public const string DefaultTitle = "新对话";

    private string _title = DefaultTitle;
    private DateTime _updatedAt = DateTime.Now;
    private bool _isStreaming;

    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; } = DateTime.Now;

    [JsonPropertyName("title")]
    public string Title
    {
        get => _title;
        set => Set(ref _title, value, nameof(Title));
    }

    /// <summary>用户手动改过标题：之后不再被首条消息自动覆盖。</summary>
    [JsonPropertyName("titleIsCustom")] public bool TitleIsCustom { get; set; }

    /// <summary>
    /// 建会话时对全局系统提示词做的快照。改设置只影响新会话，
    /// 不会让旧对话的上下文在用户不知情的情况下变样。
    /// </summary>
    [JsonPropertyName("systemPrompt")] public string SystemPrompt { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => Set(ref _updatedAt, value, nameof(UpdatedAt));
    }

    [JsonPropertyName("messages")]
    public ObservableCollection<ChatMessage> Messages { get; init; } = new();

    /// <summary>这条会话正在生成（可能不是当前选中的那条，列表上要给个提示）。</summary>
    [JsonIgnore]
    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value, nameof(IsStreaming));
    }

    /// <summary>列表第二行：最后一条有内容的消息开头。</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                string text = Messages[i].Content.ReplaceLineEndings(" ").Trim();
                if (text.Length > 0) return text.Length <= 40 ? text : text[..40] + "…";
            }
            return "还没有消息";
        }
    }

    /// <summary>消息变动后刷新活跃时间与列表上的预览。</summary>
    public void Touch()
    {
        UpdatedAt = DateTime.Now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
