using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Bir isteğin yürütüleceği boru hattı ve HTTP kuralları (geçici kodlar, yeniden gönderim, son yanıt).</summary>
internal readonly record struct HttpHandlerRoute(IAegisPipeline Pipeline, HttpHandlerRules Rules);
