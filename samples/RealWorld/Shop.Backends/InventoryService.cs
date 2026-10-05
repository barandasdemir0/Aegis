using Grpc.Core;
using Shop.Contracts;

namespace Shop.Backends;

/// <summary>Stok servisi: her çağrı türü kendi senaryo anahtarını kullanır (grpc.reserve, grpc.watch, grpc.bulk, grpc.sync).</summary>
public sealed class InventoryService(FaultBook faults, BackendInfo info) : Inventory.InventoryBase
{
    public override async Task<ReserveReply> Reserve(ReserveRequest request, ServerCallContext context)
    {
        await ApplyAsync("grpc.reserve", context, request.Sku);
        return new ReserveReply { Sku = request.Sku, Reserved = request.Quantity, ServedBy = info.Instance };
    }

    public override async Task Watch(WatchRequest request, IServerStreamWriter<StockLevel> responseStream, ServerCallContext context)
    {
        var step = await ApplyAsync("grpc.watch", context, request.Sku, deferFailure: true);
        for (var i = 0; i < request.Count; i++)
        {
            if (step.GrpcStatus is { } status && i == step.AfterMessages)
            {
                throw Failure(step, status);
            }

            await responseStream.WriteAsync(new StockLevel { Sku = request.Sku, Level = 100 - i }, context.CancellationToken);
        }
    }

    public override async Task<BulkReply> BulkReserve(IAsyncStreamReader<ReserveRequest> requestStream, ServerCallContext context)
    {
        var total = 0;
        var items = 0;
        await foreach (var item in requestStream.ReadAllAsync(context.CancellationToken))
        {
            total += item.Quantity;
            items++;
        }

        // Senaryo, tüm istek akışı okunduktan sonra uygulanır: yeniden denemede tamponun baştan oynatıldığı doğrulanır.
        await ApplyAsync("grpc.bulk", context, $"{items}:{total}");
        return new BulkReply { Total = total, Items = items, ServedBy = info.Instance };
    }

    public override async Task Sync(IAsyncStreamReader<ReserveRequest> requestStream, IServerStreamWriter<ReserveReply> responseStream, ServerCallContext context)
    {
        var first = true;
        await foreach (var item in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (first)
            {
                await ApplyAsync("grpc.sync", context, item.Sku); // ilk mesajda hata: henüz yanıt yok, istemci yeniden oynatabilir
                first = false;
            }

            await responseStream.WriteAsync(new ReserveReply { Sku = item.Sku, Reserved = item.Quantity, ServedBy = info.Instance });
        }
    }

    private async Task<FaultStep> ApplyAsync(string key, ServerCallContext context, string body, bool deferFailure = false)
    {
        var step = faults.Next(key, new CallRecord(null, context.RequestHeaders.GetValue("grpc-previous-rpc-attempts"), body, DateTimeOffset.UtcNow));
        if (step.DelayMs > 0)
        {
            await Task.Delay(step.DelayMs, context.CancellationToken);
        }

        if (!deferFailure && step.GrpcStatus is { } status)
        {
            throw Failure(step, status);
        }

        return step;
    }

    private static RpcException Failure(FaultStep step, int status)
    {
        var trailers = new Metadata();
        if (step.PushbackMs is { } pushback)
        {
            trailers.Add("grpc-retry-pushback-ms", pushback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new RpcException(new Status((StatusCode)status, "senaryo hatası"), trailers);
    }
}

/// <summary>Bu arka uç örneğinin adı (ör. eu, us, stable, canary).</summary>
public sealed record BackendInfo(string Instance);
