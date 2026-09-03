using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Media.Control;

namespace WinFinger.Services;

/// <summary>
/// Global media session (GSMTC): now-playing info, cover art, timeline and transport controls
/// (counterpart of mac MediaMonitor / MediaSnapshot).
/// </summary>
public sealed partial class MediaService : ObservableObject
{
    public static readonly System.Windows.Media.Color DefaultAccent = System.Windows.Media.Color.FromRgb(61, 138, 255);

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    /// <summary>App user model id / exe of the source app (mac bundleIdentifier).</summary>
    [ObservableProperty] private string _sourceAppId = "";
    /// <summary>Human-readable source ("网易云音乐", "Spotify", …, "系统媒体").</summary>
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private BitmapImage? _cover;
    [ObservableProperty] private System.Windows.Media.Color _accentColor = DefaultAccent;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private DateTimeOffset? _positionTimestamp;
    [ObservableProperty] private double _playbackRate = 1;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private byte[]? _lastArtworkData;

    /// <summary>Pixel size of the cover, to avoid upscaling tiny thumbnails (mac artworkPixelSize).</summary>
    public int CoverPixelWidth => Cover?.PixelWidth ?? 0;
    public int CoverPixelHeight => Cover?.PixelHeight ?? 0;

    /// <summary>mac effectiveElapsedTime: extrapolates the last reported position while playing.</summary>
    public TimeSpan EffectivePosition
    {
        get
        {
            if (!IsPlaying || PositionTimestamp is not { } stamp) return Position;
            double rate = PlaybackRate > 0.01 ? PlaybackRate : 1;
            double elapsed = Position.TotalSeconds + (DateTimeOffset.Now - stamp).TotalSeconds * rate;
            double upper = Math.Max(Duration.TotalSeconds, Position.TotalSeconds);
            return TimeSpan.FromSeconds(Math.Clamp(elapsed, 0, upper));
        }
    }

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => OnUi(() => AttachSession(_manager.GetCurrentSession()));
            AttachSession(_manager.GetCurrentSession());
        }
        catch
        {
            // GSMTC unavailable (very old Win10); media page stays empty
        }
    }

    public void Stop()
    {
        DetachSession();
        _manager = null;
    }

    public async void TogglePlayPause()
    {
        try
        {
            if (_session is null) return;
            if (IsPlaying) await _session.TryPauseAsync();
            else await _session.TryPlayAsync();
        }
        catch
        {
            // session vanished mid-call
        }
    }

    public async void Next()
    {
        try
        {
            if (_session is not null) await _session.TrySkipNextAsync();
        }
        catch
        {
        }
    }

    public async void Previous()
    {
        try
        {
            if (_session is not null) await _session.TrySkipPreviousAsync();
        }
        catch
        {
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        DetachSession();
        _session = session;
        if (_session is null)
        {
            HasSession = false;
            IsPlaying = false;
            Title = "";
            Artist = "";
            Album = "";
            SourceAppId = "";
            SourceName = "";
            Cover = null;
            _lastArtworkData = null;
            AccentColor = DefaultAccent;
            Duration = TimeSpan.Zero;
            Position = TimeSpan.Zero;
            PositionTimestamp = null;
            return;
        }

        try
        {
            SourceAppId = _session.SourceAppUserModelId ?? "";
        }
        catch
        {
            SourceAppId = "";
        }
        SourceName = SourceNameFor(SourceAppId);
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    // WinRT events arrive on MTA threads; hop to the UI dispatcher.
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => OnUi(() => _ = RefreshPropertiesAsync());

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => OnUi(() =>
        {
            RefreshPlayback();
            RefreshTimeline();
        });

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => OnUi(RefreshTimeline);

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = info?.PlaybackRate ?? 1;
            if (rate > 0.01) playing = playing || info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            PlaybackRate = rate > 0 ? rate : 1;
            IsPlaying = playing;
        }
        catch
        {
            IsPlaying = false;
        }
    }

    private void RefreshTimeline()
    {
        try
        {
            var timeline = _session?.GetTimelineProperties();
            if (timeline is null) return;
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            // mac diff thresholds: only republish on meaningful change
            bool durationChanged = Math.Abs((duration - Duration).TotalSeconds) >= 0.05;
            bool positionChanged = Math.Abs((position - Position).TotalSeconds) >= 0.35;
            if (durationChanged) Duration = duration;
            if (positionChanged || PositionTimestamp is null)
            {
                Position = position;
                PositionTimestamp = timeline.LastUpdatedTime == default ? DateTimeOffset.Now : timeline.LastUpdatedTime;
            }
        }
        catch
        {
            // session closed while reading
        }
    }

    private async Task RefreshPropertiesAsync()
    {
        if (_session is null) return;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            Album = props.AlbumTitle ?? "";

            byte[]? data = null;
            if (props.Thumbnail is { } thumbnail)
            {
                using var winrtStream = await thumbnail.OpenReadAsync();
                using var stream = winrtStream.AsStreamForRead();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                data = memory.ToArray();
            }

            if (data is null || data.Length == 0)
            {
                _lastArtworkData = null;
                Cover = null;
                AccentColor = DefaultAccent;
            }
            else if (_lastArtworkData is null || !data.AsSpan().SequenceEqual(_lastArtworkData))
            {
                _lastArtworkData = data;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(data);
                image.EndInit();
                image.Freeze();
                Cover = image;
                AccentColor = ExtractAccentColor(image);
            }
            // mac hasSession: !title.isEmpty || artwork != nil
            HasSession = Title.Length > 0 || Cover is not null;
        }
        catch
        {
            // session may have closed while reading
        }
    }

    /// <summary>mac accent: average colour → HSB with saturation ×1.35 (0.52…0.92) and brightness ×1.3 (0.72…1).</summary>
    private static System.Windows.Media.Color ExtractAccentColor(BitmapImage image)
    {
        try
        {
            var scaled = new TransformedBitmap(image,
                new System.Windows.Media.ScaleTransform(32.0 / image.PixelWidth, 32.0 / image.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            double r = 0, g = 0, b = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
                count++;
            }
            if (count == 0) return DefaultAccent;
            r /= count * 255.0;
            g /= count * 255.0;
            b /= count * 255.0;

            RgbToHsv(r, g, b, out double h, out double s, out double v);
            s = Math.Clamp(s * 1.35, 0.52, 0.92);
            v = Math.Clamp(v * 1.3, 0.72, 1);
            HsvToRgb(h, s, v, out r, out g, out b);
            return System.Windows.Media.Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        catch
        {
            return DefaultAccent;
        }
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        double delta = max - min;
        s = max <= 0 ? 0 : delta / max;
        if (delta <= 0) { h = 0; return; }
        if (max == r) h = (g - b) / delta % 6;
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;
        h *= 60;
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        r = r1 + m;
        g = g1 + m;
        b = b1 + m;
    }

    /// <summary>mac sourceName mapping, with Windows app ids.</summary>
    private static string SourceNameFor(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Length == 0) return "系统媒体";
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("cloudmusic") || id.Contains("netease")) return "网易云音乐";
        if (id.Contains("qqmusic")) return "QQ音乐";
        if (id.Contains("kugou")) return "酷狗音乐";
        if (id.Contains("kuwo")) return "酷我音乐";
        if (id.Contains("applemusic") || id.Contains("appleinc.applemusic")) return "Apple Music";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "媒体播放器";
        if (id.Contains("potplayer")) return "PotPlayer";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("bilibili")) return "哔哩哔哩";
        // "app.exe" → "app"
        var name = id;
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        int bang = name.IndexOf('!');
        if (bang >= 0) name = name[..bang];
        if (name.EndsWith(".exe")) name = name[..^4];
        return name.Length > 0 ? name : "系统媒体";
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
