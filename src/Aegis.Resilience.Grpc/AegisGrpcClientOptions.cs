namespace Aegis.Resilience.Grpc;

/// <summary><see cref="AegisGrpcClientInterceptor"/> seçenekleri.</summary>
public sealed class AegisGrpcClientOptions
{
    /// <summary>
    /// Aegis retleri <c>RpcException</c>'a çevrilsin mi (varsayılan: evet). gRPC çağıranları <c>RpcException</c> yakalar: açık devre
    /// → <c>Unavailable</c>, hız sınırı / eşzamanlılık reddi → <c>ResourceExhausted</c>, Aegis zaman aşımı → <c>DeadlineExceeded</c>
    /// (özgün istisna <c>Status.DebugException</c>'ta). False ise Aegis istisnaları olduğu gibi yükselir.
    /// </summary>
    public bool MapRejectionsToRpcException { get; set; } = true;

    /// <summary>
    /// İstemci ve çift yönlü akışta yeniden oynatma için çağrı başına tamponlanacak en fazla bayt (varsayılan 1 MB; Grpc.Net.Client
    /// <c>MaxRetryBufferPerCallSize</c> ile aynı). Gönderilen mesajlar bu sınıra kadar saklanır ve yeniden denemede baştan
    /// gönderilir; sınır aşılınca çağrı commit olur ve artık yeniden denenmez. 0: tamponlama yok, bu akışlar olduğu gibi geçer.
    /// </summary>
    public long MaxRetryBufferBytes { get; set; } = 1024 * 1024;
}
