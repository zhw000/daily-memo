import Foundation

/// 中文日期显示
enum DateText {
    static var calendar: Calendar {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone.current
        c.locale = Locale(identifier: "zh_CN")
        c.firstWeekday = 2
        return c
    }

    private static let weekdays = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"]
    private static let weekdaysShort = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"]

    static func weekday(_ d: Date) -> String {
        weekdays[(calendar.component(.weekday, from: d) - 1 + 7) % 7]
    }

    static func weekdayShort(_ d: Date) -> String {
        weekdaysShort[(calendar.component(.weekday, from: d) - 1 + 7) % 7]
    }

    static func monthDay(_ d: Date) -> String {
        let c = calendar.dateComponents([.month, .day], from: d)
        return "\(c.month ?? 1)月\(c.day ?? 1)日"
    }

    static func monthDay(_ d: Date, today: Date) -> String {
        let cal = calendar
        if cal.component(.year, from: d) == cal.component(.year, from: today) { return monthDay(d) }
        return "\(cal.component(.year, from: d))年" + monthDay(d)
    }

    static func day(_ d: Date) -> String { "\(calendar.component(.day, from: d))" }

    static func month(_ d: Date) -> String { "\(calendar.component(.month, from: d))月" }

    static func time(_ d: Date) -> String {
        let c = calendar.dateComponents([.hour, .minute], from: d)
        return String(format: "%02d:%02d", c.hour ?? 0, c.minute ?? 0)
    }

    static func daysBetween(_ from: Date, _ to: Date) -> Int {
        let cal = calendar
        return cal.dateComponents([.day], from: cal.startOfDay(for: from), to: cal.startOfDay(for: to)).day ?? 0
    }

    /// 今天 / 明天 / 后天 / 昨天 / 周X
    static func relative(_ d: Date, today: Date) -> String {
        switch daysBetween(today, d) {
        case 0: return "今天"
        case 1: return "明天"
        case 2: return "后天"
        case -1: return "昨天"
        case -2: return "前天"
        default: return weekdayShort(d)
        }
    }

    /// 「明天 · 9月28日 周一」
    static func dayHeader(_ d: Date, today: Date) -> String {
        let diff = daysBetween(today, d)
        if (-2...2).contains(diff) {
            return "\(relative(d, today: today)) · \(monthDay(d, today: today)) \(weekdayShort(d))"
        }
        return "\(monthDay(d, today: today)) \(weekdayShort(d))"
    }

    /// 截止日期：今天 / 明天 15:00 / 周五 / 9月25日
    static func due(_ d: Date, hasTime: Bool, today: Date) -> String {
        let diff = daysBetween(today, d)
        let dayText: String
        if (-2...2).contains(diff) {
            dayText = relative(d, today: today)
        } else if diff > 2 && diff < 7 {
            dayText = weekdayShort(d)
        } else {
            dayText = monthDay(d, today: today)
        }
        return hasTime ? "\(dayText) \(time(d))" : dayText
    }

    /// 「10 分钟后」「2 小时后」「进行中」
    static func countdown(to d: Date, now: Date) -> String {
        let mins = Int((d.timeIntervalSince(now) / 60).rounded())
        if mins <= 0 { return "进行中" }
        if mins < 60 { return "\(mins) 分钟后" }
        let hours = mins / 60
        if hours < 6 { return mins % 60 == 0 ? "\(hours) 小时后" : "\(hours) 小时 \(mins % 60) 分后" }
        return time(d)
    }

    /// 一条内容的时间描述（列表里右侧/副标题显示）
    static func itemTime(_ item: AgendaItem, day: Date, now: Date, compact: Bool) -> String {
        let cal = calendar
        let today = cal.startOfDay(for: now)
        let d0 = cal.startOfDay(for: day)
        if item.isEvent {
            guard let s = item.start else { return "" }
            if item.isAllDay {
                let lastDay = cal.date(byAdding: .day, value: -1, to: cal.startOfDay(for: item.effectiveEnd)) ?? s
                if !compact && lastDay > cal.startOfDay(for: s) {
                    return "全天 · \(monthDay(s, today: today)) – \(monthDay(lastDay, today: today))"
                }
                return "全天"
            }
            let e = item.end ?? s
            let d1 = cal.date(byAdding: .day, value: 1, to: d0) ?? d0
            let startsBefore = s < d0
            let endsAfter = e > d1
            if startsBefore && endsAfter { return "全天" }
            if startsBefore { return "至 \(time(e))" }
            if e <= s { return time(s) }
            if endsAfter { return "\(time(s)) 起" }
            return compact ? time(s) : "\(time(s)) – \(time(e))"
        }
        guard let dueDate = item.start else { return "" }
        if cal.isDate(dueDate, inSameDayAs: d0) { return item.hasTime ? time(dueDate) : "" }
        return DateText.due(dueDate, hasTime: item.hasTime, today: today)
    }
}
