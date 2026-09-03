using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>把动作目录接到剪贴板右键菜单上（内联动作也一并列出，方便键盘 / 菜单用户）。</summary>
public sealed class CatalogActionProvider : IEntryActionProvider
{
    private readonly ActionCatalogService _catalog;
    private readonly Func<ActionExecutor?> _executor;

    public CatalogActionProvider(ActionCatalogService catalog, Func<ActionExecutor?> executor)
    {
        _catalog = catalog;
        _executor = executor;
    }

    /// <summary>AI 类动作归到这个子菜单。</summary>
    public const string AiGroup = "AI";

    public IEnumerable<EntryAction> ActionsFor(ClipboardEntry entry, IReadOnlyList<ClipboardEntry> selection)
    {
        foreach (var def in _catalog.For(entry))
        {
            var captured = def;
            bool isAi = captured.Run.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase) ||
                        captured.Id.StartsWith("ai-", StringComparison.OrdinalIgnoreCase);
            yield return new EntryAction(
                captured.Title,
                _ => true,
                target => _executor()?.RunAsync(captured, target).ContinueWith(
                    t => { _ = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted),
                Icon: Controls.ActionGlyph.IsGlyph(captured.Icon) ? Controls.ActionGlyph.Text(captured.Icon) : null,
                IsDanger: false,
                Order: captured.Order,
                Group: isAi ? AiGroup : null);
        }
    }
}
