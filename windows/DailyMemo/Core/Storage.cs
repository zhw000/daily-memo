using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DailyMemo.Core;

public static class AppPaths
{
    public static string DataDir { get; } = InitDataDir();

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Cache => Path.Combine(DataDir, "cache.json");
    public static string LocalTasks => Path.Combine(DataDir, "local_tasks.json");
    public static string GoogleToken => Path.Combine(DataDir, "google_token.bin");
    public static string ICloudPassword => Path.Combine(DataDir, "icloud.bin");
    public static string Log => Path.Combine(DataDir, "log.txt");

    private static string InitDataDir()
    {
        // 测试或预览时可以用环境变量指定独立的数据目录
        var dir = Environment.GetEnvironmentVariable("DAILYMEMO_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DailyMemo");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var value = JsonSerializer.Deserialize<T>(text, Options);
                if (value != null) return value;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"读取 {Path.GetFileName(path)} 失败", ex);
        }
        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>用 Windows DPAPI（当前用户）加密保存令牌和密码</summary>
public static class SecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DailyMemo.v1");

    public static void Write(string path, string plain)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, data);
    }

    public static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Log.Error($"解密 {Path.GetFileName(path)} 失败", ex);
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.Log;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
