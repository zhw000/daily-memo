import SwiftUI

struct EditorView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: EditorDraft
    @State private var saving = false
    @State private var errorText: String?
    @State private var confirmDelete = false

    init(draft: EditorDraft) {
        _draft = State(initialValue: draft)
    }

    private var targets: [SourceContainer] {
        var list = draft.kind == .task ? model.taskTargets : model.eventTargets
        if let o = draft.original, o.kind == draft.kind, !list.contains(where: { $0.key == o.containerKey }),
           let c = model.container(for: o.containerKey) {
            list.append(c)
        }
        return list
    }

    private var isReadOnly: Bool { draft.original?.isReadOnly ?? false }

    private var parseSuggestion: QuickParseResult? {
        guard draft.isNew else { return nil }
        let r = QuickParser.parse(draft.title, now: Date())
        return r.date != nil && r.title != draft.title ? r : nil
    }

    private var hint: String? {
        guard let o = draft.original else {
            return draft.kind == .task ? "小技巧：标题里写「明天下午3点」「周五」会自动识别日期。" : nil
        }
        if o.isReadOnly { return "这个日历是只读的，不能修改。" }
        if o.isRecurring && o.isEvent { return "这是重复日程中的一次，修改和删除只影响这一次。" }
        if o.source == .googleTask && o.hasTime { return "Google Tasks 只保存日期，时间会写在标题开头（电脑上同样显示）。" }
        return nil
    }

    var body: some View {
        NavigationStack {
            Form {
                if draft.isNew {
                    Section {
                        Picker("类型", selection: $draft.kind) {
                            Text("待办").tag(ItemKind.task)
                            Text("日程").tag(ItemKind.event)
                        }
                        .pickerStyle(.segmented)
                    }
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
                }

                Section {
                    TextField(draft.kind == .task ? "要做什么？" : "日程标题", text: $draft.title, axis: .vertical)
                        .font(.title3)
                        .disabled(isReadOnly)
                    if let s = parseSuggestion {
                        Button {
                            apply(s)
                        } label: {
                            Label("识别到「\(s.describe(today: Date()))」，点这里应用", systemImage: "sparkles")
                                .font(.footnote)
                        }
                    }
                }

                Section {
                    if draft.kind == .task {
                        Toggle("日期", isOn: $draft.hasDate.animation())
                        if draft.hasDate {
                            DatePicker("日期", selection: $draft.date, displayedComponents: .date)
                            Toggle("具体时间", isOn: $draft.hasTime.animation())
                            if draft.hasTime {
                                DatePicker("时间", selection: $draft.startTime, displayedComponents: .hourAndMinute)
                            }
                        }
                    } else {
                        Toggle("全天", isOn: Binding(get: { !draft.hasTime }, set: { draft.hasTime = !$0 }).animation())
                        DatePicker("日期", selection: $draft.date, displayedComponents: .date)
                        if draft.hasTime {
                            DatePicker("开始", selection: $draft.startTime, displayedComponents: .hourAndMinute)
                            DatePicker("结束", selection: $draft.endTime, displayedComponents: .hourAndMinute)
                        }
                    }
                }
                .disabled(isReadOnly)

                Section {
                    if targets.isEmpty {
                        Text("没有可保存的位置：请先允许访问日历/提醒事项，或登录 Google。")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    } else {
                        Picker("保存到", selection: $draft.targetKey) {
                            ForEach(targets) { c in
                                Text(c.displayName).tag(c.key)
                            }
                        }
                    }
                }
                .disabled(isReadOnly)

                if draft.kind == .event {
                    Section {
                        TextField("地点", text: $draft.location)
                    }
                    .disabled(isReadOnly)
                }

                Section {
                    TextField("备注", text: $draft.notes, axis: .vertical)
                        .lineLimit(3...8)
                }
                .disabled(isReadOnly)

                if let hint = hint {
                    Section {
                        Label(hint, systemImage: "info.circle")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }

                if let err = errorText {
                    Section {
                        Text(err)
                            .font(.footnote)
                            .foregroundStyle(Color.overdue)
                    }
                }

                if !draft.isNew && !isReadOnly {
                    Section {
                        Button(role: .destructive) {
                            confirmDelete = true
                        } label: {
                            Label("删除", systemImage: "trash")
                        }
                    }
                }
            }
            .navigationTitle(draft.isNew ? (draft.kind == .task ? "新建待办" : "新建日程") : (draft.kind == .task ? "编辑待办" : "编辑日程"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("取消") { close() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    if saving {
                        ProgressView()
                    } else {
                        Button("保存") { Task { await save() } }
                            .fontWeight(.semibold)
                            .disabled(isReadOnly || draft.title.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                }
            }
            .confirmationDialog("确定删除吗？", isPresented: $confirmDelete, titleVisibility: .visible) {
                Button("删除", role: .destructive) {
                    if let o = draft.original {
                        Task { await model.delete(o) }
                    }
                    close()
                }
            }
            .onChange(of: draft.kind) { _, newKind in
                draft.hasTime = newKind == .event
                draft.targetKey = (newKind == .task ? model.resolveTaskTarget() : model.resolveEventTarget()) ?? ""
            }
        }
    }

    private func apply(_ r: QuickParseResult) {
        draft.title = r.title
        if let d = r.date {
            draft.hasDate = true
            draft.date = d
        }
        if let when = r.when, r.minuteOfDay != nil {
            draft.hasTime = true
            draft.startTime = when
            draft.endTime = when.addingTimeInterval(3600)
        }
    }

    private func save() async {
        saving = true
        errorText = nil
        do {
            try await model.save(draft)
            close()
        } catch {
            errorText = AgendaLoader.describe(error)
        }
        saving = false
    }

    private func close() {
        model.editorDraft = nil
        dismiss()
    }
}
