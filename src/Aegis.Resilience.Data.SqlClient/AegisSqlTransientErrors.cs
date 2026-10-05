using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Data.SqlClient;

/// <summary>
/// SQL Server / Azure SQL geçici hatalarını tanır (Enterprise Library Transient Fault Handling "Topaz" ve
/// <c>TransientFaultHandling.Core</c> eşdeğeri). Hata numarası listesi EF Core'un
/// <c>SqlServerTransientExceptionDetector</c>'ı ile aynıdır (MIT); Topaz'ın eski listesinden günceldir.
/// <para>
/// Aegis farkı: istisnanın kendisiyle yetinmez, iç istisna zincirini de tarar. Böylece <c>DbUpdateException</c> gibi
/// ORM sarmalayıcılarının içindeki <see cref="SqlException"/> da tanınır.
/// </para>
/// </summary>
public static class AegisSqlTransientErrors
{
    /// <summary>İç istisna zincirinde en fazla bu kadar derine inilir (döngüsel zincire karşı güvenlik).</summary>
    private const int MaxInnerExceptionDepth = 8;

    /// <summary>İç istisnası <see cref="Win32Exception"/> olduğunda geçici sayılan el sıkışma hatası.</summary>
    private const int PreLoginHandshakeError = 203;

    private static readonly HashSet<int> TransientNumbers =
    [
        20, 64, 121, 233, 539, 601, 615, 617, 669, 921, 926, 927, 941, 952, 982, 988, 997, 1203, 1204, 1205, 1215, 1216,
        1221, 1222, 1232, 1404, 1413, 1421, 1438, 1532, 1533, 1534, 1535, 1807, 2021, 2816, 3429, 3635, 3935, 3941, 3947,
        3948, 3950, 3953, 3957, 3960, 3966, 3980, 4060, 4117, 4184, 4221, 5280, 5529, 6292, 7951, 8628, 8645, 8651, 9020,
        9515, 9985, 10053, 10054, 10060, 10922, 10928, 10929, 10930, 10936, 11001, 11539, 14355, 14817, 14868, 14906,
        16528, 16554, 16555, 17065, 17066, 17067, 17197, 17889, 18401, 18858, 19413, 19416, 19494, 20041, 21503, 22225,
        22226, 22335, 22353, 22355, 22358, 22427, 22430, 22491, 22493, 22498, 22754, 22758, 22759, 22760, 22984, 25003,
        25738, 25740, 30080, 30085, 33123, 35216, 35218, 35256, 35293, 37202, 37327, 39025, 39108, 39110, 39151, 39152,
        40106, 40143, 40189, 40197, 40501, 40540, 40613, 40623, 40642, 40648, 40671, 40675, 40890, 40903, 40918, 40938,
        41301, 41302, 41305, 41325, 41339, 41383, 41614, 41619, 41640, 41700, 41701, 41823, 41839, 41840, 42029, 42108,
        42109, 45153, 45156, 45157, 45161, 45179, 45182, 45319, 45547, 47132, 47137, 47139, 49510, 49802, 49918, 49919,
        49920, 49977, 49983
    ];

    /// <summary>
    /// Koşulsuz geçici sayılan SQL hata numaraları (ör. 1205 kilitlenme kurbanı, 40501 servis meşgul, 40613 veritabanı
    /// kullanılamıyor). 203 yalnızca iç istisnası <see cref="Win32Exception"/> ise geçicidir. -2 (komut zaman aşımı)
    /// bilerek listede yoktur: işlem sunucuda tamamlanmış olabilir, yeniden denemek çift yazma riski taşır.
    /// </summary>
    public static IReadOnlyCollection<int> ErrorNumbers => TransientNumbers;

    /// <summary>
    /// İstisna ya da iç istisna zincirindeki herhangi bir halka geçici bir SQL hatasıysa (veya <see cref="TimeoutException"/>)
    /// <c>true</c>. Retry / devre kesici koşullarında doğrudan kullanılır:
    /// <c>o.ShouldHandle = AegisSqlTransientErrors.IsTransient</c>.
    /// </summary>
    public static bool IsTransient(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < MaxInnerExceptionDepth; depth++, exception = exception.InnerException)
        {
            if (exception is TimeoutException || (exception is SqlException sql && IsTransient(sql)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransient(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (TransientNumbers.Contains(error.Number) ||
                (error.Number == PreLoginHandshakeError && exception.InnerException is Win32Exception))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Koşul oluşturucuya SQL geçici hatalarını ekler (Topaz: <c>SqlDatabaseTransientErrorDetectionStrategy</c>).
    /// Örnek: <c>o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleSqlTransientErrors()</c>.
    /// </summary>
    public static AegisPredicateBuilder HandleSqlTransientErrors(this AegisPredicateBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Handle<Exception>(IsTransient);
    }
}
