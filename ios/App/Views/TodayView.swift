import SwiftUI

struct TodayView: View {
    @EnvironmentObject private var model: AppModel
    @State private var quickText = ""
    @FocusState private var quickFocused: Bool

    var body: some View {
        NavigationStack {
            let agenda = model.today
            List {
                Section {
                    TodayHeader(agenda: agenda, now: model.now)
                }
                .listRowInsets(EdgeInsets(top: 4, leading: 4, bottom: 4, trailing: 4))
                .listRowBackground(Color.clear)

                Section {
                    quickAddRow
                }

                if !model.errors.isEmpty {
                    Section {
                        ForEach(model.errors, id: \.self) { err in
                            Label(err, systemImage: "exclamationmark.triangle.fill")
                                .font(.footnote)
                                .foregroundStyle(Color.overdue)
                        }
                    }
                }

                if agenda.isEmpty {
                    Section {
                        EmptyDayView()
                    }
                    .listRowBackground(Color.clear)
                }

                if !agenda.overdue.isEmpty {
                    Section {
                        ForEach(agenda.overdue) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "已过期", count: agenda.overdue.count, warning: true)
                    }
                }
                if !agenda.allDay.isEmpty {
                    Section {
                        ForEach(agenda.allDay) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "全天", count: agenda.allDay.count)
                    }
                }
                if !agenda.scheduled.isEmpty {
                    Section {
                        ForEach(agenda.scheduled) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "时间安排", count: agenda.scheduled.count)
                    }
                }
                if !agenda.dayTasks.isEmpty {
                    Section {
                        ForEach(agenda.dayTasks) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "今天要做", count: agenda.dayTasks.count)
                    }
                }
                if model.settings.showUndated && !agenda.undated.isEmpty {
                    Section {
                        ForEach(agenda.undated) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "随时可做", count: agenda.undated.count)
                    }
                }
                if !agenda.completed.isEmpty {
                    Section {
                        ForEach(agenda.completed) { ItemRow(item: $0, day: model.now) }
                    } header: {
                        SectionTitle(title: "今天已完成", count: agenda.completed.count)
                    }
                }
            }
            .listStyle(.insetGrouped)
            .refreshable { await model.refresh() }
            .navigationTitle("今天")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    if model.isLoading { ProgressView() }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button {
                            model.editorDraft = model.newDraft(kind: .task)
                        } label: {
                            Label("新建待办", systemImage: "checklist")
                        }
                        Button {
                            model.editorDraft = model.newDraft(kind: .event)
                        } label: {
                            Label("新建日程", systemImage: "calendar.badge.plus")
                        }
                    } label: {
                        Image(systemName: "plus.circle.fill")
                            .font(.title3)
                    }
                }
            }
        }
    }

    private var quickAddRow: some View {
        let parsed = quickText.isEmpty ? nil : QuickParser.parse(quickText, now: Date())
        return HStack(spacing: 10) {
            Image(systemName: "plus")
                .foregroundStyle(Color.brand)
                .fontWeight(.semibold)
            TextField("记一件事，例如「明天下午3点 交报告」", text: $quickText)
                .focused($quickFocused)
                .submitLabel(.done)
                .onSubmit { submit() }
            if let p = parsed, p.date != nil {
                Text(p.describe(today: Date()))
                    .font(.caption)
                    .foregroundStyle(Color.brand)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Color.brand.opacity(0.12), in: Capsule())
                    .lineLimit(1)
                    .fixedSize()
            }
        }
    }

    private func submit() {
        let text = quickText
        quickText = ""
        Task { await model.quickAdd(text) }
    }
}

struct TodayHeader: View {
    let agenda: DayAgenda
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text(DateText.monthDay(now))
                    .font(.system(size: 30, weight: .bold, design: .rounded))
                Text(DateText.weekday(now))
                    .font(.title3)
                    .foregroundStyle(.secondary)
                Spacer()
            }
            Text(agenda.isEmpty ? "今天没有安排，给自己放个假吧" : agenda.summary + (agenda.overdue.isEmpty ? "" : " · 其中 \(agenda.overdue.count) 项已过期"))
                .font(.subheadline)
                .foregroundStyle(.secondary)
            HStack(spacing: 8) {
                if let next = agenda.nextUp(now: now), let start = next.start {
                    Label("\(DateText.countdown(to: start, now: now)) · \(next.title)", systemImage: "clock")
                        .font(.footnote.weight(.medium))
                        .foregroundStyle(Color.brand)
                        .lineLimit(1)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(Color.brand.opacity(0.12), in: Capsule())
                }
                let total = agenda.openTaskCount + agenda.overdue.count + agenda.completed.count
                if total > 0 {
                    HStack(spacing: 6) {
                        ProgressBar(progress: agenda.progress)
                            .frame(width: 44, height: 5)
                        Text("\(agenda.completed.count)/\(total)")
                            .font(.footnote.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Color.secondary.opacity(0.1), in: Capsule())
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }
}

struct EmptyDayView: View {
    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "sun.max.fill")
                .font(.system(size: 44))
                .foregroundStyle(Color.orange.gradient)
            Text("今天没有安排")
                .font(.headline)
            Text("在上面记一件事；或者去「设置」允许访问日历、提醒事项，并连接 Google 与电脑同步。")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 24)
    }
}
