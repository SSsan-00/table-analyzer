param(
    [Parameter(Position = 0)]
    [ValidateSet("init", "build", "run", "all")]
    [string] $Command = "all",

    [string] $Input,

    [string] $Out,

    [switch] $Force
)

$ErrorActionPreference = "Stop"

# Embedded runnable-source snapshot only. Tests and samples are intentionally omitted.
$Files = @{
    "src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj" = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@

    "src/TableAnalyzer.Cli/Program.cs" = @'
using System.Text;
using System.Text.RegularExpressions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
return ProgramMain.Run(args);

internal static class ProgramMain
{
    private static readonly string[] IncludedExtensions = [".cs", ".cshtml.cs"];
    private static readonly string[] ExcludedDirs = ["bin", "obj", ".git", ".vs", "node_modules", "Migrations"];
    private static readonly string[] SinkNames =
    [
        "Execute", "ExecuteAsync", "Query", "QueryAsync", "QueryFirst", "QueryFirstOrDefault",
        "QuerySingle", "QuerySingleOrDefault", "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "FromSqlRaw", "FromSqlRawInterpolated", "SqlQueryRaw", "SqlQuery", "SqlCommand"
    ];

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || !string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: TableAnalyzer analyze --input <file-or-folder> --out <output-root>");
                return 1;
            }

            var options = ParseOptions(args.Skip(1).ToArray());
            var input = Required(options, "input");
            var outRoot = Required(options, "out");
            ValidateReadOnly(input, outRoot);

            var reportDir = CreateReportDirectory(outRoot, input);
            var files = GetFiles(input).ToArray();
            var usages = new List<string[]>();
            var snippets = new List<string[]>();
            var unresolved = new List<string[]>();
            var warnings = new List<string[]>();
            var sqlSeq = 0;
            var usageSeq = 0;
            var warningSeq = 0;

            foreach (var file in files)
            {
                var relative = File.Exists(input)
                    ? Path.GetFileName(file)
                    : Path.GetRelativePath(Path.GetFullPath(input), file).Replace(Path.DirectorySeparatorChar, '/');
                var read = ReadText(file);
                if (!read.Success)
                {
                    warnings.Add([$"W{++warningSeq:000000}", "Medium", "FILE_READ_FAILED", relative, "0", "", read.Error ?? "failed to read", "", ""]);
                    continue;
                }

                AnalyzeSource(read.Text, relative, usages, snippets, unresolved, ref sqlSeq, ref usageSeq);
            }

            WriteCsv(Path.Combine(reportDir, "table-usages.csv"),
                ["UsageId", "SqlId", "ObjectType", "ObjectName", "FullName", "Operation", "SqlRole", "Confidence", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "CallChain", "CallDepth", "DynamicPattern", "CandidateGroupId", "Notes"],
                usages);
            WriteCsv(Path.Combine(reportDir, "sql-snippets.csv"),
                ["SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Confidence", "SqlText", "NormalizedSqlText", "CallChain", "Notes"],
                snippets);
            WriteCsv(Path.Combine(reportDir, "unresolved-sql.csv"),
                ["SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Reason", "Expression", "CallChain", "Notes"],
                unresolved);
            WriteCsv(Path.Combine(reportDir, "dynamic-sql.csv"),
                ["CandidateGroupId", "SourceFile", "Line", "ContainingSymbol", "DynamicPattern", "CandidateCount", "Candidates", "Confidence", "ResolutionPath", "Notes"],
                []);
            WriteCsv(Path.Combine(reportDir, "warnings.csv"),
                ["WarningId", "Severity", "Code", "SourceFile", "Line", "ContainingSymbol", "Message", "RelatedUsageId", "RelatedSqlId"],
                warnings);
            WriteCsv(Path.Combine(reportDir, "table-summary.csv"),
                ["ObjectType", "ObjectName", "FullName", "Operations", "UsageCount", "Files", "ConfidenceMax", "HasDynamicUsage", "HasUnknownUsage"],
                BuildSummary(usages));

            File.WriteAllText(Path.Combine(reportDir, "run-summary.txt"),
                $"Input: {Path.GetFullPath(input)}{Environment.NewLine}Files analyzed: {files.Length}{Environment.NewLine}Table usages: {usages.Count}{Environment.NewLine}SQL snippets: {snippets.Count}{Environment.NewLine}",
                new UTF8Encoding(true));

            Console.WriteLine($"Output: {reportDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            result[key] = value;
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: --{name}");

    private static void ValidateReadOnly(string input, string outRoot)
    {
        var fullInput = Path.GetFullPath(input);
        if (!File.Exists(fullInput) && !Directory.Exists(fullInput)) throw new FileNotFoundException(fullInput);
        var protectedRoot = File.Exists(fullInput) ? Path.GetDirectoryName(fullInput)! : fullInput;
        var output = EnsureTrailingSeparator(Path.GetFullPath(outRoot));
        var root = EnsureTrailingSeparator(Path.GetFullPath(protectedRoot));
        if (output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output directory must not be inside input directory.");
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string CreateReportDirectory(string outRoot, string input)
    {
        Directory.CreateDirectory(outRoot);
        var inputName = Directory.Exists(input) ? new DirectoryInfo(input).Name : Path.GetFileName(input);
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Regex.Replace(inputName, "[^A-Za-z0-9-]", "_").Trim('_');
        var path = Path.Combine(outRoot, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException($"Report directory already exists: {path}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IEnumerable<string> GetFiles(string input)
    {
        var full = Path.GetFullPath(input);
        if (File.Exists(full))
        {
            if (IncludedExtensions.Any(ext => full.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) yield return full;
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            if (!IncludedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;
            var relative = Path.GetRelativePath(full, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(seg => ExcludedDirs.Contains(seg, StringComparer.OrdinalIgnoreCase))) continue;
            yield return file;
        }
    }

    private static (bool Success, string Text, string? Error) ReadText(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return (true, Encoding.UTF8.GetString(bytes), null);
            try
            {
                return (true, new UTF8Encoding(false, true).GetString(bytes), null);
            }
            catch (DecoderFallbackException)
            {
                return (true, Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return (false, "", ex.Message);
        }
    }

    private static void AnalyzeSource(string source, string relative, List<string[]> usages, List<string[]> snippets, List<string[]> unresolved, ref int sqlSeq, ref int usageSeq)
    {
        var variables = Regex.Matches(source, @"\b(?:const\s+)?(?:var|string)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<expr>.*?);", RegexOptions.Singleline)
            .Cast<Match>()
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["expr"].Value.Trim(), StringComparer.Ordinal);

        foreach (var sink in SinkNames)
        {
            foreach (Match match in Regex.Matches(source, @"\b" + Regex.Escape(sink) + @"\s*\("))
            {
                var close = FindClosing(source, source.IndexOf('(', match.Index));
                if (close < 0) continue;
                var args = SplitTopLevel(source[(source.IndexOf('(', match.Index) + 1)..close], ',');
                if (args.Count == 0) continue;
                var sqlId = $"S{++sqlSeq:000000}";
                var loc = Location(source, match.Index);
                var sql = Resolve(args[0].Trim(), variables);
                if (sql is null)
                {
                    unresolved.Add([sqlId, relative, loc.Line.ToString(), loc.Column.ToString(), "", sink, "RuntimeValue", args[0].Trim(), "", ""]);
                    continue;
                }

                snippets.Add([sqlId, relative, loc.Line.ToString(), loc.Column.ToString(), "", sink, "certain", sql, Regex.Replace(sql, @"\s+", " ").Trim(), "", ""]);
                foreach (var obj in ExtractObjects(sql))
                {
                    usages.Add([$"U{++usageSeq:000000}", sqlId, obj.Type, obj.ObjectName, obj.FullName, obj.Operation, obj.Role, "certain", relative, loc.Line.ToString(), loc.Column.ToString(), "", sink, "", "0", "", "", ""]);
                }
            }
        }
    }

    private static string? Resolve(string expr, Dictionary<string, string> variables)
    {
        expr = expr.Trim();
        if (variables.TryGetValue(expr, out var assigned)) return Resolve(assigned, variables);
        if (expr.StartsWith('"') && expr.EndsWith('"')) return Regex.Unescape(expr[1..^1]);
        var format = Regex.Match(expr, @"^(?:string\.)?Format\s*\((?<args>.*)\)$", RegexOptions.Singleline);
        if (format.Success)
        {
            var parts = SplitTopLevel(format.Groups["args"].Value, ',');
            var result = Resolve(parts[0], variables);
            if (result is null) return null;
            for (var i = 1; i < parts.Count; i++)
            {
                var value = Resolve(parts[i], variables);
                if (value is null) return null;
                result = result.Replace("{" + (i - 1) + "}", value, StringComparison.Ordinal);
            }
            return result;
        }
        var plus = SplitTopLevel(expr, '+');
        if (plus.Count > 1)
        {
            var builder = new StringBuilder();
            foreach (var part in plus)
            {
                var value = Resolve(part, variables);
                if (value is null) return null;
                builder.Append(value);
            }
            return builder.ToString();
        }
        return null;
    }

    private static List<(string Type, string ObjectName, string FullName, string Operation, string Role)> ExtractObjects(string sql)
    {
        var list = new List<(string, string, string, string, string)>();
        Add(sql, list, @"\bFROM\s+(?<name>[#@]?\w+(?:\.\w+)*)", "SELECT", "Source");
        Add(sql, list, @"\bJOIN\s+(?<name>[#@]?\w+(?:\.\w+)*)", "SELECT", "Join");
        Add(sql, list, @"\bUPDATE\s+(?<name>[#@]?\w+(?:\.\w+)*)", "UPDATE", "Target");
        Add(sql, list, @"\bINSERT\s+INTO\s+(?<name>[#@]?\w+(?:\.\w+)*)", "INSERT", "Target");
        Add(sql, list, @"\bDELETE\s+FROM\s+(?<name>[#@]?\w+(?:\.\w+)*)", "DELETE", "Target");
        Add(sql, list, @"\bEXEC(?:UTE)?\s+(?<name>[#@]?\w+(?:\.\w+)*)", "EXEC", "Procedure");
        return list;
    }

    private static void Add(string sql, List<(string Type, string ObjectName, string FullName, string Operation, string Role)> list, string pattern, string operation, string role)
    {
        foreach (Match match in Regex.Matches(sql, pattern, RegexOptions.IgnoreCase))
        {
            var full = match.Groups["name"].Value;
            var name = full.Split('.').Last();
            var type = operation == "EXEC" ? "Procedure" : full.StartsWith('#') ? "TempTable" : full.StartsWith('@') ? "TableVariable" : "TableOrView";
            list.Add((type, name, full, operation, role));
        }
    }

    private static List<string[]> BuildSummary(List<string[]> usages) =>
        usages.GroupBy(row => row[4], StringComparer.OrdinalIgnoreCase)
            .Select(g => new[]
            {
                g.First()[2],
                g.First()[3],
                g.First()[4],
                string.Join("|", g.Select(row => row[5]).Distinct(StringComparer.OrdinalIgnoreCase)),
                g.Count().ToString(),
                string.Join("|", g.Select(row => row[8]).Distinct(StringComparer.OrdinalIgnoreCase)),
                "certain",
                "false",
                "false"
            })
            .ToList();

    private static void WriteCsv(string path, IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", header.Select(Escape)));
        foreach (var row in rows) builder.AppendLine(string.Join(",", row.Select(Escape)));
        File.WriteAllText(path, builder.ToString().ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
    }

    private static string Escape(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static (int Line, int Column) Location(string text, int position)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\n') { line++; column = 1; } else { column++; }
        }
        return (line, column);
    }

    private static int FindClosing(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '"' && (i == 0 || text[i - 1] != '\\')) inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '"' && (i == 0 || text[i - 1] != '\\')) inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == delimiter && depth == 0)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }
        result.Add(text[start..]);
        return result;
    }
}
'@
}

function Initialize-Source {
    $srcRoot = Join-Path $PSScriptRoot "src"
    if ((Test-Path $srcRoot) -and -not $Force) {
        Write-Host "src already exists. Skipping init. Use -Force to replace it."
        return
    }

    foreach ($entry in $Files.GetEnumerator()) {
        $target = Join-Path $PSScriptRoot $entry.Key
        if ((Test-Path $target) -and -not $Force) {
            throw "File already exists: $target. Use -Force to replace it."
        }

        $directory = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [System.IO.File]::WriteAllText($target, $entry.Value, [System.Text.UTF8Encoding]::new($false))
    }

    Write-Host "Source expanded under: $srcRoot"
}

function Build-Tool {
    $project = Join-Path $PSScriptRoot "src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj"
    if (-not (Test-Path $project)) {
        Initialize-Source
    }

    & dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

function Run-Tool {
    if ([string]::IsNullOrWhiteSpace($Input)) {
        throw "run requires -Input."
    }
    if ([string]::IsNullOrWhiteSpace($Out)) {
        throw "run requires -Out."
    }

    $dll = Join-Path $PSScriptRoot "src/TableAnalyzer.Cli/bin/Release/net10.0/TableAnalyzer.Cli.dll"
    if (-not (Test-Path $dll)) {
        Build-Tool
    }

    & dotnet $dll analyze --input $Input --out $Out
    if ($LASTEXITCODE -ne 0) {
        throw "TableAnalyzer run failed."
    }
}

switch ($Command) {
    "init" { Initialize-Source }
    "build" { Build-Tool }
    "run" { Run-Tool }
    "all" {
        Initialize-Source
        Build-Tool
        if (-not [string]::IsNullOrWhiteSpace($Input) -or -not [string]::IsNullOrWhiteSpace($Out)) {
            Run-Tool
        }
    }
}
