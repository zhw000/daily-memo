import SwiftUI

struct ListsView: View {
    @EnvironmentObject private var model: AppModel

    private var reminderLists: [SourceContainer] {
        model.containers.filter { $0.source == .ekReminder && model.settings.isVisible($0) }
    }

    private var googleLists: [SourceContainer] {
        model.containers.filter { $0.source == .googleTask && model.settings.isVisible($0) }
    }

    private func openCount(_ c: SourceContainer) -> Int {
        model.items.filter { $0.isTask && !$0.isCompleted && $0.containerKey == c.key }.count
    }

    var body: some View {
        NavigationStack {
            List {
                if reminderLists.isEmpty && googleLists.isEmpty {
                    Section {
                        Text("还没有待办清单。请在「设置」里允许访问提醒事项，或者登录 Google。")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
                if !reminderLists.isEmpty {
                    Section {
                        ForEach(reminderLists) { c in
                            NavigationLink(value: c.key) {
                                ListRowLabel(container: c, count: openCount(c),
                                             synced: model.settings.syncedReminderLists.contains(c.nativeID))
                            }
                        }
                    } header: {
                        Text("iPhone 提醒事项")
                    } footer: {
                        Text("带双向箭头的列表会和 Google Tasks 同步，Windows 版也能看到。")
                    }
                }
                if !googleLists.isEmpty {
                    Section("Google Tasks") {
                        ForEach(googleLists) { c in
                            NavigationLink(value: c.key) {
                                ListRowLabel(container: c, count: openCount(c), synced: true)
                            }
                        }
                    }
                }
            }
            .listStyle(.insetGrouped)
            .navigationTitle("清单")
            .navigationDestination(for: String.self) { key in
                ListDetailView(containerKey: key)
            }
            .refreshable { await model.refresh() }
        }
    }
}

struct ListRowLabel: View {
    let container: SourceContainer
    let count: Int
    let synced: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "list.bullet.circle.fill")
                .font(.title2)
                .foregroundStyle(Color(hex: container.colorHex))
            VStack(alignment: .leading, spacing: 2) {
                Text(container.name)
                if let account = container.accountName {
                    Text(account)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
            if synced {
                Image(systemName: "arrow.triangle.2.circlepath")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if count > 0 {
                Text("\(count)")
                    .foregroundStyle(.secondary)
            }
        }
    }
}

struct ListDetailView: View {
    @EnvironmentObject private var model: AppModel
    let containerKey: String
    @State private var quickText = ""

    private var container: SourceContainer? { model.container(for: containerKey) }

    private var tasks: [AgendaItem] {
        model.items.filter { $0.isTask && $0.containerKey == containerKey }
    }

    private var open: [AgendaItem] {
        tasks.filter { !$0.isCompleted }.sorted { a, b in
            switch (a.start, b.start) {
            case let (x?, y?): return x < y
            case (.some, nil): return true
            case (nil, .some): return false
            default: return a.title < b.title
            }
        }
    }

    private var done: [AgendaItem] {
        tasks.filter { $0.isCompleted }.sorted { ($0.completedAt ?? .distantPast) > ($1.completedAt ?? .distantPast) }
    }

    var body: some View {
        List {
            Section {
                HStack(spacing: 10) {
                    Image(systemName: "plus")
                        .foregroundStyle(Color.brand)
                    TextField("添加到「\(container?.name ?? "清单")」", text: $quickText)
                        .submitLabel(.done)
                        .onSubmit { add() }
                }
            }
            Section {
                if open.isEmpty {
                    Text("没有未完成的待办 ✓")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(open) { ItemRow(item: $0, day: model.now, showDate: true) }
                }
            }
            if !done.isEmpty {
                Section("最近完成") {
                    ForEach(done) { ItemRow(item: $0, day: model.now, showDate: true) }
                }
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle(container?.name ?? "清单")
        .refreshable { await model.refresh() }
    }

    private func add() {
        let text = quickText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        quickText = ""
        let parsed = QuickParser.parse(text, now: Date())
        var draft = EditorDraft(original: nil, kind: .task)
        draft.title = parsed.title
        draft.hasDate = parsed.date != nil
        draft.date = parsed.date ?? Date()
        if let when = parsed.when, parsed.minuteOfDay != nil {
            draft.hasTime = true
            draft.startTime = when
        }
        draft.targetKey = containerKey
        Task {
            do {
                try await model.save(draft)
            } catch {
                model.show(error)
            }
        }
    }
}
