using System;
using System.Buffers;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using THNETII.Common;
using THNETII.Common.Serialization;

namespace THNETII.Base64UrlSafe.Cli
{
    public static class Program
    {
        public static async Task InvokeAsync(
            IConsole console,
            bool decode = false,
            bool ignoreGarbage = false,
            int? wrap = null,
            FileInfo file = null,
            Encoding charset = null,
            int buffer = 4096,
            string data = null,
            CancellationToken cancelToken = default
            )
        {
            var stdout = console.Out;
            string[][] table = new[]
            {
                new[] { nameof(decode), $"{decode}" },
                new[] { nameof(ignoreGarbage), $"{ignoreGarbage}" },
                new[] { nameof(wrap), wrap is null ? $"{wrap}" : $"{wrap:N0} {"character".SuffixPluralS(wrap)}" },
                new[] { nameof(file), $"{file}" },
                new[] { nameof(charset), $"{charset?.EncodingName}" },
                new[] { nameof(buffer), $"{buffer:N0} {"Byte".SuffixPluralS(buffer)}" },
                new[] { nameof(data), $"{data}"}
            };
            var sep = table.Max(r => r[0].Length);
            foreach (var row in table)
                stdout.WriteLine($"{row[0].PadRight(sep)}: {row[1]}");
        }

        public static Task<int> Main(string[] args)
        {
#if !NOCODEPAGES
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif // !NOCODEPAGES

            var cliParser = new CommandLineBuilder(RootCommand())
                .UseDefaults()
                .UseMiddleware((invokeCtx, next) =>
                {
                    var cts = new CancellationTokenSource();
                    void OnCancelKeyPress(object senter, ConsoleCancelEventArgs e)
                    {
                        // If cancellation already has been requested,
                        // do not cancel process termination signal.
                        e.Cancel = !cts.IsCancellationRequested;

                        cts.Cancel(throwOnFirstException: true);
                    }
                    Console.CancelKeyPress += OnCancelKeyPress;

                    invokeCtx.BindingContext.AddService(typeof(CancellationTokenSource), () => cts);
                    invokeCtx.BindingContext.AddService(typeof(CancellationToken), () => cts.Token);

                    try { return next(invokeCtx); }
                    finally
                    {
                        Console.CancelKeyPress -= OnCancelKeyPress;
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
                    BoolArgument()
                    ));
                root.AddOption(new Option(
                    new string[] { "-i", "--ignore-garbage" },
                    "when decoding, ignore non-alphabet characters",
                    BoolArgument()
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
                root.Argument = DataArgument();
                root.Handler = CommandHandler.Create(typeof(Program).GetMethod(nameof(InvokeAsync)));

                return root;

                Argument<bool> BoolArgument(bool @default = default)
                {
                    var arg = new Argument<bool>(ConvertToBool)
                    {
                        Name = "B"
                    };
                    arg.SetDefaultValue(@default);
                    return arg;

                    ArgumentResult ConvertToBool(SymbolResult symbol)
                    {
                        try
                        {
                            string value = symbol.Arguments.FirstOrDefault();
                            return ArgumentResult.Success(BooleanStringConverter.Parse(value));
                        }
                        catch (Exception e)
                        { return ArgumentResult.Failure(e.Message); }
                    }
                }

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

                Argument<string> DataArgument()
                {
                    var arg = new Argument<string>
                    {
                        Name = "DATA",
                        Arity = ArgumentArity.ZeroOrOne,
                        Description = "encode or decode DATA, overrides '--file'"
                    };

                    return arg;
                }
            }
        }

        private static readonly string description = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyDescriptionAttribute>()?
            .Description;
    }
}
