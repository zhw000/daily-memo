using System;
using System.Text.RegularExpressions;

namespace DailyMemo.Core;

public sealed record QuickParseResult(string Title, DateTime? Date, TimeSpan? Time)
{
    public DateTime? When => Date.HasValue ? Date.Value.Date + (Time ?? TimeSpan.Zero) : null;

    public string Describe(DateTime today)
    {
        if (!Date.HasValue) return "";
        var text = ChineseDate.Due(Date.Value, false, today);
        if ((Date.Value.Date - today).Days is > 2 and < 7)
            text = $"{text}（{ChineseDate.MonthDay(Date.Value, today)}）";
        if (Time.HasValue) text += " " + DateTime.Today.Add(Time.Value).ToString("HH:mm");
        return text;
    }
}

/// <summary>
/// 从一句中文里识别日期和时间，例如「明天下午3点 交报告」「周五 买菜」「10月1日 回家」「半小时后 关火」。
/// 识别出来的部分会从标题里去掉。
/// </summary>
public static class QuickParser
{
    private const string CnNum = "[一二两三四五六七八九十]{1,3}";
    private const string AnyNum = @"\d{1,2}|" + CnNum;
    private const string Period = "凌晨|早上|早晨|清晨|上午|中午|下午|傍晚|晚上|夜里|夜间|晚";
    private const string MinuteTail = @"半|一刻|三刻|(?<min>\d{1,2}|[一二三四五六七八九十]{1,3})\s*分";

    public static QuickParseResult Parse(string input, DateTime now)
    {
        string text = " " + (input ?? "") + " ";
        DateTime today = now.Date;
        DateTime? date = null;
        TimeSpan? time = null;
        string? dayPeriod = null;
        Match m;

        // 「提醒我」「记得」之类的口头语
        text = Regex.Replace(text, @"^\s*请?(提醒我|提醒|记得|别忘了|不要忘了)\s*", " ");

        // 相对时间：N小时后 / 半小时后 / N分钟后 / N天后
        m = Regex.Match(text, $@"(半|{AnyNum})\s*个?\s*(小时|钟头)\s*以?后");
        if (m.Success)
        {
            double hours = m.Groups[1].Value == "半" ? 0.5 : CnNumber(m.Groups[1].Value) ?? 0;
            SetDateTime(now.AddHours(hours), ref date, ref time);
            text = Cut(text, m);
        }
        else if ((m = Regex.Match(text, $@"(\d{{1,3}}|{CnNum})\s*分钟\s*以?后")).Success)
        {
            SetDateTime(now.AddMinutes(CnNumber(m.Groups[1].Value) ?? 0), ref date, ref time);
            text = Cut(text, m);
        }
        else if ((m = Regex.Match(text, $@"({AnyNum})\s*天\s*以?后")).Success)
        {
            date = today.AddDays(CnNumber(m.Groups[1].Value) ?? 0);
            text = Cut(text, m);
        }

        // 2026-10-01 / 2026年10月1日
        if (date == null && (m = Regex.Match(text, @"(\d{4})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})\s*[日号]?")).Success
            && TryDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out var full))
        {
            date = full;
            text = Cut(text, m);
        }

        // 10月1日 / 十月一号
        if (date == null && (m = Regex.Match(text, $@"({AnyNum})\s*月\s*({AnyNum})\s*[日号]?")).Success)
        {
            int? mo = CnNumber(m.Groups[1].Value), da = CnNumber(m.Groups[2].Value);
            if (mo.HasValue && da.HasValue && TryDate(today.Year, mo.Value, da.Value, out var d))
            {
                if (d < today) TryDate(today.Year + 1, mo.Value, da.Value, out d);
                date = d;
                text = Cut(text, m);
            }
        }

        // 10/1
        if (date == null && (m = Regex.Match(text, @"(?<![\d:：/])(\d{1,2})/(\d{1,2})(?![\d/])")).Success
            && TryDate(today.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), out var md))
        {
            if (md < today) TryDate(today.Year + 1, md.Month, md.Day, out md);
            date = md;
            text = Cut(text, m);
        }

        // 周五 / 下周三 / 这个星期天
        if (date == null && (m = Regex.Match(text, @"(下下个?|下个?|这个?|本)?\s*(周|星期|礼拜)\s*([一二三四五六日天1-7])")).Success)
        {
            date = WeekdayDate(today, m.Groups[1].Value, m.Groups[3].Value);
            text = Cut(text, m);
        }

        // 今天 / 明天 / 后天 / 今晚 ...
        if (date == null && (m = Regex.Match(text, "大后天|后天|明天|明日|明儿|明早|明晚|今天|今日|今儿|今早|今晚|今夜")).Success)
        {
            var w = m.Value;
            date = today.AddDays(w switch
            {
                "大后天" => 3,
                "后天" => 2,
                "明天" or "明日" or "明儿" or "明早" or "明晚" => 1,
                _ => 0,
            });
            if (w.EndsWith("早")) dayPeriod = "早上";
            if (w.EndsWith("晚") || w.EndsWith("夜")) dayPeriod = "晚上";
            text = Cut(text, m);
        }

        // 本月 N 号（后面必须是空白或时间词，避免把「10号楼」当成日期）
        if (date == null && (m = Regex.Match(text, $@"(?<=^|[\s，,])({AnyNum})\s*[号日](?=\s|{Period}|\d)")).Success)
        {
            int? da = CnNumber(m.Groups[1].Value);
            if (da.HasValue && TryDate(today.Year, today.Month, da.Value, out var d))
            {
                if (d < today)
                {
                    var next = today.AddMonths(1);
                    if (!TryDate(next.Year, next.Month, da.Value, out d)) d = today;
                }
                date = d;
                text = Cut(text, m);
            }
        }

        // 15:30 / 下午3:30
        if (time == null && (m = Regex.Match(text, $@"({Period})?\s*(\d{{1,2}})\s*[:：]\s*(\d{{2}})(?!\d)")).Success)
        {
            time = MakeTime(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[1].Value, dayPeriod);
            if (time != null) text = Cut(text, m);
        }

        // 下午3点 / 3点半 / 10点20分
        if (time == null && (m = Regex.Match(text, $@"({Period})?\s*(\d{{1,2}})\s*[点點时]钟?\s*({MinuteTail})?")).Success)
        {
            time = MakeTime(int.Parse(m.Groups[2].Value), MinuteOf(m), m.Groups[1].Value, dayPeriod);
            if (time != null) text = Cut(text, m);
        }

        // 九点一刻 / 晚上八点。中文数字容易误判（「买一点东西」），所以要求有时段词、日期或分钟
        if (time == null && (m = Regex.Match(text, $@"({Period})?\s*({CnNum})\s*[点點]钟?\s*({MinuteTail})?")).Success
            && (m.Groups[1].Success || m.Groups[3].Success || date != null || m.Value.Contains('钟')))
        {
            time = MakeTime(CnNumber(m.Groups[2].Value), MinuteOf(m), m.Groups[1].Value, dayPeriod);
            if (time != null) text = Cut(text, m);
        }

        // 单独的「晚上」「上午」等词不设置时间，只从标题里去掉
        if (time == null && date != null && (m = Regex.Match(text, $@"(?<=^|\s)({Period})(?=\s)")).Success)
            text = Cut(text, m);

        if (time != null && date == null)
        {
            date = today;
            if (today + time.Value < now.AddMinutes(-1)) date = today.AddDays(1);
        }

        var title = Regex.Replace(text, @"\s+", " ").Trim().Trim(',', '，', '。', '.', '、', ':', '：', ' ');
        if (title.Length == 0) title = (input ?? "").Trim();
        return new QuickParseResult(title, date, time);
    }

    private static void SetDateTime(DateTime t, ref DateTime? date, ref TimeSpan? time)
    {
        date = t.Date;
        time = new TimeSpan(t.Hour, t.Minute, 0);
    }

    private static int MinuteOf(Match m)
    {
        var tail = m.Groups[3].Value.Trim();
        if (tail == "半") return 30;
        if (tail == "一刻") return 15;
        if (tail == "三刻") return 45;
        return m.Groups["min"].Success ? CnNumber(m.Groups["min"].Value) ?? 0 : 0;
    }

    private static string Cut(string text, Match m) => text.Remove(m.Index, m.Length).Insert(m.Index, " ");

    private static TimeSpan? MakeTime(int? hour, int minute, string periodInText, string? periodFromDay)
    {
        if (hour == null || minute is < 0 or > 59) return null;
        int h = hour.Value;
        var p = string.IsNullOrEmpty(periodInText) ? periodFromDay : periodInText;
        switch (p)
        {
            case "下午" or "傍晚" or "晚上" or "夜里" or "夜间" or "晚":
                if (h < 12) h += 12;
                break;
            case "中午":
                if (h < 11) h += 12;
                break;
            case "凌晨":
                if (h == 12) h = 0;
                break;
        }
        if (h is < 0 or > 23) return null;
        return new TimeSpan(h, minute, 0);
    }

    private static DateTime WeekdayDate(DateTime today, string prefix, string dayWord)
    {
        int target = dayWord switch
        {
            "一" or "1" => 0,
            "二" or "2" => 1,
            "三" or "3" => 2,
            "四" or "4" => 3,
            "五" or "5" => 4,
            "六" or "6" => 5,
            _ => 6,
        };
        int todayIdx = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-todayIdx);
        if (prefix.StartsWith("下下")) return monday.AddDays(14 + target);
        if (prefix.StartsWith("下")) return monday.AddDays(7 + target);
        if (prefix.StartsWith("这") || prefix == "本") return monday.AddDays(target);
        int diff = target - todayIdx;
        if (diff < 0) diff += 7;
        return today.AddDays(diff);
    }

    private static bool TryDate(int y, int m, int d, out DateTime date)
    {
        date = default;
        if (y < 1 || y > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateTime(y, m, d);
        return true;
    }

    /// <summary>阿拉伯数字或「十二」「二十三」「两」这样的中文数字</summary>
    public static int? CnNumber(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var n)) return n;
        if (s.Length == 0) return null;

        static int Digit(char c) => c switch
        {
            '零' => 0, '一' => 1, '二' => 2, '两' => 2, '三' => 3, '四' => 4,
            '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1,
        };

        int tenIdx = s.IndexOf('十');
        if (tenIdx < 0)
        {
            if (s.Length != 1) return null;
            int v = Digit(s[0]);
            return v >= 0 ? v : null;
        }
        if (tenIdx > 1 || s.Length > tenIdx + 2) return null;
        int tens = tenIdx == 0 ? 1 : Digit(s[0]);
        int ones = tenIdx == s.Length - 1 ? 0 : Digit(s[tenIdx + 1]);
        if (tens < 0 || ones < 0) return null;
        return tens * 10 + ones;
    }
}
