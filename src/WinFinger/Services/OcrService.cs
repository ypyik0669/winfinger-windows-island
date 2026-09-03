using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>OCR 任务状态。</summary>
public enum OcrStatus
{
    Idle,
    Running,
    Done,
    NoEngine,
    Failed
}

/// <summary>一次识别的结果：整段文本、实际使用的语言标签、逐行文本。</summary>
public sealed record OcrResult(string Text, string LanguageTag, IReadOnlyList<string> Lines);

/// <summary>
/// 基于 Windows.Media.Ocr 的本地离线 OCR（无网络、无第三方依赖）。
/// 引擎按语言标签缓存并由信号量串行创建；识别全程在线程池执行，不占用 UI 线程。
/// </summary>
public sealed partial class OcrService : ObservableObject
{
    /// <summary>系统未安装任何 OCR 语言包时给用户的提示。</summary>
    public const string UnavailableMessage =
        "未安装 OCR 语言包：设置 → 时间和语言 → 语言 → 添加语言并勾选“光学字符识别”";

    /// <summary>没有引擎时可直接跳转的系统设置页。</summary>
    public const string LanguageSettingsUri = "ms-settings:regionlanguage";

    /// <summary>langHint 为 auto/空 时的候选语言顺序（用户配置语言优先，再退回中英）。</summary>
    private static readonly string[] FallbackTags = { "zh-Hans-CN", "zh-Hans", "en-US", "en" };

    [ObservableProperty] private OcrStatus _status = OcrStatus.Idle;

    /// <summary>
    /// 全局串行闸门：OCR 引擎不是线程安全的，所以整个识别流程（引擎创建 + 解码 + 识别）都在这把锁内执行。
    /// 副作用是同一时刻只有一次识别在跑，<see cref="Status"/> / <see cref="LastStatus"/> 因此不会被并发调用互相覆盖。
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>引擎缓存（含创建失败的 null 负缓存），仅在 <see cref="_gate"/> 内访问。</summary>
    private readonly Dictionary<string, OcrEngine?> _engines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IsAvailable 的惰性缓存：语言包安装需要重启会话才生效，进程内不会变。</summary>
    private bool? _isAvailable;

    /// <summary>最近一次「已完成」识别的结束状态（后台线程可读，UI 请用 <see cref="Status"/>）。识别是串行的，不存在互相覆盖。</summary>
    public OcrStatus LastStatus { get; private set; } = OcrStatus.Idle;

    /// <summary>系统是否至少能创建出一个 OCR 引擎（结果缓存，进程内只探测一次）。</summary>
    public bool IsAvailable
    {
        get
        {
            if (_isAvailable is { } cached) return cached;
            bool available = false;
            try
            {
                available = OcrEngine.TryCreateFromUserProfileLanguages() is not null
                            || FallbackTags.Any(tag => TryCreateFromTag(tag) is not null);
            }
            catch
            {
                // WinRT 不可用（缺组件 / 精简版系统）时按不可用处理
            }
            _isAvailable = available;
            return available;
        }
    }

    /// <summary>系统已安装的 OCR 识别语言标签。</summary>
    public IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>识别 PNG 字节；失败或无引擎返回 null（<see cref="LastStatus"/> 区分原因）。</summary>
    public Task<OcrResult?> RecognizeAsync(byte[] png, string? langHint, CancellationToken ct)
    {
        if (png.Length == 0) return Task.FromResult<OcrResult?>(null);
        return Task.Run(() => RecognizeCoreAsync(png, langHint, ct), ct);
    }

    private async Task<OcrResult?> RecognizeCoreAsync(byte[] png, string? langHint, CancellationToken ct)
    {
        // 整个识别流程串行：引擎非线程安全，同时也保证 Status/LastStatus 是单次调用的结果
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await RecognizeLockedAsync(png, langHint, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>已持有 <see cref="_gate"/> 时的识别主体。</summary>
    private async Task<OcrResult?> RecognizeLockedAsync(byte[] png, string? langHint, CancellationToken ct)
    {
        SetStatus(OcrStatus.Running);
        OcrEngine? engine;
        try
        {
            engine = ResolveEngine(langHint);
        }
        catch
        {
            SetStatus(OcrStatus.Failed);
            return null;
        }

        if (engine is null)
        {
            SetStatus(OcrStatus.NoEngine);
            return null;
        }

        SoftwareBitmap? bitmap = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            bitmap = await DecodeAsync(png).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var ocr = await engine.RecognizeAsync(bitmap);
            string tag = engine.RecognizerLanguage.LanguageTag;
            bool cjk = tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                       || tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var line in ocr.Lines)
            {
                // 中日文引擎会把每个字当作一个 word，直接用 line.Text 会得到满是空格的结果
                lines.Add(cjk ? string.Concat(line.Words.Select(w => w.Text)) : line.Text);
            }
            var result = new OcrResult(string.Join(Environment.NewLine, lines), tag, lines);
            SetStatus(OcrStatus.Done);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetStatus(OcrStatus.Idle);
            return null;
        }
        catch
        {
            SetStatus(OcrStatus.Failed);
            return null;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>识别剪贴板图片条目，并把结果写回 <see cref="ClipboardEntry.OcrText"/>（UI 线程）后落盘。</summary>
    public async Task<OcrResult?> RecognizeEntryAsync(ClipboardEntry entry, ClipboardStore store,
        string? langHint, CancellationToken ct)
    {
        byte[]? png;
        try
        {
            png = store.ImageData(entry);
        }
        catch
        {
            png = null;
        }
        if (png is null || png.Length == 0) return null;

        var result = await RecognizeAsync(png, langHint, ct).ConfigureAwait(false);
        if (result is null || ct.IsCancellationRequested) return result;

        OnUi(() =>
        {
            entry.OcrText = result.Text;
            entry.OcrLang = result.LanguageTag;
            store.Save();
        });
        return result;
    }

    /// <summary>
    /// 解析语言标签对应的引擎（调用方必须已持有 <see cref="_gate"/>）。
    /// 显式语言绝不做静默替换：装不上就返回 null（→ NoEngine + <see cref="UnavailableMessage"/> 引导装语言包），
    /// 并把 null 一起写进缓存作为负缓存，避免每次识别都重试创建。
    /// </summary>
    private OcrEngine? ResolveEngine(string? langHint)
    {
        bool auto = string.IsNullOrWhiteSpace(langHint)
                    || langHint.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        string key = auto ? "auto" : langHint!.Trim();

        if (_engines.TryGetValue(key, out var cached)) return cached;

        OcrEngine? engine;
        if (auto)
        {
            engine = TryCreateFromUserProfile();
            foreach (var tag in FallbackTags)
            {
                if (engine is not null) break;
                engine = TryCreateFromTag(tag);
            }
        }
        else
        {
            engine = TryCreateFromTag(key);
        }

        _engines[key] = engine;
        return engine;
    }

    private static OcrEngine? TryCreateFromUserProfile()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return null;
        }
    }

    private static OcrEngine? TryCreateFromTag(string tag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PNG → Bgra8/Premultiplied 的 SoftwareBitmap，必要时等比缩放到引擎上限内。</summary>
    private static async Task<SoftwareBitmap> DecodeAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream(); // 先解绑，Dispose 时才不会连带关掉底层流
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        uint max = OcrEngine.MaxImageDimension;

        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (width > max || height > max)
        {
            double scale = Math.Min((double)max / width, (double)max / height);
            transform.ScaledWidth = Math.Max(1u, (uint)Math.Floor(width * scale));
            transform.ScaledHeight = Math.Max(1u, (uint)Math.Floor(height * scale));
        }
        else
        {
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    private void SetStatus(OcrStatus status)
    {
        LastStatus = status;
        OnUi(() => Status = status);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
