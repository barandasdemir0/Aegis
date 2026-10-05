using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Çoklu veri merkezi / uç nokta arasında spekülatif paralel istekler göndererek kuyruk gecikmesini (tail latency)
/// minimuma indiren Multi-Endpoint Hedging DelegatingHandler.
/// <para>
/// Her deneme kendi iptal jetonuna sahiptir; kazanan yanıtın akışı asla iptal edilmez, kaybeden ve terk edilen
/// denemeler ise iptal edilip yanıtları dispose edilerek bağlantı havuzunun tükenmesi engellenir (AEGIS-114).
/// </para>
/// </summary>
public sealed class MultiEndpointHedgingHandler : AegisDelegatingHandler
{
    private readonly MultiEndpointHedgingOptions _options;

    protected override bool RunsAttemptsConcurrently => true;

    public MultiEndpointHedgingHandler(MultiEndpointHedgingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private sealed class HedgedCall
    {
        public required Task<HttpResponseMessage> Task { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    protected override async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoints = _options.EndpointsProvider?.Invoke(request) ?? _options.Endpoints;
        if (endpoints == null || endpoints.Count <= 1)
        {
            return await innerSend(request, cancellationToken).ConfigureAwait(false);
        }

        // Yeniden gönderilmesi güvenli olmayan istek tek uç noktaya bir kez gider (AegisStandardHedgingHandler ile aynı kural).
        var resendSafe = _options.AllowNonIdempotentHedging || HttpResilienceExecutor.IsIdempotent(request);
        var attemptsToMake = resendSafe ? Math.Min(1 + Math.Max(0, _options.MaxHedgedAttempts), endpoints.Count) : 1;
        var pending = new List<HedgedCall>(attemptsToMake);
        var outcomes = new FailedOutcomes();
        var responseHandedToCaller = false;
        try
        {
            for (var i = 0; i < attemptsToMake; i++)
            {
                pending.Add(StartCall(request, endpoints[i], innerSend, cancellationToken));

                var isLastAttempt = i == attemptsToMake - 1;
                if (!isLastAttempt && _options.HedgingDelay > TimeSpan.Zero &&
                    await WaitForEarlyWinnerAsync(pending, outcomes, cancellationToken).ConfigureAwait(false) is { } earlyWinner)
                {
                    responseHandedToCaller = true;
                    return earlyWinner;
                }
            }

            if (await AwaitRemainingAsync(pending, outcomes, cancellationToken).ConfigureAwait(false) is { } winner)
            {
                responseHandedToCaller = true;
                return winner;
            }

            var finalResponse = SelectFinalResponse(outcomes, cancellationToken);
            responseHandedToCaller = true;
            return finalResponse;
        }
        finally
        {
            // Kaybeden/terk edilen denemeleri iptal et; yanıtlarını ve kaynaklarını serbest bırak.
            // Kazanan denemenin jetonu iptal edilmez; içerik akışı çağıran tarafından okunmaya devam edebilir.
            foreach (var call in pending)
            {
                CancelSafely(call.Cts);
                DisposeWhenCompleted(call);
            }

            if (!responseHandedToCaller)
            {
                outcomes.LastResponse?.Dispose();
            }
        }
    }

    // Hedging gecikmesi boyunca bekler: bu sürede bir deneme kabul edilebilir yanıt verirse kazanan odur; gecikme dolarsa null
    // (bir sonraki deneme başlar).
    private async Task<HttpResponseMessage?> WaitForEarlyWinnerAsync(List<HedgedCall> pending, FailedOutcomes outcomes, CancellationToken cancellationToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Zamanlayıcı sınırını (int.MaxValue ms) aşan gecikme sonsuz sayılır; Task.Delay aksi halde çalışma anında fırlatır.
        var delayTask = Task.Delay(_options.HedgingDelay.TotalMilliseconds > int.MaxValue ? Timeout.InfiniteTimeSpan : _options.HedgingDelay, delayCts.Token);

        // DİKKAT: Task.WhenAny(tasks) bir Task<Task<HttpResponseMessage>> döner; dış WhenAny sonucu
        // doğrudan Task<HttpResponseMessage>'a cast EDİLEMEZ (AEGIS-115).
        var anyCallTask = Task.WhenAny(SnapshotTasks(pending));
        if (await Task.WhenAny(anyCallTask, delayTask).ConfigureAwait(false) != anyCallTask)
        {
            return null;
        }

        delayCts.Cancel();
        var completedTask = await anyCallTask.ConfigureAwait(false);
        return Settle(completedTask, Remove(pending, completedTask), outcomes, cancellationToken);
    }

    // Başlatılmış tüm denemeleri bitene kadar dinler; ilk kabul edilebilir yanıt kazanır.
    private async Task<HttpResponseMessage?> AwaitRemainingAsync(List<HedgedCall> pending, FailedOutcomes outcomes, CancellationToken cancellationToken)
    {
        while (pending.Count > 0)
        {
            var finishedTask = await Task.WhenAny(SnapshotTasks(pending)).ConfigureAwait(false);
            if (Settle(finishedTask, Remove(pending, finishedTask), outcomes, cancellationToken) is { } winner)
            {
                return winner;
            }
        }

        return null;
    }

    // Hiçbir deneme kabul edilebilir yanıt vermedi: son başarısız yanıt döner; yanıt yoksa son istisna fırlatılır.
    private static HttpResponseMessage SelectFinalResponse(FailedOutcomes outcomes, CancellationToken cancellationToken)
    {
        if (outcomes.LastResponse != null)
        {
            return outcomes.LastResponse;
        }

        if (outcomes.LastException != null)
        {
            throw outcomes.LastException;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Yedek denemeler (hedging) tamamlandı ancak geçerli bir sonuç üretilemedi.");
    }

    private static HedgedCall StartCall(HttpRequestMessage request, Uri endpoint, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return new HedgedCall
            {
                Task = SendCloneAsync(request, endpoint, innerSend, cts.Token),
                Cts = cts
            };
        }
        catch
        {
            cts.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendCloneAsync(HttpRequestMessage request, Uri endpoint, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        var clonedRequest = await CloneRequestAsync(request, endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            return await innerSend(clonedRequest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            clonedRequest.Dispose();
        }
    }

    private static List<Task<HttpResponseMessage>> SnapshotTasks(List<HedgedCall> calls)
    {
        var tasks = new List<Task<HttpResponseMessage>>(calls.Count);
        foreach (var call in calls)
        {
            tasks.Add(call.Task);
        }
        return tasks;
    }

    private static HedgedCall? Remove(List<HedgedCall> calls, Task<HttpResponseMessage> task)
    {
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i].Task != task)
            {
                continue;
            }

            var call = calls[i];
            calls.RemoveAt(i);
            return call;
        }

        return null;
    }

    /// <summary>
    /// Biten bir çağrıyı değerlendirir. Başarılıysa yanıtı döner (çağırana teslim edilir); değilse son başarısız yanıtı
    /// ya da istisnayı kaydeder ve null döner. Çağrının iptal kaynağı her durumda bırakılır.
    /// </summary>
    private HttpResponseMessage? Settle(Task<HttpResponseMessage> task, HedgedCall? call, FailedOutcomes outcomes, CancellationToken callerToken)
    {
        try
        {
            if (!task.IsCompletedSuccessfully)
            {
                outcomes.LastException = UnwrapException(task, callerToken) ?? outcomes.LastException;
                return null;
            }

            var response = task.Result;
            if (IsSuccessful(response))
            {
                return response;
            }

            outcomes.LastResponse?.Dispose();
            outcomes.LastResponse = response;
            return null;
        }
        finally
        {
            call?.Cts.Dispose();
        }
    }

    /// <summary>Başarısız denemelerin sonuncusu: hiçbiri başarılı olmazsa çağırana bu döner ya da fırlatılır.</summary>
    private sealed class FailedOutcomes
    {
        public HttpResponseMessage? LastResponse { get; set; }

        public Exception? LastException { get; set; }
    }

    /// <summary>
    /// Terk edilen denemenin istisnasını gözlemler, döndüğü yanıtı ve iptal jetonunu serbest bırakır.
    /// </summary>
    private static void DisposeWhenCompleted(HedgedCall call)
    {
        call.Task.ContinueWith(static (t, state) =>
        {
            if (t.IsCompletedSuccessfully)
            {
                t.Result.Dispose();
            }
            else
            {
                _ = t.Exception;
            }

            ((CancellationTokenSource)state!).Dispose();
        }, call.Cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void CancelSafely(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // İptal sırasındaki yarış durumlarını bastır
        }
    }

    /// <summary>
    /// Çağıranın kendi iptali dışındaki hataları raporlanmak üzere döner; kendi iptalinde null döner.
    /// </summary>
    private static Exception? UnwrapException(Task task, CancellationToken callerToken)
    {
        var exception = task.Exception?.GetBaseException();

        if (exception == null && task.IsCanceled)
        {
            exception = new OperationCanceledException();
        }

        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return null;
        }

        return exception;
    }

    private bool IsSuccessful(HttpResponseMessage response)
    {
        if (_options.ShouldHandleResult != null)
        {
            return !_options.ShouldHandleResult(response);
        }

        return !AegisHttpTransientErrors.IsTransient(response.StatusCode);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req, Uri targetBaseUri, CancellationToken ct)
    {
        var targetUri = BuildTargetUri(req.RequestUri, targetBaseUri);
        var clone = HttpRequestMessageCompat.CopyWithoutContent(req, targetUri);

        if (req.Content != null)
        {
            // Paralel denemelerin aynı akışı tüketmemesi için gövde belleğe alınır ve her denemeye kopyalanır
            await HttpResilienceExecutor.BufferContentAsync(req.Content, HttpRequestReplayHandler.DefaultMaxRequestBodySize, ct).ConfigureAwait(false);
            clone.Content = await HttpRequestReplayHandler.CopyContentAsync(req.Content, HttpRequestReplayHandler.DefaultMaxRequestBodySize, ct).ConfigureAwait(false);
        }

        return clone;
    }

    private static Uri BuildTargetUri(Uri? originalUri, Uri baseUri)
    {
        if (originalUri == null)
        {
            return baseUri;
        }

        if (!originalUri.IsAbsoluteUri)
        {
            return new Uri(baseUri, originalUri);
        }

        var builder = new UriBuilder(originalUri)
        {
            Scheme = baseUri.Scheme,
            Host = baseUri.Host,
            Port = baseUri.Port
        };

        return builder.Uri;
    }
}
