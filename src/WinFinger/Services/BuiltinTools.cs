using System.IO;
using System.Text;
using System.Text.Json;

namespace WinFinger.Services;

/// <summary>内置动作里的纯文本处理（无 UI 依赖，便于单测）。</summary>
public static class BuiltinTools
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    /// <summary>格式化 JSON；不是合法 JSON 时抛 <see cref="JsonException"/>。</summary>
    public static string FormatJson(string text) => Rewrite(text, indented: true);

    /// <summary>压缩 JSON（去掉空白）。</summary>
    public static string MinifyJson(string text) => Rewrite(text, indented: false);

    private static string Rewrite(string text, bool indented)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 128
        });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, indented ? Indented : default))
        {
            doc.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>时间戳（秒 / 毫秒）转成本地与 UTC 时间的可读描述；无法解析时返回 null。</summary>
    public static string? DescribeTimestamp(string text)
    {
        if (!ContentDetector.TryParseTimestamp(text, out var dt, out bool wasMillis)) return null;
        var local = dt.ToLocalTime();
        var sb = new StringBuilder();
        sb.AppendLine($"本地时间：{local:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"UTC：{dt.UtcDateTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"ISO 8601：{local:yyyy-MM-ddTHH:mm:sszzz}");
        sb.AppendLine($"秒：{dt.ToUnixTimeSeconds()}");
        sb.AppendLine($"毫秒：{dt.ToUnixTimeMilliseconds()}");
        sb.Append($"来源单位：{(wasMillis ? "毫秒" : "秒")}");
        return sb.ToString();
    }

    /// <summary>字数统计：字符 / 不含空白字符 / 词 / 行 / 中日韩字符。</summary>
    public static string WordCount(string text)
    {
        text ??= "";
        int chars = text.Length;
        int noSpace = text.Count(c => !char.IsWhiteSpace(c));
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int lines = text.Length == 0 ? 0 : text.ReplaceLineEndings("\n").Split('\n').Length;
        int cjk = text.Count(c => ContentDetector.HasCjk(c.ToString()));
        return $"字符：{chars}\n非空白字符：{noSpace}\n词：{words}\n行：{lines}\n中日韩字符：{cjk}";
    }

    /// <summary>只保留数字与前导 +（电话号码复制）。</summary>
    public static string DigitsOnly(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        if (text.TrimStart().StartsWith('+')) sb.Append('+');
        foreach (char c in text)
            if (char.IsAsciiDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
