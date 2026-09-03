using System.IO;
using System.Text.Json;

namespace WinFinger.Services;

/// <summary>原子化 JSON 读写：先写 .tmp 再 File.Move 覆盖，避免写入过程中崩溃/断电导致文件半写损坏。</summary>
public static class AtomicJson
{
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, options));
        File.Move(tmp, path, overwrite: true);
    }

    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// 判断异常是否表示 JSON 内容本身已损坏（应改名为 .corrupt 保留现场），
    /// 而不是文件被杀软/备份等短暂占用的瞬时 I/O 失败（不应动这个文件，本次会话退回默认值即可）。
    /// </summary>
    public static bool IsCorruptionError(Exception ex) => ex is JsonException or NotSupportedException;
}
