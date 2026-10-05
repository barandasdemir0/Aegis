namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Önceden okunmuş baş kısım + akışın kalanı: tampon sınırını aşan geri sarılamaz gövde, okunan kısmı kaybetmeden tek denemede
/// gönderilir. Yalnızca ileri okunur; kapatılınca kaynak akış da kapatılır.
/// </summary>
internal sealed class PrefixedReadStream(MemoryStream prefix, Stream rest) : Stream
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = prefix.Read(buffer, offset, count);
        return read > 0 ? read : rest.Read(buffer, offset, count);
    }

#if NET
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = prefix.Read(buffer.Span);
        return read > 0 ? new ValueTask<int>(read) : rest.ReadAsync(buffer, cancellationToken);
    }
#else
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = prefix.Read(buffer, offset, count);
        return read > 0 ? read : await rest.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }
#endif

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
            prefix.Dispose();
            rest.Dispose();
        }

        base.Dispose(disposing);
    }
}
