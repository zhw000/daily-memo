import SwiftUI
import WidgetKit

/// 第一次打开时的介绍和授权
struct OnboardingView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var working = false

    var body: some View {
        VStack(spacing: 22) {
            Spacer(minLength: 12)
            ZStack {
                RoundedRectangle(cornerRadius: 22, style: .continuous)
                    .fill(LinearGradient(colors: [Color(hex: "#6F8BFF"), Color(hex: "#3B57E8")], startPoint: .topLeading, endPoint: .bottomTrailing))
                    .frame(width: 88, height: 88)
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 44, weight: .semibold))
                    .foregroundStyle(.white)
            }
            VStack(spacing: 6) {
                Text("今日事")
                    .font(.largeTitle.bold())
                Text("每天要做的事，一眼看清")
                    .foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 18) {
                FeatureRow(icon: "calendar", color: .red, title: "日历和提醒事项放在一起",
                           text: "iPhone 日历、提醒事项、Google 日历和待办合成一个「今天」。")
                FeatureRow(icon: "square.grid.2x2.fill", color: .blue, title: "主屏幕和锁屏小组件",
                           text: "随时看到今天要做什么，待办可以直接在小组件上打勾。")
                FeatureRow(icon: "bell.badge.fill", color: .orange, title: "每天早上提醒你",
                           text: "每天定时推送今天的安排，再也不会忘。")
                FeatureRow(icon: "desktopcomputer", color: .green, title: "和 Windows 电脑同步",
                           text: "登录 Google 后，电脑版今日事显示同样的内容。")
            }
            .padding(.horizontal, 6)
            Spacer()
            Button {
                working = true
                Task {
                    await model.requestPermissions()
                    model.settings.onboardingDone = true
                    working = false
                    dismiss()
                }
            } label: {
                HStack {
                    if working { ProgressView().tint(.white) }
                    Text("允许访问并开始")
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(working)
            Button("以后再说") {
                model.settings.onboardingDone = true
                dismiss()
            }
            .font(.subheadline)
        }
        .padding(28)
    }
}

struct FeatureRow: View {
    let icon: String
    let color: Color
    let title: String
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon)
                .font(.title2)
                .foregroundStyle(color)
                .frame(width: 34)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.headline)
                Text(text).font(.subheadline).foregroundStyle(.secondary)
            }
        }
    }
}

/// 在 App 里预览各种尺寸的小组件
struct WidgetGalleryView: View {
    @EnvironmentObject private var model: AppModel

    private var entry: DayEntry {
        DayEntry(date: model.now, items: model.items, showCompleted: model.settings.widgetShowCompleted, showUndated: model.settings.showUndated)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                Text("长按主屏幕空白处 → 左上角「编辑」→「添加小组件」→ 搜索「今日事」。锁屏：长按锁屏 →「自定」→ 锁定屏幕 → 添加小组件。")
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                gallerySection("小") {
                    card(.systemSmall, width: 170, height: 170)
                }
                gallerySection("中") {
                    card(.systemMedium, width: 364, height: 170)
                }
                gallerySection("大") {
                    card(.systemLarge, width: 364, height: 382)
                }
                gallerySection("锁屏") {
                    HStack(spacing: 14) {
                        accessory(.accessoryCircular, width: 72, height: 72)
                        accessory(.accessoryRectangular, width: 170, height: 72)
                    }
                    accessory(.accessoryInline, width: 300, height: 26)
                }
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color(uiColor: .systemGroupedBackground))
        .navigationTitle("小组件")
        .navigationBarTitleDisplayMode(.inline)
    }

    private func gallerySection<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
            content()
        }
    }

    private func card(_ family: WidgetFamily, width: CGFloat, height: CGFloat) -> some View {
        WidgetContentView(entry: entry, family: family)
            .padding(16)
            .frame(width: width, height: height)
            .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 22, style: .continuous))
            .shadow(color: .black.opacity(0.08), radius: 8, y: 2)
    }

    private func accessory(_ family: WidgetFamily, width: CGFloat, height: CGFloat) -> some View {
        WidgetContentView(entry: entry, family: family)
            .frame(width: width, height: height)
            .padding(8)
            .background(Color.black.opacity(0.75), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            .foregroundStyle(.white)
            .environment(\.colorScheme, .dark)
    }
}

struct HelpView: View {
    var body: some View {
        List {
            Section("每天怎么用") {
                HelpRow(title: "看今天要做什么", text: "打开 App 的「今天」，或者把小组件放在主屏幕和锁屏。每天早上会推送当天的安排。")
                HelpRow(title: "随手记一件事", text: "在「今天」顶部的输入框里写，比如「明天下午3点 交报告」「周五 买菜」「半小时后 关火」，会自动识别时间。")
                HelpRow(title: "打勾", text: "点前面的圆圈，或者在小组件里直接点；也可以在列表里向右滑。")
            }
            Section("和电脑同步") {
                HelpRow(title: "1. 在电脑上配置 Google", text: "按 Windows 版「设置 → Google 账号」里的步骤创建 Google Cloud 项目，启用 Calendar API 和 Tasks API。")
                HelpRow(title: "2. 创建 iOS 客户端 ID", text: "同一个项目里再创建一个「iOS」类型的 OAuth 客户端，把客户端 ID 填到这里的设置中，然后登录。")
                HelpRow(title: "3. 开启提醒事项同步", text: "设置 → 提醒事项 ↔ Google Tasks，选择要同步的列表。之后在 iPhone 或电脑上改动都会同步。")
                HelpRow(title: "4. iPhone 日历", text: "电脑版可以在设置里直接连接 iCloud 日历（需要 App 专用密码）；或者把日程建在 Google 日历里。")
            }
            Section("常见问题") {
                HelpRow(title: "Google 连不上", text: "在国内访问 Google 需要网络代理，请确认 iPhone 上的代理已开启。")
                HelpRow(title: "小组件没有 Google 的内容", text: "小组件和 App 靠 App Group 共享数据。如果签名时去掉了 App Group，小组件只显示 iPhone 日历和提醒事项。")
                HelpRow(title: "登录 7 天后失效", text: "在 Google Cloud 的「OAuth 同意屏幕」里把应用「发布」为正式版即可（个人使用无需审核）。")
            }
        }
        .navigationTitle("使用说明")
        .navigationBarTitleDisplayMode(.inline)
    }
}

struct HelpRow: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.subheadline.weight(.semibold))
            Text(text).font(.footnote).foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}
