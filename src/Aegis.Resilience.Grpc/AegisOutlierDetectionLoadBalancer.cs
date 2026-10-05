#if NET
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Sunucu ayıklamalı döngüsel (round-robin) yük dengeleyici. Hazır uç noktalar arasında sırayla seçer, ayıklanmışları atlar; hepsi
/// ayıklanmışsa tümünü kullanır (Envoy "panik modu"). Seçilen uç noktayı isteğe yazar ki
/// <see cref="AegisOutlierStatusHandler"/> çağrının gRPC durumunu (trailer) o uç noktaya kaydedebilsin; bağlantı hataları burada
/// kaydedilir. Grpc.Net.Client'ın yerleşik çağrı izleyicisi yalnızca bağlantı hatasını görür, <c>grpc-status</c>'u görmez.
/// </summary>
internal sealed class AegisOutlierDetectionLoadBalancer(IChannelControlHelper controller, ILoggerFactory loggerFactory, AegisOutlierDetector detector)
    : SubchannelsLoadBalancer(controller, loggerFactory)
{
    protected override SubchannelPicker CreatePicker(IReadOnlyList<Subchannel> readySubchannels) =>
        new Picker(readySubchannels, detector);

    private sealed class Picker(IReadOnlyList<Subchannel> subchannels, AegisOutlierDetector detector) : SubchannelPicker
    {
        private int _next = -1;

        public override PickResult Pick(PickContext context)
        {
            var subchannel = Select();
            var endpoint = subchannel.CurrentAddress is { } address ? AegisOutlierEndpoints.KeyOf(address) : null;
            if (endpoint is null)
            {
                return PickResult.ForSubchannel(subchannel);
            }

            context.Request?.Options.Set(AegisOutlierEndpoints.RequestKey, new AegisOutlierEndpoint(endpoint, subchannels.Count));
            return PickResult.ForSubchannel(subchannel, new ConnectionTracker(detector, endpoint, subchannels.Count));
        }

        // Ayıklanmamış ilk uç nokta (sıradan başlayarak); hepsi ayıklanmışsa sıradaki (panik modu).
        private Subchannel Select()
        {
            var start = (int)((uint)Interlocked.Increment(ref _next) % (uint)subchannels.Count);
            for (var i = 0; i < subchannels.Count; i++)
            {
                var candidate = subchannels[(start + i) % subchannels.Count];
                if (candidate.CurrentAddress is not { } address || !detector.IsEjected(AegisOutlierEndpoints.KeyOf(address)))
                {
                    return candidate;
                }
            }

            return subchannels[start];
        }
    }

    // Bağlantı hatası (yanıt başlıkları hiç gelmedi) uç noktanın hatasıdır; başarı ise gRPC durumuna göre işleyicide kaydedilir.
    private sealed class ConnectionTracker(AegisOutlierDetector detector, string endpoint, int knownEndpoints) : ISubchannelCallTracker
    {
        public void Start()
        {
        }

        public void Complete(CompletionContext context)
        {
            if (context.Error is not null and not OperationCanceledException)
            {
                detector.RecordFailure(endpoint, knownEndpoints);
            }
        }
    }
}

/// <summary>Sunucu ayıklamalı yük dengeleyici fabrikası (ServiceConfig adı: <see cref="Name"/>).</summary>
public sealed class AegisOutlierDetectionLoadBalancerFactory(AegisOutlierDetector detector) : LoadBalancerFactory
{
    /// <summary>ServiceConfig'teki yük dengeleme adı.</summary>
    public const string PolicyName = "aegis_outlier_detection";

    /// <inheritdoc />
    public override string Name => PolicyName;

    /// <inheritdoc />
    public override LoadBalancer Create(LoadBalancerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AegisOutlierDetectionLoadBalancer(options.Controller, options.LoggerFactory, detector);
    }
}
#endif
