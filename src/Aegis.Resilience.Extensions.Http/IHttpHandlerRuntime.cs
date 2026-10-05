using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Yeniden yüklenebilen bir HTTP işleyicisinin bir nesli: isteğin boru hattı ve HTTP kuralları.</summary>
internal interface IHttpHandlerRuntime : IDisposable
{
    /// <summary>İsteğin boru hattı (ör. authority başına ayrı ya da tek, paylaşılan) ve HTTP kuralları.</summary>
    HttpHandlerRoute Route(HttpRequestMessage request);
}
