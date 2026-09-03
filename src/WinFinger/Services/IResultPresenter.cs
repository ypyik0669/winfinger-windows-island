using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>结果抽屉底部按钮（按需组合）。</summary>
[Flags]
public enum ResultActions
{
    None = 0,
    Copy = 1,
    Paste = 2,
    ReplaceEntry = 4,
    AppendEntry = 8,
    SaveFile = 16,
    OpenUrl = 32,
    Translate = 64,
    Ai = 128,

    /// <summary>文本结果的常用组合。</summary>
    Text = Copy | Paste | AppendEntry | SaveFile | Translate | Ai
}

/// <summary>动作执行结果的呈现方（<see cref="Controls.ResultDrawer"/> 实现）。</summary>
public interface IResultPresenter
{
    bool IsOpen { get; }

    void ShowText(string title, string text, ResultActions actions, ClipboardEntry? sourceEntry = null);

    /// <summary>开一个流式文本结果；随后用 <see cref="AppendChunk"/> 追加、<see cref="Complete"/> 收尾。</summary>
    void ShowStreaming(string title, ResultActions actions, CancellationTokenSource cts, ClipboardEntry? sourceEntry = null);

    void AppendChunk(string chunk);

    /// <summary>结束流式；<paramref name="error"/> 非空表示失败信息。</summary>
    void Complete(string? error);

    void ShowImage(string title, BitmapSource image, ResultActions actions, byte[]? pngBytes = null);

    void ShowColor(string title, Color color, string hex, string rgb, string hsl);

    void ShowMessage(string title, string message, (string Label, Action Run)? cta = null);

    void Close();
}
