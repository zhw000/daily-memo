import SwiftUI
import AuthenticationServices
import UIKit

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession
    @State private var signingIn = false
    @State private var notificationsAllowed = false
    @State private var confirmSignOut = false

    private var taskTargetBinding: Binding<String> {
        Binding(get: { model.resolveTaskTarget() ?? "" }, set: { model.settings.defaultTaskTarget = $0 })
    }

    private var eventTargetBinding: Binding<String> {
        Binding(get: { model.resolveEventTarget() ?? "" }, set: { model.settings.defaultEventTarget = $0 })
    }

    private var summaryTime: Binding<Date> {
        Binding(get: {
            DateText.calendar.date(bySettingHour: model.settings.dailySummaryHour, minute: model.settings.dailySummaryMinute, second: 0, of: Date()) ?? Date()
        }, set: { newValue in
            let c = DateText.calendar.dateComponents([.hour, .minute], from: newValue)
            model.settings.dailySummaryHour = c.hour ?? 8
            model.settings.dailySummaryMinute = c.minute ?? 30
        })
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Label("电脑和手机怎么同步", systemImage: "arrow.triangle.2.circlepath")
                            .font(.headline)
                        Text("两边登录同一个 Google 账号：Google 日历和 Google Tasks 是共同的数据。下面开启「提醒事项同步」后，iPhone 提醒事项会双向同步到 Google Tasks，Windows 版也能看到、能打勾。")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 4)
                }

                permissionSection
                googleSection

                Section {
                    NavigationLink {
                        ReminderSyncView()
                    } label: {
                        HStack {
                            Label("提醒事项 ↔ Google Tasks", systemImage: "arrow.left.arrow.right")
                            Spacer()
                            Text(model.settings.syncedReminderLists.isEmpty ? "未开启" : "\(model.settings.syncedReminderLists.count) 个列表")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .disabled(!model.googleSignedIn)
                } header: {
                    Text("同步")
                } footer: {
                    Text(syncFooter)
                }

                Section("显示内容") {
                    NavigationLink {
                        SourcesView()
                    } label: {
                        Label("选择显示的日历和清单", systemImage: "eye")
                    }
                    Toggle("「今天」显示没有日期的待办", isOn: $model.settings.showUndated)
                }

                Section("新建时默认保存到") {
                    Picker("待办", selection: taskTargetBinding) {
                        ForEach(model.taskTargets) { c in Text(c.displayName).tag(c.key) }
                    }
                    Picker("日程", selection: eventTargetBinding) {
                        ForEach(model.eventTargets) { c in Text(c.displayName).tag(c.key) }
                    }
                }

                Section {
                    Toggle("每日早报", isOn: $model.settings.dailySummaryEnabled)
                    if model.settings.dailySummaryEnabled {
                        DatePicker("推送时间", selection: summaryTime, displayedComponents: .hourAndMinute)
                    }
                    Toggle("Google 日程开始前提醒", isOn: $model.settings.googleAlertsEnabled)
                    if model.settings.googleAlertsEnabled {
                        Picker("提前", selection: $model.settings.googleAlertMinutes) {
                            ForEach([0, 5, 10, 15, 30, 60], id: \.self) { m in
                                Text(m == 0 ? "准时" : "\(m) 分钟").tag(m)
                            }
                        }
                    }
                } header: {
                    Text("通知")
                } footer: {
                    Text("iPhone 日历和提醒事项里的内容由系统按原来的设置提醒；这里只为 Google 来源的内容补充提醒。")
                }

                Section {
                    NavigationLink {
                        WidgetGalleryView()
                    } label: {
                        Label("小组件预览和添加方法", systemImage: "square.grid.2x2")
                    }
                    Toggle("小组件显示今天已完成的", isOn: $model.settings.widgetShowCompleted)
                    HStack {
                        Text("小组件数据共享")
                        Spacer()
                        Text(AppGroup.isShared ? "正常" : "不可用")
                            .foregroundStyle(AppGroup.isShared ? Color.secondary : Color.overdue)
                    }
                } header: {
                    Text("小组件")
                } footer: {
                    if !AppGroup.isShared {
                        Text("签名时 App Group 被去掉了：小组件仍会显示 iPhone 日历和提醒事项，但看不到 Google 的内容。用 Sideloadly / AltStore 等保留 App Group 的方式重新签名即可。")
                    }
                }

                Section("关于") {
                    NavigationLink {
                        HelpView()
                    } label: {
                        Label("使用说明", systemImage: "questionmark.circle")
                    }
                    HStack {
                        Text("版本")
                        Spacer()
                        Text(appVersion).foregroundStyle(.secondary)
                    }
                    if let last = model.lastRefresh {
                        HStack {
                            Text("上次同步")
                            Spacer()
                            Text(DateText.due(last, hasTime: true, today: Date())).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .navigationTitle("设置")
            .task { notificationsAllowed = await NotificationScheduler.isAuthorized() }
            .confirmationDialog("退出 Google 账号？", isPresented: $confirmSignOut, titleVisibility: .visible) {
                Button("退出登录", role: .destructive) { Task { await model.signOutGoogle() } }
            } message: {
                Text("退出后这台 iPhone 不再显示 Google 日历和待办，云端数据不受影响。")
            }
        }
    }

    // MARK: - 权限

    private var permissionSection: some View {
        Section {
            PermissionRow(title: "日历", icon: "calendar", granted: model.eventsAuthorized, status: EventKitSource.statusText(.event))
            PermissionRow(title: "提醒事项", icon: "checklist", granted: model.remindersAuthorized, status: EventKitSource.statusText(.reminder))
            PermissionRow(title: "通知", icon: "bell.badge", granted: notificationsAllowed, status: notificationsAllowed ? "已允许" : "未允许")
            if !model.eventsAuthorized || !model.remindersAuthorized || !notificationsAllowed {
                Button {
                    Task {
                        await model.requestPermissions()
                        notificationsAllowed = await NotificationScheduler.isAuthorized()
                    }
                } label: {
                    Label("允许访问", systemImage: "hand.raised")
                }
                Button {
                    if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                } label: {
                    Label("打开系统设置", systemImage: "gear")
                }
            }
        } header: {
            Text("权限")
        } footer: {
            Text("如果之前点了「不允许」，需要在系统设置里手动打开。")
        }
    }

    // MARK: - Google

    private var googleSection: some View {
        Section {
            if model.googleSignedIn {
                HStack {
                    Label(model.googleEmail ?? "Google 账号", systemImage: "person.crop.circle.badge.checkmark")
                    Spacer()
                    Text("已连接").foregroundStyle(.secondary)
                }
                Toggle("显示 Google 日历", isOn: $model.settings.googleCalendarEnabled)
                Toggle("显示 Google Tasks", isOn: $model.settings.googleTasksEnabled)
                Button("退出 Google 账号", role: .destructive) { confirmSignOut = true }
            } else {
                NavigationLink {
                    GoogleSetupView()
                } label: {
                    HStack {
                        Label("iOS 客户端 ID", systemImage: "key")
                        Spacer()
                        Text(model.settings.googleClientID.isEmpty ? "未填写" : "已填写")
                            .foregroundStyle(.secondary)
                    }
                }
                Button {
                    Task { await signIn() }
                } label: {
                    HStack {
                        Label("登录 Google", systemImage: "person.crop.circle.badge.plus")
                        if signingIn {
                            Spacer()
                            ProgressView()
                        }
                    }
                }
                .disabled(signingIn || GoogleOAuth.callbackScheme(for: model.settings.googleClientID) == nil)
            }
        } header: {
            Text("Google 账号")
        } footer: {
            Text(model.googleSignedIn ? "如果这个 Google 账号也添加到了 iPhone「日历」，重复的日程会自动合并。" : "登录同一个 Google 账号，iPhone 和电脑上的日程、待办就会同步。需要先在 Google Cloud 创建一个 iOS 客户端 ID（见「使用说明」）。")
        }
    }

    private var syncFooter: String {
        guard model.googleSignedIn else { return "登录 Google 后可用。" }
        let s = model.syncState
        if let err = s.lastError { return "上次同步出错：\(err)" }
        if let last = s.lastRun { return "上次同步：\(DateText.due(last, hasTime: true, today: Date()))，更新了 \(s.lastChanges) 项" }
        return "把 iPhone 提醒事项同步到 Google Tasks，Windows 版就能看到。"
    }

    private var appVersion: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
        let b = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "1"
        return "\(v) (\(b))"
    }

    private func signIn() async {
        let clientID = model.settings.googleClientID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let scheme = GoogleOAuth.callbackScheme(for: clientID) else {
            model.show(GoogleAuthError.invalidClientID)
            return
        }
        let verifier = GoogleOAuth.makeVerifier()
        let state = UUID().uuidString
        guard let url = GoogleOAuth.authorizationURL(clientID: clientID, verifier: verifier, state: state) else { return }
        signingIn = true
        defer { signingIn = false }
        do {
            let callback = try await webAuthenticationSession.authenticate(using: url, callbackURLScheme: scheme)
            let code = try GoogleOAuth.code(from: callback, expectedState: state)
            let token = try await GoogleOAuth.exchange(code: code, clientID: clientID, verifier: verifier)
            await model.finishGoogleSignIn(token: token)
        } catch let error as ASWebAuthenticationSessionError where error.code == .canceledLogin {
            // 用户取消
        } catch GoogleAuthError.cancelled {
            // 用户取消
        } catch {
            model.show(error)
        }
    }
}

struct PermissionRow: View {
    let title: String
    let icon: String
    let granted: Bool
    let status: String

    var body: some View {
        HStack {
            Label(title, systemImage: icon)
            Spacer()
            Text(granted ? "已允许" : status)
                .foregroundStyle(granted ? Color.secondary : Color.overdue)
            Image(systemName: granted ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(granted ? Color.done : Color.overdue)
        }
    }
}

// MARK: - Google 客户端 ID

struct GoogleSetupView: View {
    @EnvironmentObject private var model: AppModel
    @State private var text = ""

    var body: some View {
        Form {
            Section {
                TextField("xxxxxxxx-xxxx.apps.googleusercontent.com", text: $text, axis: .vertical)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .font(.system(.footnote, design: .monospaced))
                Button {
                    if let s = UIPasteboard.general.string { text = s.trimmingCharacters(in: .whitespacesAndNewlines) }
                } label: {
                    Label("从剪贴板粘贴", systemImage: "doc.on.clipboard")
                }
            } header: {
                Text("iOS 客户端 ID")
            } footer: {
                if let scheme = GoogleOAuth.callbackScheme(for: text) {
                    Text("格式正确。回调地址：\(scheme):/oauth2redirect")
                } else if !text.isEmpty {
                    Text("格式不对：应该以 .apps.googleusercontent.com 结尾").foregroundStyle(Color.overdue)
                }
            }

            Section("怎么获得") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("1. 用和 Windows 版相同的 Google Cloud 项目（已启用 Calendar API 和 Tasks API）。")
                    Text("2. 「API 和服务 → 凭据 → 创建凭据 → OAuth 客户端 ID」，应用类型选「iOS」。")
                    Text("3. 「软件包 ID」填 \(Bundle.main.bundleIdentifier ?? "com.dailymemo.app")（签名后如果改了，就填改后的）。")
                    Text("4. 创建后复制「客户端 ID」，粘贴到上面，返回点「登录 Google」。")
                    Text("5. 浏览器提示「Google 尚未验证此应用」时，点「高级 → 转到…」即可。")
                }
                .font(.footnote)
                .foregroundStyle(.secondary)
            }
        }
        .navigationTitle("Google 客户端 ID")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { text = model.settings.googleClientID }
        .onDisappear { model.settings.googleClientID = text.trimmingCharacters(in: .whitespacesAndNewlines) }
        .onChange(of: text) { _, newValue in
            let trimmed = newValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if GoogleOAuth.callbackScheme(for: trimmed) != nil { model.settings.googleClientID = trimmed }
        }
    }
}

// MARK: - 提醒事项同步

struct ReminderSyncView: View {
    @EnvironmentObject private var model: AppModel

    private var lists: [SourceContainer] {
        model.containers.filter { $0.source == .ekReminder && $0.writable }
    }

    var body: some View {
        Form {
            Section {
                ForEach(lists) { list in
                    Toggle(isOn: Binding(
                        get: { model.settings.syncedReminderLists.contains(list.nativeID) },
                        set: { model.setReminderSync(listID: list.nativeID, enabled: $0) })) {
                        HStack(spacing: 10) {
                            Image(systemName: "list.bullet.circle.fill")
                                .foregroundStyle(Color(hex: list.colorHex))
                            Text(list.name)
                        }
                    }
                }
                if lists.isEmpty {
                    Text("没有可同步的提醒事项列表（需要先允许访问提醒事项）")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            } header: {
                Text("同步这些列表")
            } footer: {
                Text("每个列表对应 Google Tasks 里同名的清单（没有就自动创建）。在任意一边新建、修改、打勾、删除，另一边都会跟着变。")
            }

            Section {
                Button {
                    Task { await model.refresh() }
                } label: {
                    HStack {
                        Label("立即同步", systemImage: "arrow.triangle.2.circlepath")
                        if model.isLoading {
                            Spacer()
                            ProgressView()
                        }
                    }
                }
                .disabled(model.isLoading)
                if let last = model.syncState.lastRun {
                    HStack {
                        Text("上次同步")
                        Spacer()
                        Text(DateText.due(last, hasTime: true, today: Date())).foregroundStyle(.secondary)
                    }
                }
                if let err = model.syncState.lastError {
                    Text(err).font(.footnote).foregroundStyle(Color.overdue)
                }
            } footer: {
                Text("打开 App、下拉刷新、以及系统空闲时会自动同步。Google Tasks 只保存日期，带时间的提醒会把时间写在标题开头（如「15:00 交报告」），同步回来时自动还原成提醒时间。")
            }
        }
        .navigationTitle("提醒事项同步")
        .navigationBarTitleDisplayMode(.inline)
    }
}

// MARK: - 显示的日历和清单

struct SourcesView: View {
    @EnvironmentObject private var model: AppModel

    private let groups: [(ItemSource, String)] = [
        (.ekEvent, "iPhone 日历"),
        (.ekReminder, "iPhone 提醒事项"),
        (.googleEvent, "Google 日历"),
        (.googleTask, "Google Tasks"),
    ]

    var body: some View {
        Form {
            ForEach(0..<groups.count, id: \.self) { index in
                let group = groups[index]
                let items = model.containers.filter { $0.source == group.0 }
                if !items.isEmpty {
                    Section(group.1) {
                        ForEach(items) { c in
                            Toggle(isOn: Binding(get: { model.settings.isVisible(c) }, set: { model.setVisible(c, $0) })) {
                                HStack(spacing: 10) {
                                    Circle().fill(Color(hex: c.colorHex)).frame(width: 10, height: 10)
                                    Text(c.name)
                                    if let account = c.accountName, group.0 == .ekEvent || group.0 == .ekReminder {
                                        Text(account).font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle("显示内容")
        .navigationBarTitleDisplayMode(.inline)
    }
}
