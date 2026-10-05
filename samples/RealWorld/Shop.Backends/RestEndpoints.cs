namespace Shop.Backends;

/// <summary>
/// Sahte bağımlılıkların REST uç noktaları. Her rota kendi senaryo anahtarıyla <see cref="FaultBook"/>'tan sıradaki davranışı alır.
/// <c>/_faults/{key}</c> senaryoyu kurar, <c>/_calls/{key}</c> gelen çağrıları döner, <c>DELETE /_faults</c> sıfırlar.
/// </summary>
public static class RestEndpoints
{
    private static int _fxVersion;

    public static void Map(WebApplication app, string instance)
    {
        app.MapPost("/_faults/{key}", (string key, FaultStep[] steps, FaultBook faults) => { faults.SetPlan(key, steps); return Results.NoContent(); });
        app.MapDelete("/_faults", (FaultBook faults) => { faults.Reset(); return Results.NoContent(); });
        app.MapGet("/_calls/{key}", (string key, FaultBook faults) => faults.Calls(key));

        app.MapPost("/payment/charge", (HttpContext ctx) => Handle(ctx, "payment", () => new { paymentId = Guid.NewGuid().ToString("N"), servedBy = instance }));
        app.MapGet("/pricing/{sku}", (HttpContext ctx, string sku) => Handle(ctx, "pricing", () => new { sku, price = 99.90m, servedBy = instance }));
        app.MapGet("/catalog/{sku}", (HttpContext ctx, string sku) => Handle(ctx, "catalog", () => new { sku, name = $"Ürün {sku}", servedBy = instance }));
        app.MapGet("/recommend", (HttpContext ctx) => Handle(ctx, "recommend", () => new { variant = instance }));
        app.MapGet("/products/{sku}", (HttpContext ctx, string sku) => Handle(ctx, "products", () => new { sku, name = $"Ürün {sku}", stock = 42 }));
        app.MapGet("/fx/{symbol}", (HttpContext ctx, string symbol) => Handle(ctx, "fx", () => new { symbol, rate = 32.5m, version = Interlocked.Increment(ref _fxVersion) }));
        app.MapGet("/partners/{tenant}", (HttpContext ctx, string tenant) => Handle(ctx, "partners", () => new { tenant, ok = true }));
        app.MapGet("/legacy/{id}", (HttpContext ctx, string id) => Handle(ctx, "legacy", () => new { id, legacy = true }));
        app.MapGet("/slow", (HttpContext ctx) => Handle(ctx, "slow", () => new { ok = true }));
        app.MapGet("/status", (HttpContext ctx) => Handle(ctx, "status", () => new { ok = true, servedBy = instance }));
        app.MapGet("/tenant/{tenant}", (HttpContext ctx, string tenant) => Handle(ctx, $"tenant-{tenant}", () => new { tenant }));
        app.MapGet("/reload", (HttpContext ctx) => Handle(ctx, "reload", () => new { ok = true }));

        // Genel servis: her ad kendi senaryo anahtarıdır (kargo, depo, sms...). Gövdeli isteklerde okunan bayt sayısı döner.
        app.MapMethods("/svc/{name}", ["GET", "POST", "PUT", "DELETE"], (HttpContext ctx, string name) =>
            Handle(ctx, name, () => new { name, servedBy = instance, method = ctx.Request.Method }));
    }

    // Yanıtı kendisi yazar: yalnızca HttpContext alan lambda RequestDelegate sayılır ve dönen IResult atılırdı (ASP0016).
    private static async Task Handle(HttpContext ctx, string key, Func<object> body) =>
        await (await DecideAsync(ctx, key, body)).ExecuteAsync(ctx);

    private static async Task<IResult> DecideAsync(HttpContext ctx, string key, Func<object> body)
    {
        string? text = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            text = await reader.ReadToEndAsync(ctx.RequestAborted);
        }

        var step = ctx.RequestServices.GetRequiredService<FaultBook>().Next(key, new CallRecord(
            ctx.Request.Headers["Idempotency-Key"].FirstOrDefault(), null, text, DateTimeOffset.UtcNow,
            ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault()));

        if (step.DelayMs > 0)
        {
            try
            {
                await Task.Delay(step.DelayMs, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return Results.Empty; // istemci vazgeçti (zaman aşımı / kaybeden hedging denemesi)
            }
        }

        if (step.RetryAfter is { } retryAfter)
        {
            ctx.Response.Headers.RetryAfter = retryAfter;
        }

        return step.Status == 200 ? Results.Ok(body()) : Results.StatusCode(step.Status);
    }
}
