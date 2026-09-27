import SwiftUI
import WidgetKit
import AppIntents

struct DayEntry: TimelineEntry {
    let date: Date
    let items: [AgendaItem]
    var showCompleted: Bool = false
    var showUndated: Bool = true

    var agenda: DayAgenda { AgendaBuilder.build(items, day: date, carryOver: true) }

    static func sample(now: Date = Date()) -> DayEntry {
        DayEntry(date: now, items: SampleData.items(now: now), showCompleted: true)
    }
}

/// 小组件里的一行：分组标题或条目
struct WidgetLine: Identifiable {
    enum Kind {
        case header(String, Bool)
        case item(AgendaItem)
    }

    let id: String
    let kind: Kind
}

extension DayEntry {
    /// 按重要程度排好的条目：已过期 → 全天 → 时间安排 → 今天要做 → 随时 → 已完成
    func orderedItems() -> [(section: String, warning: Bool, items: [AgendaItem])] {
        let a = agenda
        var groups: [(String, Bool, [AgendaItem])] = []
        if !a.overdue.isEmpty { groups.append(("已过期", true, a.overdue)) }
        let today = a.allDay + a.scheduled + a.dayTasks
        if !today.isEmpty { groups.append(("今天", false, today)) }
        if showUndated && !a.undated.isEmpty { groups.append(("随时", false, a.undated)) }
        if showCompleted && !a.completed.isEmpty { groups.append(("已完成", false, a.completed)) }
        return groups.map { (section: $0.0, warning: $0.1, items: $0.2) }
    }

    func flatItems(limit: Int) -> (items: [AgendaItem], hidden: Int) {
        let all = orderedItems().flatMap { $0.items }
        return (Array(all.prefix(limit)), max(0, all.count - limit))
    }

    func lines(limit: Int) -> (lines: [WidgetLine], hidden: Int) {
        var result: [WidgetLine] = []
        var shown = 0
        var total = 0
        for group in orderedItems() {
            total += group.items.count
            guard shown < limit else { continue }
            result.append(WidgetLine(id: "h-" + group.section, kind: .header(group.section, group.warning)))
            for item in group.items where shown < limit {
                result.append(WidgetLine(id: item.id, kind: .item(item)))
                shown += 1
            }
        }
        return (result, max(0, total - shown))
    }

    var openCount: Int { agenda.openTaskCount + agenda.overdue.count }
}

// MARK: - 按尺寸分发

struct WidgetContentView: View {
    let entry: DayEntry
    let family: WidgetFamily

    var body: some View {
        switch family {
        case .systemSmall:
            SmallWidgetView(entry: entry)
        case .systemMedium:
            MediumWidgetView(entry: entry)
        case .systemLarge, .systemExtraLarge:
            LargeWidgetView(entry: entry)
        case .accessoryRectangular:
            RectangularWidgetView(entry: entry)
        case .accessoryInline:
            InlineWidgetView(entry: entry)
        case .accessoryCircular:
            CircularWidgetView(entry: entry)
        @unknown default:
            MediumWidgetView(entry: entry)
        }
    }
}

struct WidgetBackground: View {
    let family: WidgetFamily

    var body: some View {
        switch family {
        case .accessoryCircular, .accessoryRectangular, .accessoryInline:
            Color.clear
        default:
            Color(uiColor: .systemBackground)
        }
    }
}

// MARK: - 条目行

struct WidgetItemRow: View {
    let item: AgendaItem
    let now: Date
    var fontSize: CGFloat = 13

    private var isOverdue: Bool {
        guard item.isTask, !item.isCompleted, let due = item.start else { return false }
        return item.hasTime ? due < now : DateText.calendar.startOfDay(for: due) < DateText.calendar.startOfDay(for: now)
    }

    private var isPast: Bool { item.isEvent && !item.isAllDay && item.effectiveEnd <= now }

    var body: some View {
        HStack(spacing: 7) {
            if item.isTask {
                Button(intent: ToggleItemIntent(itemID: item.id, completed: !item.isCompleted)) {
                    Image(systemName: item.isCompleted ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: fontSize + 2, weight: .regular))
                        .foregroundStyle(Color(hex: item.colorHex))
                }
                .buttonStyle(.plain)
            } else {
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(Color(hex: item.colorHex))
                    .frame(width: 3.5, height: fontSize + 3)
                    .padding(.horizontal, (fontSize + 2 - 3.5) / 2)
            }
            Text(item.title)
                .font(.system(size: fontSize))
                .lineLimit(1)
                .strikethrough(item.isCompleted)
                .foregroundStyle(item.isCompleted || isPast ? Color.secondary : Color.primary)
            Spacer(minLength: 4)
            Text(isOverdue && !item.hasTime ? DateText.due(item.start ?? now, hasTime: false, today: now)
                 : DateText.itemTime(item, day: now, now: now, compact: true))
                .font(.system(size: fontSize - 2, weight: .medium).monospacedDigit())
                .foregroundStyle(isOverdue ? Color.overdue : Color.secondary)
                .lineLimit(1)
        }
        .opacity(isPast ? 0.55 : 1)
    }
}

// MARK: - 小

struct SmallWidgetView: View {
    let entry: DayEntry

    var body: some View {
        let list = entry.flatItems(limit: 3)
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 6) {
                Text(DateText.day(entry.date))
                    .font(.system(size: 32, weight: .bold, design: .rounded))
                    .foregroundStyle(Color.brand)
                VStack(alignment: .leading, spacing: 0) {
                    Text(DateText.weekdayShort(entry.date))
                        .font(.system(size: 13, weight: .semibold))
                    Text(entry.openCount > 0 ? "\(entry.openCount) 项待办" : DateText.month(entry.date))
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            if list.items.isEmpty {
                Spacer(minLength: 0)
                Label("今天没有安排", systemImage: "sun.max")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
            } else {
                VStack(alignment: .leading, spacing: 5) {
                    ForEach(list.items) { item in
                        WidgetItemRow(item: item, now: entry.date, fontSize: 12)
                    }
                }
                Spacer(minLength: 0)
                if list.hidden > 0 {
                    Text("还有 \(list.hidden) 项")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
            }
        }
    }
}

// MARK: - 中

struct MediumWidgetView: View {
    let entry: DayEntry

    var body: some View {
        let agenda = entry.agenda
        let list = entry.flatItems(limit: 4)
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 2) {
                Text(DateText.weekday(entry.date))
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Color.brand)
                Text(DateText.day(entry.date))
                    .font(.system(size: 40, weight: .bold, design: .rounded))
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                Text(DateText.month(entry.date))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if agenda.eventCount > 0 {
                    Text("\(agenda.eventCount) 个日程").font(.system(size: 11)).foregroundStyle(.secondary)
                }
                if entry.openCount > 0 {
                    Text("\(entry.openCount) 项待办").font(.system(size: 11)).foregroundStyle(.secondary)
                }
                ProgressBar(progress: agenda.progress)
                    .frame(height: 4)
                    .padding(.top, 3)
            }
            .frame(width: 72, alignment: .leading)

            VStack(alignment: .leading, spacing: 7) {
                if list.items.isEmpty {
                    Spacer(minLength: 0)
                    Label("今天没有安排，放松一下", systemImage: "sun.max")
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                } else {
                    ForEach(list.items) { item in
                        WidgetItemRow(item: item, now: entry.date)
                    }
                    Spacer(minLength: 0)
                    if list.hidden > 0 {
                        Text("还有 \(list.hidden) 项 · 打开查看")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
    }
}

// MARK: - 大

struct LargeWidgetView: View {
    let entry: DayEntry

    var body: some View {
        let agenda = entry.agenda
        let content = entry.lines(limit: 10)
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 10) {
                Text(DateText.day(entry.date))
                    .font(.system(size: 38, weight: .bold, design: .rounded))
                    .foregroundStyle(Color.brand)
                VStack(alignment: .leading, spacing: 1) {
                    Text("\(DateText.month(entry.date)) · \(DateText.weekday(entry.date))")
                        .font(.system(size: 15, weight: .semibold))
                    Text(agenda.summary)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
                ProgressRing(progress: agenda.progress)
                    .frame(width: 30, height: 30)
            }
            .padding(.bottom, 2)

            if content.lines.isEmpty {
                Spacer()
                VStack(spacing: 6) {
                    Image(systemName: "sun.max").font(.system(size: 30)).foregroundStyle(Color.brand)
                    Text("今天没有安排").font(.system(size: 14)).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
                Spacer()
            } else {
                ForEach(content.lines) { line in
                    switch line.kind {
                    case .header(let title, let warning):
                        Text(title)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(warning ? Color.overdue : Color.secondary)
                            .padding(.top, 3)
                    case .item(let item):
                        WidgetItemRow(item: item, now: entry.date, fontSize: 14)
                    }
                }
                Spacer(minLength: 0)
                if content.hidden > 0 {
                    Text("还有 \(content.hidden) 项 · 打开今日事查看")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
        }
    }
}

// MARK: - 锁屏

struct RectangularWidgetView: View {
    let entry: DayEntry

    var body: some View {
        let list = entry.flatItems(limit: 2)
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 4) {
                Image(systemName: "checklist")
                Text(entry.openCount > 0 ? "今天 \(entry.openCount) 项待办" : "今天没有待办")
            }
            .font(.system(size: 13, weight: .semibold))
            ForEach(list.items) { item in
                Text(Self.line(item, now: entry.date))
                    .font(.system(size: 12))
                    .lineLimit(1)
            }
            if list.items.isEmpty {
                Text(entry.agenda.eventCount > 0 ? "\(entry.agenda.eventCount) 个日程" : "好好休息")
                    .font(.system(size: 12))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    static func line(_ item: AgendaItem, now: Date) -> String {
        let t = DateText.itemTime(item, day: now, now: now, compact: true)
        let mark = item.isTask ? (item.isCompleted ? "✓ " : "○ ") : "▍"
        return t.isEmpty ? mark + item.title : "\(mark)\(t) \(item.title)"
    }
}

struct InlineWidgetView: View {
    let entry: DayEntry

    var body: some View {
        let agenda = entry.agenda
        if let next = agenda.nextUp(now: entry.date), let start = next.start {
            Text("\(entry.openCount) 项待办 · \(DateText.time(start)) \(next.title)")
        } else {
            Text(entry.openCount > 0 ? "今天还有 \(entry.openCount) 项待办" : "今天没有待办")
        }
    }
}

struct CircularWidgetView: View {
    let entry: DayEntry

    var body: some View {
        Gauge(value: entry.agenda.progress) {
            Text("待办")
        } currentValueLabel: {
            Text("\(entry.openCount)")
        }
        .gaugeStyle(.accessoryCircularCapacity)
    }
}

// MARK: - 小部件

struct ProgressBar: View {
    let progress: Double

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.secondary.opacity(0.2))
                Capsule().fill(Color.done)
                    .frame(width: max(0, min(1, progress)) * geo.size.width)
            }
        }
    }
}

struct ProgressRing: View {
    let progress: Double

    var body: some View {
        ZStack {
            Circle().stroke(Color.secondary.opacity(0.2), lineWidth: 4)
            Circle()
                .trim(from: 0, to: max(0, min(1, progress)))
                .stroke(Color.done, style: StrokeStyle(lineWidth: 4, lineCap: .round))
                .rotationEffect(.degrees(-90))
            Text("\(Int((progress * 100).rounded()))")
                .font(.system(size: 9, weight: .semibold, design: .rounded))
                .foregroundStyle(.secondary)
        }
    }
}
