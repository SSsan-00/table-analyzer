namespace TableAnalyzer.Core;

public sealed record TableUsageRow(
    string UsageId,
    string SqlId,
    string ObjectType,
    string ObjectName,
    string FullName,
    string Operation,
    string SqlRole,
    string Confidence,
    string SourceFile,
    int Line,
    int Column,
    string ContainingSymbol,
    string SqlExecutionMethod,
    string CallChain,
    int CallDepth,
    string DynamicPattern,
    string CandidateGroupId,
    string Notes);

public sealed record TableSummaryRow(
    string ObjectType,
    string ObjectName,
    string FullName,
    string Operations,
    int UsageCount,
    string Files,
    string ConfidenceMax,
    bool HasDynamicUsage,
    bool HasUnknownUsage);

public sealed record DynamicSqlRow(
    string CandidateGroupId,
    string SourceFile,
    int Line,
    string ContainingSymbol,
    string DynamicPattern,
    int CandidateCount,
    string Candidates,
    string Confidence,
    string ResolutionPath,
    string Notes);

public sealed record UnresolvedSqlRow(
    string SqlId,
    string SourceFile,
    int Line,
    int Column,
    string ContainingSymbol,
    string SqlExecutionMethod,
    string Reason,
    string Expression,
    string CallChain,
    string Notes);

public sealed record SqlSnippetRow(
    string SqlId,
    string SourceFile,
    int Line,
    int Column,
    string ContainingSymbol,
    string SqlExecutionMethod,
    string Confidence,
    string SqlText,
    string NormalizedSqlText,
    string CallChain,
    string Notes);

public sealed record WarningRow(
    string WarningId,
    string Severity,
    string Code,
    string SourceFile,
    int Line,
    string ContainingSymbol,
    string Message,
    string RelatedUsageId,
    string RelatedSqlId);

public sealed record AnalysisProgress(
    string Stage,
    int Completed,
    int Total,
    string CurrentFile);

public sealed record ReportMetadataRow(string Name, string Value);

public sealed class AnalysisResult
{
    public List<TableUsageRow> TableUsages { get; } = [];

    public List<TableSummaryRow> TableSummaries { get; } = [];

    public List<DynamicSqlRow> DynamicSql { get; } = [];

    public List<UnresolvedSqlRow> UnresolvedSql { get; } = [];

    public List<SqlSnippetRow> SqlSnippets { get; } = [];

    public List<WarningRow> Warnings { get; } = [];

    public List<ReportMetadataRow> ReportMetadata { get; } = [];
}
