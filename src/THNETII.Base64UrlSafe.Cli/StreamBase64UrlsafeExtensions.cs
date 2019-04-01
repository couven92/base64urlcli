using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CommonBase64UrlSafe = THNETII.Common.Buffers.Text.Base64UrlSafe;

namespace THNETII.Base64UrlSafe.Cli
{
    public static class StreamBase64UrlsafeExtensions
    {
        private static readonly byte base64PadByte = GetUtf8Byte('=');
        private static readonly MemoryPool<byte> bytesPool = MemoryPool<byte>.Shared;

        public static async Task WriteFromBase64Urlsafe(
            this Stream stream, PipeReader base64Pipe,
            CancellationToken cancelToken = default)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));
            if (base64Pipe is null)
                throw new ArgumentNullException(nameof(base64Pipe));

            try
            {
                using (var base64Buffer = bytesPool.Rent())
                {
                    Memory<byte> base64Memory = base64Buffer.Memory;
                    int base64Remaining = 0;
                    while (true)
                    {
                        ReadResult readResult = await base64Pipe
                            .ReadAsync(cancelToken)
                            .ConfigureAwait(false);

                        (base64Remaining, _) = await stream
                            .WritePartialFromBase64Urlsafe(readResult.Buffer, base64Memory, base64Remaining, cancelToken)
                            .ConfigureAwait(false);
                        base64Pipe.AdvanceTo(readResult.Buffer.End);

                        if (readResult.IsCompleted)
                            break;
                    }

                    base64Memory = base64Memory.Slice(0, base64Remaining);
                    await stream.WriteFinalFromBase64Urlsafe(base64Memory, cancelToken)
                        .ConfigureAwait(false);
                }

                await stream.FlushAsync(cancelToken).ConfigureAwait(false);
                base64Pipe.Complete();
            }
            catch (Exception e)
            {
                base64Pipe.Complete(e);
                throw;
            }
        }

        internal static async Task<(int base64Remaining, int base64PadCount)> WritePartialFromBase64Urlsafe(
            this Stream stream, ReadOnlySequence<byte> base64Sequence,
            Memory<byte> base64Memory, int base64Remaining = 0,
            CancellationToken cancelToken = default)
        {
            int base64PaddingRequired = 0;

            Memory<byte> base64Next = base64Memory.Slice(base64Remaining);
            long base64Consumed = 0L;
            while (base64Consumed < base64Sequence.Length)
            {
                // base64Length will always fit into an int,
                // since base64NextAvailable.Length is an int
                long base64Length = Math.Min(base64Next.Length, base64Sequence.Length - base64Consumed);
                ReadOnlySequence<byte> base64Slice = base64Sequence.Slice(base64Consumed, base64Length);
                base64Slice.CopyTo(base64Next.Span);
                base64Length = base64Slice.Length;
                base64Consumed += base64Length;

                base64Length += base64Remaining;
                base64Memory = base64Memory.Slice(0, (int)base64Length);

                (base64Remaining, base64PaddingRequired) = await stream
                    .WritePartialFromBase64Urlsafe(base64Memory, cancelToken)
                    .ConfigureAwait(false);
            }

            return (base64Remaining, base64PaddingRequired);
        }

        internal static async Task<(int base64Remaining, int base64PadCount)> WritePartialFromBase64Urlsafe(
            this Stream stream, Memory<byte> base64Memory,
            CancellationToken cancelToken = default)
        {
            int base64Count = CommonBase64UrlSafe.RevertUrlSafeUtf8(base64Memory.Span,
                out int base64PaddingRequired);
            int bytesRequired = Base64.GetMaxDecodedFromUtf8Length(base64Count);
            int base64Consumed = await stream.WritePartialFromBase64(base64Memory,
                bytesRequired, isFinalBlock: false, cancelToken)
                .ConfigureAwait(false);

            Memory<byte> base64Remaining = base64Memory.Slice(base64Consumed);
            base64Remaining.CopyTo(base64Memory);
            return (base64Remaining.Length, base64PaddingRequired);
        }

        [SuppressMessage("Usage", "PC001: API not supported on all platforms")]
        internal static async Task WriteFinalFromBase64Urlsafe(
            this Stream stream, Memory<byte> base64Memory,
            CancellationToken cancelToken = default)
        {
            int base64Count = CommonBase64UrlSafe.RevertUrlSafeUtf8(base64Memory.Span,
                out int base64PadCount);
            int base64Length = base64Memory.Length + base64PadCount;
            using (var base64FinalBuffer = bytesPool.Rent(base64Length))
            {
                Memory<byte> base64FinalMemory = base64FinalBuffer.Memory;
                base64Memory.CopyTo(base64FinalMemory);
                Memory<byte> base64PadMemory = base64FinalMemory.Slice(base64Memory.Length, base64PadCount);
                base64PadMemory.Span.Fill(base64PadByte);

                int bytesRequired = Base64.GetMaxDecodedFromUtf8Length(base64Count + base64PadCount);
                int base64Consumed = await stream.WritePartialFromBase64(
                    base64FinalMemory.Slice(0, base64Length), bytesRequired,
                    isFinalBlock: true, cancelToken).ConfigureAwait(false);
                if (base64Consumed < base64Length)
                {
                    throw new InvalidOperationException("Base64 decode of final block did not consume all bytes");
                }
            }
        }

        internal static async Task<int> WritePartialFromBase64(
            this Stream stream, Memory<byte> base64Memory, int bytesRequired,
            bool isFinalBlock = true, CancellationToken cancelToken = default)
        {
            int base64Consumed;
            using (var bytesBuffer = bytesPool.Rent(bytesRequired))
            {
                Memory<byte> bytesMemory = bytesBuffer.Memory;
                var base64Status = Base64.DecodeFromUtf8(
                    base64Memory.Span, bytesMemory.Span,
                    out base64Consumed, out int bytesWritten,
                    isFinalBlock);
                base64Status.ThrowIfFailed(nameof(Base64.DecodeFromUtf8));

                Memory<byte> bytesAvailable = bytesMemory.Slice(0, bytesWritten);
                await stream.WriteAsync(bytesAvailable, cancelToken);
            }

            return base64Consumed;
        }

        private static byte GetUtf8Byte(char ch)
        {
            ReadOnlySpan<char> chars = stackalloc char[] { ch };
            Span<byte> bytes = stackalloc byte[1];
            Encoding.UTF8.GetBytes(chars, bytes);
            return bytes[0];
        }
    }
}
