using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Services;

/// <summary>把一条 <see cref="ActionDefinition"/> 真正跑起来；任何分支都不抛异常。</summary>
public sealed class ActionExecutor
{
    /// <summary>shell 里带入的文本上限，避免命令行超长。</summary>
    public const int ShellTextLimit = 8000;

    private const string PromptSystem = "你是一个简洁的助手，只输出结果。";

    private readonly AppViewModel _model;
    private readonly IResultPresenter _presenter;
    private CancellationTokenSource? _streamCts;

    public ActionExecutor(AppViewModel model, IResultPresenter presenter)
    {
        _model = model;
        _presenter = presenter;
    }

    public async Task RunAsync(ActionDefinition def, ClipboardEntry entry)
    {
        try
        {
            if (!ActionCatalogService.ParseRun(def.Run, out var kind, out string payload))
            {
                _presenter.ShowMessage(def.Title, "动作配置有误：run 字段无法识别。");
                return;
            }
            switch (kind)
            {
                case ActionRunKind.Open:
                    RunOpen(def, Expand(payload, entry));
                    break;
                case ActionRunKind.Shell:
                    RunShell(def, entry, payload);
                    break;
                case ActionRunKind.Prompt:
                    await RunPromptAsync(def.Title, Expand(payload, entry), entry);
                    break;
                default:
                    await RunBuiltinAsync(def, payload.Trim().ToLowerInvariant(), entry);
                    break;
            }
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"动作执行失败：{ex.Message}");
        }
    }

    // ── placeholders ──

    /// <summary>
    /// 展开 {text} {path} {png} {paths} {app}。
    /// <paramref name="forShell"/> 只做长度截断——shell 动作不经过任何 shell，展开后的值整段作为一个参数传给
    /// <see cref="ProcessStartInfo.ArgumentList"/>，所以不需要（也不能）做引号转义。
    /// </summary>
    internal static string Expand(string template, ClipboardEntry entry, bool forShell = false)
    {
        string text = entry.Text ?? entry.OcrText ?? "";
        string path = entry.ImagePath ?? entry.FirstFilePath ?? "";
        string paths = entry.FilePaths.Count > 0
            ? string.Join(" ", entry.FilePaths.Select(p => $"\"{p}\""))
            : (path.Length > 0 ? $"\"{path}\"" : "");
        if (forShell && text.Length > ShellTextLimit) text = text[..ShellTextLimit];
        return template
            .Replace("{text}", text)
            .Replace("{png}", path)
            .Replace("{paths}", paths)
            .Replace("{path}", path)
            .Replace("{app}", entry.SourceAppName ?? "");
    }

    /// <summary>
    /// 把 shell 动作的模板切成"程序 + 参数表"：按空白切分，双引号内的空白不切。
    /// 占位符在切分之后才展开，所以剪贴板里的空格、引号、<c>&amp;</c>、<c>%VAR%</c>、换行都只会留在同一个参数里。
    /// </summary>
    internal static List<string> Tokenize(string template)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, started = false;
        foreach (char c in template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) tokens.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }
            current.Append(c);
            started = true;
        }
        if (started) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>切分并展开一条 shell 动作；模板为空时返回 false。</summary>
    internal static bool BuildShellCommand(string template, ClipboardEntry entry,
        out string fileName, out List<string> arguments)
    {
        fileName = "";
        arguments = new List<string>();
        var tokens = Tokenize(template);
        if (tokens.Count == 0) return false;

        fileName = Expand(tokens[0], entry, forShell: true);
        if (fileName.Length == 0) return false;
        foreach (string token in tokens.Skip(1))
        {
            // 单独的 {paths} 展开成多个参数，其余整段作为一个参数
            if (token == "{paths}")
            {
                var list = entry.FilePaths.Count > 0
                    ? entry.FilePaths.AsEnumerable()
                    : new[] { entry.ImagePath ?? entry.FirstFilePath ?? "" }.Where(p => p.Length > 0);
                arguments.AddRange(list);
                continue;
            }
            arguments.Add(Expand(token, entry, forShell: true));
        }
        return true;
    }

    // ── run kinds ──

    private void RunOpen(ActionDefinition def, string target)
    {
        target = target.Trim();
        if (target.Length == 0)
        {
            _presenter.ShowMessage(def.Title, "没有可打开的内容。");
            return;
        }
        // "www.example.com" 没有协议头，ShellExecute 会当成文件名找不到
        if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) target = "http://" + target;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"打开失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行 shell 动作。<b>不经过 cmd.exe</b>：模板切成程序 + 参数表后直接 CreateProcess，
    /// 剪贴板内容永远只是一个参数，不会被 <c>&amp;</c> / <c>|</c> 断开，<c>%VAR%</c> 也不会展开。
    /// 确实需要 cmd 特性的用户可以自己写 <c>shell:cmd /c …</c>（风险自负）。
    /// </summary>
    private void RunShell(ActionDefinition def, ClipboardEntry entry, string template)
    {
        if (!BuildShellCommand(template, entry, out string fileName, out var arguments))
        {
            _presenter.ShowMessage(def.Title, "命令为空。");
            return;
        }
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"命令执行失败：{ex.Message}");
        }
    }

    private async Task RunBuiltinAsync(ActionDefinition def, string name, ClipboardEntry entry)
    {
        string body = entry.Text ?? entry.OcrText ?? "";
        switch (name)
        {
            case "json-format":
            case "json-minify":
                try
                {
                    string result = name == "json-format" ? BuiltinTools.FormatJson(body) : BuiltinTools.MinifyJson(body);
                    _presenter.ShowText(def.Title, result, ResultActions.Text | ResultActions.ReplaceEntry, entry);
                }
                catch (JsonException ex)
                {
                    _presenter.ShowMessage(def.Title, $"不是合法的 JSON：{ex.Message}");
                }
                break;

            case "timestamp":
            {
                string? described = BuiltinTools.DescribeTimestamp(body.Trim());
                if (described is null) _presenter.ShowMessage(def.Title, "没能识别成时间戳。");
                else _presenter.ShowText(def.Title, described, ResultActions.Copy | ResultActions.Paste | ResultActions.AppendEntry, entry);
                break;
            }

            case "word-count":
                _presenter.ShowText(def.Title, BuiltinTools.WordCount(body),
                    ResultActions.Copy | ResultActions.AppendEntry, entry);
                break;

            case "copy-digits":
            {
                string digits = BuiltinTools.DigitsOnly(body);
                if (digits.Length == 0)
                {
                    _presenter.ShowMessage(def.Title, "没有可复制的号码。");
                    break;
                }
                _model.ClipboardMonitor.CopyText(digits);
                _model.Notifications.Post("📋", $"已复制 {digits}");
                break;
            }

            case "color":
                ShowColor(def, body.Trim());
                break;

            case "open-path":
                OpenPath(def, entry);
                break;

            case "pin":
                PinImage(def, entry);
                break;

            case "ocr":
                await RunOcrAsync(def, entry);
                break;

            case "qr-decode":
                await RunQrDecodeAsync(def, entry);
                break;

            case "qr-encode":
                await RunQrEncodeAsync(def, body);
                break;

            case "ai-translate":
            {
                string target = _model.SettingsStore.Settings.AiTargetLanguage;
                await RunAiAsync(def.Title, AiService.TranslateSystemPrompt,
                    AiService.BuildTranslatePrompt(body, target), entry);
                break;
            }

            default:
                _presenter.ShowMessage(def.Title, $"未知的内置动作：{name}");
                break;
        }
    }

    private async Task RunPromptAsync(string title, string prompt, ClipboardEntry entry) =>
        await RunAiAsync(title, PromptSystem, prompt, entry);

    // ── builtin implementations ──

    private void ShowColor(ActionDefinition def, string text)
    {
        if (!ContentDetector.TryParseColor(text, out var color))
        {
            _presenter.ShowMessage(def.Title, "没能识别成颜色。");
            return;
        }
        _presenter.ShowColor(def.Title, color, HexOf(color), RgbOf(color), HslOf(color));
    }

    internal static string HexOf(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static string RgbOf(Color c) =>
        c.A == 255 ? $"rgb({c.R}, {c.G}, {c.B})" : $"rgba({c.R}, {c.G}, {c.B}, {Math.Round(c.A / 255.0, 2)})";

    internal static string HslOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 0.0001)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }

    private void OpenPath(ActionDefinition def, ClipboardEntry entry)
    {
        string path = (entry.FirstFilePath ?? entry.Text ?? "").Trim().Trim('"');
        if (path.Length == 0 || (!File.Exists(path) && !Directory.Exists(path)))
        {
            _presenter.ShowMessage(def.Title, "路径不存在。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"打开失败：{ex.Message}");
        }
    }

    private void PinImage(ActionDefinition def, ClipboardEntry entry)
    {
        string path = entry.ImagePath ?? "";
        if (path.Length == 0 || !File.Exists(path))
        {
            _presenter.ShowMessage(def.Title, "图片文件已不存在。");
            return;
        }
        try
        {
            if (_model.IsExpanded) _model.Collapse();
            var win = new Views.PinnedImageWindow(path);
            win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"悬浮显示失败：{ex.Message}");
        }
    }

    private async Task RunOcrAsync(ActionDefinition def, ClipboardEntry entry)
    {
        if (!_model.Ocr.IsAvailable)
        {
            _presenter.ShowMessage(def.Title, OcrService.UnavailableMessage,
                ("打开语言设置", () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(OcrService.LanguageSettingsUri) { UseShellExecute = true });
                    }
                    catch
                    {
                        // 设置页打不开就算了
                    }
                }));
            return;
        }
        _presenter.ShowMessage(def.Title, "正在识别…");
        try
        {
            var result = await _model.Ocr.RecognizeEntryAsync(entry, _model.ClipboardStore,
                _model.SettingsStore.Settings.OcrLanguage, CancellationToken.None);
            string text = result?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _presenter.ShowMessage(def.Title, "未识别到文字");
                return;
            }
            _presenter.ShowText(def.Title, text, ResultActions.Text, entry);
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"识别失败：{ex.Message}");
        }
    }

    private async Task RunQrDecodeAsync(ActionDefinition def, ClipboardEntry entry)
    {
        byte[]? png = _model.ClipboardStore.ImageData(entry);
        if (png is null)
        {
            _presenter.ShowMessage(def.Title, "图片文件已不存在。");
            return;
        }
        string? text;
        try
        {
            text = await Task.Run(() => QrService.Decode(png));
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"识别失败：{ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _presenter.ShowMessage(def.Title, "没有发现二维码");
            return;
        }
        entry.QrText = text;
        _model.ClipboardStore.Save();
        var actions = ResultActions.Copy | ResultActions.Paste | ResultActions.AppendEntry;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            actions |= ResultActions.OpenUrl;
        _presenter.ShowText(def.Title, text, actions, entry);
    }

    private async Task RunQrEncodeAsync(ActionDefinition def, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _presenter.ShowMessage(def.Title, "没有可生成二维码的文本。");
            return;
        }
        try
        {
            // 只编码一次，PNG 字节直接从这张位图导出
            var (image, png) = await Task.Run(() =>
            {
                var bmp = QrService.Encode(text, 320);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var buffer = new MemoryStream();
                encoder.Save(buffer);
                return (bmp, buffer.ToArray());
            });
            _presenter.ShowImage(def.Title, image, ResultActions.Copy | ResultActions.SaveFile, png);
        }
        catch (InvalidOperationException ex)
        {
            _presenter.ShowMessage(def.Title, ex.Message);
        }
        catch (Exception ex)
        {
            _presenter.ShowMessage(def.Title, $"生成失败：{ex.Message}");
        }
    }

    // ── AI streaming ──

    /// <summary>流式跑一次 AI，结果进抽屉；未配置 Key 时给出提示。</summary>
    public async Task RunAiAsync(string title, string systemPrompt, string userPrompt, ClipboardEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            _presenter.ShowMessage(title, "没有可处理的文本。");
            return;
        }
        if (!_model.Ai.IsConfigured)
        {
            _presenter.ShowMessage(title, "未配置 AI，请在托盘 → 功能设置 中填写 API Key",
                ("打开功能设置", () => _model.RequestOpenFeatureSettings()));
            return;
        }

        CancelStream();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _presenter.ShowStreaming(title, ResultActions.Text, cts, entry);
        var sb = new StringBuilder();
        string? error = null;
        try
        {
            await foreach (string chunk in _model.Ai.StreamChatAsync(systemPrompt, userPrompt, cts.Token))
            {
                sb.Append(chunk);
                _presenter.AppendChunk(chunk);
            }
            if (sb.Length == 0) error = "AI 没有返回内容";
        }
        catch (OperationCanceledException)
        {
            error = null; // 用户主动停止
        }
        catch (AiException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = $"AI 请求失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_streamCts, cts))
            {
                _streamCts = null;
                _presenter.Complete(error);
            }
            cts.Dispose();
        }
    }

    private void CancelStream()
    {
        var cts = _streamCts;
        _streamCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经结束
        }
    }
}
