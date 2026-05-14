namespace TableAnalyzer.Core;

internal sealed record ReportTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

internal sealed record ReportData(IReadOnlyList<ReportTable> Tables, IReadOnlyList<string> SummaryLines);

internal static class ReportDataBuilder
{
    public static ReportData Build(AnalysisResult result)
    {
        var summaries = result.TableSummaries.Count > 0
            ? result.TableSummaries
            : BuildSummaries(result.TableUsages);
        var sqlTextById = result.SqlSnippets
            .GroupBy(row => row.SqlId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SqlText, StringComparer.Ordinal);

        var tables = new List<ReportTable>
        {
            new("table-usages",
            [
                "UsageId", "SqlId", "ObjectType", "ObjectName", "FullName", "Operation", "SqlRole", "Confidence",
                "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "CallChain", "CallDepth",
                "DynamicPattern", "CandidateGroupId", "Notes", "SqlText"
            ], result.TableUsages.Select(row => (IReadOnlyList<string>)
            [
                row.UsageId, row.SqlId, row.ObjectType, row.ObjectName, row.FullName, row.Operation, row.SqlRole,
                row.Confidence, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.CallChain, row.CallDepth.ToString(), row.DynamicPattern,
                row.CandidateGroupId, row.Notes, sqlTextById.GetValueOrDefault(row.SqlId, "")
            ]).ToArray()),

            new("table-summary",
            [
                "ObjectType", "ObjectName", "FullName", "Operations", "UsageCount", "Files", "ConfidenceMax",
                "HasDynamicUsage", "HasUnknownUsage"
            ], summaries.Select(row => (IReadOnlyList<string>)
            [
                row.ObjectType, row.ObjectName, row.FullName, row.Operations, row.UsageCount.ToString(), row.Files,
                row.ConfidenceMax, ToCsvBool(row.HasDynamicUsage), ToCsvBool(row.HasUnknownUsage)
            ]).ToArray()),

            new("dynamic-sql",
            [
                "CandidateGroupId", "SourceFile", "Line", "ContainingSymbol", "DynamicPattern", "CandidateCount",
                "Candidates", "Confidence", "ResolutionPath", "Notes"
            ], result.DynamicSql.Select(row => (IReadOnlyList<string>)
            [
                row.CandidateGroupId, row.SourceFile, row.Line.ToString(), row.ContainingSymbol, row.DynamicPattern,
                row.CandidateCount.ToString(), row.Candidates, row.Confidence, row.ResolutionPath, row.Notes
            ]).ToArray()),

            new("unresolved-sql",
            [
                "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Reason",
                "Expression", "CallChain", "Notes"
            ], result.UnresolvedSql.Select(row => (IReadOnlyList<string>)
            [
                row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.Reason, row.Expression, row.CallChain, row.Notes
            ]).ToArray()),

            new("sql-snippets",
            [
                "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Confidence",
                "SqlText", "NormalizedSqlText", "CallChain", "Notes"
            ], result.SqlSnippets.Select(row => (IReadOnlyList<string>)
            [
                row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.Confidence, row.SqlText, row.NormalizedSqlText, row.CallChain, row.Notes
            ]).ToArray()),

            new("warnings",
            [
                "WarningId", "Severity", "Code", "SourceFile", "Line", "ContainingSymbol", "Message",
                "RelatedUsageId", "RelatedSqlId"
            ], result.Warnings.Select(row => (IReadOnlyList<string>)
            [
                row.WarningId, row.Severity, row.Code, row.SourceFile, row.Line.ToString(), row.ContainingSymbol,
                row.Message, row.RelatedUsageId, row.RelatedSqlId
            ]).ToArray())
        };

        var summaryLines = new[]
        {
            $"GeneratedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Table usages: {result.TableUsages.Count}",
            $"Table summaries: {summaries.Count}",
            $"Dynamic SQL: {result.DynamicSql.Count}",
            $"Unresolved SQL: {result.UnresolvedSql.Count}",
            $"SQL snippets: {result.SqlSnippets.Count}",
            $"Warnings: {result.Warnings.Count}"
        };

        return new ReportData(tables, summaryLines);
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

    private static string ToCsvBool(bool value)
    {
        return value ? "true" : "false";
    }
}
