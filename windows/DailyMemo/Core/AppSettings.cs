using System.Collections.Generic;

namespace DailyMemo.Core;

public enum WidgetMode { Desktop, Topmost, Normal }

public sealed class AppSettings
{
    // Google（桌面应用类型的 OAuth 客户端）
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public bool GoogleCalendarEnabled { get; set; } = true;
    public bool GoogleTasksEnabled { get; set; } = true;

    /// <summary>访问 Google 用的 HTTP 代理，例如 127.0.0.1:7890；留空则使用系统代理</summary>
    public string ProxyAddress { get; set; } = "";

    // iCloud 日历（CalDAV，密码单独加密保存）
    public string ICloudAppleId { get; set; } = "";

    /// <summary>iCloud CalDAV 服务器；中国大陆 Apple ID 会自动改用 caldav.icloud.com.cn</summary>
    public string ICloudServer { get; set; } = "";

    /// <summary>容器键 → 是否显示；没有记录时使用容器自己的默认值</summary>
    public Dictionary<string, bool> Visibility { get; set; } = new();

    public string DefaultTaskTarget { get; set; } = ContainerKeys.Local;
    public string DefaultEventTarget { get; set; } = "";

    // 桌面小组件
    public bool WidgetVisible { get; set; } = true;
    public WidgetMode WidgetMode { get; set; } = WidgetMode.Desktop;
    public double WidgetLeft { get; set; } = double.NaN;
    public double WidgetTop { get; set; } = double.NaN;
    public double WidgetWidth { get; set; } = 340;
    public double WidgetHeight { get; set; } = 460;
    public double WidgetOpacity { get; set; } = 0.9;
    public bool WidgetLocked { get; set; }
    public bool WidgetShowCompleted { get; set; } = true;
    public bool WidgetShowUndated { get; set; } = true;

    // 提醒
    public bool DailySummaryEnabled { get; set; } = true;
    public string DailySummaryTime { get; set; } = "08:30";
    public string LastSummaryDate { get; set; } = "";
    public bool EventReminderEnabled { get; set; } = true;
    public int EventReminderMinutes { get; set; } = 10;

    // 常规
    public bool LaunchAtStartup { get; set; } = true;
    public string Theme { get; set; } = "system";
    public int RefreshMinutes { get; set; } = 5;
    public bool ShowUndatedInToday { get; set; } = true;
    public bool ClosedToTrayHintShown { get; set; }
    public bool FirstRunDone { get; set; }
}
