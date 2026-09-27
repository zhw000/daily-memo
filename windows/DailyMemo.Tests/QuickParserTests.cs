using DailyMemo.Core;
using Xunit;

namespace DailyMemo.Tests;

public class QuickParserTests
{
    // 2026-09-27 是星期日
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0);

    [Theory]
    [InlineData("明天下午3点 交报告", "交报告", "2026-09-28", "15:00")]
    [InlineData("明天下午3点交报告", "交报告", "2026-09-28", "15:00")]
    [InlineData("后天 买菜", "买菜", "2026-09-29", null)]
    [InlineData("大后天上午9点半 体检", "体检", "2026-09-30", "09:30")]
    [InlineData("周五 交房租", "交房租", "2026-10-02", null)]
    [InlineData("下周一 开会", "开会", "2026-09-28", null)]
    [InlineData("下周三 交作业", "交作业", "2026-09-30", null)]
    [InlineData("下下周二 复诊", "复诊", "2026-10-06", null)]
    [InlineData("这周日 休息", "休息", "2026-09-27", null)]
    [InlineData("10月1日 回家", "回家", "2026-10-01", null)]
    [InlineData("十月一号 回家", "回家", "2026-10-01", null)]
    [InlineData("3月5日 交税", "交税", "2027-03-05", null)]
    [InlineData("2026-12-24 平安夜", "平安夜", "2026-12-24", null)]
    [InlineData("12/31 跨年", "跨年", "2026-12-31", null)]
    [InlineData("今晚8点 看电影", "看电影", "2026-09-27", "20:00")]
    [InlineData("明早7:30 跑步", "跑步", "2026-09-28", "07:30")]
    [InlineData("晚上九点 吃药", "吃药", "2026-09-27", "21:00")]
    [InlineData("中午12点 吃饭", "吃饭", "2026-09-27", "12:00")]
    [InlineData("中午1点 午休", "午休", "2026-09-27", "13:00")]
    [InlineData("15:30 开会", "开会", "2026-09-27", "15:30")]
    [InlineData("8点 开会", "开会", "2026-09-28", "08:00")]
    [InlineData("提醒我明天 买牛奶", "买牛奶", "2026-09-28", null)]
    [InlineData("3天后 还书", "还书", "2026-09-30", null)]
    [InlineData("半小时后 关火", "关火", "2026-09-27", "10:30")]
    [InlineData("20分钟后 出门", "出门", "2026-09-27", "10:20")]
    [InlineData("30号 交电费", "交电费", "2026-09-30", null)]
    public void RecognizesDates(string input, string title, string date, string? time)
    {
        var r = QuickParser.Parse(input, Now);
        Assert.Equal(title, r.Title);
        Assert.Equal(DateTime.Parse(date), r.Date);
        if (time == null) Assert.Null(r.Time);
        else Assert.Equal(TimeSpan.Parse(time), r.Time);
    }

    [Theory]
    [InlineData("买一点东西")]
    [InlineData("去10号楼开会")]
    [InlineData("买2斤苹果")]
    [InlineData("整理书架")]
    [InlineData("看《三体》")]
    public void LeavesPlainTextAlone(string input)
    {
        var r = QuickParser.Parse(input, Now);
        Assert.Equal(input, r.Title);
        Assert.Null(r.Date);
        Assert.Null(r.Time);
    }

    [Theory]
    [InlineData("十", 10)]
    [InlineData("十二", 12)]
    [InlineData("二十三", 23)]
    [InlineData("两", 2)]
    [InlineData("九", 9)]
    [InlineData("31", 31)]
    public void ParsesChineseNumbers(string s, int expected) => Assert.Equal(expected, QuickParser.CnNumber(s));
}
