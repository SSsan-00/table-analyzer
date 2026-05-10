namespace TableAnalyzer.Core;

public sealed class RunOptionsValidator
{
    public void Validate(string inputPath, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required.", nameof(inputPath));
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Output path is required.", nameof(outputRoot));
        }

        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath) && !Directory.Exists(fullInputPath))
        {
            throw new FileNotFoundException($"Input path does not exist: {inputPath}", inputPath);
        }

        var protectedRoot = File.Exists(fullInputPath)
            ? Path.GetDirectoryName(fullInputPath)!
            : fullInputPath;

        var fullOutputRoot = Path.GetFullPath(outputRoot);
        if (IsSameOrUnder(fullOutputRoot, protectedRoot))
        {
            throw new InvalidOperationException("Output directory must not be inside input directory.");
        }
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var normalizedCandidate = EnsureTrailingSeparator(Path.GetFullPath(candidate));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
