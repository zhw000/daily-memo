using System;
using DailyMemo.Core;

namespace DailyMemo.Services;

public sealed class SettingsService
{
    public SettingsService()
    {
        Current = JsonFile.Load(AppPaths.Settings, () => new AppSettings());
    }

    public AppSettings Current { get; }

    public event EventHandler? Saved;

    public void Save()
    {
        try
        {
            JsonFile.Save(AppPaths.Settings, Current);
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public string? ICloudPassword
    {
        get => SecureStore.Read(AppPaths.ICloudPassword);
        set
        {
            if (string.IsNullOrEmpty(value)) SecureStore.Delete(AppPaths.ICloudPassword);
            else SecureStore.Write(AppPaths.ICloudPassword, value);
        }
    }

    public bool ICloudConfigured => !string.IsNullOrWhiteSpace(Current.ICloudAppleId) && !string.IsNullOrEmpty(ICloudPassword);
}
