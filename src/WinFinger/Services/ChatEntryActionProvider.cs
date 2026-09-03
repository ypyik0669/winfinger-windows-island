using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Services;

/// <summary>
/// 剪贴板条目右键菜单里的「发送到 AI 对话」。图片条目先走离线 OCR，只把识别出的文字带进对话，
/// 不发图片本身——省 token，也不依赖视觉模型。
/// </summary>
public sealed class ChatEntryActionProvider : IEntryActionProvider
{
    private readonly AppViewModel _model;

    public ChatEntryActionProvider(AppViewModel model) => _model = model;

    public IEnumerable<EntryAction> ActionsFor(ClipboardEntry entry, IReadOnlyList<ClipboardEntry> selection)
    {
        var targets = selection.Count > 1 ? selection : new List<ClipboardEntry> { entry };
        yield return new EntryAction(
            selection.Count > 1 ? $"发送 {selection.Count} 条到 AI 对话" : "发送到 AI 对话",
            e => e.Kind != ClipboardEntryKind.File,
            target => { _ = SendAsync(targets.Count > 0 ? targets : new[] { target }); },
            Icon: "\uE8BD",
            IsDanger: false,
            Order: -100,
            Group: CatalogActionProvider.AiGroup);
    }

    private async Task SendAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        var parts = new List<string>();
        int skippedImages = 0;

        foreach (var entry in entries)
        {
            string? text = await ResolveTextAsync(entry).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text)) skippedImages++;
            else parts.Add(text!);
        }

        if (parts.Count == 0)
        {
            _model.Notifications.Post("🤖", skippedImages > 0 ? "没能识别出文字" : "这条没有可发送的文本");
            return;
        }

        string source = entries.Count > 1 ? $"剪贴板 · {parts.Count} 条" : "来自剪贴板";
        _model.SendToChat(string.Join("\n\n---\n\n", parts), source);
    }

    /// <summary>文本直接用；图片先看有没有识别过，没有就现场 OCR（没装语言包就提示并跳过）。</summary>
    private async Task<string?> ResolveTextAsync(ClipboardEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Text)) return entry.Text;
        if (!string.IsNullOrWhiteSpace(entry.OcrText)) return entry.OcrText;
        if (entry.Kind != ClipboardEntryKind.Image) return null;

        if (!_model.Ocr.IsAvailable)
        {
            _model.Notifications.Post("🔤", OcrService.UnavailableMessage);
            return null;
        }

        _model.Notifications.Post("🔤", "正在识别图片文字…");
        try
        {
            var result = await _model.Ocr.RecognizeEntryAsync(entry, _model.ClipboardStore,
                _model.SettingsStore.Settings.OcrLanguage, CancellationToken.None).ConfigureAwait(true);
            return result?.Text;
        }
        catch
        {
            return null;
        }
    }
}
