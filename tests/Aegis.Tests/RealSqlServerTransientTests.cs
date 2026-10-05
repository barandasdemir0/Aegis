using Microsoft.Data.SqlClient;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Data.SqlClient;

namespace Aegis.Tests;

/// <summary>
/// Gerçek SQL Server'da gerçek kilitlenme (hata 1205): iki işlem kaynakları ters sırada kilitler, sunucu birini kurban seçer;
/// <c>HandleSqlTransientErrors</c> ile kurulan retry kurbanı yeniden çalıştırır ve iki işlem de tamamlanır.
/// Bağlantı dizesi AEGIS_TEST_SQL ortam değişkeninden okunur; yoksa test atlanır (kurulum: docs/TEST-INFRASTRUCTURE.md).
/// </summary>
public sealed class RealSqlServerTransientTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("AEGIS_TEST_SQL");

    [SkippableFact]
    public async Task RealDeadlockVictim_IsRetried_AndBothTransactionsComplete()
    {
        Skip.If(ConnectionString is null, "AEGIS_TEST_SQL tanımlı değil - test atlandı.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (tableA, tableB) = ($"AegisA_{suffix}", $"AegisB_{suffix}");
        await ExecuteAsync($"CREATE TABLE {tableA} (Id INT PRIMARY KEY, V INT); INSERT {tableA} VALUES (1, 0);" +
                           $"CREATE TABLE {tableB} (Id INT PRIMARY KEY, V INT); INSERT {tableB} VALUES (1, 0);");
        try
        {
            var observed = new List<int>();
            using var pipeline = new AegisPipelineBuilder("sql")
                .AddRetry(o =>
                {
                    o.MaxRetryAttempts = 5;
                    o.Delay = TimeSpan.FromMilliseconds(50);
                    o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleSqlTransientErrors();
                    o.OnRetry = args =>
                    {
                        lock (observed)
                        {
                            observed.Add(args.Exception is SqlException sql ? sql.Number : 0);
                        }

                        return default;
                    };
                })
                .Build();

            // Her iki işlem de ilk kilidini aldıktan sonra diğerini ister: klasik ters sıra kilitlenmesi.
            using var bothLocked = new Barrier(2);
            Task Transfer(string first, string second) => pipeline.ExecuteAsync(async ctx =>
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync(ctx.CancellationToken);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ctx.CancellationToken);
                await Command(connection, transaction, $"UPDATE {first} SET V = V + 1 WHERE Id = 1").ExecuteNonQueryAsync(ctx.CancellationToken);
                bothLocked.SignalAndWait(TimeSpan.FromSeconds(5)); // yalnızca ilk turda iki taraf da bekler; yeniden denemede zaman aşımıyla geçer
                await Command(connection, transaction, $"UPDATE {second} SET V = V + 1 WHERE Id = 1").ExecuteNonQueryAsync(ctx.CancellationToken);
                await transaction.CommitAsync(ctx.CancellationToken);
            }).AsTask();

            await Task.WhenAll(Task.Run(() => Transfer(tableA, tableB)), Task.Run(() => Transfer(tableB, tableA)));

            Assert.Contains(1205, observed);                                     // gerçek kilitlenme yaşandı ve yeniden denendi
            Assert.Equal(2, await ScalarAsync($"SELECT V FROM {tableA} WHERE Id = 1")); // iki işlem de eksiksiz tamamlandı
            Assert.Equal(2, await ScalarAsync($"SELECT V FROM {tableB} WHERE Id = 1"));
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE {tableA}; DROP TABLE {tableB};");
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = 30 };

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
