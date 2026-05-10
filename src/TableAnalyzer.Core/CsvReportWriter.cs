using System.Text;

namespace TableAnalyzer.Core;

public sealed class CsvReportWriter
{
    public void Write(string reportDirectory, AnalysisResult result)
    {
        Directory.CreateDirectory(reportDirectory);

        var summaries = result.TableSummaries.Count > 0
            ? result.TableSummaries
            : BuildSummaries(result.TableUsages);

        WriteCsv(Path.Combine(reportDirectory, "table-usages.csv"),
        [
            "UsageId", "SqlId", "ObjectType", "ObjectName", "FullName", "Operation", "SqlRole", "Confidence",
            "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "CallChain", "CallDepth",
            "DynamicPattern", "CandidateGroupId", "Notes"
        ], result.TableUsages.Select(row => new[]
        {
            row.UsageId, row.SqlId, row.ObjectType, row.ObjectName, row.FullName, row.Operation, row.SqlRole,
            row.Confidence, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
            row.SqlExecutionMethod, row.CallChain, row.CallDepth.ToString(), row.DynamicPattern,
            row.CandidateGroupId, row.Notes
        }));

        WriteCsv(Path.Combine(reportDirectory, "table-summary.csv"),
        [
            "ObjectType", "ObjectName", "FullName", "Operations", "UsageCount", "Files", "ConfidenceMax",
            "HasDynamicUsage", "HasUnknownUsage"
        ], summaries.Select(row => new[]
        {
            row.ObjectType, row.ObjectName, row.FullName, row.Operations, row.UsageCount.ToString(), row.Files,
            row.ConfidenceMax, ToCsvBool(row.HasDynamicUsage), ToCsvBool(row.HasUnknownUsage)
        }));

        WriteCsv(Path.Combine(reportDirectory, "dynamic-sql.csv"),
        [
            "CandidateGroupId", "SourceFile", "Line", "ContainingSymbol", "DynamicPattern", "CandidateCount",
            "Candidates", "Confidence", "ResolutionPath", "Notes"
        ], result.DynamicSql.Select(row => new[]
        {
            row.CandidateGroupId, row.SourceFile, row.Line.ToString(), row.ContainingSymbol, row.DynamicPattern,
            row.CandidateCount.ToString(), row.Candidates, row.Confidence, row.ResolutionPath, row.Notes
        }));

        WriteCsv(Path.Combine(reportDirectory, "unresolved-sql.csv"),
        [
            "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Reason",
            "Expression", "CallChain", "Notes"
        ], result.UnresolvedSql.Select(row => new[]
        {
            row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
            row.SqlExecutionMethod, row.Reason, row.Expression, row.CallChain, row.Notes
        }));

        WriteCsv(Path.Combine(reportDirectory, "sql-snippets.csv"),
        [
            "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Confidence",
            "SqlText", "NormalizedSqlText", "CallChain", "Notes"
        ], result.SqlSnippets.Select(row => new[]
        {
            row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
            row.SqlExecutionMethod, row.Confidence, row.SqlText, row.NormalizedSqlText, row.CallChain, row.Notes
        }));

        WriteCsv(Path.Combine(reportDirectory, "warnings.csv"),
        [
            "WarningId", "Severity", "Code", "SourceFile", "Line", "ContainingSymbol", "Message",
            "RelatedUsageId", "RelatedSqlId"
        ], result.Warnings.Select(row => new[]
        {
            row.WarningId, row.Severity, row.Code, row.SourceFile, row.Line.ToString(), row.ContainingSymbol,
            row.Message, row.RelatedUsageId, row.RelatedSqlId
        }));

        WriteText(Path.Combine(reportDirectory, "run-summary.txt"), string.Join(Environment.NewLine,
        [
            $"GeneratedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Table usages: {result.TableUsages.Count}",
            $"Table summaries: {summaries.Count}",
            $"Dynamic SQL: {result.DynamicSql.Count}",
            $"Unresolved SQL: {result.UnresolvedSql.Count}",
            $"SQL snippets: {result.SqlSnippets.Count}",
            $"Warnings: {result.Warnings.Count}"
        ]) + Environment.NewLine);
    }

    private static List<TableSummaryRow> BuildSummaries(IEnumerable<TableUsageRow> usages)
    {
        static int ConfidenceRank(string confidence) => confidence switch
        {
            "certain" => 5,
            "probable" => 4,
            "dynamic" => 3,
            "unknown" => 2,
            "unresolved" => 1,
            _ => 0
        };

        return usages
            .GroupBy(row => (row.ObjectType, FullName: row.FullName.ToUpperInvariant()))
            .Select(group =>
            {
                var first = group.First();
                var operations = string.Join("|", group.Select(row => row.Operation).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
                var files = string.Join("|", group.Select(row => row.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
                var confidence = group.Select(row => row.Confidence).OrderByDescending(ConfidenceRank).FirstOrDefault() ?? "";
                return new TableSummaryRow(
                    first.ObjectType,
                    first.ObjectName,
                    first.FullName,
                    operations,
                    group.Count(),
                    files,
                    confidence,
                    group.Any(row => row.Confidence == "dynamic" || !string.IsNullOrEmpty(row.DynamicPattern)),
                    group.Any(row => row.Confidence == "unknown" || row.Confidence == "unresolved"));
            })
            .OrderBy(row => row.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        WriteText(path, builder.ToString().ReplaceLineEndings("\r\n"));
    }

    private static void WriteText(string path, string text)
    {
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static string ToCsvBool(bool value)
    {
        return value ? "true" : "false";
    }
}
