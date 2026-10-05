using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>Kullanıcı geri çağrılarını <see cref="Outcome{TResult}"/> üreten biçime çeviren yardımcılar.</summary>
internal static class OutcomeInvoker
{
    /// <summary>Geri çağrıyı çalıştırır; istisnayı yakalayıp sonuç olarak döner. Eşzamanlı tamamlanan çağrıda tahsis yapmaz.</summary>
    public static ValueTask<Outcome<TResult>> Invoke<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext context)
    {
        ValueTask<TResult> pending;
        try
        {
            pending = callback(context);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }

        return pending.IsCompletedSuccessfully
            ? new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromResult(pending.Result))
            : AwaitAsync(pending);
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<Outcome<TResult>> AwaitAsync<TResult>(ValueTask<TResult> pending)
    {
        try
        {
            return Outcome<TResult>.FromResult(await pending.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }

    /// <summary>Durumlu (TState) geri çağrıyı çalıştırır; closure gerekmez.</summary>
    public static ValueTask<Outcome<TResult>> Invoke<TResult, TState>(
        Func<AegisContext, TState, ValueTask<TResult>> callback, AegisContext context, TState state)
    {
        ValueTask<TResult> pending;
        try
        {
            pending = callback(context, state);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }

        return pending.IsCompletedSuccessfully
            ? new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromResult(pending.Result))
            : AwaitAsync(pending);
    }

    /// <summary>Dönüş değeri olmayan geri çağrıyı çalıştırır (sonuç <c>true</c> olarak temsil edilir).</summary>
    public static ValueTask<Outcome<bool>> InvokeVoid<TState>(Func<AegisContext, TState, ValueTask> callback, AegisContext context, TState state)
    {
        ValueTask pending;
        try
        {
            pending = callback(context, state);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<bool>>(Outcome<bool>.FromException(ex));
        }

        return pending.IsCompletedSuccessfully
            ? new ValueTask<Outcome<bool>>(Outcome<bool>.FromResult(true))
            : AwaitVoidAsync(pending);
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<Outcome<bool>> AwaitVoidAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
            return Outcome<bool>.FromResult(true);
        }
        catch (Exception ex)
        {
            return Outcome<bool>.FromException(ex);
        }
    }

    /// <summary>
    /// Kullanıcının <see cref="Outcome{TResult}"/> döndüren geri çağrısını korumalı çalıştırır: yine de fırlatırsa sonuca çevrilir.
    /// </summary>
    public static ValueTask<Outcome<TResult>> InvokeOutcome<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, AegisContext context, TState state)
    {
        ValueTask<Outcome<TResult>> pending;
        try
        {
            pending = callback(context, state);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }

        return pending.IsCompletedSuccessfully ? pending : AwaitOutcomeAsync(pending);
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<Outcome<TResult>> AwaitOutcomeAsync<TResult>(ValueTask<Outcome<TResult>> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }

    /// <summary><see cref="Outcome{TResult}"/> üreten bir akışı, istisna fırlatan klasik akışa çevirir.</summary>
    public static ValueTask<TResult> Unwrap<TResult>(ValueTask<Outcome<TResult>> pending) =>
        pending.IsCompletedSuccessfully
            ? new ValueTask<TResult>(pending.Result.GetResultOrThrow())
            : UnwrapAsync(pending);

    private static async ValueTask<TResult> UnwrapAsync<TResult>(ValueTask<Outcome<TResult>> pending) =>
        (await pending.ConfigureAwait(false)).GetResultOrThrow();
}
