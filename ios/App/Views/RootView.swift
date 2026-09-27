import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var showOnboarding = false
    @State private var showGallery = false

    private var editorPresented: Binding<Bool> {
        Binding(get: { model.editorDraft != nil }, set: { if !$0 { model.editorDraft = nil } })
    }

    var body: some View {
        TabView(selection: $model.selectedTab) {
            TodayView()
                .tabItem { Label("今天", systemImage: "sun.max") }
                .tag("today")
            UpcomingView()
                .tabItem { Label("日程", systemImage: "calendar") }
                .tag("upcoming")
            ListsView()
                .tabItem { Label("清单", systemImage: "checklist") }
                .tag("lists")
            SettingsView()
                .tabItem { Label("设置", systemImage: "gearshape") }
                .tag("settings")
        }
        .overlay(alignment: .bottom) {
            if let toast = model.toast {
                Text(toast)
                    .font(.subheadline)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.12), radius: 10, y: 3)
                    .padding(.bottom, 64)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.spring(duration: 0.3), value: model.toast)
        .sheet(isPresented: editorPresented) {
            if let draft = model.editorDraft {
                EditorView(draft: draft)
                    .environmentObject(model)
            }
        }
        .sheet(isPresented: $showOnboarding) {
            OnboardingView()
                .environmentObject(model)
                .interactiveDismissDisabled()
        }
        .fullScreenCover(isPresented: $showGallery) {
            NavigationStack {
                WidgetGalleryView()
            }
            .environmentObject(model)
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                model.now = Date()
                Task { await model.refresh() }
            case .background:
                BackgroundRefresh.schedule()
            default:
                break
            }
        }
        .task {
            if model.needsOnboarding {
                showOnboarding = true
            } else {
                await model.refresh()
            }
            if model.isDemo, model.demoScreen == "editor" {
                var draft = model.newDraft(kind: .task)
                draft.title = "明天下午3点 交报告"
                model.editorDraft = draft
            }
            if model.isDemo, model.demoScreen == "onboarding" { showOnboarding = true }
            if model.isDemo, model.demoScreen == "widgets" { showGallery = true }
        }
    }
}

/// 列表里的一行
struct ItemRow: View {
    @EnvironmentObject private var model: AppModel
    let item: AgendaItem
    let day: Date
    var showDate = false

    private var now: Date { model.now }

    private var isOverdue: Bool {
        guard item.isTask, !item.isCompleted, let due = item.start else { return false }
        return item.hasTime ? due < now : DateText.calendar.startOfDay(for: due) < DateText.calendar.startOfDay(for: now)
    }

    private var isPast: Bool {
        item.isEvent && !item.isAllDay && item.effectiveEnd <= now && DateText.calendar.isDate(day, inSameDayAs: now)
    }

    private var isNow: Bool {
        item.isEvent && !item.isAllDay && (item.start ?? .distantFuture) <= now && item.effectiveEnd > now
    }

    private var timeText: String {
        if showDate, let s = item.start {
            if item.isEvent && !item.isAllDay {
                return DateText.due(s, hasTime: false, today: now) + " " + DateText.itemTime(item, day: s, now: now, compact: false)
            }
            return DateText.due(s, hasTime: item.hasTime, today: now)
        }
        let t = DateText.itemTime(item, day: day, now: now, compact: false)
        return isOverdue && !t.isEmpty ? "已过期 · " + t : t
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            marker
            VStack(alignment: .leading, spacing: 3) {
                Text(item.title)
                    .strikethrough(item.isCompleted)
                    .foregroundStyle(item.isCompleted ? Color.secondary : Color.primary)
                    .lineLimit(2)
                HStack(spacing: 8) {
                    if !timeText.isEmpty {
                        Text(timeText)
                            .foregroundStyle(isOverdue ? Color.overdue : (isNow ? Color.brand : Color.secondary))
                            .fontWeight(isNow ? .semibold : .regular)
                    }
                    if let loc = item.location {
                        Label(loc, systemImage: "mappin.and.ellipse")
                            .labelStyle(.titleAndIcon)
                            .foregroundStyle(.secondary)
                    }
                    Text(item.containerName)
                        .foregroundStyle(.tertiary)
                }
                .font(.footnote)
                .lineLimit(1)
            }
            Spacer(minLength: 0)
            if item.isRecurring {
                Image(systemName: "repeat")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.vertical, 3)
        .opacity(isPast ? 0.5 : 1)
        .contentShape(Rectangle())
        .onTapGesture { model.editorDraft = model.draft(for: item) }
        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
            if !item.isReadOnly {
                Button(role: .destructive) {
                    Task { await model.delete(item) }
                } label: {
                    Label("删除", systemImage: "trash")
                }
            }
        }
        .swipeActions(edge: .leading, allowsFullSwipe: true) {
            if item.isTask && !item.isReadOnly {
                Button {
                    Task { await model.toggle(item) }
                } label: {
                    Label(item.isCompleted ? "未完成" : "完成", systemImage: item.isCompleted ? "arrow.uturn.backward" : "checkmark")
                }
                .tint(item.isCompleted ? .gray : .green)
            }
        }
    }

    @ViewBuilder
    private var marker: some View {
        if item.isTask {
            Button {
                Task { await model.toggle(item) }
            } label: {
                Image(systemName: item.isCompleted ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 22, weight: .regular))
                    .foregroundStyle(Color(hex: item.colorHex))
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(.borderless)
            .disabled(item.isReadOnly)
        } else {
            RoundedRectangle(cornerRadius: 2)
                .fill(Color(hex: item.colorHex))
                .frame(width: 4, height: 20)
                .padding(.horizontal, 9)
        }
    }
}

/// 分组标题：「已过期 2」
struct SectionTitle: View {
    let title: String
    let count: Int
    var warning = false

    var body: some View {
        HStack(spacing: 6) {
            Text(title)
            Text("\(count)")
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 7)
                .padding(.vertical, 1)
                .background((warning ? Color.overdue : Color.brand).opacity(0.14), in: Capsule())
        }
        .foregroundStyle(warning ? Color.overdue : Color.primary)
        .font(.subheadline.weight(.semibold))
        .textCase(nil)
    }
}
