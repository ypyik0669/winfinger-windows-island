using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>Sticky-note persistence: pinned first, then by UpdatedAt descending.</summary>
public sealed class NoteStore
{
    public ObservableCollection<Note> Notes { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NoteStore()
    {
        Load();
    }

    public Note Create()
    {
        var note = new Note();
        Notes.Insert(0, note);
        Save();
        return note;
    }

    public void Update(Guid id, string title, string body)
    {
        var note = Notes.FirstOrDefault(n => n.Id == id);
        if (note is null) return;
        note.Title = string.IsNullOrWhiteSpace(title) ? "未命名便签" : title;
        note.Body = body;
        note.UpdatedAt = DateTime.Now;
        Sort();
        Save();
    }

    public void TogglePin(Note note)
    {
        note.IsPinned = !note.IsPinned;
        note.UpdatedAt = DateTime.Now;
        Sort();
        Save();
    }

    public void Remove(Note note)
    {
        Notes.Remove(note);
        Save();
    }

    private void Sort()
    {
        var ordered = Notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt).ToList();
        for (int target = 0; target < ordered.Count; target++)
        {
            int current = Notes.IndexOf(ordered[target]);
            if (current != target)
                Notes.Move(current, target);
        }
    }

    private void Load()
    {
        if (!File.Exists(StoragePaths.NotesJson)) return;
        try
        {
            var decoded = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(StoragePaths.NotesJson), JsonOptions);
            if (decoded is null) return;
            foreach (var note in decoded.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt))
                Notes.Add(note);
        }
        catch (Exception ex) when (AtomicJson.IsCorruptionError(ex))
        {
            // 文件存在但解析失败：先改名保留现场，避免后续 Save 把损坏内容悄悄覆盖
            TryMarkCorrupt(StoragePaths.NotesJson);
        }
        catch
        {
            // 瞬时 I/O 失败（文件被杀软/备份占用等）：文件本身健康，不动它，本次会话退回空列表
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

    private void Save()
    {
        try
        {
            StoragePaths.EnsureCreated();
            AtomicJson.Write(StoragePaths.NotesJson, Notes.ToList(), JsonOptions);
        }
        catch
        {
            // best effort
        }
    }
}
