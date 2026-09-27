import WidgetKit
import SwiftUI

struct AgendaProvider: TimelineProvider {
    func placeholder(in context: Context) -> DayEntry {
        DayEntry.sample()
    }

    func getSnapshot(in context: Context, completion: @escaping (DayEntry) -> Void) {
        if context.isPreview {
            completion(DayEntry.sample())
            return
        }
        Task {
            let entries = await makeEntries(now: Date())
            completion(entries.first ?? DayEntry.sample())
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<DayEntry>) -> Void) {
        Task {
            let now = Date()
            let entries = await makeEntries(now: now)
            let cal = DateText.calendar
            let midnight = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: now)) ?? now.addingTimeInterval(3600)
            // 至少每 30 分钟重新读一次数据（在别处改过的内容也能跟上）
            let refresh = min(midnight, now.addingTimeInterval(30 * 60))
            completion(Timeline(entries: entries, policy: .after(refresh)))
        }
    }

    /// 在每个日程开始/结束的时刻各生成一个条目，让「已结束变灰」「下一项」按时更新
    private func makeEntries(now: Date) async -> [DayEntry] {
        let items = await WidgetData.load(now: now)
        let settings = SharedStore.settings
        let cal = DateText.calendar
        let midnight = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: now)) ?? now

        var dates: [Date] = [now, midnight]
        for item in items where item.start != nil && !item.isAllDay {
            for d in [item.start, item.end].compactMap({ $0 }) where d > now && d < midnight {
                dates.append(d)
            }
        }
        let unique = Array(Set(dates)).sorted().prefix(40)
        return unique.map { date in
            DayEntry(date: date, items: items, showCompleted: settings.widgetShowCompleted, showUndated: settings.showUndated)
        }
    }
}

struct DailyMemoWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: DayEntry

    var body: some View {
        WidgetContentView(entry: entry, family: family)
            .containerBackground(for: .widget) {
                WidgetBackground(family: family)
            }
            .widgetURL(URL(string: "dailymemo://today"))
    }
}

struct DailyMemoWidget: Widget {
    let kind = "DailyMemoWidget"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: AgendaProvider()) { entry in
            DailyMemoWidgetView(entry: entry)
        }
        .configurationDisplayName("今日事")
        .description("今天的日程和待办，待办可以直接打勾。")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge, .accessoryRectangular, .accessoryInline, .accessoryCircular])
    }
}

@main
struct DailyMemoWidgetBundle: WidgetBundle {
    var body: some Widget {
        DailyMemoWidget()
    }
}
