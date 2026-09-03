using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinFinger.Services;

namespace WinFinger.Models;

[JsonConverter(typeof(ClipboardEntryKindConverter))]
public enum ClipboardEntryKind
{
    Text,
    Image,
    File
}

/// <summary>Serialises the kind as lowercase "text"/"image"/"file" (mac clipboard.json compatible).</summary>
public sealed class ClipboardEntryKindConverter : JsonConverter<ClipboardEntryKind>
{
    public override ClipboardEntryKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "image" => ClipboardEntryKind.Image,
            "file" => ClipboardEntryKind.File,
            _ => ClipboardEntryKind.Text
        };

    public override void Write(Utf8JsonWriter writer, ClipboardEntryKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ClipboardEntryKind.Image => "image",
            ClipboardEntryKind.File => "file",
            _ => "text"
        });
}

/// <summary>Filter tabs above the clipboard list (mac ClipboardFilter).</summary>
public enum ClipboardFilter
{
    All,
    Text,
    Image,
    File,
    Favorite
}

public static class ClipboardFilterInfo
{
    public static string Title(this ClipboardFilter filter) => filter switch
    {
        ClipboardFilter.All => "全部",
        ClipboardFilter.Text => "文本",
        ClipboardFilter.Image => "图像",
        ClipboardFilter.File => "文件",
        ClipboardFilter.Favorite => "收藏",
        _ => ""
    };
}

/// <summary>One clipboard history record (field-compatible with mac's clipboard.json).</summary>
public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private bool _isFavorite;
    private string? _text;
    private DateTime _createdAt = DateTime.Now;
    private string? _ocrText;
    private string? _ocrLang;
    private string? _contentType;
    private string? _qrText;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public ClipboardEntryKind Kind { get; set; }
    [JsonPropertyName("imagePath")] public string? ImagePath { get; set; }
    [JsonPropertyName("filePaths")] public List<string> FilePaths { get; set; } = new();
    [JsonPropertyName("sourceAppBundleId")] public string? SourceAppBundleId { get; set; }
    [JsonPropertyName("sourceAppName")] public string? SourceAppName { get; set; }
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = "";

    /// <summary>文本内容是否被截断保存（超长文本场景，Task 4/5 使用）。</summary>
    [JsonPropertyName("truncated")] public bool IsTruncated { get; set; }

    [JsonPropertyName("text")]
    public string? Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CharacterCount)));
        }
    }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt
    {
        get => _createdAt;
        set
        {
            if (_createdAt == value) return;
            _createdAt = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CreatedAt)));
        }
    }

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    /// <summary>OCR 识别出的文字（图片条目；无结果时为 null，不写入 JSON）。</summary>
    [JsonPropertyName("ocrText")]
    public string? OcrText
    {
        get => _ocrText;
        set
        {
            if (_ocrText == value) return;
            _ocrText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OcrText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOcrText)));
        }
    }

    /// <summary>OCR 使用的识别语言标记。</summary>
    [JsonPropertyName("ocrLang")]
    public string? OcrLang
    {
        get => _ocrLang;
        set
        {
            if (_ocrLang == value) return;
            _ocrLang = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OcrLang)));
        }
    }

    /// <summary>内容类型（<see cref="ContentDetector"/> 的常量之一）。</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType
    {
        get => _contentType;
        set
        {
            if (_contentType == value) return;
            _contentType = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentType)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentTypeLabel)));
        }
    }

    /// <summary>图片中识别出的二维码内容。</summary>
    [JsonPropertyName("qrText")]
    public string? QrText
    {
        get => _qrText;
        set
        {
            if (_qrText == value) return;
            _qrText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QrText)));
        }
    }

    [JsonIgnore] public bool HasOcrText => !string.IsNullOrWhiteSpace(OcrText);

    [JsonIgnore] public string? ContentTypeLabel => ContentDetector.Label(ContentType);

    /// <summary>相对时间显示（如"3 分钟前"）需要定时刷新时调用：CreatedAt 值未变，但显示要重算。</summary>
    public void RaiseCreatedAtChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CreatedAt)));

    public ClipboardEntry()
    {
    }

    public ClipboardEntry(Guid id, ClipboardEntryKind kind, string? text, string? imagePath,
        string? sourceAppBundleId, string? sourceAppName, DateTime createdAt, string contentHash)
    {
        Id = id;
        Kind = kind;
        Text = text;
        ImagePath = imagePath;
        SourceAppBundleId = sourceAppBundleId;
        SourceAppName = sourceAppName;
        CreatedAt = createdAt;
        ContentHash = contentHash;
    }

    /// <summary>mac displayTitle.</summary>
    [JsonIgnore]
    public string DisplayTitle => Kind switch
    {
        ClipboardEntryKind.Text => (Text ?? "").ReplaceLineEndings(" "),
        ClipboardEntryKind.Image => "已复制图片",
        ClipboardEntryKind.File => FilePaths.Count == 1
            ? Path.GetFileName(FilePaths[0].TrimEnd(Path.DirectorySeparatorChar))
            : $"{FilePaths.Count} 个文件",
        _ => ""
    };

    /// <summary>mac kindTitle.</summary>
    [JsonIgnore]
    public string KindTitle => Kind switch
    {
        ClipboardEntryKind.Text => "文本",
        ClipboardEntryKind.Image => "图片",
        ClipboardEntryKind.File => "文件",
        _ => ""
    };

    /// <summary>mac detailLabel（截断文本追加"（已截断）"）。</summary>
    [JsonIgnore]
    public string DetailLabel => Kind switch
    {
        ClipboardEntryKind.Text => IsTruncated ? $"{CharacterCount} 字符（已截断）" : $"{CharacterCount} 字符",
        ClipboardEntryKind.Image => "图片",
        ClipboardEntryKind.File => FilePaths.Count == 1 ? "1 个文件" : $"{FilePaths.Count} 个文件",
        _ => ""
    };

    [JsonIgnore] public int CharacterCount => Text?.Length ?? 0;

    [JsonIgnore] public string SourceAppLabel => string.IsNullOrWhiteSpace(SourceAppName) ? "未知应用" : SourceAppName!;

    [JsonIgnore] public string? FirstFilePath => FilePaths.Count > 0 ? FilePaths[0] : null;

    /// <summary>单个文件类条目且指向的是目录（mac isDirectory）。</summary>
    [JsonIgnore]
    public bool IsDirectory => Kind == ClipboardEntryKind.File && FilePaths.Count == 1 && Directory.Exists(FilePaths[0]);

    public event PropertyChangedEventHandler? PropertyChanged;
}
