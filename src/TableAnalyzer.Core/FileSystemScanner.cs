namespace TableAnalyzer.Core;

public sealed class FileSystemScanner
{
    public IReadOnlyList<SourceFile> GetSourceFiles(string inputPath, AnalyzerConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required.", nameof(inputPath));
        }

        var fullInputPath = Path.GetFullPath(inputPath);
        if (File.Exists(fullInputPath))
        {
            if (!IsIncluded(fullInputPath, configuration))
            {
                return [];
            }

            return [new SourceFile(fullInputPath, NormalizePath(Path.GetFileName(fullInputPath)))];
        }

        if (!Directory.Exists(fullInputPath))
        {
            throw new DirectoryNotFoundException($"Input path does not exist: {inputPath}");
        }

        var files = Directory.EnumerateFiles(fullInputPath, "*", SearchOption.AllDirectories)
            .Where(path => IsIncluded(path, configuration))
            .Where(path => !IsUnderExcludedDirectory(fullInputPath, path, configuration))
            .Select(path => new SourceFile(path, NormalizePath(Path.GetRelativePath(fullInputPath, path))))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return files;
    }

    private static bool IsIncluded(string path, AnalyzerConfiguration configuration)
    {
        return configuration.IncludeExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderExcludedDirectory(string root, string path, AnalyzerConfiguration configuration)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(Math.Max(0, segments.Length - 1)).Any(segment =>
            configuration.ExcludeDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
