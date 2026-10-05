using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Http.Diagnostics;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Telemetry;

/// <summary>Microsoft ile aynı ek etiket adları.</summary>
public static class AegisResilienceTagNames
{
    /// <summary>Özetlenmiş hata türü (<see cref="IExceptionSummarizer"/>; ör. "TaskTimeout", "HostNotFound").</summary>
    public const string ErrorType = "error.type";

    /// <summary>İşlemin adı (<see cref="RequestMetadata.RequestName"/>).</summary>
    public const string RequestName = "request.name";

    /// <summary>Bağımlılığın adı (<see cref="RequestMetadata.DependencyName"/>).</summary>
    public const string DependencyName = "request.dependency.name";
}
