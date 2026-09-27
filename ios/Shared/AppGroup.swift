import Foundation

/// App 和小组件共享数据用的 App Group。
/// 自己签名（Sideloadly / AltStore / 证书签名工具）时 App Group 的名字可能被改掉，
/// 所以运行时依次尝试：AltStore 写入的 ALTAppGroups → 描述文件里的 App Group → 构建时的默认值。
enum AppGroup {
    static var configuredID: String {
        (Bundle.main.object(forInfoDictionaryKey: "DMAppGroup") as? String) ?? "group.com.dailymemo.app"
    }

    static let identifier: String? = resolve()

    /// 共享目录可用时，小组件才能读到 App 同步好的 Google 数据
    static var isShared: Bool { identifier != nil }

    static let containerURL: URL = {
        if let id = identifier, let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: id) {
            return url
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let dir = base.appendingPathComponent("DailyMemo", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private static func resolve() -> String? {
        var bundles: [Bundle] = [Bundle.main]
        if let app = containingAppBundle() { bundles.append(app) }

        var candidates: [String] = []
        for b in bundles {
            if let alt = b.object(forInfoDictionaryKey: "ALTAppGroups") as? [String] { candidates.append(contentsOf: alt) }
        }
        for b in bundles { candidates.append(contentsOf: ProvisioningProfile.appGroups(in: b)) }
        for b in bundles {
            if let id = b.object(forInfoDictionaryKey: "DMAppGroup") as? String { candidates.append(id) }
        }
        candidates.append("group.com.dailymemo.app")

        var seen = Set<String>()
        for id in candidates where !id.isEmpty && seen.insert(id).inserted {
            if FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: id) != nil { return id }
        }
        return nil
    }

    /// 在小组件进程里找到外层 App 的 Bundle（…/DailyMemo.app/PlugIns/xxx.appex）
    private static func containingAppBundle() -> Bundle? {
        let url = Bundle.main.bundleURL
        guard url.pathExtension == "appex" else { return nil }
        return Bundle(url: url.deletingLastPathComponent().deletingLastPathComponent())
    }
}

enum ProvisioningProfile {
    /// 从 embedded.mobileprovision 里读出允许的 App Group
    static func appGroups(in bundle: Bundle) -> [String] {
        guard let url = bundle.url(forResource: "embedded", withExtension: "mobileprovision"),
              let data = try? Data(contentsOf: url),
              let start = data.range(of: Data("<?xml".utf8)),
              let end = data.range(of: Data("</plist>".utf8)),
              start.lowerBound < end.upperBound else { return [] }
        let plistData = data.subdata(in: start.lowerBound..<end.upperBound)
        guard let plist = (try? PropertyListSerialization.propertyList(from: plistData, options: [], format: nil)) as? [String: Any],
              let entitlements = plist["Entitlements"] as? [String: Any],
              let groups = entitlements["com.apple.security.application-groups"] as? [String] else { return [] }
        return groups.filter { !$0.contains("*") }
    }
}
