using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using THNETII.Common;
using THNETII.Common.Buffers;
using THNETII.Common.IO;
using THNETII.Common.Text;

namespace THNETII.Base64UrlSafe.Cli
{
    partial class Program
    {
        public static Task DecodeAsync(TextReader input, Stream output,
            bool ignoreGarbage, CancellationToken cancelToken = default)
        {
            var utf8Encoder = Encoding.UTF8.GetEncoder();
            var inputChannel = Channel.CreateUnbounded<IMemoryOwner<char>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            var cleanupChannel = Channel.CreateUnbounded<IMemoryOwner<char>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            var base64Pipe = new Pipe();

            var inputReaderTask = input.ReadIntoChannelAsync(inputChannel.Writer, cancelToken);
            var cleanupTask = CleanupChannelAsync(inputChannel.Reader, cleanupChannel.Writer, ignoreGarbage, cancelToken);
            var utf8EncoderTask = utf8Encoder.EncodeChannelToPipeline(cleanupChannel.Reader, base64Pipe.Writer, cancelToken);
            var outputWriterTask = output.WriteFromBase64Urlsafe(base64Pipe.Reader, cancelToken);

            return Task.WhenAll(inputReaderTask, cleanupTask, utf8EncoderTask, outputWriterTask);

            async Task CleanupChannelAsync(ChannelReader<IMemoryOwner<char>> reader,
                ChannelWriter<IMemoryOwner<char>> writer, bool removeGarbage,
                CancellationToken ct)
            {
                try
                {
                    for (var readBuffer = await reader.ReadAsync(ct); readBuffer is IMemoryOwner<char>; readBuffer = await reader.ReadAsync(ct))
                    {
                        using (readBuffer)
                        {
                            var readRemaining = readBuffer.Memory;
                            do
                            {
                                Memory<char> cleanMemory = SplitNextCleanSection(readRemaining, removeGarbage, out readRemaining);
                                if (cleanMemory.IsEmpty)
                                    continue;

                                var writeBuffer = ArrayMemoryPool<char>.Shared.Rent(cleanMemory.Length);
                                cleanMemory.CopyTo(writeBuffer.Memory);

                                await writer.WriteAsync(writeBuffer.Slice(0, cleanMemory.Length), ct);
                            } while (!readRemaining.IsEmpty);
                        }
                    }
                }
                catch (ChannelClosedException) { }
                catch (Exception except) when (CompleteWriterWithoutUnwind(except))
                { throw; }

                writer.Complete();

                bool CompleteWriterWithoutUnwind(Exception e)
                {
                    writer.Complete(e);
                    return false; // Return false to not catch exception
                }
            }
        }

        private static Memory<char> SplitNextCleanSection(Memory<char> memory, bool ignoreGarbage, out Memory<char> remaining)
        {
            var span = memory.Span;
            var tmp = span;
            int length = 0;
            int endIdx;
            while (true)
            {
                endIdx = tmp.IndexOfAny(Base64UrlSafeConvert.UrlSafeAlphabet);
                if (endIdx < 0) // No more valid Base64 characters
                {
                    remaining = ignoreGarbage ? Memory<char>.Empty : memory.Slice(length);
                    return memory.Slice(0, length);
                }
                else if (endIdx > 0) // Invalid Base64 characters found
                    break;

                // Advance to next character
                length++;
                tmp = tmp.Slice(start: 1);
            }

            // Found invalid Base64 characters.
            for (int i = 0; i < endIdx; i++)
            {
                char ch = tmp[i];
                if (ignoreGarbage)
                {
                    remaining = memory.Slice(endIdx);
                    return memory.Slice(0, length);
                }
                else if (char.IsWhiteSpace(ch)) // Always split at whitespace
                {
                    remaining = ignoreGarbage ? memory.Slice(endIdx) : memory.Slice(length);
                    return memory.Slice(0, length);
                }

                // Do not ignore, include non Base64 character
                length++;
            }

            remaining = ignoreGarbage ? memory.Slice(endIdx) : memory.Slice(length);
            return memory.Slice(0, length);
        }
    }
}
