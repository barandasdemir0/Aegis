using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Extensions.Aspire;

namespace Aegis.Tests;

/// <summary><c>AddAegisServiceDefaults</c>: Aspire ServiceDefaults'ta tek çağrılık kurulum.</summary>
public class AspireServiceDefaultsTests
{
    private sealed class FlakyHandler(int failures) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(++Calls <= failures ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    }

    private static HostApplicationBuilder NewBuilder() => Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

    [Fact]
    public async Task Every_HttpClient_Gets_Standard_Handler()
    {
        var builder = NewBuilder();
        builder.AddAegisServiceDefaults(o => o.ConfigureHttp = http => http.Retry.Delay = TimeSpan.FromMilliseconds(1));
        var flaky = new FlakyHandler(failures: 2);
        builder.Services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => flaky);

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using var response = await client.GetAsync(new Uri("http://localhost/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, flaky.Calls);
    }

    [Fact]
    public void Registers_Health_Check_By_Default()
    {
        var builder = NewBuilder();
        builder.AddAegisServiceDefaults();
        using var host = builder.Build();

        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Contains(registrations, r => r.Name == "aegis_resilience");
    }

    [Fact]
    public async Task Parts_Can_Be_Disabled()
    {
        var builder = NewBuilder();
        builder.AddAegisServiceDefaults(o => { o.AddHttpResilience = false; o.AddHealthCheck = false; });
        var flaky = new FlakyHandler(failures: 1);
        builder.Services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => flaky);
        using var host = builder.Build();

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using var response = await client.GetAsync(new Uri("http://localhost/"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, flaky.Calls);
        Assert.Null(host.Services.GetService<IOptions<HealthCheckServiceOptions>>()?.Value.Registrations.FirstOrDefault(r => r.Name == "aegis_resilience"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tracing_Registration_Follows_Option(bool addTracing)
    {
        var builder = NewBuilder();
        builder.AddAegisServiceDefaults(o => { o.AddTracing = addTracing; o.AddMetrics = false; o.AddHealthCheck = false; o.AddHttpResilience = false; });
        // OpenTelemetry SDK'sı bu projede yok ve IConfigureTracerProviderBuilder internal: kayıt, servis türü adıyla aranır. Bu yalnızca
        // TracerProvider yapılandırma kancasının eklendiğini gösterir; Aegis kaynağının gerçekten dinlendiği smoke testinde
        // gerçek SDK ile doğrulanır (tests/Aegis.AotSmokeTest).
        Assert.Equal(addTracing, builder.Services.Any(d => d.ServiceType.Name == "IConfigureTracerProviderBuilder"));
    }

    [Fact]
    public void Binds_Configuration_Section()
    {
        var builder = NewBuilder();
        builder.Configuration["Aegis:Http:Retry:MaxRetryAttempts"] = "0";
        builder.AddAegisServiceDefaults();
        var flaky = new FlakyHandler(failures: 1);
        builder.Services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => flaky);
        using var host = builder.Build();

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        // Aegis varsayılanı: denemeler tükenince istisna (ReturnFinalResponse kapalı); retry 0 → tek çağrı.
        Assert.ThrowsAny<Exception>(() => client.GetAsync(new Uri("http://localhost/")).GetAwaiter().GetResult());
        Assert.Equal(1, flaky.Calls);
    }
}
