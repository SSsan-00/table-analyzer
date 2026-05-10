namespace TableAnalyzer.Core;

public sealed class ReportDirectoryFactory
{
    public string Create(string outputRoot, string inputPath, DateTime now)
    {
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);

        var inputName = Directory.Exists(inputPath)
            ? new DirectoryInfo(inputPath).Name
            : Path.GetFileName(inputPath);

        var folderName = $"{now:yyyyMMdd-HHmmss}_{SanitizeName(inputName)}";
        var reportDirectory = Path.Combine(fullOutputRoot, folderName);
        if (Directory.Exists(reportDirectory) || File.Exists(reportDirectory))
        {
            throw new IOException($"Report directory already exists: {reportDirectory}");
        }

        Directory.CreateDirectory(reportDirectory);
        return reportDirectory;
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "input" : sanitized;
    }
}
