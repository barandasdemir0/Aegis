using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;

namespace Shop.Api;

/// <summary>
/// Siparişleri SQL Server'a yazar. Her deneme yeni bağlantı ve işlemle çalışır; "orders-db" boru hattı geçici SQL hatalarını
/// (ör. 1205 kilitlenme kurbanı) <c>HandleSqlTransientErrors</c> ile yeniden dener.
/// </summary>
public sealed class OrderStore(ShopSettings settings, IAegisPipelineRegistry registry)
{
    public const string DatabaseName = "ShopRealWorld";

    private readonly IAegisPipeline _pipeline = registry.GetPipeline("orders-db");

    public string ConnectionString { get; } = new SqlConnectionStringBuilder(settings.Sql) { InitialCatalog = DatabaseName }.ConnectionString;

    /// <summary>Veritabanı ve tablolar yoksa oluşturulur (uygulama açılışı).</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using (var master = new SqlConnection(settings.Sql))
        {
            await master.OpenAsync(cancellationToken);
            await Exec(master, $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];", cancellationToken);
        }

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync(cancellationToken);
        await Exec(db, """
            IF OBJECT_ID('Stock') IS NULL CREATE TABLE Stock (Sku NVARCHAR(64) PRIMARY KEY, Qty INT NOT NULL);
            IF OBJECT_ID('Orders') IS NULL CREATE TABLE Orders (Id NVARCHAR(64) PRIMARY KEY, Sku NVARCHAR(64), Qty INT, Customer NVARCHAR(128), PaymentId NVARCHAR(64));
            IF OBJECT_ID('Counters') IS NULL CREATE TABLE Counters (Id INT PRIMARY KEY, N INT NOT NULL);
            IF NOT EXISTS (SELECT 1 FROM Counters WHERE Id = 1) INSERT Counters VALUES (1, 0);
            """, cancellationToken);
    }

    /// <summary>Siparişi yazar: stok düşülür, sipariş eklenir, sayaç artar (tek işlem). Dönen değer deneme sayısıdır.</summary>
    public async Task<int> SaveAsync(string orderId, OrderRequest order, string paymentId, CancellationToken cancellationToken)
    {
        var attempts = 0;
        await _pipeline.ExecuteAsync(async ctx =>
        {
            Interlocked.Increment(ref attempts);
            await using var db = new SqlConnection(ConnectionString);
            await db.OpenAsync(ctx.CancellationToken);
            await Exec(db, "SET DEADLOCK_PRIORITY LOW;", ctx.CancellationToken); // kilitlenmede kurban bu bağlantı olsun
            await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ctx.CancellationToken);

            await Exec(db, tx, "IF NOT EXISTS (SELECT 1 FROM Stock WHERE Sku=@s) INSERT Stock VALUES (@s, 1000); UPDATE Stock SET Qty = Qty - @q WHERE Sku = @s;",
                ctx.CancellationToken, ("@s", order.Sku), ("@q", order.Quantity));
            await Exec(db, tx, "IF NOT EXISTS (SELECT 1 FROM Orders WHERE Id=@id) INSERT Orders VALUES (@id, @s, @q, @c, @p);",
                ctx.CancellationToken, ("@id", orderId), ("@s", order.Sku), ("@q", order.Quantity), ("@c", order.Customer), ("@p", paymentId));
            await Exec(db, tx, "UPDATE Counters SET N = N + 1 WHERE Id = 1;", ctx.CancellationToken);
            await tx.CommitAsync(ctx.CancellationToken);
        }, new AegisContext(cancellationToken) { OperationKey = "SaveOrder" });
        return attempts;
    }

    public async Task<int> CountOrdersAsync(string sku, CancellationToken cancellationToken)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM Orders WHERE Sku = @s", db);
        cmd.Parameters.AddWithValue("@s", sku);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Task Exec(SqlConnection db, string sql, CancellationToken ct) => Exec(db, null, sql, ct);

    private static async Task Exec(SqlConnection db, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, db, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
