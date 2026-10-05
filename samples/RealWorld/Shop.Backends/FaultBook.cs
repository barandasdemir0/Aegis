using System.Collections.Concurrent;

namespace Shop.Backends;

/// <summary>Bir rotanın sıradaki davranışı. <see cref="Times"/> kez uygulanır, sonra bir sonraki adıma geçilir.</summary>
public sealed record FaultStep
{
    public int Status { get; init; } = 200;
    public int DelayMs { get; init; }
    public string? RetryAfter { get; init; }
    public int? GrpcStatus { get; init; }
    public int? PushbackMs { get; init; }
    public int Times { get; init; } = 1;

    /// <summary>Sunucu akışında hatadan önce gönderilecek mesaj sayısı (commit kuralını sınamak için).</summary>
    public int AfterMessages { get; init; }
}

/// <summary>Gelen bir çağrının kaydı (testler doğrular).</summary>
public sealed record CallRecord(string? IdempotencyKey, string? PreviousAttempts, string? Body, DateTimeOffset At, string? CorrelationId = null);

/// <summary>
/// Rota başına hata senaryosu ve çağrı günlüğü. Testler senaryoyu HTTP ile kurar, arka uç her çağrıda sıradaki adımı uygular.
/// Senaryo bitince rota sağlıklıdır (200, gecikmesiz).
/// </summary>
public sealed class FaultBook
{
    private readonly ConcurrentDictionary<string, Queue<FaultStep>> _plans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CallRecord>> _calls = new(StringComparer.OrdinalIgnoreCase);

    public void SetPlan(string key, IEnumerable<FaultStep> steps)
    {
        var queue = new Queue<FaultStep>();
        foreach (var step in steps)
        {
            for (var i = 0; i < Math.Max(1, step.Times); i++)
            {
                queue.Enqueue(step with { Times = 1 });
            }
        }

        _plans[key] = queue;
    }

    public void Reset()
    {
        _plans.Clear();
        _calls.Clear();
    }

    /// <summary>Çağrıyı kaydeder ve uygulanacak adımı verir.</summary>
    public FaultStep Next(string key, CallRecord record)
    {
        _calls.GetOrAdd(key, _ => new ConcurrentQueue<CallRecord>()).Enqueue(record);
        var queue = _plans.GetValueOrDefault(key);
        if (queue is null)
        {
            return new FaultStep();
        }

        lock (queue)
        {
            return queue.TryDequeue(out var step) ? step : new FaultStep();
        }
    }

    public IReadOnlyList<CallRecord> Calls(string key) =>
        _calls.TryGetValue(key, out var calls) ? calls.ToArray() : [];
}
