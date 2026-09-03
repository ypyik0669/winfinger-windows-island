using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>
/// 动作目录：内置 Resources/actions.json + 用户 %APPDATA%\WinFinger\actions.json（同 id 覆盖、hidden 移除），
/// 文件改动 500ms 去抖热重载。解析失败保留上一份目录并提示行号。
/// </summary>
public sealed class ActionCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly NotificationService? _notifications;
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _watcher;
    private List<ActionDefinition> _embedded = new();
    private volatile bool _writingDefaults;

    public ActionCatalogService(NotificationService? notifications = null)
    {
        _notifications = notifications;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };
    }

    /// <summary>页面用的静态入口（卡片内联按钮的转换器拿不到 DI）。</summary>
    public static ActionCatalogService? Current { get; set; }

    /// <summary>合并后的动作列表（已按 Inline 优先、Order 升序排好）。</summary>
    public IReadOnlyList<ActionDefinition> All { get; private set; } = Array.Empty<ActionDefinition>();

    /// <summary>目录发生变化（热重载）。</summary>
    public event Action? Changed;

    /// <summary>最近一次用户文件解析错误（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    public string ActionsPath => StoragePaths.ActionsJson;

    public void Start()
    {
        _embedded = LoadEmbedded() ?? new List<ActionDefinition>();
        EnsureUserFile();
        Reload();
        StartWatcher();
    }

    public void Stop()
    {
        _debounce.Stop();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>重新读用户文件并合并；解析失败时保留旧目录。</summary>
    public void Reload()
    {
        List<ActionDefinition> user;
        try
        {
            user = File.Exists(ActionsPath)
                ? JsonSerializer.Deserialize<List<ActionDefinition>>(File.ReadAllText(ActionsPath), JsonOptions) ?? new()
                : new List<ActionDefinition>();
            LastError = null;
        }
        catch (JsonException ex)
        {
            LastError = ex.LineNumber is { } line ? $"actions.json 第 {line + 1} 行有误" : "actions.json 解析失败";
            _notifications?.Post("⚙️", LastError);
            return; // 保留上一份可用目录
        }
        catch (IOException)
        {
            return; // 正在被写，等下一次去抖
        }
        catch (Exception)
        {
            LastError = "actions.json 读取失败";
            _notifications?.Post("⚙️", LastError);
            return;
        }

        _regexCache.Clear();
        All = Merge(_embedded, user);
        Changed?.Invoke();
    }

    /// <summary>内置 + 用户合并：同 id 用户覆盖，hidden 移除，新 id 追加。</summary>
    internal static List<ActionDefinition> Merge(IEnumerable<ActionDefinition> embedded, IEnumerable<ActionDefinition> user)
    {
        var result = new List<ActionDefinition>(embedded);
        foreach (var def in user)
        {
            if (string.IsNullOrWhiteSpace(def.Id)) continue;
            int index = result.FindIndex(d => string.Equals(d.Id, def.Id, StringComparison.OrdinalIgnoreCase));
            if (def.Hidden)
            {
                if (index >= 0) result.RemoveAt(index);
                continue;
            }
            if (index >= 0) result[index] = def;
            else result.Add(def);
        }
        return result
            .Where(d => !d.Hidden && !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Run))
            .OrderBy(d => d.Inline ? 0 : 1)
            .ThenBy(d => d.Order)
            .ToList();
    }

    /// <summary>这条记录能用的动作（已排序）。</summary>
    public IReadOnlyList<ActionDefinition> For(ClipboardEntry entry)
    {
        var list = All;
        var hits = new List<ActionDefinition>();
        foreach (var def in list)
            if (Matches(def, entry)) hits.Add(def);
        return hits;
    }

    /// <summary>
    /// 匹配规则：types 与 kinds 同时给出时取"或"（两种指认条目的方式）；regex、apps 为附加约束。
    /// </summary>
    internal bool Matches(ActionDefinition def, ClipboardEntry entry) => Matches(def, entry, GetRegex);

    internal static bool Matches(ActionDefinition def, ClipboardEntry entry, Func<string, Regex?> regexFactory)
    {
        var match = def.Match;
        if (match is null) return true;

        bool hasTypes = match.Types is { Length: > 0 };
        bool hasKinds = match.Kinds is { Length: > 0 };
        if (hasTypes || hasKinds)
        {
            bool typeHit = hasTypes && entry.ContentType is { Length: > 0 } type &&
                           match.Types!.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
            bool kindHit = hasKinds && match.Kinds!.Any(k => KindHit(k, entry));
            if (!typeHit && !kindHit) return false;
        }

        string body = entry.Text ?? entry.OcrText ?? "";
        if (!string.IsNullOrEmpty(match.Regex))
        {
            var regex = regexFactory(match.Regex!);
            if (regex is null) return false;
            try
            {
                if (!regex.IsMatch(body)) return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        if (match.Apps is { Length: > 0 })
        {
            string bundle = (entry.SourceAppBundleId ?? "").ToLowerInvariant();
            string name = (entry.SourceAppName ?? "").ToLowerInvariant();
            bool appHit = match.Apps.Any(a =>
            {
                string want = (a ?? "").ToLowerInvariant();
                return want.Length > 0 && (bundle == want || name == want ||
                                           bundle == want + ".exe" || bundle.Replace(".exe", "") == want);
            });
            if (!appHit) return false;
        }

        return true;
    }

    private static bool KindHit(string kind, ClipboardEntry entry) => kind?.ToLowerInvariant() switch
    {
        "text" => entry.Kind == ClipboardEntryKind.Text,
        "image" => entry.Kind == ClipboardEntryKind.Image,
        "file" => entry.Kind == ClipboardEntryKind.File,
        "ocr" => entry.Kind == ClipboardEntryKind.Image && entry.HasOcrText,
        _ => false
    };

    private Regex? GetRegex(string pattern) => _regexCache.GetOrAdd(pattern, p =>
    {
        try
        {
            return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return null; // 用户写错的正则：这条动作永不匹配
        }
    });

    /// <summary>拆 <c>run</c> 字段：前缀 → 类型，其余 → 载荷；无前缀按 builtin 处理。</summary>
    public static bool ParseRun(string run, out ActionRunKind kind, out string payload)
    {
        kind = ActionRunKind.Builtin;
        payload = "";
        if (string.IsNullOrWhiteSpace(run)) return false;
        int colon = run.IndexOf(':');
        if (colon <= 0) return false;
        string prefix = run[..colon].Trim().ToLowerInvariant();
        payload = run[(colon + 1)..];
        switch (prefix)
        {
            case "open": kind = ActionRunKind.Open; return payload.Length > 0;
            case "shell": kind = ActionRunKind.Shell; return payload.Length > 0;
            case "builtin": kind = ActionRunKind.Builtin; payload = payload.Trim(); return payload.Length > 0;
            case "prompt": kind = ActionRunKind.Prompt; return payload.Length > 0;
            default: payload = ""; return false;
        }
    }

    // ── files ──

    private void EnsureUserFile()
    {
        try
        {
            if (File.Exists(ActionsPath)) return;
            Directory.CreateDirectory(StoragePaths.Root);
            _writingDefaults = true;
            File.WriteAllText(ActionsPath, ReadEmbeddedText() ?? "[]");
        }
        catch
        {
            // 写不进去也不影响内置动作
        }
        finally
        {
            _writingDefaults = false;
        }
    }

    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.Root);
            _watcher = new FileSystemWatcher(StoragePaths.Root, "actions.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null; // 热重载不可用不影响主流程
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        if (_writingDefaults) return; // 自己写的默认副本不触发重载
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private static List<ActionDefinition>? LoadEmbedded()
    {
        try
        {
            string? text = ReadEmbeddedText();
            return text is null ? null : JsonSerializer.Deserialize<List<ActionDefinition>>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadEmbeddedText()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("actions.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
