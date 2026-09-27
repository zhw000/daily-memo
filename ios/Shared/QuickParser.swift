import Foundation

struct QuickParseResult: Equatable {
    var title: String
    /// 当天 0 点
    var date: Date?
    /// 一天中的第几分钟（15:30 → 930）
    var minuteOfDay: Int?

    var when: Date? {
        guard let d = date else { return nil }
        return d.addingTimeInterval(TimeInterval((minuteOfDay ?? 0) * 60))
    }

    func describe(today: Date) -> String {
        guard let d = date else { return "" }
        var text = DateText.due(d, hasTime: false, today: today)
        let diff = DateText.daysBetween(today, d)
        if diff > 2 && diff < 7 { text += "（\(DateText.monthDay(d, today: today))）" }
        if let m = minuteOfDay { text += String(format: " %02d:%02d", m / 60, m % 60) }
        return text
    }
}

/// 从一句中文里识别日期和时间：「明天下午3点 交报告」「周五 买菜」「10月1日 回家」「半小时后 关火」。
/// 和 Windows 版的规则保持一致。
enum QuickParser {
    private static let cnNum = "[一二两三四五六七八九十]{1,3}"
    private static let anyNum = "\\d{1,2}|" + cnNum
    private static let period = "凌晨|早上|早晨|清晨|上午|中午|下午|傍晚|晚上|夜里|夜间|晚"
    private static let minuteTail = "半|一刻|三刻|(?<min>\\d{1,2}|[一二三四五六七八九十]{1,3})\\s*分"

    static func parse(_ input: String, now: Date) -> QuickParseResult {
        let cal = DateText.calendar
        let today = cal.startOfDay(for: now)
        var text = " " + input + " "
        var date: Date? = nil
        var minute: Int? = nil
        var dayPeriod: String? = nil

        text = replaceAll("^\\s*请?(提醒我|提醒|记得|别忘了|不要忘了)\\s*", in: text, with: " ")

        // 相对时间
        if let m = match("(半|\(anyNum))\\s*个?\\s*(小时|钟头)\\s*以?后", text) {
            let hours: Double = m.group(1) == "半" ? 0.5 : Double(cnNumber(m.group(1) ?? "") ?? 0)
            let t = now.addingTimeInterval(hours * 3600)
            date = cal.startOfDay(for: t)
            minute = minuteOfDay(t)
            text = cut(text, m)
        } else if let m = match("(\\d{1,3}|\(cnNum))\\s*分钟\\s*以?后", text) {
            let t = now.addingTimeInterval(Double(cnNumber(m.group(1) ?? "") ?? 0) * 60)
            date = cal.startOfDay(for: t)
            minute = minuteOfDay(t)
            text = cut(text, m)
        } else if let m = match("(\(anyNum))\\s*天\\s*以?后", text) {
            date = cal.date(byAdding: .day, value: cnNumber(m.group(1) ?? "") ?? 0, to: today)
            text = cut(text, m)
        }

        // 2026-10-01 / 2026年10月1日
        if date == nil, let m = match("(\\d{4})\\s*[-/.年]\\s*(\\d{1,2})\\s*[-/.月]\\s*(\\d{1,2})\\s*[日号]?", text),
           let d = makeDate(Int(m.group(1) ?? "") ?? 0, Int(m.group(2) ?? "") ?? 0, Int(m.group(3) ?? "") ?? 0) {
            date = d
            text = cut(text, m)
        }

        // 10月1日 / 十月一号
        if date == nil, let m = match("(\(anyNum))\\s*月\\s*(\(anyNum))\\s*[日号]?", text),
           let mo = cnNumber(m.group(1) ?? ""), let da = cnNumber(m.group(2) ?? ""),
           var d = makeDate(cal.component(.year, from: today), mo, da) {
            if d < today, let next = makeDate(cal.component(.year, from: today) + 1, mo, da) { d = next }
            date = d
            text = cut(text, m)
        }

        // 10/1
        if date == nil, let m = match("(?<![\\d:：/])(\\d{1,2})/(\\d{1,2})(?![\\d/])", text),
           let mo = Int(m.group(1) ?? ""), let da = Int(m.group(2) ?? ""),
           var d = makeDate(cal.component(.year, from: today), mo, da) {
            if d < today, let next = makeDate(cal.component(.year, from: today) + 1, mo, da) { d = next }
            date = d
            text = cut(text, m)
        }

        // 周五 / 下周三 / 这个星期天
        if date == nil, let m = match("(下下个?|下个?|这个?|本)?\\s*(周|星期|礼拜)\\s*([一二三四五六日天1-7])", text) {
            date = weekdayDate(today: today, prefix: m.group(1) ?? "", word: m.group(3) ?? "")
            text = cut(text, m)
        }

        // 今天 / 明天 / 后天 / 今晚 …
        if date == nil, let m = match("大后天|后天|明天|明日|明儿|明早|明晚|今天|今日|今儿|今早|今晚|今夜", text) {
            let w = m.value
            let offset: Int
            switch w {
            case "大后天": offset = 3
            case "后天": offset = 2
            case "明天", "明日", "明儿", "明早", "明晚": offset = 1
            default: offset = 0
            }
            date = cal.date(byAdding: .day, value: offset, to: today)
            if w.hasSuffix("早") { dayPeriod = "早上" }
            if w.hasSuffix("晚") || w.hasSuffix("夜") { dayPeriod = "晚上" }
            text = cut(text, m)
        }

        // 本月 N 号（后面必须是空白或时间词，避免把「10号楼」当成日期）
        if date == nil, let m = match("(?<=^|[\\s，,])(\(anyNum))\\s*[号日](?=\\s|\(period)|\\d)", text),
           let da = cnNumber(m.group(1) ?? "") {
            let y = cal.component(.year, from: today), mo = cal.component(.month, from: today)
            if var d = makeDate(y, mo, da) {
                if d < today {
                    let next = cal.date(byAdding: .month, value: 1, to: today) ?? today
                    d = makeDate(cal.component(.year, from: next), cal.component(.month, from: next), da) ?? today
                }
                date = d
                text = cut(text, m)
            }
        }

        // 15:30 / 下午3:30
        if minute == nil, let m = match("(\(period))?\\s*(\\d{1,2})\\s*[:：]\\s*(\\d{2})(?!\\d)", text),
           let t = makeTime(Int(m.group(2) ?? ""), Int(m.group(3) ?? "") ?? 0, m.group(1), dayPeriod) {
            minute = t
            text = cut(text, m)
        }

        // 下午3点 / 3点半 / 10点20分
        if minute == nil, let m = match("(\(period))?\\s*(\\d{1,2})\\s*[点點时]钟?\\s*(\(minuteTail))?", text),
           let t = makeTime(Int(m.group(2) ?? ""), minuteOf(m), m.group(1), dayPeriod) {
            minute = t
            text = cut(text, m)
        }

        // 九点一刻 / 晚上八点：中文数字容易误判（「买一点东西」），要求有时段词、日期或分钟
        if minute == nil, let m = match("(\(period))?\\s*(\(cnNum))\\s*[点點]钟?\\s*(\(minuteTail))?", text),
           m.group(1) != nil || m.group(3) != nil || date != nil || m.value.contains("钟"),
           let t = makeTime(cnNumber(m.group(2) ?? ""), minuteOf(m), m.group(1), dayPeriod) {
            minute = t
            text = cut(text, m)
        }

        // 单独的「晚上」「上午」只从标题里去掉
        if minute == nil, date != nil, let m = match("(?<=^|\\s)(\(period))(?=\\s)", text) {
            text = cut(text, m)
        }

        if let t = minute, date == nil {
            date = today
            if today.addingTimeInterval(TimeInterval(t * 60)) < now.addingTimeInterval(-60) {
                date = cal.date(byAdding: .day, value: 1, to: today)
            }
        }

        var title = replaceAll("\\s+", in: text, with: " ")
        title = title.trimmingCharacters(in: CharacterSet(charactersIn: " ,，。.、:：\n\t"))
        if title.isEmpty { title = input.trimmingCharacters(in: .whitespacesAndNewlines) }
        return QuickParseResult(title: title, date: date, minuteOfDay: minute)
    }

    // MARK: - 工具

    private struct Match {
        let result: NSTextCheckingResult
        let source: NSString

        func group(_ i: Int) -> String? {
            guard i < result.numberOfRanges else { return nil }
            let r = result.range(at: i)
            guard r.location != NSNotFound else { return nil }
            return source.substring(with: r)
        }

        func named(_ name: String) -> String? {
            let r = result.range(withName: name)
            guard r.location != NSNotFound else { return nil }
            return source.substring(with: r)
        }

        var value: String { source.substring(with: result.range) }
    }

    private static func match(_ pattern: String, _ text: String) -> Match? {
        guard let re = try? NSRegularExpression(pattern: pattern, options: []) else { return nil }
        let ns = text as NSString
        guard let r = re.firstMatch(in: text, options: [], range: NSRange(location: 0, length: ns.length)) else { return nil }
        return Match(result: r, source: ns)
    }

    private static func replaceAll(_ pattern: String, in text: String, with template: String) -> String {
        guard let re = try? NSRegularExpression(pattern: pattern, options: []) else { return text }
        let ns = text as NSString
        return re.stringByReplacingMatches(in: text, options: [], range: NSRange(location: 0, length: ns.length), withTemplate: template)
    }

    private static func cut(_ text: String, _ m: Match) -> String {
        (text as NSString).replacingCharacters(in: m.result.range, with: " ")
    }

    private static func minuteOf(_ m: Match) -> Int {
        let tail = (m.group(3) ?? "").trimmingCharacters(in: .whitespaces)
        if tail == "半" { return 30 }
        if tail == "一刻" { return 15 }
        if tail == "三刻" { return 45 }
        if let v = m.named("min") { return cnNumber(v) ?? 0 }
        return 0
    }

    private static func minuteOfDay(_ d: Date) -> Int {
        let c = DateText.calendar.dateComponents([.hour, .minute], from: d)
        return (c.hour ?? 0) * 60 + (c.minute ?? 0)
    }

    private static func makeTime(_ hour: Int?, _ minute: Int, _ periodInText: String?, _ periodFromDay: String?) -> Int? {
        guard var h = hour, (0...59).contains(minute) else { return nil }
        let p: String = (periodInText?.isEmpty == false ? periodInText : periodFromDay) ?? ""
        switch p {
        case "下午", "傍晚", "晚上", "夜里", "夜间", "晚":
            if h < 12 { h += 12 }
        case "中午":
            if h < 11 { h += 12 }
        case "凌晨":
            if h == 12 { h = 0 }
        default:
            break
        }
        guard (0...23).contains(h) else { return nil }
        return h * 60 + minute
    }

    private static func weekdayDate(today: Date, prefix: String, word: String) -> Date {
        let cal = DateText.calendar
        let target: Int
        switch word {
        case "一", "1": target = 0
        case "二", "2": target = 1
        case "三", "3": target = 2
        case "四", "4": target = 3
        case "五", "5": target = 4
        case "六", "6": target = 5
        default: target = 6
        }
        let todayIdx = (cal.component(.weekday, from: today) + 5) % 7
        let monday = cal.date(byAdding: .day, value: -todayIdx, to: today) ?? today
        func plus(_ n: Int) -> Date { cal.date(byAdding: .day, value: n, to: monday) ?? today }
        if prefix.hasPrefix("下下") { return plus(14 + target) }
        if prefix.hasPrefix("下") { return plus(7 + target) }
        if prefix.hasPrefix("这") || prefix == "本" { return plus(target) }
        var diff = target - todayIdx
        if diff < 0 { diff += 7 }
        return cal.date(byAdding: .day, value: diff, to: today) ?? today
    }

    private static func makeDate(_ y: Int, _ m: Int, _ d: Int) -> Date? {
        guard y > 0, (1...12).contains(m), d >= 1 else { return nil }
        let cal = DateText.calendar
        guard let first = cal.date(from: DateComponents(year: y, month: m, day: 1)),
              let days = cal.range(of: .day, in: .month, for: first), d <= days.count else { return nil }
        return cal.date(from: DateComponents(year: y, month: m, day: d))
    }

    /// 阿拉伯数字或「十二」「二十三」「两」这样的中文数字
    static func cnNumber(_ raw: String) -> Int? {
        let s = raw.trimmingCharacters(in: .whitespaces)
        if let n = Int(s) { return n }
        if s.isEmpty { return nil }
        func digit(_ c: Character) -> Int? {
            switch c {
            case "零": return 0
            case "一": return 1
            case "二", "两": return 2
            case "三": return 3
            case "四": return 4
            case "五": return 5
            case "六": return 6
            case "七": return 7
            case "八": return 8
            case "九": return 9
            default: return nil
            }
        }
        let chars = Array(s)
        guard let tenIdx = chars.firstIndex(of: "十") else {
            return chars.count == 1 ? digit(chars[0]) : nil
        }
        if tenIdx > 1 || chars.count > tenIdx + 2 { return nil }
        let tens: Int? = tenIdx == 0 ? 1 : digit(chars[0])
        let ones: Int? = tenIdx == chars.count - 1 ? 0 : digit(chars[tenIdx + 1])
        guard let t = tens, let o = ones else { return nil }
        return t * 10 + o
    }
}
