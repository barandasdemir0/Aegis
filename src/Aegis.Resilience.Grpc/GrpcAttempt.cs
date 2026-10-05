using System.Globalization;
using Grpc.Core;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc;

/// <summary>Tek bir gRPC denemesinin ortak kuralları (çağrı türünden bağımsız).</summary>
internal static class GrpcAttempt
{
    /// <summary>
    /// Denemenin çağrı seçenekleri: bağlamın iptali ve yeniden denemelerde <see cref="AegisGrpcMetadata.PreviousAttempts"/>
    /// başlığı. Deadline özgün haliyle kalır (sunucu da aynı deadline'ı görür).
    /// </summary>
    public static CallOptions Options(CallOptions original, CancellationToken token, int previousAttempts)
    {
        var options = original.WithCancellationToken(token);
        if (previousAttempts == 0)
        {
            return options;
        }

        var headers = new Metadata();
        if (original.Headers is { } existing)
        {
            foreach (var entry in existing)
            {
                if (!string.Equals(entry.Key, AegisGrpcMetadata.PreviousAttempts, StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add(entry);
                }
            }
        }

        headers.Add(AegisGrpcMetadata.PreviousAttempts, previousAttempts.ToString(CultureInfo.InvariantCulture));
        return options.WithHeaders(headers);
    }

    /// <summary>
    /// Başarısız denemenin istisnasını boru hattına uygun biçime çevirir. Grpc.Net.Client iptali
    /// <c>RpcException(Cancelled)</c> olarak fırlatır; bağlamın iptalinden geliyorsa <see cref="OperationCanceledException"/>'a
    /// çevrilir ki deneme zaman aşımı ve çağıran iptali tanınsın. Sunucu pushback'i Retry stratejisine iletilir.
    /// </summary>
    public static Exception Translate(RpcException exception, AegisContext context)
    {
        if (exception.StatusCode == StatusCode.Cancelled && context.CancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(exception.Message, exception, context.CancellationToken);
        }

        if (AegisGrpcTransientErrors.TryGetPushback(exception.Trailers, out var delay) && delay is { } wait)
        {
            context.Properties[AegisContextKeys.RetryAfterDelay] = wait;
        }

        return exception;
    }
}
