import SwiftUI

struct UpcomingView: View {
    @EnvironmentObject private var model: AppModel

    private var days: [Date] {
        let cal = DateText.calendar
        let today = cal.startOfDay(for: model.now)
        return (1...13).compactMap { cal.date(byAdding: .day, value: $0, to: today) }
    }

    var body: some View {
        NavigationStack {
            List {
                ForEach(days, id: \.self) { day in
                    let agenda = model.agenda(for: day)
                    let rows = agenda.allDay + agenda.scheduled + agenda.dayTasks
                    Section {
                        if rows.isEmpty {
                            Text("空闲")
                                .font(.footnote)
                                .foregroundStyle(.tertiary)
                        } else {
                            ForEach(rows) { ItemRow(item: $0, day: day) }
                        }
                    } header: {
                        DayHeader(day: day, today: model.now, events: agenda.eventCount, tasks: agenda.openTaskCount)
                    }
                }
            }
            .listStyle(.insetGrouped)
            .refreshable { await model.refresh() }
            .navigationTitle("未来两周")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        let cal = DateText.calendar
                        let tomorrow = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: model.now))
                        model.editorDraft = model.newDraft(kind: .event, date: tomorrow)
                    } label: {
                        Image(systemName: "calendar.badge.plus")
                    }
                }
            }
        }
    }
}

struct DayHeader: View {
    let day: Date
    let today: Date
    let events: Int
    let tasks: Int

    private var isWeekend: Bool {
        let wd = DateText.calendar.component(.weekday, from: day)
        return wd == 1 || wd == 7
    }

    var body: some View {
        HStack {
            Text(DateText.dayHeader(day, today: today))
                .foregroundStyle(isWeekend ? Color.brand : Color.primary)
            Spacer()
            if events + tasks > 0 {
                Text([events > 0 ? "\(events) 日程" : nil, tasks > 0 ? "\(tasks) 待办" : nil].compactMap { $0 }.joined(separator: " · "))
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
        }
        .font(.subheadline.weight(.semibold))
        .textCase(nil)
    }
}
