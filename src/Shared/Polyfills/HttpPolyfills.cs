// Yalnızca netstandard2.0 / net462 derlemelerine eklenir (Directory.Build.targets).
using System.Net.Http;

namespace Aegis.Polyfills;

internal static class HttpPolyfills
{
    extension(HttpContent content)
    {
        /// <summary>.NET 5 <c>HttpContent.CopyToAsync(Stream, CancellationToken)</c>: iptal kopyalamadan önce denetlenir.</summary>
        public Task CopyToAsync(Stream stream, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return content.CopyToAsync(stream);
        }
    }
}
