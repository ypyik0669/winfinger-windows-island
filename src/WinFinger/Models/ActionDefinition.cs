using System.Text.Json.Serialization;

namespace WinFinger.Models;

/// <summary>动作的执行方式（<c>run</c> 字段的前缀）。</summary>
public enum ActionRunKind
{
    /// <summary>open: 用系统默认程序打开（URL / mailto / 路径）。</summary>
    Open,
    /// <summary>shell: 交给 cmd.exe /c 执行。</summary>
    Shell,
    /// <summary>builtin: 内置能力（json-format、ocr、qr-encode…）。</summary>
    Builtin,
    /// <summary>prompt: 交给 AI 流式回答。</summary>
    Prompt
}

/// <summary>动作的匹配条件；全部为空表示"任何条目都显示"。</summary>
public sealed record ActionMatch
{
    /// <summary><see cref="Services.ContentDetector"/> 的内容类型（url / json / color…）。</summary>
    [JsonPropertyName("types")] public string[]? Types { get; init; }

    /// <summary>条目类别：text / image / file / ocr（"ocr" = 已识字的图片）。</summary>
    [JsonPropertyName("kinds")] public string[]? Kinds { get; init; }

    /// <summary>对正文（Text ?? OcrText）做的正则匹配，忽略大小写。</summary>
    [JsonPropertyName("regex")] public string? Regex { get; init; }

    /// <summary>来源应用（进程名小写或应用名小写）。</summary>
    [JsonPropertyName("apps")] public string[]? Apps { get; init; }
}

/// <summary>
/// 一条可配置动作（内置 Resources/actions.json + 用户 %APPDATA%\WinFinger\actions.json 合并）。
/// <c>run</c> 形如 <c>open:{text}</c> / <c>shell:explorer.exe …</c> / <c>builtin:ocr</c> / <c>prompt:…</c>；
/// 占位符 <c>{text} {path} {png} {paths} {app}</c>。
/// </summary>
public sealed record ActionDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    /// <summary>4 位十六进制 Segoe 字形码（如 "E71B"）或直接一个 emoji / 文字。</summary>
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("match")] public ActionMatch? Match { get; init; }
    [JsonPropertyName("run")] public string Run { get; init; } = "";
    /// <summary>是否在卡片上直接露出图标按钮（最多 3 个）。</summary>
    [JsonPropertyName("inline")] public bool Inline { get; init; }
    [JsonPropertyName("order")] public int Order { get; init; } = 100;
    /// <summary>用户文件里把同 id 的内置动作标 hidden 即可移除。</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; init; }
}
