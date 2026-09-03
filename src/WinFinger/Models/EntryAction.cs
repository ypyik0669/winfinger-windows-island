namespace WinFinger.Models;

/// <summary>
/// 剪贴板条目的一个可执行动作（右键菜单 / 内联按钮同源）。
/// Icon 用 Segoe Fluent 字形（写成 \uXXXX 转义），Order 越小越靠前。
/// </summary>
public sealed record EntryAction(
    string Header,
    Func<ClipboardEntry, bool> IsVisible,
    Action<ClipboardEntry> Execute,
    string? Icon = null,
    bool IsDanger = false,
    int Order = 0);

/// <summary>动作扩展点：OCR / AI 等能力向剪贴板条目挂载自己的菜单项。</summary>
public interface IEntryActionProvider
{
    IEnumerable<EntryAction> ActionsFor(ClipboardEntry entry, IReadOnlyList<ClipboardEntry> selection);
}
