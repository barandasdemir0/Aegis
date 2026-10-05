using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Tests;

/// <summary>
/// 1.0.9 hızlı yol (AegisStrategy + Outcome) güvenceleri: sıfır tahsis, istisnasız katman geçişi,
/// özel IAegisStrategy uyumluluğu ve yığın izinin korunması.
/// </summary>
public class OutcomeFastPathTests
{
    // Kullanıcının kendi yazdığı, AegisStrategy'den TÜREMEYEN klasik strateji: iç katmanın istisnasını yakalar.
    private sealed class CatchingCustomStrategy : IAegisStrategy
    {
        public string Name => "Custom";
        public int Caught;

        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> next, AegisContext context)
        {
            try
            {
                return await next(context);
            }
            catch (InvalidOperationException)
            {
                Caught++;
                return default!;
            }
        }
    }

    [Fact]
    public async Task CustomLegacyStrategy_MixedWithBuiltIns_StillSeesInnerExceptionsAsThrown()
    {
        var custom = new CatchingCustomStrategy();
        var attempts = 0;
        using var pipeline = new AegisPipelineBuilder("karisik")
            .AddTimeout(TimeSpan.FromSeconds(5))
            .AddStrategy(custom)                                                     // eski yol
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; })   // hızlı yol
            .Build();

        var result = await pipeline.ExecuteAsync<int>(_ => { attempts++; throw new InvalidOperationException("iç hata"); });

        Assert.Equal(0, result);        // özel strateji yakaladı ve default döndü
        Assert.Equal(1, custom.Caught);
        Assert.Equal(3, attempts);      // içteki Retry 3 deneme yaptı, sonra hata özel stratejiye FIRLATILMIŞ olarak ulaştı
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<int> ThrowingUserMethod() => throw new FormatException("kullanıcı hatası");

    [Fact]
    public async Task UserException_CarriedAsOutcome_IsRethrownWithOriginalStackTrace()
    {
        using var pipeline = new AegisPipelineBuilder("yigin")
            .AddTimeout(TimeSpan.FromSeconds(5))
            .AddCircuitBreaker(o => o.MinimumThroughput = 100)
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; o.ShouldHandle = _ => false; })
            .Build();

        var ex = await Assert.ThrowsAsync<FormatException>(async () => await pipeline.ExecuteAsync(_ => ThrowingUserMethod()));

        Assert.Equal("kullanıcı hatası", ex.Message);
        Assert.Contains(nameof(ThrowingUserMethod), ex.StackTrace); // özgün fırlatma yeri korunur
    }

    [Fact]
    public async Task OpenCircuitRejection_TypeAndMessageUnchanged()
    {
        using var pipeline = new AegisPipelineBuilder("acik-devre")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromMinutes(5); })
            .Build();

        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException()));
        var ex = await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Assert.Contains("acik-devre", ex.Message);
        Assert.Contains("istek iletilmedi", ex.Message);
        Assert.NotNull(ex.StackTrace); // en dışta bir kez fırlatıldı
    }

    [Fact]
    public async Task SuccessPath_StandardChain_AllocatesNothingPerCall()
    {
        using var pipeline = new AegisPipelineBuilder("sifir-tahsis")
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; })
            .AddCircuitBreaker(o => o.MinimumThroughput = 100)
            .AddConcurrencyLimiter(100)
            .AddTimeout(TimeSpan.FromSeconds(10))
            .Build();

        static ValueTask<int> Work(AegisContext _) => ValueTask.FromResult(1);

        for (var i = 0; i < 2_000; i++) // ısınma: havuzlar dolar, JIT tamamlanır
        {
            await pipeline.ExecuteAsync(Work);
        }

        const int calls = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            _ = SynchronousResult.Of(pipeline.ExecuteAsync(Work));
        }
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;

        // Eskiden bu zincir çağrı başına ~400-850 B ayırıyordu (katman başına closure + Timeout CTS).
        Assert.True(perCall < 8, $"Başarı yolunda çağrı başına {perCall:F1} bayt ayrıldı");
    }
}
