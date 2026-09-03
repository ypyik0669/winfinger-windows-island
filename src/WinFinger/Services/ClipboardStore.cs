using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>Clipboard history persistence (mirrors mac ClipboardStore: dedupe, cap, favourites, PNG on disk).</summary>
public sealed class ClipboardStore
{
    public const int MaxEntries = 100;
    public const int MaxImageBytes = 10 * 1024 * 1024;

    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    /// <summary>Raised after an entry's favourite flag flips (the list filter must re-evaluate).</summary>
    public event Action<ClipboardEntry>? FavoriteChanged;

    /// <summary>Raised after an entry is created or touched (moved to front on a duplicate hit) and persisted.</summary>
    public event Action<ClipboardEntry>? EntryChanged;

    /// <summary>clipboard.json 的序列化配置（公开以便单测断言 null 键省略 / filePaths 仍写出）。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        // 可选字段（ocrText/contentType 等）为 null 时不写入，保持与 mac clipboard.json 兼容
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DispatcherTimer _saveTimer;

    /// <summary>串行化的落盘任务链：每次 FlushAsync 都接在上一次之后执行，避免旧快照后写覆盖新快照（乱序落盘丢数据）。</summary>
    private Task _flushChain = Task.CompletedTask;

    public ClipboardStore()
    {
        // 300ms 去抖：连续多次修改只落盘一次（构造函数在 UI 线程调用，DispatcherTimer 可安全创建）。
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = FlushAsync();
        };
        Load();
    }

    public ClipboardEntry? AppendText(string text, string? sourceApp, string? sourceAppId = null, bool truncated = false)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var hash = Hash(Encoding.UTF8.GetBytes(text));
        var existing = Entries.FirstOrDefault(e => e.ContentHash == hash);
        if (existing is not null)
        {
            // 去重命中只 Touch（前置 + 刷新时间戳），不会用本次的 truncated 更新既有条目的 IsTruncated。
            Touch(existing);
            return existing;
        }

        var entry = new ClipboardEntry(Guid.NewGuid(), ClipboardEntryKind.Text, text,
            null, sourceAppId, sourceApp, DateTime.Now, hash)
        {
            IsTruncated = truncated,
            ContentType = ContentDetector.Detect(text)
        };
        Entries.Insert(0, entry);
        TrimAndSave();
        return entry;
    }

    public ClipboardEntry? AppendImage(byte[] pngData, string? sourceApp, string? sourceAppId = null)
    {
        if (pngData.Length == 0 || pngData.Length > MaxImageBytes) return null;
        var hash = Hash(pngData);
        var existing = Entries.FirstOrDefault(e => e.ContentHash == hash);
        if (existing is not null)
        {
            Touch(existing);
            return existing;
        }

        var id = Guid.NewGuid();
        var path = Path.Combine(StoragePaths.ClipboardMedia, $"{id}.png");
        try
        {
            File.WriteAllBytes(path, pngData);
        }
        catch
        {
            return null;
        }

        var entry = new ClipboardEntry(id, ClipboardEntryKind.Image, null,
            path, sourceAppId, sourceApp, DateTime.Now, hash);
        Entries.Insert(0, entry);
        TrimAndSave();
        return entry;
    }

    /// <summary>mac appendFiles: dedupes on the joined path list.</summary>
    public ClipboardEntry? AppendFiles(IEnumerable<string> paths, string? sourceApp, string? sourceAppId = null)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try { return Path.GetFullPath(p); }
                catch { return p; }
            })
            .ToList();
        if (normalized.Count == 0) return null;
        var hash = FileHash(normalized);
        var existing = Entries.FirstOrDefault(e => e.ContentHash == hash);
        if (existing is not null)
        {
            Touch(existing);
            return existing;
        }

        var entry = new ClipboardEntry(Guid.NewGuid(), ClipboardEntryKind.File, null, null,
            sourceAppId, sourceApp, DateTime.Now, hash)
        {
            FilePaths = normalized
        };
        Entries.Insert(0, entry);
        TrimAndSave();
        return entry;
    }

    public static string FileHash(IEnumerable<string> paths) =>
        Hash(Encoding.UTF8.GetBytes(string.Join("\n", paths)));

    /// <summary>命中去重哈希时调用：移到最前、刷新时间戳，但不走 Insert/Clear（Move 不触发 Add 通知，不会重复弹 toast）。</summary>
    public void Touch(ClipboardEntry entry)
    {
        var index = Entries.IndexOf(entry);
        if (index > 0)
            Entries.Move(index, 0);
        entry.CreatedAt = DateTime.Now;
        Save();
        EntryChanged?.Invoke(entry);
    }

    /// <summary>重算内容哈希后覆盖文本（如 OCR 结果回填），并广播变更。</summary>
    public void UpdateText(ClipboardEntry entry, string newText)
    {
        entry.Text = newText;
        entry.ContentHash = Hash(Encoding.UTF8.GetBytes(newText));
        Save();
        EntryChanged?.Invoke(entry);
    }

    public void ToggleFavorite(ClipboardEntry entry)
    {
        entry.IsFavorite = !entry.IsFavorite;
        Save();
        FavoriteChanged?.Invoke(entry);
    }

    public void Remove(ClipboardEntry entry)
    {
        Entries.Remove(entry);
        DeleteImageFile(entry);
        Save();
    }

    /// <summary>清空历史；includeFavorites=false 时保留收藏项。反向 RemoveAt 逐条删除（含 PNG），不用 Entries.Clear()。</summary>
    public void Clear(bool includeFavorites)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (!includeFavorites && entry.IsFavorite) continue;
            DeleteImageFile(entry);
            Entries.RemoveAt(i);
        }
        Save();
    }

    public byte[]? ImageData(ClipboardEntry entry)
    {
        if (entry.ImagePath is null) return null;
        try
        {
            return File.ReadAllBytes(entry.ImagePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>mac ClipboardStore.filtered(entries, filter, query).</summary>
    public static bool Matches(ClipboardEntry entry, ClipboardFilter filter, string query)
    {
        bool kindOk = filter switch
        {
            ClipboardFilter.All => true,
            ClipboardFilter.Text => entry.Kind == ClipboardEntryKind.Text,
            ClipboardFilter.Image => entry.Kind == ClipboardEntryKind.Image,
            ClipboardFilter.File => entry.Kind == ClipboardEntryKind.File,
            ClipboardFilter.Favorite => entry.IsFavorite,
            _ => true
        };
        if (!kindOk) return false;

        // 支持 type:xxx 过滤词（可出现在查询任意位置，大小写不敏感；type:ocr 表示"已识别出文字"）
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rest = new List<string>(words.Length);
        foreach (var word in words)
        {
            if (word.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = word["type:".Length..];
                if (wanted.Length == 0) continue;
                if (wanted.Equals("ocr", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.HasOcrText) return false;
                }
                else if (!string.Equals(entry.ContentType, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                continue;
            }
            rest.Add(word);
        }

        var needle = string.Join(" ", rest);
        if (needle.Length == 0) return true;
        var haystack = string.Join(" ", new[]
        {
            entry.DisplayTitle,
            entry.Text ?? "",
            entry.SourceAppName ?? "",
            entry.OcrText ?? "",
            entry.QrText ?? "",
            entry.ContentTypeLabel ?? "",
            string.Join(" ", entry.FilePaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))))
        });
        return haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    private static void DeleteImageFile(ClipboardEntry entry)
    {
        if (entry.ImagePath is null) return;
        try
        {
            File.Delete(entry.ImagePath);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>mac trim: 收藏项不占配额；非收藏数量超过上限时从队尾删除最旧的非收藏项（含 PNG）。</summary>
    private void TrimAndSave()
    {
        int nonFavoriteCount = Entries.Count(e => !e.IsFavorite);
        for (int i = Entries.Count - 1; i >= 0 && nonFavoriteCount > MaxEntries; i--)
        {
            var entry = Entries[i];
            if (entry.IsFavorite) continue;
            DeleteImageFile(entry);
            Entries.RemoveAt(i);
            nonFavoriteCount--;
        }
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StoragePaths.ClipboardJson)) return;
            var decoded = JsonSerializer.Deserialize<List<ClipboardEntry>>(
                File.ReadAllText(StoragePaths.ClipboardJson), JsonOptions);
            if (decoded is null) return;
            foreach (var entry in decoded)
            {
                switch (entry.Kind)
                {
                    case ClipboardEntryKind.Image when entry.ImagePath is null || !File.Exists(entry.ImagePath):
                        continue;
                    case ClipboardEntryKind.File when !entry.FilePaths.Any(p => File.Exists(p) || Directory.Exists(p)):
                        continue;
                }
                // 旧条目（升级前写入）没有 contentType，补一次检测；不立即落盘，等下次 Save 顺带写回
                if (entry.Kind == ClipboardEntryKind.Text && entry.ContentType is null && entry.Text is not null)
                    entry.ContentType = ContentDetector.Detect(entry.Text);
                Entries.Add(entry);
            }
        }
        catch
        {
            // corrupt file: start fresh
        }
    }

    /// <summary>300ms 去抖：连续多次改动只在计时器到期后落盘一次。</summary>
    public void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即同步落盘（App.OnExit 调用，确保进程退出前数据不丢）：停掉去抖计时器，
    /// 排入落盘任务链并等待其执行完成（含之前所有排队中的写入），超过 3s 放弃等待以免退出卡死。</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        FlushAsync().Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>把本次快照接到 _flushChain 之后串行执行，避免并发写入乱序（旧快照后写覆盖新快照）。</summary>
    private Task FlushAsync()
    {
        var snapshot = Entries.ToList();
        _flushChain = _flushChain.ContinueWith(_ => WriteToDisk(snapshot),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return _flushChain;
    }

    private static void WriteToDisk(List<ClipboardEntry> snapshot)
    {
        try
        {
            StoragePaths.EnsureCreated();
            AtomicJson.Write(StoragePaths.ClipboardJson, snapshot, JsonOptions);
        }
        catch
        {
            // best effort
        }
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>文本哈希：先按 maxLength 截断再哈希，保证采集端与写回端口径一致。</summary>
    public static string TextHash(string text, int maxLength)
    {
        int limit = Math.Max(1, maxLength);
        if (text.Length > limit) text = text[..limit];
        return Hash(Encoding.UTF8.GetBytes(text));
    }
}
