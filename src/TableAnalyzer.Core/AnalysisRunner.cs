namespace TableAnalyzer.Core;

public sealed record AnalysisRunRequest(
    string ProjectFolder,
    string AnalysisFolder,
    string? AnalysisFile,
    string OutputRoot);

public sealed record AnalysisRunResult(
    string ReportDirectory,
    IReadOnlyList<SourceFile> AnalysisFiles,
    IReadOnlyList<SourceFile> ContextFiles,
    AnalysisResult AnalysisResult);

public sealed class AnalysisRunner
{
    public AnalysisRunResult Run(
        AnalysisRunRequest request,
        AnalyzerConfiguration configuration,
        IProgress<AnalysisProgress>? progress = null)
    {
        return Run(request, configuration, DateTime.Now, progress);
    }

    public AnalysisRunResult Run(
        AnalysisRunRequest request,
        AnalyzerConfiguration configuration,
        DateTime now,
        IProgress<AnalysisProgress>? progress = null)
    {
        var projectFolder = RequireExistingDirectory(request.ProjectFolder, nameof(request.ProjectFolder));
        var analysisFolder = RequireExistingDirectory(request.AnalysisFolder, nameof(request.AnalysisFolder));
        var outputRoot = RequireValue(request.OutputRoot, nameof(request.OutputRoot));
        var analysisFile = string.IsNullOrWhiteSpace(request.AnalysisFile)
            ? null
            : RequireExistingFile(request.AnalysisFile!, nameof(request.AnalysisFile));

        var validator = new RunOptionsValidator();
        validator.Validate(projectFolder, outputRoot);
        validator.Validate(analysisFolder, outputRoot);
        if (analysisFile is not null)
        {
            validator.Validate(analysisFile, outputRoot);
        }

        var scanner = new FileSystemScanner();
        var analysisInput = analysisFile ?? analysisFolder;
        var analysisFiles = scanner.GetSourceFiles(analysisInput, configuration);
        var contextFiles = Merge(scanner.GetSourceFiles(projectFolder, configuration), analysisFiles);

        var reportDirectory = new ReportDirectoryFactory().Create(outputRoot, analysisInput, now);
        var result = new SimpleSourceAnalyzer().Analyze(analysisFiles, contextFiles, configuration, progress);
        new CsvReportWriter().Write(reportDirectory, result);

        return new AnalysisRunResult(reportDirectory, analysisFiles, contextFiles, result);
    }

    private static IReadOnlyList<SourceFile> Merge(IReadOnlyList<SourceFile> first, IReadOnlyList<SourceFile> second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SourceFile>();
        foreach (var file in first.Concat(second))
        {
            if (seen.Add(Path.GetFullPath(file.FullPath)))
            {
                merged.Add(file);
            }
        }

        return merged;
    }

    private static string RequireExistingDirectory(string value, string argumentName)
    {
        var path = RequireValue(value, argumentName);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {value}");
        }

        return path;
    }

    private static string RequireExistingFile(string value, string argumentName)
    {
        var path = RequireValue(value, argumentName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File does not exist: {value}", value);
        }

        return path;
    }

    private static string RequireValue(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{argumentName} is required.", argumentName);
        }

        return Path.GetFullPath(value);
    }
}
