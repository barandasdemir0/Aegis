using System.Collections.Concurrent;
using System.Diagnostics;
using Aegis.Resilience.Core.Pipeline;
using Aegis.TortureTests.Harness;

namespace Aegis.TortureTests.Scenarios;

/// <summary>
/// İz (trace) açıkken fırtına: 32 iş parçacığı × 300 çağrı, her çağrının kendi dış span'ı var; geri çağrı eşzamansız sınırda alt span
/// açar; bir kısmı fırlatır, bir kısmı zaman aşımına düşer, bir kısmı rastgele anda iptal edilir. Aegis'e özeldir (Polly ve
/// Microsoft iz üretmez).
/// Değişmezler: her başlayan Aegis span'ı kapanır (sızıntı yok); her alt span KENDİ çağrısının Aegis span'ının çocuğudur (çapraz
/// ebeveyn karışması yok); çağrı bitince çağıranın Activity.Current'ı yerindedir (bağlam sızıntısı yok).
/// </summary>
public sealed class TracingStormScenario : TortureScenario
{
    private const string SpanName = "Aegis torture-trace";

    // Çağrı boru hattına GİRMEDEN iptal edilmişse strateji çalışmaz ve span açılmaz: bilinçli davranış (bu yüzden tam sayılır).
    private static int PreCancelledCalls;

    public override string Name => "İz açıkken fırtına (span sızıntısı, ebeveyn karışması)";

    public override string Description =>
        "Tüm örnekleme açık; 32×300 çağrı (hata, zaman aşımı, rastgele iptal, eşzamansız alt span). Her Aegis span'ı kapanmalı, her alt span " +
        "kendi çağrısının span'ının çocuğu olmalı, çağıranın Activity.Current'ı çağrıdan sonra yerinde olmalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis];

    public override async Task RunAsync(Library library, Random random)
    {
        using var outerSource = new ActivitySource("Aegis.Torture.Outer");
        var started = 0;
        Volatile.Write(ref PreCancelledCalls, 0);
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Aegis" or "Aegis.Torture.Outer",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == SpanName)
                {
                    Interlocked.Increment(ref started);
                }
            },
            ActivityStopped = activity =>
            {
                if (activity.OperationName == SpanName)
                {
                    stopped.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        using var pipeline = new AegisPipelineBuilder("torture-trace")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.UseJitter = false; })
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        var seeds = Enumerable.Range(0, 32).Select(_ => random.Next()).ToArray();
        await Task.WhenAll(seeds.Select(seed => Task.Run(() => WorkerAsync(pipeline, outerSource, new Random(seed))))).ConfigureAwait(false);

        // Başlayan her span kapandı mı (kısa bir yerleşme süresiyle: kapanış zaman aşımı/iptal devamında olabilir).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref started) != stopped.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        var expected = (32 * 300) - Volatile.Read(ref PreCancelledCalls);
        Invariant.That(started == expected, $"{started} Aegis span'ı başladı ({expected} bekleniyordu: {32 * 300} çağrı, {Volatile.Read(ref PreCancelledCalls)} tanesi boru hattından önce iptal edildi)");
        Invariant.That(stopped.Count == started, $"{started - stopped.Count} Aegis span'ı kapanmadı (sızıntı)");
        Invariant.That(stopped.Select(a => a.Id).Distinct().Count() == stopped.Count, "iki span aynı kimliği taşıyor");
    }

    private static async Task WorkerAsync(Aegis.Resilience.Core.Abstractions.IAegisPipeline pipeline, ActivitySource outerSource, Random random)
    {
        for (var i = 0; i < 300; i++)
        {
            using var outer = outerSource.StartActivity("istek");
            Invariant.That(outer is not null && ReferenceEquals(Activity.Current, outer), "dış span başlatılamadı");
            var mode = random.Next(100);
            using var cts = new CancellationTokenSource();
            // Belirlenimci iptal (zamanlayıcı yarışı yok): %5 çağrı boru hattına girmeden iptal edilmiş gelir (hiçbir strateji
            // çalışmaz, span açılmaz; tam sayılır); %5'inde iptal geri çağrının İÇİNDEN yapılır (yolda iptal: span her zaman var).
            if (mode >= 95)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                Interlocked.Increment(ref PreCancelledCalls);
            }
            var cancelFromInside = mode is >= 90 and < 95;

            string? childParentId = null;
            ActivityTraceId? childTrace = null;
            try
            {
                await pipeline.ExecuteAsync(async ct =>
                {
                    await Task.Yield();                                    // eşzamansız sınır
                    using var child = outerSource.StartActivity("alt-islem");
                    childParentId = child?.ParentId;
                    childTrace = child?.TraceId;
                    if (cancelFromInside)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                    }

                    if (mode < 20)
                    {
                        throw new InvalidOperationException("geçici");
                    }

                    if (mode < 35)
                    {
                        await Task.Delay(200, ct).ConfigureAwait(false);     // zaman aşımına düşer (50 ms)
                    }

                    return 1;
                }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException || Failures.IsTimeout(ex))
            {
            }

            // Çağıranın bağlamı yerinde: await sonrası (ve eşzamanlı kısım sonrası) Activity.Current dış span'dır.
            Invariant.That(ReferenceEquals(Activity.Current, outer), "çağrıdan sonra çağıranın Activity.Current'ı değişti (bağlam sızıntısı)");

            // Alt span, kendi çağrısının Aegis span'ının çocuğudur ve aynı iz kimliğini taşır (çapraz karışma yok).
            if (childParentId is not null)
            {
                Invariant.That(childTrace == outer!.TraceId, "alt span başka bir çağrının iz kimliğini taşıyor (çapraz karışma)");
                Invariant.That(childParentId != outer.Id, "alt span Aegis span'ını atlayıp doğrudan dış span'ın çocuğu olmuş");
            }
        }
    }
}
