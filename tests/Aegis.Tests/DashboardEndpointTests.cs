using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Tests;

/// <summary>
/// Pano uç noktalarının gerçek HTTP üzerinden davranışı: JSON sözleşmesi (arayüzün okuduğu camelCase adlar), CSRF başlığı,
/// Isolate/Reset (yerel ve dağıtık devre kesici), bilinmeyen boru hattı.
/// </summary>
public sealed class DashboardEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAegisPipeline("yerel", b => b.AddRetry().AddCircuitBreaker());
        builder.Services.AddAegisPipeline("dagitik", b => b.AddStrategy(
            new DistributedCircuitBreakerStrategy(new InMemoryCircuitBreakerStateStore(), new DistributedCircuitBreakerOptions { CircuitKey = "dagitik" })));
        builder.Services.AddAegisPipeline("devresiz", b => b.AddRetry());

        _app = builder.Build();
        _app.MapAegisDashboard("/aegis");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // Yetki politikası yokken müdahale yalnızca yerelden: uzak istemci 403 alır, devre değişmez; okuma açık kalır.
    [Fact]
    public async Task Action_WithoutPolicy_FromRemoteAddress_IsForbidden()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAegisPipeline("uzak", b => b.AddCircuitBreaker());
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.5"); // uzak istemci
            return next(context);
        });
        app.MapAegisDashboard("/aegis");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/aegis/circuits/uzak/isolate");
        request.Headers.Add("X-Aegis-Action", "true");
        using var action = await client.SendAsync(request);
        using var status = await client.GetAsync("/aegis/status");

        Assert.Equal(HttpStatusCode.Forbidden, action.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.DoesNotContain("Isolated", await status.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> PostActionAsync(string pipeline, string action, bool withHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/aegis/circuits/{pipeline}/{action}");
        if (withHeader)
        {
            request.Headers.Add("X-Aegis-Action", "true");
        }

        return _client.SendAsync(request);
    }

    private async Task<JsonElement> GetPipelineStatusAsync(string name)
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/aegis/status"));
        return document.RootElement.GetProperty("pipelines").EnumerateArray()
            .First(p => p.GetProperty("pipelineName").GetString() == name).Clone();
    }

    [Fact]
    public async Task Status_ReturnsCamelCaseContract_ReadByDashboardUi()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/aegis/status"));
        var root = document.RootElement;

        Assert.Equal(3, root.GetProperty("totalPipelines").GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out _));
        var local = (await GetPipelineStatusAsync("yerel"));
        Assert.True(local.GetProperty("hasCircuitBreaker").GetBoolean());
        Assert.Equal("Healthy", local.GetProperty("status").GetString());
        Assert.Equal(["Retry", "CircuitBreaker"], local.GetProperty("strategies").EnumerateArray().Select(s => s.GetProperty("strategyName").GetString()));
    }

    [Fact]
    public async Task Html_IsServed()
    {
        using var response = await _client.GetAsync("/aegis");
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("Aegis Resilience Dashboard", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Action_WithoutCsrfHeader_IsForbidden()
    {
        using var response = await PostActionAsync("yerel", "isolate", withHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Healthy", (await GetPipelineStatusAsync("yerel")).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("yerel")]
    [InlineData("dagitik")] // 1.1.0: dağıtık devre de panodan yönetilir
    public async Task IsolateThenReset_ChangesCircuitState(string pipeline)
    {
        using (var isolate = await PostActionAsync(pipeline, "isolate"))
        {
            Assert.Equal(HttpStatusCode.OK, isolate.StatusCode);
            using var body = JsonDocument.Parse(await isolate.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        }

        if (pipeline == "yerel")
        {
            Assert.Equal("Degraded", (await GetPipelineStatusAsync(pipeline)).GetProperty("status").GetString());
        }
        else
        {
            // Dağıtık devrenin durumu çağrıyla okunur (yerel önbellek): izole devre çağrıyı reddeder
            var registry = _app.Services.GetRequiredService<IAegisPipelineRegistry>();
            await Assert.ThrowsAsync<Aegis.Resilience.Core.Exceptions.IsolatedCircuitException>(
                async () => await registry.GetPipeline(pipeline).ExecuteAsync(_ => ValueTask.FromResult(1)));
        }

        using (var reset = await PostActionAsync(pipeline, "reset"))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        Assert.Equal("Healthy", (await GetPipelineStatusAsync(pipeline)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Action_OnPipelineWithoutCircuit_ReturnsNotFound()
    {
        using var response = await PostActionAsync("devresiz", "isolate");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
    }
}
