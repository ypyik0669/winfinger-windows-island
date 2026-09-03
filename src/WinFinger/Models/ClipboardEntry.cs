using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public ClipboardEntryKind Kind { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("imagePath")] public string? ImagePath { get; set; }
    [JsonPropertyName("filePaths")] public List<string> FilePaths { get; set; } = new();
    [JsonPropertyName("sourceAppBundleId")] public string? SourceAppBundleId { get; set; }
    [JsonPropertyName("sourceAppName")] public string? SourceAppName { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = "";

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

    /// <summary>mac detailLabel.</summary>
    [JsonIgnore]
    public string DetailLabel => Kind switch
    {
        ClipboardEntryKind.Text => $"{CharacterCount} 字符",
        ClipboardEntryKind.Image => "图片",
        ClipboardEntryKind.File => FilePaths.Count == 1 ? "1 个文件" : $"{FilePaths.Count} 个文件",
        _ => ""
    };

    [JsonIgnore] public int CharacterCount => Text?.Length ?? 0;

    [JsonIgnore] public string SourceAppLabel => string.IsNullOrWhiteSpace(SourceAppName) ? "未知应用" : SourceAppName!;

    [JsonIgnore] public string? FirstFilePath => FilePaths.Count > 0 ? FilePaths[0] : null;

    public event PropertyChangedEventHandler? PropertyChanged;
}
