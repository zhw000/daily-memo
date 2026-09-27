using System;
using Microsoft.Win32;
using DailyMemo.Core;

namespace DailyMemo.Services;

/// <summary>开机自启：写入当前用户的 Run 注册表项</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DailyMemo";

    /// <summary>开发调试时设置 DAILYMEMO_DEV=1，避免把调试版本写进开机启动</summary>
    public static bool IsDevMode => Environment.GetEnvironmentVariable("DAILYMEMO_DEV") == "1";

    public static void Apply(bool enabled)
    {
        if (IsDevMode) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("设置开机启动失败", ex);
        }
    }
}
