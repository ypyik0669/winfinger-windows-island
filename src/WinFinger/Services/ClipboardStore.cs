using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ClipboardStore()
    {
        Load();
    }

    public void AppendText(string text, string? sourceApp, string? sourceAppId = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var hash = Hash(Encoding.UTF8.GetBytes(text));
        if (Entries.Any(e => e.ContentHash == hash)) return;

        Entries.Insert(0, new ClipboardEntry(Guid.NewGuid(), ClipboardEntryKind.Text, text,
            null, sourceAppId, sourceApp, DateTime.Now, hash));
        TrimAndSave();
    }

    public void AppendImage(byte[] pngData, string? sourceApp, string? sourceAppId = null)
    {
        if (pngData.Length == 0 || pngData.Length > MaxImageBytes) return;
        var hash = Hash(pngData);
        if (Entries.Any(e => e.ContentHash == hash)) return;

        var id = Guid.NewGuid();
        var path = Path.Combine(StoragePaths.ClipboardMedia, $"{id}.png");
        try
        {
            File.WriteAllBytes(path, pngData);
        }
        catch
        {
            return;
        }

        Entries.Insert(0, new ClipboardEntry(id, ClipboardEntryKind.Image, null,
            path, sourceAppId, sourceApp, DateTime.Now, hash));
        TrimAndSave();
    }

    /// <summary>mac appendFiles: dedupes on the joined path list.</summary>
    public void AppendFiles(IEnumerable<string> paths, string? sourceApp, string? sourceAppId = null)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try { return Path.GetFullPath(p); }
                catch { return p; }
            })
            .ToList();
        if (normalized.Count == 0) return;
        var hash = FileHash(normalized);
        if (Entries.Any(e => e.ContentHash == hash)) return;

        var entry = new ClipboardEntry(Guid.NewGuid(), ClipboardEntryKind.File, null, null,
            sourceAppId, sourceApp, DateTime.Now, hash)
        {
            FilePaths = normalized
        };
        Entries.Insert(0, entry);
        TrimAndSave();
    }

    public static string FileHash(IEnumerable<string> paths) =>
        Hash(Encoding.UTF8.GetBytes(string.Join("\n", paths)));

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

    public void Clear()
    {
        foreach (var entry in Entries)
            DeleteImageFile(entry);
        Entries.Clear();
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

        var needle = query.Trim();
        if (needle.Length == 0) return true;
        var haystack = string.Join(" ", new[]
        {
            entry.DisplayTitle,
            entry.Text ?? "",
            entry.SourceAppName ?? "",
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

    /// <summary>mac trim: keep the newest 100 but re-insert favourites from the overflow at the front.</summary>
    private void TrimAndSave()
    {
        if (Entries.Count > MaxEntries)
        {
            var all = Entries.ToList();
            var keep = all.Take(MaxEntries).ToList();
            var overflow = all.Skip(MaxEntries).ToList();
            foreach (var favorite in Enumerable.Reverse(overflow.Where(e => e.IsFavorite).ToList()))
            {
                if (keep.All(e => e.Id != favorite.Id))
                    keep.Insert(0, favorite);
            }
            keep = keep.Take(MaxEntries).ToList();
            var keepIds = keep.Select(e => e.Id).ToHashSet();
            foreach (var removed in all.Where(e => !keepIds.Contains(e.Id)))
                DeleteImageFile(removed);

            Entries.Clear();
            foreach (var entry in keep) Entries.Add(entry);
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
                Entries.Add(entry);
            }
        }
        catch
        {
            // corrupt file: start fresh
        }
    }

    private void Save()
    {
        try
        {
            StoragePaths.EnsureCreated();
            var tmp = StoragePaths.ClipboardJson + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.ToList(), JsonOptions));
            File.Move(tmp, StoragePaths.ClipboardJson, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
