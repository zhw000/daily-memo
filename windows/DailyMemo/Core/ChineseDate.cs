using System;

namespace DailyMemo.Core;

public static class ChineseDate
{
    private static readonly string[] WeekLong = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
    private static readonly string[] WeekShort = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public static string Weekday(DateTime d) => WeekLong[(int)d.DayOfWeek];
    public static string WeekdayShort(DateTime d) => WeekShort[(int)d.DayOfWeek];
    public static string MonthDay(DateTime d) => $"{d.Month}月{d.Day}日";

    public static string MonthDay(DateTime d, DateTime today) =>
        d.Year == today.Year ? MonthDay(d) : $"{d.Year}年{d.Month}月{d.Day}日";

    public static string DayTitle(DateTime d) => $"{MonthDay(d)} {Weekday(d)}";

    public static string Time(DateTime d) => d.ToString("HH:mm");

    /// <summary>今天 / 明天 / 后天 / 昨天 / 周X</summary>
    public static string Relative(DateTime d, DateTime today)
    {
        int diff = (d.Date - today.Date).Days;
        return diff switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            -1 => "昨天",
            -2 => "前天",
            _ => WeekdayShort(d),
        };
    }

    /// <summary>「明天 · 9月28日 周一」这样的分组标题</summary>
    public static string DayHeader(DateTime d, DateTime today)
    {
        int diff = (d.Date - today.Date).Days;
        return diff is >= -2 and <= 2
            ? $"{Relative(d, today)} · {MonthDay(d, today)} {WeekdayShort(d)}"
            : $"{MonthDay(d, today)} {WeekdayShort(d)}";
    }

    /// <summary>截止日期的简短描述：今天 / 明天 15:00 / 9月25日 / 周五</summary>
    public static string Due(DateTime due, bool hasTime, DateTime today)
    {
        int diff = (due.Date - today.Date).Days;
        string day = diff is >= -2 and <= 2 ? Relative(due, today)
            : diff is > 2 and < 7 ? WeekdayShort(due)
            : MonthDay(due, today);
        return hasTime ? $"{day} {Time(due)}" : day;
    }
}
