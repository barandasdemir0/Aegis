using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Hedging;
using Aegis.Resilience.Extensions.Http;
using Aegis.TortureTests.Harness;

namespace Aegis.TortureTests.Scenarios;

/// <summary>HTTP senaryolarının ortak kurulumu: sahte sunucu + kütüphanenin standart işleyicisi.</summary>
internal static class HttpClients
{
    public static HttpClient Standard(Library library, HttpMessageHandler server, TimeSpan? maxDelay = null) =>
        Create(server, builder =>
        {
            if (library == Library.Aegis)
            {
                builder.AddStandardAegisHandler(o =>
                {
                    o.Retry.Delay = TimeSpan.Zero;
                    o.Retry.UseJitter = false;
                    o.Retry.MaxDelay = maxDelay ?? o.Retry.MaxDelay;
                    o.AllowNonIdempotentRetry = true; // Microsoft varsayılanıyla aynı koşul: POST da yeniden denenir
                });
            }
            else
            {
                builder.AddStandardResilienceHandler(o =>
                {
                    o.Retry.Delay = TimeSpan.Zero;
                    o.Retry.UseJitter = false;
                    o.Retry.MaxDelay = maxDelay ?? o.Retry.MaxDelay;
                });
            }
        });

    public static HttpClient Hedging(Library library, HttpMessageHandler server) =>
        Create(server, builder =>
        {
            if (library == Library.Aegis)
            {
                builder.AddStandardAegisHedgingHandler(o =>
                {
                    o.MaxHedgedAttempts = 2;
                    o.HedgingDelay = TimeSpan.FromMilliseconds(5);
                });
            }
            else
            {
                builder.AddStandardHedgingHandler().Configure(o =>
                {
                    o.Hedging.MaxHedgedAttempts = 2;
                    o.Hedging.Delay = TimeSpan.FromMilliseconds(5);
                });
            }
        });

    private static HttpClient Create(HttpMessageHandler server, Action<IHttpClientBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("torture", c => c.BaseAddress = new Uri("https://api.local"));
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("torture");
    }
}

/// <summary>
/// Hedging fırtınası: kaybeden denemelerin yanıtları bırakılmalı. Değişmez: çağıran kendi yanıtını bıraktıktan sonra
/// sunucunun ürettiği TÜM yanıt gövdeleri dispose edilmiş olmalı (bağlantı/bellek sızıntısı yok).
/// </summary>
public sealed class HedgingResponseLeakScenario : TortureScenario
{
    public override string Name => "Hedging: kaybeden yanıt sızıntısı";

    public override string Description =>
        "Yanıtlar 0-30 ms rastgele gecikir, %25'i 503; 120 istek, 4'er eşzamanlı, 5 ms'de yedek deneme. Çağıran yanıtını bıraktıktan " +
        "sonra sunucunun ürettiği tüm yanıt gövdeleri dispose edilmiş olmalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly, Library.Microsoft];

    public override async Task RunAsync(Library library, Random random)
    {
        var seed = random.Next();
        var server = new ScriptedServer(async (call, _, ct) =>
        {
            var rnd = new Random(seed ^ call);
            await Task.Delay(rnd.Next(0, 30), ct).ConfigureAwait(false);
            var status = rnd.Next(4) == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new TrackedContent("govde") };
        });
        var tracked = new TrackingServer(server);
        Func<Task<HttpResponseMessage>> send = library == Library.Polly ? PollySender(tracked) : ClientSender(library, tracked);

        for (var batch = 0; batch < 30; batch++)
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                try
                {
                    using var response = await send().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || Failures.IsBrokenCircuit(ex))
                {
                    // %25 503 uç nokta devresini açabilir; red meşru bir sonuçtur, sızıntı denetimi etkilenmez
                }
            })).ConfigureAwait(false);
        }

        await tracked.WaitQuietAsync().ConfigureAwait(false);
        var leaked = tracked.Contents.Count(c => !c.IsDisposed);
        Invariant.That(leaked == 0, $"{leaked}/{tracked.Contents.Count} yanıt gövdesi dispose edilmedi (sızıntı)");
    }

    private static Func<Task<HttpResponseMessage>> ClientSender(Library library, HttpMessageHandler server)
    {
        var client = HttpClients.Hedging(library, server);
        return () => client.GetAsync("/x");
    }

    private static Func<Task<HttpResponseMessage>> PollySender(HttpMessageHandler server)
    {
        var invoker = new HttpMessageInvoker(server);
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddHedging(new HedgingStrategyOptions<HttpResponseMessage>
            {
                MaxHedgedAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(5),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is HttpRequestException ||
                    args.Outcome.Result is { IsSuccessStatusCode: false })
            })
            .Build();
        return () => pipeline.ExecuteAsync(ct => new ValueTask<HttpResponseMessage>(
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.local/x"), ct))).AsTask();
    }

    /// <summary>Sunucunun ürettiği yanıt gövdelerini kaydeder (hangi yol kullanılırsa kullanılsın).</summary>
    private sealed class TrackingServer(ScriptedServer inner) : DelegatingHandler(inner)
    {
        public System.Collections.Concurrent.ConcurrentBag<TrackedContent> Contents { get; } = [];

        private int _inFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Content is TrackedContent content)
                {
                    Contents.Add(content);
                }

                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async Task WaitQuietAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            await Task.Delay(200).ConfigureAwait(false); // kaybedenlerin bırakılması (arka plan) tamamlansın
        }
    }
}

/// <summary>Senkron <c>HttpClient.Send</c> dayanıklılıktan geçmeli (Aegis 1.3.0 öncesi atlıyordu).</summary>
public sealed class HttpSyncSendScenario : TortureScenario
{
    public override string Name => "HTTP senkron Send yeniden denenir";

    public override string Description => "Sunucu 503, 503, 200 döner; senkron HttpClient.Send çağrısı yeniden denenip 200 almalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Microsoft];

    public override Task RunAsync(Library library, Random random)
    {
        var server = new ScriptedServer((call, _, _) =>
            ScriptedServer.Respond(call % 3 == 0 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        using var client = HttpClients.Standard(library, server);

        for (var i = 0; i < 10; i++)
        {
            using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, "/x"));
            Invariant.That(response.StatusCode == HttpStatusCode.OK, $"senkron Send {response.StatusCode} döndü (yeniden denenmedi)");
        }

        Invariant.That(server.Calls == 30, $"sunucu {server.Calls} çağrı aldı (30 bekleniyordu)");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bozuk / saçma <c>Retry-After</c> başlıkları. Değişmez: istek yeniden denenip 200 almalı ve üst sınırı (1 sn) aşan bir
/// bekleme olmamalı (saçma değer çağrıyı kilitlememeli, istisna fırlatmamalı).
/// </summary>
public sealed class GarbageRetryAfterScenario : TortureScenario
{
    private static readonly string[] Values =
    [
        "-1", "abc", "", "1.5", "99999999999999999999", "2147483647", "9223372036854775807",
        "Fri, 31 Dec 9999 23:59:59 GMT", "Thu, 01 Jan 1970 00:00:00 GMT", "Mon, 32 Foo 2026 99:99:99 GMT"
    ];

    public override string Name => "Bozuk Retry-After başlıkları";

    public override string Description =>
        "429 yanıtında Retry-After: -1, abc, boş, 1.5, 20 haneli sayı, int/long üst sınırı, 9999 yılı, 1970, bozuk tarih. " +
        "Üst sınır 1 sn iken her istek 5 sn içinde 200 ile bitmeli; istisna olmamalı.";

    public override TimeSpan Budget => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Microsoft];

    public override async Task RunAsync(Library library, Random random)
    {
        foreach (var value in Values.OrderBy(_ => random.Next()))
        {
            var server = new ScriptedServer((call, _, _) =>
            {
                if (call > 1)
                {
                    return ScriptedServer.Respond(HttpStatusCode.OK);
                }

                var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("Retry-After", value);
                return Task.FromResult(response);
            });
            using var client = HttpClients.Standard(library, server, maxDelay: TimeSpan.FromSeconds(1));

            var watch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync("/x").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvariantViolationException($"Retry-After '{value}': {ex.GetType().Name}: {ex.Message}");
            }

            using (response)
            {
                Invariant.That(watch.Elapsed < TimeSpan.FromSeconds(5), $"Retry-After '{value}': {watch.Elapsed.TotalSeconds:F1} sn bekledi (üst sınır 1 sn)");
                Invariant.That(response.StatusCode == HttpStatusCode.OK, $"Retry-After '{value}': {response.StatusCode} (yeniden denenmedi)");
            }
        }
    }
}

/// <summary>
/// Geri sarılamayan (ileri okunur) gövdeli POST, ilk denemede 503 alır. Değişmez: sunucuya ulaşan HER gövde özgün
/// veriyle birebir aynı (yeniden denemede boş ya da kesik gövde = veri bozulması); sonuç ya doğru yeniden deneme (200) ya
/// da yeniden denememe (503 / HttpRequestException); başka istisna yok.
/// </summary>
public sealed class NonReplayableBodyScenario : TortureScenario
{
    public override string Name => "Geri sarılamayan gövdeli POST";

    public override string Description =>
        "64 KB ileri-okunur akış gövdeli POST (Idempotency-Key ile), ilk yanıt 503. Sunucuya giden her gövde özgün veriyle aynı " +
        "olmalı (boş/kesik yeniden gönderim = veri bozulması); sonuç 200, 503 ya da HttpRequestException olmalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Microsoft];

    public override async Task RunAsync(Library library, Random random)
    {
        var data = new byte[64 * 1024];
        random.NextBytes(data);
        var server = new ScriptedServer((call, _, _) =>
            ScriptedServer.Respond(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var client = HttpClients.Standard(library, server);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odeme") { Content = new StreamContent(new ForwardOnlyStream(data)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        try
        {
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            Invariant.That(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"beklenmeyen durum {response.StatusCode}");
        }
        catch (HttpRequestException)
        {
        }
        catch (Exception ex)
        {
            throw new InvariantViolationException($"yeniden gönderim {ex.GetType().Name} fırlattı: {ex.Message}");
        }

        foreach (var body in server.ReceivedBodies)
        {
            Invariant.That(body.AsSpan().SequenceEqual(data), $"sunucuya bozuk gövde ulaştı ({body.Length}/{data.Length} bayt)");
        }
    }
}
