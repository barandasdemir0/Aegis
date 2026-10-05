using Aegis.Resilience.Core.Strategies.CircuitBreaker;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Dağıtık mimarilerde (Kubernetes, çoklu pod vb.) Circuit Breaker durumunu paylaşmak için kullanılan arayüz.
/// Redis, Memcached veya Veritabanı implementasyonları bu arayüzü uygular.
/// <para>
/// Davranış sözleşmesi (tüm implementasyonlar aynı davranmalıdır):
/// <list type="bullet">
/// <item><see cref="SetStateAsync"/> ile <see cref="CircuitState.Open"/> yazıldıktan sonra <c>ttl</c> süresince
/// <see cref="GetStateAsync"/> Open döner; süre dolunca <see cref="CircuitState.HalfOpen"/> döner (Closed DEĞİL) —
/// devre ancak başarılı bir deneme isteğinden sonra <see cref="CircuitState.Closed"/> yazılarak kapanır.</item>
/// <item><see cref="CircuitState.Closed"/> yazmak pencere sayaçlarını da sıfırlar.</item>
/// <item>Depo erişilemezse işlemler istisna fırlatmamalı, yerel duruma düşmeli (fail-open) ve yerel Open durumu da
/// <c>ttl</c> sonunda HalfOpen'a dönmelidir.</item>
/// </list>
/// </para>
/// </summary>
public interface ICircuitBreakerStateStore
{
    ValueTask<CircuitState> GetStateAsync(string circuitKey, CancellationToken cancellationToken = default);

    ValueTask SetStateAsync(string circuitKey, CircuitState state, TimeSpan ttl, CancellationToken cancellationToken = default);

    ValueTask<(int SuccessCount, int FailureCount)> RecordResultAsync(
        string circuitKey,
        bool isSuccess,
        TimeSpan samplingDuration,
        CancellationToken cancellationToken = default);

    // Aşağıdaki üç üye isteğe bağlıdır: .NET 8+'da varsayılan gövdeleri vardır. .NET Framework / netstandard2.0 çalışma
    // zamanı varsayılan arayüz gövdesi desteklemediği için orada uygulayıcı bunları da yazar (varsayılan davranış yorumda).

    /// <summary>
    /// HalfOpen durumunda tüm pod'lar arasında TEK deneme isteği hakkını almaya çalışır. Hak <paramref name="lease"/>
    /// sonunda kendiliğinden düşer (hakkı alan pod çökerse devre kilitli kalmaz).
    /// Varsayılan implementasyon her zaman <c>true</c> döner; bu durumda deneme tekilliği yalnızca pod içinde sağlanır.
    /// </summary>
    ValueTask<bool> TryAcquireProbeAsync(string circuitKey, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
#if AEGIS_LEGACY
        ;
#else
        => ValueTask.FromResult(true);
#endif

    /// <summary>
    /// <see cref="TryAcquireProbeAsync"/> ile alınan hakkı, yalnızca hâlâ <paramref name="ownerId"/>'ye aitse bırakır.
    /// Varsayılan implementasyon hiçbir şey yapmaz.
    /// </summary>
    ValueTask ReleaseProbeAsync(string circuitKey, string ownerId, CancellationToken cancellationToken = default)
#if AEGIS_LEGACY
        ;
#else
        => ValueTask.CompletedTask;
#endif

    /// <summary>
    /// Depoya şu an ulaşılabiliyor mu (yan etkisiz, uzak çağrı yapmaz). <c>false</c> iken depo yerel duruma düşer ve pod'lar
    /// durum paylaşmaz; health check bunu raporlar (AEGIS-160). Varsayılan: her zaman erişilebilir (<c>true</c>).
    /// </summary>
    bool IsAvailable
#if AEGIS_LEGACY
    {
        get;
    }
#else
        => true;
#endif
}
