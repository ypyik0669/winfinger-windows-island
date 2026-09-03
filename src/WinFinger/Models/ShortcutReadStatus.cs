namespace WinFinger.Models;

/// <summary>mac ShortcutReadStatus.</summary>
public enum ShortcutReadStatus
{
    Idle,
    Loading,
    Live,
    PermissionRequired,
    Unavailable
}

public sealed record ShortcutReadResult(IReadOnlyList<ShortcutGroup> Groups, ShortcutReadStatus Status, int Count)
{
    public static readonly ShortcutReadResult Unavailable = new(Array.Empty<ShortcutGroup>(), ShortcutReadStatus.Unavailable, 0);
}
