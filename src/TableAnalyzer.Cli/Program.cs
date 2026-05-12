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
            if (!options.TryGetValue("out", out var outputRoot) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Missing required option: --out");
                return 1;
            }

            var configuration = BuildConfiguration(options);
            var request = BuildRunRequest(options, outputRoot);
            Console.Error.WriteLine($"Project folder: {Path.GetFullPath(request.ProjectFolder)}");
            Console.Error.WriteLine($"Analysis folder: {Path.GetFullPath(request.AnalysisFolder)}");
            if (!string.IsNullOrWhiteSpace(request.AnalysisFile))
            {
                Console.Error.WriteLine($"Analysis file: {Path.GetFullPath(request.AnalysisFile)}");
            }

            var progress = options.ContainsKey("quiet")
                ? null
                : new ConsoleAnalysisProgressReporter();
            Console.Error.WriteLine("Scanning project context and analysis targets...");
            var run = new AnalysisRunner().Run(request, configuration, progress);
            Console.WriteLine($"Project folder: {Path.GetFullPath(request.ProjectFolder)}");
            Console.WriteLine($"Analysis folder: {Path.GetFullPath(request.AnalysisFolder)}");
            Console.WriteLine($"Analysis file: {(string.IsNullOrWhiteSpace(request.AnalysisFile) ? "(none)" : Path.GetFullPath(request.AnalysisFile))}");
            Console.WriteLine($"Mode: {(string.IsNullOrWhiteSpace(request.AnalysisFile) ? "Directory recursive" : "Single file")}");
            Console.WriteLine($"Context files indexed: {run.ContextFiles.Count}");
            Console.WriteLine($"Files analyzed: {run.AnalysisFiles.Count}");
            Console.WriteLine($"SQL snippets: {run.AnalysisResult.SqlSnippets.Count}");
            Console.WriteLine($"Table usages: {run.AnalysisResult.TableUsages.Count}");
            Console.WriteLine($"Dynamic SQL: {run.AnalysisResult.DynamicSql.Count}");
            Console.WriteLine($"Unresolved SQL: {run.AnalysisResult.UnresolvedSql.Count}");
            Console.WriteLine($"Warnings: {run.AnalysisResult.Warnings.Count}");
            Console.WriteLine($"Output: {run.ReportDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static AnalysisRunRequest BuildRunRequest(IReadOnlyDictionary<string, string> options, string outputRoot)
    {
        var hasProjectModeOptions = options.ContainsKey("project-folder") ||
                                    options.ContainsKey("analysis-folder") ||
                                    options.ContainsKey("analysis-file");
        if (hasProjectModeOptions)
        {
            if (!options.TryGetValue("project-folder", out var projectFolder) || string.IsNullOrWhiteSpace(projectFolder))
            {
                throw new ArgumentException("Missing required option: --project-folder");
            }

            if (!options.TryGetValue("analysis-folder", out var analysisFolder) || string.IsNullOrWhiteSpace(analysisFolder))
            {
                throw new ArgumentException("Missing required option: --analysis-folder");
            }

            options.TryGetValue("analysis-file", out var analysisFile);
            return new AnalysisRunRequest(projectFolder, analysisFolder, analysisFile, outputRoot);
        }

        if (!options.TryGetValue("input", out var input) || string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Missing required option: --input");
        }

        var fullInput = Path.GetFullPath(input);
        if (File.Exists(fullInput))
        {
            var parent = Path.GetDirectoryName(fullInput) ?? Directory.GetCurrentDirectory();
            return new AnalysisRunRequest(parent, parent, fullInput, outputRoot);
        }

        return new AnalysisRunRequest(fullInput, fullInput, null, outputRoot);
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
        Console.WriteLine("  TableAnalyzer analyze --project-folder <folder> --analysis-folder <folder> [--analysis-file <file>] --out <output-root>");
        Console.WriteLine("  TableAnalyzer analyze --input <file-or-folder> --out <output-root>");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --project-folder <folder>   Cross-file method resolution context.");
        Console.WriteLine("  --analysis-folder <folder>  Required analysis root. Used recursively when --analysis-file is omitted.");
        Console.WriteLine("  --analysis-file <file>      Optional single file to analyze.");
        Console.WriteLine("  --input <file-or-folder>    Compatibility shortcut. Uses input as both project and analysis root.");
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
        private string _lastStage = "";

        public void Report(AnalysisProgress value)
        {
            lock (_gate)
            {
                if (!string.Equals(_lastStage, value.Stage, StringComparison.Ordinal))
                {
                    _lastStage = value.Stage;
                    _lastPrintedAt = DateTime.MinValue;
                    _lastPrintedCompleted = -1;
                }

                if (!ShouldPrint(value))
                {
                    return;
                }

                _lastPrintedAt = DateTime.UtcNow;
                _lastPrintedCompleted = value.Completed;

                if (value.Total == 0)
                {
                    Console.Error.WriteLine($"{GetStageLabel(value.Stage)}: no source files found");
                    return;
                }

                var percent = (int)Math.Floor(value.Completed * 100.0 / value.Total);
                var current = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? ""
                    : $" {value.CurrentFile}";
                Console.Error.WriteLine($"{GetStageLabel(value.Stage)}: {value.Completed}/{value.Total} ({percent}%){current}");
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

        private static string GetStageLabel(string stage)
        {
            return stage switch
            {
                "indexing" => "Indexing",
                "analyzing" => "Analyzing",
                _ => stage.Length == 0 ? "Progress" : stage
            };
        }
    }
}
