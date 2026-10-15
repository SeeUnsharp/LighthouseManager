using System.Net;
using LighthouseManagerService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace LighthouseManager.Tests.Service;

public class LocalRequestGuardTests : IAsyncLifetime
{
    private IHost? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            {
                app.UseLocalRequestGuard(["http://localhost:5173"]);
                app.Run(context => context.Response.WriteAsync("ok"));
            }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
    }

    private async Task<HttpStatusCode> SendAsync(string host, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{host}/api/stations/sleep");
        if (origin != null) request.Headers.Add("Origin", origin);
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData("127.0.0.1:47800")]
    [InlineData("localhost:47800")]
    [InlineData("[::1]:47800")]
    public async Task Allows_native_clients_on_loopback(string host)
    {
        Assert.Equal(HttpStatusCode.OK, await SendAsync(host));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("192.168.1.10:47800")]
    public async Task Rejects_non_loopback_hosts_against_DNS_rebinding(string host)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(host));
    }

    [Fact]
    public async Task Rejects_requests_from_other_web_pages()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync("127.0.0.1:47800", "https://evil.example"));
    }

    [Fact]
    public async Task Allows_configured_origins()
    {
        Assert.Equal(HttpStatusCode.OK, await SendAsync("127.0.0.1:47800", "http://localhost:5173"));
    }
}
