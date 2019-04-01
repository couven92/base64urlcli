using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using CommonBase64UrlSafe = THNETII.Common.Buffers.Text.Base64UrlSafe;

namespace THNETII.Base64UrlSafe.Cli
{
    public static class PipeWriterBase64UrlsafeExtensions
    {
        private static readonly MemoryPool<byte> bytesPool = MemoryPool<byte>.Shared;

        public static async Task WriteToBase64Urlsafe(this PipeWriter base64Pipe,
            Stream stream, CancellationToken cancelToken = default)
        {
            if (base64Pipe is null)
                throw new ArgumentNullException(nameof(base64Pipe));
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                using (var bytesBuffer = bytesPool.Rent())
                {
                    Memory<byte> bytesMemory = bytesBuffer.Memory;
                    Memory<byte> bytesReadBuffer = bytesMemory;
                    int bytesReadOffset = 0;
                    for (int bytesRead = await stream.ReadAsync(bytesReadBuffer, cancelToken); bytesRead != 0; bytesRead = await stream.ReadAsync(bytesReadBuffer, cancelToken))
                    {
                        Memory<byte> bytesAvailable = bytesMemory
                            .Slice(0, bytesReadOffset + bytesRead);
                        int bytesConsumed = await base64Pipe
                            .WritePartialToBase64Urlsafe(
                                bytesAvailable, isFinalBlock: false, cancelToken
                            )
                            .ConfigureAwait(false);

                        Memory<byte> bytesRemaining = bytesAvailable.Slice(bytesConsumed);
                        bytesRemaining.CopyTo(bytesMemory);
                        bytesReadOffset = bytesRemaining.Length;
                        bytesReadBuffer = bytesMemory.Slice(bytesReadOffset);
                    }

                    Memory<byte> bytesLast = bytesMemory.Slice(0, bytesReadOffset);
                    int bytesLastConsumed = await base64Pipe
                        .WritePartialToBase64Urlsafe(
                            bytesLast, isFinalBlock: true, cancelToken
                        )
                        .ConfigureAwait(false);
                    if (bytesLastConsumed < bytesLast.Length)
                    {
                        throw new InvalidOperationException("Base64 encode of final block did not consume all bytes");
                    }
                }

                base64Pipe.Complete();
            }
            catch (Exception e)
            {
                base64Pipe.Complete(e);
                throw;
            }
        }

        private static async Task<int> WritePartialToBase64Urlsafe(
            this PipeWriter base64Pipe, Memory<byte> bytesMemory,
            bool isFinalBlock = true, CancellationToken cancelToken = default)
        {
            int base64Required = Base64.GetMaxEncodedToUtf8Length(bytesMemory.Length);
            Memory<byte> base64Memory = base64Pipe.GetMemory(base64Required);

            var base64Status = Base64.EncodeToUtf8(bytesMemory.Span,
                base64Memory.Span, out int bytesConsumed, out int base64Written,
                isFinalBlock);
            base64Status.ThrowIfFailed(nameof(Base64.EncodeToUtf8));
            base64Written = CommonBase64UrlSafe.MakeUrlSafeUtf8(
                base64Memory.Slice(0, base64Written).Span
                );
            base64Pipe.Advance(base64Written);
            await base64Pipe.FlushAsync(cancelToken).ConfigureAwait(false);
            return bytesConsumed;
        }
    }
}
