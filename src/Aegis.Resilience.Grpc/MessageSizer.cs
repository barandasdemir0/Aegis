using System.Buffers;
using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Mesajın seri hale getirilmiş bayt boyutu (yeniden oynatma tamponunun sınırı için). Marshaller'ın bağlamsal serileştiricisi
/// kullanılır: Grpc.Tools'un ürettiği protobuf kodu yalnızca onu destekler, basit <c>Serializer</c> özelliği
/// <see cref="NotImplementedException"/> fırlatır. Üretilen kod boyutu önce bildirir; baytlar havuzdan alınan karalama
/// tamponuna yazılır ve tutulmaz.
/// </summary>
internal static class MessageSizer
{
    public static int Measure<T>(Marshaller<T> marshaller, T message)
    {
        var context = new SizingContext();
        try
        {
            marshaller.ContextualSerializer(message, context);
            return context.Length;
        }
        finally
        {
            context.Release();
        }
    }

    private sealed class SizingContext : SerializationContext, IBufferWriter<byte>
    {
        private byte[]? _scratch;
        private int? _declared;
        private long _written;

        public int Length => _declared ?? checked((int)_written);

        public override void SetPayloadLength(int payloadLength) => _declared = payloadLength;

        public override void Complete(byte[] payload) => _written = payload.Length;

        public override IBufferWriter<byte> GetBufferWriter() => this;

        public override void Complete()
        {
        }

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Scratch(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Scratch(sizeHint);

        public void Release()
        {
            if (_scratch is { } scratch)
            {
                ArrayPool<byte>.Shared.Return(scratch);
                _scratch = null;
            }
        }

        private byte[] Scratch(int sizeHint)
        {
            var size = Math.Max(sizeHint, 256);
            if (_scratch is null || _scratch.Length < size)
            {
                Release();
                _scratch = ArrayPool<byte>.Shared.Rent(size);
            }

            return _scratch;
        }
    }
}
