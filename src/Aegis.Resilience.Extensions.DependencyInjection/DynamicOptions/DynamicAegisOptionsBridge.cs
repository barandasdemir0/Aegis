using Microsoft.Extensions.Options;

namespace Aegis.Resilience.Extensions.DependencyInjection.DynamicOptions;

/// <summary>
/// <see cref="IOptionsMonitor{TOptions}"/> üzerinden gelen yapılandırma değişikliklerini strateji
/// <c>OptionsProvider</c> delegelerine köprüler; böylece appsettings / yapılandırma sunucusu değişiklikleri
/// uygulama yeniden başlatılmadan boru hattına yansır.
/// <para>
/// Kullanım: <c>o.OptionsProvider = DynamicAegisOptionsBridge.Create(monitor, "odeme");</c>
/// Tek generic metot tüm seçenek tipleri (Retry, CircuitBreaker, Timeout, ConcurrencyLimiter...) için geçerlidir.
/// </para>
/// </summary>
public static class DynamicAegisOptionsBridge
{
    public static Func<TOptions> Create<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(IOptionsMonitor<TOptions> monitor, string? name = null)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return string.IsNullOrEmpty(name)
            ? () => monitor.CurrentValue
            : () => monitor.Get(name);
    }
}
