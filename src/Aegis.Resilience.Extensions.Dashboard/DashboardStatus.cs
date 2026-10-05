using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Dashboard;

internal sealed record DashboardStatus(DateTimeOffset Timestamp, int TotalPipelines, IReadOnlyList<PipelineStatus> Pipelines);
