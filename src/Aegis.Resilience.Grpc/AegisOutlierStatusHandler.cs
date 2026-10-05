#if NET
using System.Globalization;
using System.Net;
using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Kanal içi HTTP işleyicisi: yük dengeleyicinin seçtiği uç noktanın gRPC durumunu (<c>grpc-status</c>) okuyup dedektöre yazar.
/// Durum "trailers-only" yanıtta başlıktadır, normal yanıtta gövde bittikten sonra trailer'dadır; gövde akışı izlenir ve
/// sonunda trailer okunur. Akış kendisi değişmez.
/// </summary>
public sealed class AegisOutlierStatusHandler(AegisOutlierDetector detector) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!request.Options.TryGetValue(AegisOutlierEndpoints.RequestKey, out var endpoint))
        {
            return response;
        }

        if (TryReadStatus(response.Headers, out var trailersOnly))
        {
            detector.Record(endpoint.Key, trailersOnly, endpoint.KnownEndpoints); // trailers-only: durum başlıkta
        }
        else if (response.StatusCode != HttpStatusCode.OK)
        {
            detector.RecordFailure(endpoint.Key, endpoint.KnownEndpoints); // gRPC olmayan HTTP hatası (502/503...)
        }
        else
        {
            response.Content = new StatusObservingContent(response, detector, endpoint);
        }

        return response;
    }

    internal static bool TryReadStatus(System.Net.Http.Headers.HttpHeaders headers, out StatusCode status)
    {
        status = StatusCode.OK;
        if (!headers.NonValidated.TryGetValues("grpc-status", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                status = (StatusCode)code;
                return true;
            }
        }

        return false;
    }

    // Gövdeyi olduğu gibi aktarır; akışın sonunda (EOF) trailer'daki grpc-status'u kaydeder. Başlıklar özgün içerikten kopyalanır.
    private sealed class StatusObservingContent : HttpContent
    {
        private readonly HttpResponseMessage _response;
        private readonly HttpContent _inner;
        private readonly AegisOutlierDetector _detector;
        private readonly AegisOutlierEndpoint _endpoint;
        private int _recorded;

        public StatusObservingContent(HttpResponseMessage response, AegisOutlierDetector detector, AegisOutlierEndpoint endpoint)
        {
            _response = response;
            _inner = response.Content;
            _detector = detector;
            _endpoint = endpoint;
            foreach (var header in _inner.Headers.NonValidated)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ObservingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), this);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public void OnEndOfStream()
        {
            if (Interlocked.Exchange(ref _recorded, 1) == 0)
            {
                var status = TryReadStatus(_response.TrailingHeaders, out var code) ? code : StatusCode.OK;
                _detector.Record(_endpoint.Key, status, _endpoint.KnownEndpoints);
            }
        }
    }

    private sealed class ObservingStream(Stream inner, StatusObservingContent owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0 && buffer.Length > 0)
            {
                owner.OnEndOfStream();
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0 && count > 0)
            {
                owner.OnEndOfStream();
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
#endif
