using System;
using System.Buffers;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using THNETII.Common;

namespace THNETII.Base64UrlSafe.Cli
{
    public static partial class Program
    {
        public static async Task InvokeAsync(
            IConsole console,
            bool decode = false,
            bool ignoreGarbage = false,
            int? wrap = null,
            FileInfo file = null,
            Encoding charset = null,
            int buffer = 4096,
            CancellationToken cancelToken = default
            )
        {
            if (decode)
            {
                TextReader reader;
                if (file is FileInfo && file.Name != "-")
                {
                    if (charset is Encoding)
                        reader = new StreamReader(file.OpenRead(), charset);
                    else
                        reader = file.OpenText();
                }
                else if (charset is Encoding)
                    reader = new StreamReader(Console.OpenStandardInput(), charset);
                else
                    reader = Console.In;
                using (reader)
                using (var output = Console.OpenStandardOutput())
                {
                    await DecodeAsync(reader, output, ignoreGarbage, cancelToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                Stream input;
                if (file is FileInfo && file.Name != "-")
                    input = file.OpenRead();
                else
                    input = Console.OpenStandardInput();
                using (input)
                {
                    await EncodeAsync(input, Console.Out, wrap.GetValueOrDefault(), cancelToken)
                        .ConfigureAwait(false);
                }
            }
        }

        [SuppressMessage("Design", "CA1031: Do not catch general exception types")]
        public static Task<int> Main(string[] args)
        {
#if !NOCODEPAGES
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif // !NOCODEPAGES

            var cliParser = new CommandLineBuilder(RootCommand())
                .UseDefaults()
                .UseMiddleware(async (invokeCtx, next) =>
                {
                    var cts = new CancellationTokenSource();
                    var onCancelKeyPress = new ConsoleCancelEventHandler((object senter, ConsoleCancelEventArgs e) =>
                    {
                        // If cancellation already has been requested,
                        // do not cancel process termination signal.
                        e.Cancel = !cts.IsCancellationRequested;

                        cts.Cancel(throwOnFirstException: true);
                    });
                    Console.CancelKeyPress += onCancelKeyPress;

                    invokeCtx.BindingContext.AddService(typeof(CancellationTokenSource), () => cts);
                    invokeCtx.BindingContext.AddService(typeof(CancellationToken), () => cts.Token);

                    try { await next(invokeCtx).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        Console.CancelKeyPress -= onCancelKeyPress;
                    }
                })
                .Build();
            return cliParser.InvokeAsync(args ?? Array.Empty<string>());

            RootCommand RootCommand()
            {
                var root = new RootCommand(description);
                root.AddOption(new Option(
                    new string[] { "-d", "--decode" },
                    "decode data (encodes by default)",
                    new Argument<bool>()
                    ));
                root.AddOption(new Option(
                    new string[] { "-i", "--ignore-garbage" },
                    "when decoding, ignore non-alphabet characters",
                    new Argument<bool>()
                    ));
                root.AddOption(new Option(
                    new string[] { "-w", "--wrap" },
                    "wrap encoded lines after COLS characters (default 76). Use 0 to disable line wrapping",
                    WrapArgument()
                    ));
                root.AddOption(new Option(
                    new string[] { "-f", "--file" },
                    "Encode or decode contents of FILE ('-' for STDIN)",
                    FileArgument()
                    ));
                root.AddOption(new Option(
                    new string[] { "-c", "--charset" },
                    $"use CHARSET when decoding data from a file (Default: {Encoding.UTF8.WebName}).",
                    CharsetArgument()
                    ));
                root.AddOption(new Option(
                    new string[] { "-b", "--buffer" },
                    "use SIZE as the read-buffer size. (Default: 4096)",
                    new Argument<int>(4096) { Name = "SIZE", Description = "Size to use for intermediate read buffer" }
                    ));
                root.Handler = CommandHandler.Create(typeof(Program).GetMethod(nameof(InvokeAsync)));

                return root;

                Argument<int?> WrapArgument()
                {
                    var arg = new Argument<int?>(symbol =>
                    {
                        if (symbol.Arguments.FirstOrDefault().TryNotNull(out string value))
                        {
                            try
                            {
                                int number = int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture);
                                return ArgumentResult.Success<int?>(number);
                            }
                            catch (OverflowException overflowExcept)
                            { return ArgumentResult.Failure(overflowExcept.Message); }
                            catch (FormatException formatExcept)
                            { return ArgumentResult.Failure(formatExcept.Message); }
                        }
                        return ArgumentResult.Success<int?>(76);
                    })
                    {
                        Name = "COLS",
                        Description = $"Number of characters per line (default {76})",
                        Arity = ArgumentArity.ZeroOrOne
                    };
                    arg.SetDefaultValue(null);

                    arg.AddValidator(symbol => symbol.Arguments.Select(s =>
                    {
                        if (int.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out int v) && v >= 0)
                            return null;
                        return $"Argument '{s}' for option '{symbol.Token}' is invalid. Expected a non-negative integer value.";
                    }).Where(msg => !string.IsNullOrWhiteSpace(msg)).FirstOrDefault());

                    return arg;
                }

                Argument<FileInfo> FileArgument()
                {
                    var argument = new Argument<FileInfo>
                    {
                        Name = "FILE",
                        Description = "Path to read from or write to",
                        Arity = ArgumentArity.ExactlyOne
                    };
                    argument.AddValidator(symbol =>
                    {
                        IEnumerable<string> source = from filePath in symbol.Arguments
                                                     where !filePath.Equals("-", StringComparison.Ordinal)
                                                     where !File.Exists(filePath)
                                                     select filePath;
                        ValidationMessages validationMessages = symbol.ValidationMessages;
                        return source.Select(validationMessages.FileDoesNotExist).FirstOrDefault();
                    });
                    return argument;
                }

                Argument<Encoding> CharsetArgument()
                {
                    var arg = new Argument<Encoding>(ConvertToEncoding)
                    {
                        Arity = ArgumentArity.ZeroOrOne,
                        Name = "CHARSET",
                        Description = $"IANA charset name (default: {Encoding.UTF8})"
                    };
                    arg.AddSuggestions(Encoding.GetEncodings().Select(enc => enc.Name).ToArray());
                    arg.SetDefaultValue(Encoding.UTF8);
                    return arg;

                    ArgumentResult ConvertToEncoding(SymbolResult symbol)
                    {
                        if (symbol.Arguments.FirstOrDefault().TryNotNullOrWhiteSpace(out string charset))
                        {
                            try
                            {
                                var encoding = Encoding.GetEncoding(charset);
                                return ArgumentResult.Success(encoding);
                            }
                            catch (ArgumentException)
                            {
                                return ArgumentResult.Failure($"Argument '{charset}' for option '{symbol.Token}' is invalid. '{charset}' is not a supported encoding name.");
                            }
                        }
                        return ArgumentResult.Success(Encoding.UTF8);
                    }
                }
            }
        }

        private static readonly string description = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyDescriptionAttribute>()?
            .Description;
    }
}
