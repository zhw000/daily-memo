using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DailyMemo.Services;
using Xunit;

namespace DailyMemo.Tests;

public class LoopbackTests
{
    [Fact]
    public async Task ReceivesOAuthRedirectEvenWithIdleBrowserConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var wait = GoogleAuth.WaitForRedirectAsync(listener, cts.Token);

            // 浏览器常见的「预连接」：连上但不发请求
            using var idle = new TcpClient();
            await idle.ConnectAsync(IPAddress.Loopback, port);

            // 浏览器还会顺便请求 favicon
            using var http = new HttpClient();
            var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
            Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);

            var resp = await http.GetAsync($"http://127.0.0.1:{port}/?state=s1&code=4%2Fabc&scope=email");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("已连接 Google 账号", await resp.Content.ReadAsStringAsync());

            var query = await wait;
            Assert.Equal("4/abc", query["code"]);
            Assert.Equal("s1", query["state"]);
        }
        finally
        {
            listener.Stop();
        }
    }
}
