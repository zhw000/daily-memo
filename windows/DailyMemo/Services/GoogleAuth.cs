using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DailyMemo.Core;

namespace DailyMemo.Services;

public sealed class GoogleAuthException : Exception
{
    public GoogleAuthException(string message) : base(message) { }
}

public sealed class GoogleToken
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Email { get; set; }
    public string? Scope { get; set; }
}

/// <summary>
/// Google OAuth 2.0（桌面应用 + 回环地址 + PKCE）。
/// 令牌用 DPAPI 加密保存在 %APPDATA%\DailyMemo。
/// </summary>
public sealed class GoogleAuth
{
    public const string Scopes =
        "openid email https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/tasks";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly Func<HttpClient> _http;
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleToken? _token;

    public GoogleAuth(Func<HttpClient> http, Func<AppSettings> settings)
    {
        _http = http;
        _settings = settings;
        var json = SecureStore.Read(AppPaths.GoogleToken);
        if (json != null)
        {
            try { _token = JsonSerializer.Deserialize<GoogleToken>(json); } catch { _token = null; }
        }
    }

    public event EventHandler? StateChanged;

    public bool IsSignedIn => !string.IsNullOrEmpty(_token?.RefreshToken);
    public string? Email => _token?.Email;

    public bool HasClient => !string.IsNullOrWhiteSpace(_settings().GoogleClientId);

    public async Task SignInAsync(CancellationToken ct)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.GoogleClientId))
            throw new GoogleAuthException("请先在设置里填写 Google OAuth 客户端 ID 和密钥（见「Google 配置教程」）。");

        string verifier = RandomUrlSafe(64);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = RandomUrlSafe(24);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirect = $"http://127.0.0.1:{port}/";
            string url = AuthEndpoint +
                         "?client_id=" + Uri.EscapeDataString(s.GoogleClientId.Trim()) +
                         "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                         "&response_type=code" +
                         "&scope=" + Uri.EscapeDataString(Scopes) +
                         "&code_challenge=" + challenge +
                         "&code_challenge_method=S256" +
                         "&access_type=offline&prompt=consent" +
                         "&state=" + state;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var query = await WaitForRedirectAsync(listener, timeout.Token);

            if (query.TryGetValue("error", out var err))
                throw new GoogleAuthException(err == "access_denied" ? "你取消了 Google 授权。" : "Google 授权失败：" + err);
            if (!query.TryGetValue("state", out var st) || st != state)
                throw new GoogleAuthException("授权回调校验失败，请重试。");
            if (!query.TryGetValue("code", out var code))
                throw new GoogleAuthException("没有收到授权码，请重试。");

            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = s.GoogleClientId.Trim(),
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            };
            if (!string.IsNullOrWhiteSpace(s.GoogleClientSecret)) form["client_secret"] = s.GoogleClientSecret.Trim();

            var resp = await PostTokenAsync(form, ct);
            if (resp.Error != null)
                throw new GoogleAuthException($"换取令牌失败：{resp.Error} {resp.ErrorDescription}");
            if (string.IsNullOrEmpty(resp.RefreshToken))
                throw new GoogleAuthException("Google 没有返回刷新令牌，请在 Google 账号「第三方访问」里移除本应用后重试。");

            await _gate.WaitAsync(ct);
            try
            {
                _token = new GoogleToken
                {
                    AccessToken = resp.AccessToken,
                    RefreshToken = resp.RefreshToken,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn),
                    Email = EmailFromIdToken(resp.IdToken),
                    Scope = resp.Scope,
                };
                Save();
            }
            finally { _gate.Release(); }
            Log.Info("Google 登录成功：" + _token.Email);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleAuthException("等待浏览器授权超时（5 分钟），请重试。");
        }
        finally
        {
            listener.Stop();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SignOutAsync()
    {
        var token = _token;
        _token = null;
        SecureStore.Delete(AppPaths.GoogleToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (token?.RefreshToken != null)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token.RefreshToken });
                await _http().PostAsync(RevokeEndpoint, content);
            }
            catch { }
        }
    }

    /// <summary>拿到有效的访问令牌，需要时自动刷新</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var t = _token ?? throw new GoogleAuthException("还没有登录 Google 账号。");
            if (!forceRefresh && !string.IsNullOrEmpty(t.AccessToken) && t.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return t.AccessToken;

            var s = _settings();
            var form = new Dictionary<string, string>
            {
                ["client_id"] = s.GoogleClientId.Trim(),
                ["refresh_token"] = t.RefreshToken ?? "",
                ["grant_type"] = "refresh_token",
            };
            if (!string.IsNullOrWhiteSpace(s.GoogleClientSecret)) form["client_secret"] = s.GoogleClientSecret.Trim();

            var resp = await PostTokenAsync(form, ct);
            if (resp.Error == "invalid_grant" || resp.Error == "invalid_client" || resp.Error == "unauthorized_client")
            {
                _token = null;
                SecureStore.Delete(AppPaths.GoogleToken);
                _ = Task.Run(() => StateChanged?.Invoke(this, EventArgs.Empty));
                throw new GoogleAuthException("Google 授权已失效，请在设置里重新登录。");
            }
            if (resp.Error != null || string.IsNullOrEmpty(resp.AccessToken))
                throw new GoogleAuthException($"刷新 Google 令牌失败：{resp.Error} {resp.ErrorDescription}");

            t.AccessToken = resp.AccessToken;
            t.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn);
            if (!string.IsNullOrEmpty(resp.RefreshToken)) t.RefreshToken = resp.RefreshToken;
            Save();
            return t.AccessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Save()
    {
        if (_token != null) SecureStore.Write(AppPaths.GoogleToken, JsonSerializer.Serialize(_token));
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http().PostAsync(TokenEndpoint, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonSerializer.Deserialize<TokenResponse>(text) ?? new TokenResponse { Error = "empty_response" };
        }
        catch
        {
            return new TokenResponse { Error = $"http_{(int)resp.StatusCode}" };
        }
    }

    /// <summary>在回环端口上等待浏览器带着 ?code=... 跳回来。每个连接单独处理，避免浏览器预连接卡住。</summary>
    internal static async Task<Dictionary<string, string>> WaitForRedirectAsync(TcpListener listener, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        _ = Task.Run(async () =>
        {
            while (!tcs.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = HandleClientAsync(client, tcs);
            }
        });

        return await tcs.Task;
    }

    private static async Task HandleClientAsync(TcpClient client, TaskCompletionSource<Dictionary<string, string>> tcs)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (string.IsNullOrEmpty(first)) return;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))) { }

                var parts = first.Split(' ');
                var target = parts.Length > 1 ? parts[1] : "/";
                var query = ParseQuery(target);
                bool isCallback = query.ContainsKey("code") || query.ContainsKey("error");

                string body = !isCallback ? "" : query.ContainsKey("code") ? SuccessPage : FailurePage;
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {(isCallback ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();

                if (isCallback) tcs.TrySetResult(query);
            }
            catch
            {
                // 浏览器的空连接、超时等忽略即可
            }
        }
    }

    internal static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>();
        int q = target.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    internal static string? EmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private const string SuccessPage =
        "<!doctype html><html lang=\"zh\"><meta charset=\"utf-8\"><title>今日事</title>" +
        "<body style=\"font-family:system-ui,'Microsoft YaHei UI';display:flex;align-items:center;justify-content:center;height:90vh;background:#f5f6fa;color:#1f2328\">" +
        "<div style=\"text-align:center\"><div style=\"font-size:48px\">✅</div><h2>已连接 Google 账号</h2><p>可以关闭这个页面，回到「今日事」。</p></div></body></html>";

    private const string FailurePage =
        "<!doctype html><html lang=\"zh\"><meta charset=\"utf-8\"><title>今日事</title>" +
        "<body style=\"font-family:system-ui,'Microsoft YaHei UI';display:flex;align-items:center;justify-content:center;height:90vh;background:#f5f6fa;color:#1f2328\">" +
        "<div style=\"text-align:center\"><div style=\"font-size:48px\">⚠️</div><h2>授权没有完成</h2><p>回到「今日事」重新点击登录即可。</p></div></body></html>";

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
