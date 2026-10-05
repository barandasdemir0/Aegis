using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Aegis.Resilience.Grpc.AspNetCore;

/// <summary>
/// Servis koduna verilen çağrı bağlamı: iptal token'ı Aegis boru hattınınkidir (sunucu zaman aşımı servis kodunu gerçekten
/// keser); diğer her şey (başlıklar, trailer'lar, deadline, durum) asıl bağlama devredilir. <c>GetHttpContext()</c> çalışmaya
/// devam eder: Grpc.AspNetCore sarmalanmış bağlamda HttpContext'i <c>UserState["__HttpContext"]</c>'tan okur.
/// </summary>
internal sealed class PipelineServerCallContext : ServerCallContext
{
    private readonly ServerCallContext _inner;
    private readonly CancellationToken _cancellationToken;
    private readonly HttpContextUserState _userState;

    public PipelineServerCallContext(ServerCallContext inner, CancellationToken cancellationToken)
    {
        _inner = inner;
        _cancellationToken = cancellationToken;
        _userState = new HttpContextUserState(inner.UserState, inner.GetHttpContext());
    }

    protected override string MethodCore => _inner.Method;

    protected override string HostCore => _inner.Host;

    protected override string PeerCore => _inner.Peer;

    protected override DateTime DeadlineCore => _inner.Deadline;

    protected override Metadata RequestHeadersCore => _inner.RequestHeaders;

    protected override CancellationToken CancellationTokenCore => _cancellationToken;

    protected override Metadata ResponseTrailersCore => _inner.ResponseTrailers;

    protected override Status StatusCore
    {
        get => _inner.Status;
        set => _inner.Status = value;
    }

    protected override WriteOptions? WriteOptionsCore
    {
        get => _inner.WriteOptions;
        set => _inner.WriteOptions = value;
    }

    protected override AuthContext AuthContextCore => _inner.AuthContext;

    protected override IDictionary<object, object> UserStateCore => _userState;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => _inner.CreatePropagationToken(options);

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => _inner.WriteResponseHeadersAsync(responseHeaders);

    /// <summary>Asıl kullanıcı durumuna devreder; ek olarak Grpc.AspNetCore'un HttpContext anahtarını yanıtlar.</summary>
    private sealed class HttpContextUserState(IDictionary<object, object> inner, HttpContext httpContext) : IDictionary<object, object>
    {
        private const string HttpContextKey = "__HttpContext"; // Grpc.AspNetCore ServerCallContextExtensions ile aynı

        public object this[object key]
        {
            get => IsHttpContextKey(key) ? httpContext : inner[key];
            set => inner[key] = value;
        }

        public ICollection<object> Keys => inner.Keys;

        public ICollection<object> Values => inner.Values;

        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        public void Add(object key, object value) => inner.Add(key, value);

        public void Add(KeyValuePair<object, object> item) => inner.Add(item);

        public void Clear() => inner.Clear();

        public bool Contains(KeyValuePair<object, object> item) => inner.Contains(item);

        public bool ContainsKey(object key) => IsHttpContextKey(key) || inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<object, object>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<object, object>> GetEnumerator() => inner.GetEnumerator();

        public bool Remove(object key) => inner.Remove(key);

        public bool Remove(KeyValuePair<object, object> item) => inner.Remove(item);

        public bool TryGetValue(object key, [MaybeNullWhen(false)] out object value)
        {
            if (IsHttpContextKey(key))
            {
                value = httpContext;
                return true;
            }

            return inner.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static bool IsHttpContextKey(object key) => key is string text && text == HttpContextKey;
    }
}
