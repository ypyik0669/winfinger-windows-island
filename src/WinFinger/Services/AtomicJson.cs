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
}
