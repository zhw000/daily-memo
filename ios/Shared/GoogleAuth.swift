import Foundation
import CryptoKit
import Security

struct GoogleToken: Codable {
    var accessToken: String
    var refreshToken: String
    var expiresAt: Date
    var email: String?
}

enum GoogleAuthError: LocalizedError {
    case notSignedIn
    case noClientID
    case invalidClientID
    case revoked
    case server(String)
    case cancelled

    var errorDescription: String? {
        switch self {
        case .notSignedIn: return "还没有登录 Google 账号"
        case .noClientID: return "请先在设置里填写 Google 的 iOS 客户端 ID"
        case .invalidClientID: return "客户端 ID 格式不对，应该以 .apps.googleusercontent.com 结尾"
        case .revoked: return "Google 授权已失效，请在设置里重新登录"
        case .server(let msg): return "Google 登录失败：\(msg)"
        case .cancelled: return "已取消登录"
        }
    }
}

/// Google OAuth（iOS 客户端 + PKCE，回调地址是反转的客户端 ID）
enum GoogleOAuth {
    static let scopes = "openid email https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/tasks"
    private static let tokenURL = URL(string: "https://oauth2.googleapis.com/token")!

    /// 1234-abc.apps.googleusercontent.com → com.googleusercontent.apps.1234-abc
    static func callbackScheme(for clientID: String) -> String? {
        let suffix = ".apps.googleusercontent.com"
        let id = clientID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard id.hasSuffix(suffix) else { return nil }
        let prefix = String(id.dropLast(suffix.count))
        guard !prefix.isEmpty, !prefix.contains("/") else { return nil }
        return "com.googleusercontent.apps." + prefix
    }

    static func redirectURI(for clientID: String) -> String? {
        callbackScheme(for: clientID).map { $0 + ":/oauth2redirect" }
    }

    static func makeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    static func challenge(for verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    static func authorizationURL(clientID: String, verifier: String, state: String) -> URL? {
        guard let redirect = redirectURI(for: clientID) else { return nil }
        var c = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")
        c?.queryItems = [
            URLQueryItem(name: "client_id", value: clientID.trimmingCharacters(in: .whitespacesAndNewlines)),
            URLQueryItem(name: "redirect_uri", value: redirect),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: scopes),
            URLQueryItem(name: "code_challenge", value: challenge(for: verifier)),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "consent"),
            URLQueryItem(name: "state", value: state),
        ]
        return c?.url
    }

    /// 从回调 URL 里取出授权码
    static func code(from callback: URL, expectedState: String) throws -> String {
        let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first(where: { $0.name == name })?.value }
        if let err = value("error") {
            throw err == "access_denied" ? GoogleAuthError.cancelled : GoogleAuthError.server(err)
        }
        guard value("state") == expectedState else { throw GoogleAuthError.server("回调校验失败") }
        guard let code = value("code"), !code.isEmpty else { throw GoogleAuthError.server("没有收到授权码") }
        return code
    }

    static func exchange(code: String, clientID: String, verifier: String) async throws -> GoogleToken {
        guard let redirect = redirectURI(for: clientID) else { throw GoogleAuthError.invalidClientID }
        let json = try await postForm([
            "code": code,
            "client_id": clientID.trimmingCharacters(in: .whitespacesAndNewlines),
            "redirect_uri": redirect,
            "grant_type": "authorization_code",
            "code_verifier": verifier,
        ])
        guard let access = json["access_token"] as? String, let refresh = json["refresh_token"] as? String else {
            throw GoogleAuthError.server((json["error_description"] as? String) ?? (json["error"] as? String) ?? "没有返回令牌")
        }
        let expires = (json["expires_in"] as? Double) ?? Double((json["expires_in"] as? Int) ?? 3600)
        return GoogleToken(accessToken: access, refreshToken: refresh, expiresAt: Date().addingTimeInterval(expires),
                           email: email(fromIDToken: json["id_token"] as? String))
    }

    static func refresh(_ token: GoogleToken, clientID: String) async throws -> GoogleToken {
        let json = try await postForm([
            "client_id": clientID.trimmingCharacters(in: .whitespacesAndNewlines),
            "refresh_token": token.refreshToken,
            "grant_type": "refresh_token",
        ])
        if let err = json["error"] as? String {
            if ["invalid_grant", "invalid_client", "unauthorized_client"].contains(err) { throw GoogleAuthError.revoked }
            throw GoogleAuthError.server(err)
        }
        guard let access = json["access_token"] as? String else { throw GoogleAuthError.server("刷新令牌失败") }
        var t = token
        t.accessToken = access
        let expires = (json["expires_in"] as? Double) ?? Double((json["expires_in"] as? Int) ?? 3600)
        t.expiresAt = Date().addingTimeInterval(expires)
        if let newRefresh = json["refresh_token"] as? String { t.refreshToken = newRefresh }
        return t
    }

    static func revoke(_ token: GoogleToken) async {
        guard var c = URLComponents(string: "https://oauth2.googleapis.com/revoke") else { return }
        c.queryItems = [URLQueryItem(name: "token", value: token.refreshToken)]
        guard let url = c.url else { return }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        _ = try? await URLSession.shared.data(for: req)
    }

    private static func postForm(_ form: [String: String]) async throws -> [String: Any] {
        var req = URLRequest(url: tokenURL)
        req.httpMethod = "POST"
        req.timeoutInterval = 30
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        req.httpBody = form.map { key, value in
            "\(key)=\(value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value)"
        }.joined(separator: "&").data(using: .utf8)
        let (data, _) = try await URLSession.shared.data(for: req)
        return ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any]) ?? [:]
    }

    static func email(fromIDToken idToken: String?) -> String? {
        guard let parts = idToken?.split(separator: "."), parts.count >= 2 else { return nil }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while payload.count % 4 != 0 { payload += "=" }
        guard let data = Data(base64Encoded: payload),
              let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        return json["email"] as? String
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// 保存和刷新 Google 令牌（App 和小组件共用同一个令牌文件）
actor GoogleSession {
    static let shared = GoogleSession()

    var isSignedIn: Bool { SharedStore.token != nil }
    var email: String? { SharedStore.token?.email }

    func save(_ token: GoogleToken?) {
        SharedStore.token = token
    }

    func signOut() async {
        let token = SharedStore.token
        SharedStore.token = nil
        if let token { await GoogleOAuth.revoke(token) }
    }

    func accessToken(forceRefresh: Bool = false) async throws -> String {
        guard let token = SharedStore.token else { throw GoogleAuthError.notSignedIn }
        if !forceRefresh && token.expiresAt > Date().addingTimeInterval(60) { return token.accessToken }
        let clientID = SharedStore.settings.googleClientID
        guard !clientID.isEmpty else { throw GoogleAuthError.noClientID }
        do {
            let fresh = try await GoogleOAuth.refresh(token, clientID: clientID)
            SharedStore.token = fresh
            return fresh.accessToken
        } catch GoogleAuthError.revoked {
            SharedStore.token = nil
            throw GoogleAuthError.revoked
        }
    }
}
