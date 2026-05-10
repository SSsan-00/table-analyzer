using TableAnalyzer.Core;

return CliProgram.Run(args);

internal static class CliProgram
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            if (!string.Equals(command, "analyze", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown command: {command}");
                PrintUsage();
                return 1;
            }

            var options = ParseOptions(args.Skip(1).ToArray());
            if (!options.TryGetValue("input", out var input) || string.IsNullOrWhiteSpace(input))
            {
                Console.Error.WriteLine("Missing required option: --input");
                return 1;
            }

            if (!options.TryGetValue("out", out var outputRoot) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Missing required option: --out");
                return 1;
            }

            var configuration = BuildConfiguration(options);
            new RunOptionsValidator().Validate(input, outputRoot);

            var reportDirectory = new ReportDirectoryFactory().Create(outputRoot, input, DateTime.Now);
            var scanner = new FileSystemScanner();
            Console.Error.WriteLine($"Scanning input: {Path.GetFullPath(input)}");
            var files = scanner.GetSourceFiles(input, configuration);
            Console.Error.WriteLine($"Files queued: {files.Count}");
            var progress = options.ContainsKey("quiet")
                ? null
                : new ConsoleAnalysisProgressReporter();
            var result = new SimpleSourceAnalyzer().Analyze(files, configuration, progress);
            Console.Error.WriteLine("Writing reports...");
            new CsvReportWriter().Write(reportDirectory, result);

            Console.WriteLine($"Input: {Path.GetFullPath(input)}");
            Console.WriteLine($"Mode: {(File.Exists(input) ? "Single file" : "Directory recursive")}");
            Console.WriteLine($"Files analyzed: {files.Count}");
            Console.WriteLine($"SQL snippets: {result.SqlSnippets.Count}");
            Console.WriteLine($"Table usages: {result.TableUsages.Count}");
            Console.WriteLine($"Dynamic SQL: {result.DynamicSql.Count}");
            Console.WriteLine($"Unresolved SQL: {result.UnresolvedSql.Count}");
            Console.WriteLine($"Warnings: {result.Warnings.Count}");
            Console.WriteLine($"Output: {reportDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static AnalyzerConfiguration BuildConfiguration(IReadOnlyDictionary<string, string> options)
    {
        var configuration = new AnalyzerConfiguration();
        var includeExtensions = configuration.IncludeExtensions;
        var maxCallDepth = configuration.MaxCallDepth;
        var maxCandidates = configuration.MaxCandidatesPerExpression;

        if (options.TryGetValue("extensions", out var extensions) && !string.IsNullOrWhiteSpace(extensions))
        {
            includeExtensions = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (options.TryGetValue("max-call-depth", out var maxCallDepthOption) && int.TryParse(maxCallDepthOption, out var parsedDepth))
        {
            maxCallDepth = parsedDepth;
        }

        if (options.TryGetValue("max-candidates", out var maxCandidatesOption) && int.TryParse(maxCandidatesOption, out var parsedCandidates))
        {
            maxCandidates = parsedCandidates;
        }

        return new AnalyzerConfiguration
        {
            IncludeExtensions = includeExtensions,
            ExcludeDirectoryNames = configuration.ExcludeDirectoryNames,
            SqlExecutionMethods = configuration.SqlExecutionMethods,
            MaxCallDepth = maxCallDepth,
            MaxCandidatesPerExpression = maxCandidates
        };
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = "true";
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            options[key] = value;
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  TableAnalyzer analyze --input <file-or-folder> --out <output-root>");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --extensions .cs,.cshtml.cs");
        Console.WriteLine("  --max-call-depth 8");
        Console.WriteLine("  --max-candidates 50");
        Console.WriteLine("  --quiet");
    }

    private sealed class ConsoleAnalysisProgressReporter : IProgress<AnalysisProgress>
    {
        private readonly object _gate = new();
        private DateTime _lastPrintedAt = DateTime.MinValue;
        private int _lastPrintedCompleted = -1;

        public void Report(AnalysisProgress value)
        {
            lock (_gate)
            {
                if (!ShouldPrint(value))
                {
                    return;
                }

                _lastPrintedAt = DateTime.UtcNow;
                _lastPrintedCompleted = value.Completed;

                if (value.Total == 0)
                {
                    Console.Error.WriteLine("Analyzing: no source files found");
                    return;
                }

                var percent = (int)Math.Floor(value.Completed * 100.0 / value.Total);
                var current = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? ""
                    : $" {value.CurrentFile}";
                Console.Error.WriteLine($"Analyzing: {value.Completed}/{value.Total} ({percent}%){current}");
            }
        }

        private bool ShouldPrint(AnalysisProgress value)
        {
            if (value.Completed == 0 || value.Completed == value.Total)
            {
                return true;
            }

            if (value.Completed - _lastPrintedCompleted >= 25)
            {
                return true;
            }

            return DateTime.UtcNow - _lastPrintedAt >= TimeSpan.FromSeconds(2);
        }
    }
}
