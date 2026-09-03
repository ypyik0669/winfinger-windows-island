using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinFinger.Services;

public sealed record LyricLine(int Id, double? Time, string Text);

public enum LyricsStatus
{
    Idle,
    Loading,
    Ready,
    Empty
}

/// <summary>
/// Lyrics lookup via lrclib.net (port of mac LyricsService, minus the Apple Music AppleScript path).
/// Results (including empty ones) are cached per track for the process lifetime.
/// </summary>
public sealed partial class LyricsService : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    [ObservableProperty] private LyricsStatus _status = LyricsStatus.Idle;
    [ObservableProperty] private string _sourceTitle = "";

    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex TagPattern = new(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private readonly Dictionary<string, IReadOnlyList<LyricLine>> _cache = new();
    private MediaService? _monitor;
    private string _lastKey = "";
    private CancellationTokenSource? _fetchCts;

    public bool HasTimedLines => Lines.Any(l => l.Time is not null);

    public void Start(MediaService monitor)
    {
        _monitor = monitor;
        monitor.PropertyChanged += OnMediaChanged;
        Handle();
    }

    public void Stop()
    {
        if (_monitor is not null) _monitor.PropertyChanged -= OnMediaChanged;
        _monitor = null;
        _fetchCts?.Cancel();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.Title) or nameof(MediaService.Artist)
            or nameof(MediaService.Album) or nameof(MediaService.SourceAppId) or nameof(MediaService.HasSession))
            Handle();
    }

    private void Handle()
    {
        if (_monitor is null) return;
        string title = _monitor.Title.Trim();
        string artist = _monitor.Artist.Trim();
        string album = _monitor.Album.Trim();
        string bundle = _monitor.SourceAppId;

        _fetchCts?.Cancel();
        if (title.Length == 0)
        {
            _lastKey = "";
            Lines = Array.Empty<LyricLine>();
            SourceTitle = "";
            Status = LyricsStatus.Idle;
            return;
        }

        string key = $"{bundle}|{artist}|{title}|{album}".ToLowerInvariant();
        if (key == _lastKey && Status != LyricsStatus.Idle) return;
        _lastKey = key;
        SourceTitle = title;
        if (_cache.TryGetValue(key, out var cached))
        {
            Lines = cached;
            Status = cached.Count == 0 ? LyricsStatus.Empty : LyricsStatus.Ready;
            return;
        }

        Lines = Array.Empty<LyricLine>();
        Status = LyricsStatus.Loading;
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        _ = FetchAsync(key, title, artist, album, cts.Token);
    }

    private async Task FetchAsync(string key, string title, string artist, string album, CancellationToken token)
    {
        IReadOnlyList<LyricLine> fetched;
        try
        {
            fetched = await Task.Run(() => FetchLinesAsync(title, artist, album, token), token);
        }
        catch
        {
            fetched = Array.Empty<LyricLine>();
        }
        if (token.IsCancellationRequested) return;
        OnUi(() =>
        {
            if (_lastKey != key) return; // stale
            _cache[key] = fetched;
            Lines = fetched;
            Status = fetched.Count == 0 ? LyricsStatus.Empty : LyricsStatus.Ready;
        });
    }

    /// <summary>mac currentIndex(at:): last timed line with time ≤ elapsed; 0 for plain lyrics.</summary>
    public int CurrentIndex(double elapsed)
    {
        var lines = Lines;
        if (!lines.Any(l => l.Time is not null)) return 0;
        int index = 0;
        for (int offset = 0; offset < lines.Count; offset++)
        {
            var line = lines[offset];
            if (line.Time is { } time)
            {
                if (time <= elapsed) index = offset;
                else break;
            }
        }
        return index;
    }

    // ── fetching ──

    private static async Task<IReadOnlyList<LyricLine>> FetchLinesAsync(string title, string artist, string album, CancellationToken token)
    {
        string? raw = await LrclibLyricsAsync(title, artist, album, token);
        if (raw is null) return Array.Empty<LyricLine>();
        return Parse(raw) ?? Array.Empty<LyricLine>();
    }

    private static async Task<string?> LrclibLyricsAsync(string title, string artist, string album, CancellationToken token)
    {
        if (await LrclibGetAsync(title, artist, album, token) is { } exact) return exact;
        if (album.Length > 0 && await LrclibGetAsync(title, artist, null, token) is { } relaxed) return relaxed;
        return await LrclibSearchAsync(title, artist, token);
    }

    private static async Task<string?> LrclibGetAsync(string title, string artist, string? album, CancellationToken token)
    {
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist.Length == 0 ? " " : artist)}";
        if (!string.IsNullOrEmpty(album)) query += $"&album_name={Uri.EscapeDataString(album)}";
        var json = await GetJsonAsync($"https://lrclib.net/api/get?{query}", token);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return NonBlank(doc.RootElement, "syncedLyrics") ?? NonBlank(doc.RootElement, "plainLyrics");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> LrclibSearchAsync(string title, string artist, CancellationToken token)
    {
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist.Length == 0 ? " " : artist)}";
        if (await SearchAndPickAsync($"https://lrclib.net/api/search?{query}", title, artist, token) is { } picked)
            return picked;
        var q = $"{title} {artist}".Trim();
        return await SearchAndPickAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}", title, artist, token);
    }

    private static async Task<string?> SearchAndPickAsync(string url, string title, string artist, CancellationToken token)
    {
        var json = await GetJsonAsync(url, token);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var objects = doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            return PickLyrics(objects, title, artist);
        }
        catch
        {
            return null;
        }
    }

    private static string? PickLyrics(List<JsonElement> objects, string title, string artist)
    {
        string wantedTitle = NormalizeForMatch(title);
        string wantedArtist = NormalizeForMatch(artist);

        bool Matches(JsonElement o)
        {
            string track = NormalizeForMatch(GetString(o, "trackName"));
            string performer = NormalizeForMatch(GetString(o, "artistName"));
            if (wantedTitle.Length == 0 || track.Length == 0) return false;
            bool titleOk = track == wantedTitle || track.Contains(wantedTitle) || wantedTitle.Contains(track);
            if (!titleOk) return false;
            if (wantedArtist.Length == 0 || performer.Length == 0) return true;
            return performer == wantedArtist || performer.Contains(wantedArtist) || wantedArtist.Contains(performer);
        }

        foreach (var o in objects.Where(Matches))
            if (NonBlank(o, "syncedLyrics") is { } synced) return synced;
        foreach (var o in objects.Where(Matches))
            if (NonBlank(o, "plainLyrics") is { } plain) return plain;
        return null;
    }

    private static string NormalizeForMatch(string raw) =>
        new(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string GetString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NonBlank(JsonElement o, string name)
    {
        var s = GetString(o, name);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static async Task<string?> GetJsonAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(token);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinFinger/1.1.0");
        return client;
    }

    // ── LRC parsing (mac parse) ──

    public static IReadOnlyList<LyricLine>? Parse(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;
        var timed = new List<(double time, string text)>();
        var plain = new List<string>();
        foreach (var line in trimmed.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var current = line.Trim();
            if (current.Length == 0) continue;
            var matches = TagPattern.Matches(current);
            if (matches.Count == 0)
            {
                plain.Add(current);
                continue;
            }
            var last = matches[^1];
            var text = current[(last.Index + last.Length)..].Trim();
            if (text.Length == 0) continue;
            foreach (Match m in matches)
            {
                int minutes = int.Parse(m.Groups[1].Value);
                int seconds = int.Parse(m.Groups[2].Value);
                double fraction = 0;
                if (m.Groups[3].Success)
                    fraction = int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0;
                timed.Add((minutes * 60 + seconds + fraction, text));
            }
        }
        if (timed.Count > 0)
            return timed.Select((t, i) => new LyricLine(i, t.time, t.text)).ToList();
        if (plain.Count > 0)
            return plain.Select((t, i) => new LyricLine(i, null, t)).ToList();
        return null;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
