using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aegis.Tests;

/// <summary>AddAegisCheck kayıtları: her kontrol kendi ayarını taşır (gerçek proje testinde bulundu).</summary>
public sealed class HealthCheckRegistrationTests
{
    // Canlılık (açık devre Degraded) ve hazır olma (açık devre Unhealthy) birlikte kullanılabilir; önceden ikinci kaydın
    // ayarı global olduğu için birinciyi de Unhealthy yapıyordu.
    [Fact]
    public async Task TwoChecks_WithDifferentOptions_DoNotOverrideEachOther()
    {
        var control = new CircuitBreakerManualControl();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAegis();
        services.AddAegisPipeline("odeme", p => p.AddCircuitBreaker(o => o.ManualControl = control));
        services.AddHealthChecks()
            .AddAegisCheck("canlilik", tags: ["live"], configureOptions: o => o.OpenCircuitStatus = HealthStatus.Degraded)
            .AddAegisCheck("hazir", tags: ["ready"], configureOptions: o => o.OpenCircuitStatus = HealthStatus.Unhealthy);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("odeme"); // devre kurulsun
        await control.IsolateAsync();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, report.Entries["canlilik"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["hazir"].Status);
    }

    // Ayar verilmeyen kayıt uygulama geneli ayarı kullanır (önceki davranış korunur).
    [Fact]
    public async Task CheckWithoutOptions_UsesApplicationWideConfiguration()
    {
        var control = new CircuitBreakerManualControl();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAegis();
        services.AddAegisPipeline("odeme", p => p.AddCircuitBreaker(o => o.ManualControl = control));
        services.Configure<AegisHealthCheckOptions>(o => o.OpenCircuitStatus = HealthStatus.Unhealthy);
        services.AddHealthChecks().AddAegisCheck();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("odeme");
        await control.IsolateAsync();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["aegis_resilience"].Status);
    }
}
