using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Extensions.DependencyInjection.Aop;

/// <summary>
/// .NET BCL DispatchProxy kullanarak arayüz metotlarını belirlenen Aegis boru hattı ile sarmalayan AOP proxy sınıfı.
/// Task, Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt; ve senkron metotları sıfır yansıma maliyetiyle (önbelleklenmiş delegeler)
/// ve Sync-over-Async kilitlenmelerini önleyen güvenli yürütmeyle destekler.
/// </summary>
public class AegisDispatchProxy<TInterface> : DispatchProxy where TInterface : class
{
    private TInterface _target = default!;
    private IAegisPipelineRegistry _registry = default!;
    private string? _defaultPipelineName;

    private static readonly ConcurrentDictionary<MethodInfo, string?> _methodPipelineNameCache = new();
    private static readonly ConcurrentDictionary<MethodInfo, Func<AegisDispatchProxy<TInterface>, IAegisPipeline, MethodInfo, object?[]?, object?>> _invokerCache = new();

    // CA1000: Fabrika metodu bilinçli olarak generic tip üzerindedir; BCL'deki DispatchProxy.Create<T, TProxy>()
    // deseninin birebir karşılığıdır ve proxy tipi çağrı anında bilinmek zorundadır.
#pragma warning disable CA1000
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AegisAotMessages.DispatchProxy)]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AegisAotMessages.DispatchProxy)]
    public static TInterface Create(TInterface target, IAegisPipelineRegistry registry, string? defaultPipelineName = null)
#pragma warning restore CA1000
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(registry);

        object proxy = Create<TInterface, AegisDispatchProxy<TInterface>>();
        var aegisProxy = (AegisDispatchProxy<TInterface>)proxy;
        aegisProxy._target = target;
        aegisProxy._registry = registry;
        aegisProxy._defaultPipelineName = defaultPipelineName;
        return (TInterface)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            return null;
        }

        var pipelineName = _methodPipelineNameCache.GetOrAdd(targetMethod, static m =>
        {
            var attr = m.GetCustomAttribute<AegisPolicyAttribute>()
                       ?? typeof(TInterface).GetCustomAttribute<AegisPolicyAttribute>();
            return attr?.PipelineName;
        }) ?? _defaultPipelineName;

        if (pipelineName is null or "" || !_registry.TryGetPipeline(pipelineName, out var pipeline) || pipeline == null)
        {
            return InvokeTarget(targetMethod, _target, args);
        }

        var invoker = _invokerCache.GetOrAdd(targetMethod, CreateInvoker);
        return invoker(this, pipeline, targetMethod, args);
    }

    // Yalnızca AOT için işaretli Create yolundan erişilir (DispatchProxy zaten çalışma anı kod üretimi gerektirir).
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Create [RequiresDynamicCode] ile işaretli; bu metoda yalnızca oradan oluşturulan proxy üzerinden ulaşılır.")]
    private static Func<AegisDispatchProxy<TInterface>, IAegisPipeline, MethodInfo, object?[]?, object?> CreateInvoker(MethodInfo targetMethod)
    {
        var returnType = targetMethod.ReturnType;

        // 1. Task (void async)
        if (returnType == typeof(Task))
        {
            return static (proxy, pipeline, method, args) => proxy.InvokeTaskAsync(pipeline, method, args);
        }

        // 2. Task<TResult>
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var helperMethod = typeof(AegisDispatchProxy<TInterface>)
                .GetMethod(nameof(InvokeGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(resultType);

            return (proxy, pipeline, method, args) => helperMethod.Invoke(proxy, new object?[] { pipeline, method, args });
        }

        // 3. ValueTask (non-generic async)
        if (returnType == typeof(ValueTask))
        {
            // CA2012: ValueTask burada tüketilmez; DispatchProxy sözleşmesi gereği çağırana
            // dönüş değeri olarak (kutulanmış şekilde) iletilir ve yalnızca çağıran tarafından bir kez beklenir.
#pragma warning disable CA2012
            return static (proxy, pipeline, method, args) => proxy.InvokeValueTaskAsync(pipeline, method, args);
#pragma warning restore CA2012
        }

        // 4. ValueTask<TResult>
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var helperMethod = typeof(AegisDispatchProxy<TInterface>)
                .GetMethod(nameof(InvokeGenericValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(resultType);

            return (proxy, pipeline, method, args) => helperMethod.Invoke(proxy, new object?[] { pipeline, method, args });
        }

        // 5. Senkron çağrılar: hedef çağıranın iş parçacığında çalışır (iş parçacığı havuzuna atlama yok). Boru hattının senkron
        // yolu eşzamanlı tamamlanmayı bloklamadan alır; içerideki beklemeler yakalanan bağlama dönmez (kilitlenme olmaz).
        return static (proxy, pipeline, method, args) =>
            pipeline.Execute(static (_, s) => InvokeTarget(s.method, s.target, s.args), (method, target: (object)proxy._target, args), CreateContext(pipeline, args));
    }

    /// <summary>
    /// Metot argümanları arasında bir <see cref="CancellationToken"/> varsa boru hattı bağlamına taşır;
    /// böylece Timeout/Retry stratejileri çağıranın iptalini onurlandırır (AEGIS-117).
    /// </summary>
    private static AegisContext CreateContext(IAegisPipeline pipeline, object?[]? args)
    {
        var cancellationToken = CancellationToken.None;

        if (args != null)
        {
            foreach (var arg in args)
            {
                if (arg is CancellationToken token)
                {
                    cancellationToken = token;
                    break;
                }
            }
        }

        return new AegisContext(cancellationToken, pipeline.Name);
    }

    /// <summary>
    /// Hedef metodu çağırır ve yansımanın (reflection) ürettiği <see cref="TargetInvocationException"/> sarmalayıcısını
    /// kaldırarak orijinal istisnayı yığın izini koruyarak yükseltir.
    /// Aksi halde kullanıcı tanımlı <c>ShouldHandle</c> koşulları hiçbir zaman eşleşmezdi (AEGIS-118).
    /// </summary>
    private static object? InvokeTarget(MethodInfo method, object target, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task InvokeTaskAsync(IAegisPipeline pipeline, MethodInfo targetMethod, object?[]? args)
    {
        await pipeline.ExecuteAsync(async _ =>
        {
            var task = (Task)InvokeTarget(targetMethod, _target, args)!;
            await task.ConfigureAwait(false);
        }, CreateContext(pipeline, args)).ConfigureAwait(false);
    }

    private async Task<TResult> InvokeGenericTaskAsync<TResult>(IAegisPipeline pipeline, MethodInfo targetMethod, object?[]? args)
    {
        return await pipeline.ExecuteAsync(async _ =>
        {
            var task = (Task<TResult>)InvokeTarget(targetMethod, _target, args)!;
            return await task.ConfigureAwait(false);
        }, CreateContext(pipeline, args)).ConfigureAwait(false);
    }

    private async ValueTask InvokeValueTaskAsync(IAegisPipeline pipeline, MethodInfo targetMethod, object?[]? args)
    {
        await pipeline.ExecuteAsync(async _ =>
        {
            var valueTask = (ValueTask)InvokeTarget(targetMethod, _target, args)!;
            await valueTask.ConfigureAwait(false);
        }, CreateContext(pipeline, args)).ConfigureAwait(false);
    }

    private async ValueTask<TResult> InvokeGenericValueTaskAsync<TResult>(IAegisPipeline pipeline, MethodInfo targetMethod, object?[]? args)
    {
        return await pipeline.ExecuteAsync(async _ =>
        {
            var valueTask = (ValueTask<TResult>)InvokeTarget(targetMethod, _target, args)!;
            return await valueTask.ConfigureAwait(false);
        }, CreateContext(pipeline, args)).ConfigureAwait(false);
    }
}
