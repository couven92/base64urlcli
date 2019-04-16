using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using THNETII.Common.IO;
using THNETII.Common.Text;

namespace THNETII.Base64UrlSafe.Cli
{
    partial class Program
    {
        public static Task EncodeAsync(
            Stream input, TextWriter output, int wrap,
            CancellationToken cancelToken = default)
        {
            var utf8Decoder = Encoding.UTF8.GetDecoder();

            var base64Utf8Pipe = new Pipe();
            var base64CharChannel = Channel.CreateUnbounded<IMemoryOwner<char>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            var inputToBase64Task = base64Utf8Pipe.Writer.WriteToBase64Urlsafe(input, cancelToken);
            var utf8ToCharTask = utf8Decoder.DecodePipelineIntoChannel(base64Utf8Pipe.Reader, base64CharChannel.Writer, cancelToken);
            var outputChannelReaderTask = wrap > 0
                 ? ChannelToWrappingTextWriter(base64CharChannel.Reader, output, wrap, cancelToken)
                 : output.WriteFromChannelAsync(base64CharChannel.Reader, cancelToken);

            return Task.WhenAll(inputToBase64Task, utf8ToCharTask,
                outputChannelReaderTask);

            async Task ChannelToWrappingTextWriter(ChannelReader<IMemoryOwner<char>> reader,
                TextWriter writer, int charsMaxPerLine, CancellationToken ct = default)
            {
                int charsWrittenOnLine = 0;
                try
                {
                    for (IMemoryOwner<char> buffer = await reader.ReadAsync(ct); buffer is IMemoryOwner<char>; buffer = await reader.ReadAsync(ct))
                    {
                        Memory<char> charsRemaining = buffer.Memory;
                        while (!charsRemaining.IsEmpty)
                        {
                            int charsAvailable = charsRemaining.Length;
                            int charsRemainingOnLine = charsMaxPerLine - charsWrittenOnLine;
                            if (charsRemainingOnLine < charsAvailable)
                            {
                                Memory<char> charsToWrite = charsRemaining.Slice(0, charsRemainingOnLine);
                                await writer.WriteLineAsync(charsToWrite, ct);
                                charsRemaining = charsRemaining.Slice(charsRemainingOnLine);
                                charsWrittenOnLine = 0;
                            }
                            else
                            {
                                await writer.WriteAsync(charsRemaining, ct);
                                charsWrittenOnLine += charsRemaining.Length;
                                charsRemaining = Memory<char>.Empty;
                            }
                        }

                        if (charsWrittenOnLine >= charsMaxPerLine)
                        {
                            await writer.WriteLineAsync();
                            charsWrittenOnLine = 0;
                        }
                    }
                }
                catch (ChannelClosedException) { }
                finally
                {
                    await writer.FlushAsync();
                }
            }
        }
    }
}
