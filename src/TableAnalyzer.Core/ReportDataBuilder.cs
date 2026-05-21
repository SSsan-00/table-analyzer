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
        var queryCrudSummaries = BuildQueryCrudSummaries(result.SqlSnippets, result.TableUsages);
        var sourceCrudSummaries = BuildSourceCrudSummaries(queryCrudSummaries, result.UnresolvedSql);

        var tables = new List<ReportTable>
        {
            new("table-usages",
            [
                "UsageId", "SqlId", "ObjectType", "ObjectName", "FullName", "Operation", "SqlRole", "Confidence",
                "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "CallChain", "CallDepth",
                "ExecutionSourceFile", "ExecutionLine", "ExecutionColumn", "DynamicPattern", "CandidateGroupId", "Notes", "SqlText"
            ], result.TableUsages.Select(row => (IReadOnlyList<string>)
            [
                row.UsageId, row.SqlId, row.ObjectType, row.ObjectName, row.FullName, row.Operation, row.SqlRole,
                row.Confidence, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.CallChain, row.CallDepth.ToString(), row.SourceFile, row.Line.ToString(),
                row.Column.ToString(), row.DynamicPattern,
                row.CandidateGroupId, row.Notes, sqlTextById.GetValueOrDefault(row.SqlId, "")
            ]).ToArray()),

            new("query-crud-summary",
            [
                "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "CrudFlags",
                "ReadTables", "CreateTables", "UpdateTables", "DeleteTables", "MergeTables", "ExecuteProcedures",
                "ObjectCount", "Confidence", "SqlText"
            ], queryCrudSummaries.Select(row => (IReadOnlyList<string>)
            [
                row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.CrudFlags, row.ReadTables, row.CreateTables, row.UpdateTables,
                row.DeleteTables, row.MergeTables, row.ExecuteProcedures, row.ObjectCount.ToString(),
                row.Confidence, row.SqlText
            ]).ToArray()),

            new("source-crud-summary",
            [
                "SourceFile", "CrudFlags", "ReadTables", "CreateTables", "UpdateTables", "DeleteTables",
                "MergeTables", "ExecuteProcedures", "QueryCount", "ObjectUsageCount", "DynamicSqlCount",
                "UnresolvedSqlCount", "SqlIds"
            ], sourceCrudSummaries.Select(row => (IReadOnlyList<string>)
            [
                row.SourceFile, row.CrudFlags, row.ReadTables, row.CreateTables, row.UpdateTables, row.DeleteTables,
                row.MergeTables, row.ExecuteProcedures, row.QueryCount.ToString(), row.ObjectUsageCount.ToString(),
                row.DynamicSqlCount.ToString(), row.UnresolvedSqlCount.ToString(), row.SqlIds
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
                "ExecutionSourceFile", "ExecutionLine", "ExecutionColumn", "Expression", "CallChain", "Notes"
            ], result.UnresolvedSql.Select(row => (IReadOnlyList<string>)
            [
                row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.Reason, row.SourceFile, row.Line.ToString(), row.Column.ToString(),
                row.Expression, row.CallChain, row.Notes
            ]).ToArray()),

            new("sql-snippets",
            [
                "SqlId", "SourceFile", "Line", "Column", "ContainingSymbol", "SqlExecutionMethod", "Confidence",
                "ExecutionSourceFile", "ExecutionLine", "ExecutionColumn", "SqlText", "NormalizedSqlText", "CallChain", "Notes"
            ], result.SqlSnippets.Select(row => (IReadOnlyList<string>)
            [
                row.SqlId, row.SourceFile, row.Line.ToString(), row.Column.ToString(), row.ContainingSymbol,
                row.SqlExecutionMethod, row.Confidence, row.SourceFile, row.Line.ToString(), row.Column.ToString(),
                row.SqlText, row.NormalizedSqlText, row.CallChain, row.Notes
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

        var summaryLines = result.ReportMetadata
            .Select(row => $"{row.Name}: {row.Value}")
            .Concat(
            [
                $"GeneratedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Table usages: {result.TableUsages.Count}",
                $"Query CRUD summaries: {queryCrudSummaries.Count}",
                $"Source CRUD summaries: {sourceCrudSummaries.Count}",
                $"Table summaries: {summaries.Count}",
                $"Dynamic SQL: {result.DynamicSql.Count}",
                $"Unresolved SQL: {result.UnresolvedSql.Count}",
                $"SQL snippets: {result.SqlSnippets.Count}",
                $"Warnings: {result.Warnings.Count}"
            ])
            .ToArray();

        return new ReportData(tables, summaryLines);
    }

    private static List<QueryCrudSummaryRow> BuildQueryCrudSummaries(
        IEnumerable<SqlSnippetRow> snippets,
        IEnumerable<TableUsageRow> usages)
    {
        var usagesBySqlId = usages
            .GroupBy(row => row.SqlId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return snippets
            .OrderBy(row => row.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Line)
            .ThenBy(row => row.Column)
            .ThenBy(row => row.SqlId, StringComparer.Ordinal)
            .Select(snippet =>
            {
                var rows = usagesBySqlId.GetValueOrDefault(snippet.SqlId, []);
                var readTables = JoinObjects(rows.Where(IsReadUsage));
                var createTables = JoinObjects(rows.Where(IsCreateUsage));
                var updateTables = JoinObjects(rows.Where(IsUpdateUsage));
                var deleteTables = JoinObjects(rows.Where(IsDeleteUsage));
                var mergeTables = JoinObjects(rows.Where(IsMergeUsage));
                var executeProcedures = JoinObjects(rows.Where(IsExecuteUsage));

                return new QueryCrudSummaryRow(
                    snippet.SqlId,
                    snippet.SourceFile,
                    snippet.Line,
                    snippet.Column,
                    snippet.ContainingSymbol,
                    snippet.SqlExecutionMethod,
                    BuildCrudFlags(readTables, createTables, updateTables, deleteTables, mergeTables, executeProcedures),
                    readTables,
                    createTables,
                    updateTables,
                    deleteTables,
                    mergeTables,
                    executeProcedures,
                    rows.Select(row => row.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    snippet.Confidence,
                    snippet.SqlText);
            })
            .ToList();
    }

    private static List<SourceCrudSummaryRow> BuildSourceCrudSummaries(
        IEnumerable<QueryCrudSummaryRow> queryRows,
        IEnumerable<UnresolvedSqlRow> unresolvedRows)
    {
        var queriesBySource = queryRows
            .GroupBy(row => row.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var unresolvedBySource = unresolvedRows
            .GroupBy(row => row.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var sources = queriesBySource.Keys
            .Concat(unresolvedBySource.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase);

        return sources
            .Select(source =>
            {
                var rows = queriesBySource.GetValueOrDefault(source, []);
                var readTables = JoinSummaryValues(rows.Select(row => row.ReadTables));
                var createTables = JoinSummaryValues(rows.Select(row => row.CreateTables));
                var updateTables = JoinSummaryValues(rows.Select(row => row.UpdateTables));
                var deleteTables = JoinSummaryValues(rows.Select(row => row.DeleteTables));
                var mergeTables = JoinSummaryValues(rows.Select(row => row.MergeTables));
                var executeProcedures = JoinSummaryValues(rows.Select(row => row.ExecuteProcedures));

                return new SourceCrudSummaryRow(
                    source,
                    BuildCrudFlags(readTables, createTables, updateTables, deleteTables, mergeTables, executeProcedures),
                    readTables,
                    createTables,
                    updateTables,
                    deleteTables,
                    mergeTables,
                    executeProcedures,
                    rows.Length,
                    rows.Sum(row => row.ObjectCount),
                    rows.Count(row => string.Equals(row.Confidence, "dynamic", StringComparison.OrdinalIgnoreCase)),
                    unresolvedBySource.GetValueOrDefault(source, 0),
                    JoinSummaryValues(rows.Select(row => row.SqlId)));
            })
            .ToList();
    }

    private static bool IsReadUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "SELECT", StringComparison.OrdinalIgnoreCase) ||
               row.SqlRole is "Source" or "Join";
    }

    private static bool IsCreateUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "INSERT", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(row.SqlRole, "Target", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpdateUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "UPDATE", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(row.SqlRole, "Target", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeleteUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "DELETE", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(row.SqlRole, "Target", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMergeUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "MERGE", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(row.SqlRole, "Target", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExecuteUsage(TableUsageRow row)
    {
        return string.Equals(row.Operation, "EXEC", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(row.ObjectType, "Procedure", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCrudFlags(
        string readTables,
        string createTables,
        string updateTables,
        string deleteTables,
        string mergeTables,
        string executeProcedures)
    {
        var flags = new List<string>();
        if (!string.IsNullOrEmpty(createTables))
        {
            flags.Add("Create");
        }

        if (!string.IsNullOrEmpty(readTables))
        {
            flags.Add("Read");
        }

        if (!string.IsNullOrEmpty(updateTables))
        {
            flags.Add("Update");
        }

        if (!string.IsNullOrEmpty(deleteTables))
        {
            flags.Add("Delete");
        }

        if (!string.IsNullOrEmpty(mergeTables))
        {
            flags.Add("Merge");
        }

        if (!string.IsNullOrEmpty(executeProcedures))
        {
            flags.Add("Execute");
        }

        return string.Join("|", flags);
    }

    private static string JoinObjects(IEnumerable<TableUsageRow> rows)
    {
        return string.Join("|", rows
            .Select(row => row.FullName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    private static string JoinSummaryValues(IEnumerable<string> values)
    {
        return string.Join("|", values
            .SelectMany(value => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
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

    private sealed record QueryCrudSummaryRow(
        string SqlId,
        string SourceFile,
        int Line,
        int Column,
        string ContainingSymbol,
        string SqlExecutionMethod,
        string CrudFlags,
        string ReadTables,
        string CreateTables,
        string UpdateTables,
        string DeleteTables,
        string MergeTables,
        string ExecuteProcedures,
        int ObjectCount,
        string Confidence,
        string SqlText);

    private sealed record SourceCrudSummaryRow(
        string SourceFile,
        string CrudFlags,
        string ReadTables,
        string CreateTables,
        string UpdateTables,
        string DeleteTables,
        string MergeTables,
        string ExecuteProcedures,
        int QueryCount,
        int ObjectUsageCount,
        int DynamicSqlCount,
        int UnresolvedSqlCount,
        string SqlIds);
}
