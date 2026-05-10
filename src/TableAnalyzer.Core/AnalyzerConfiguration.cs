namespace TableAnalyzer.Core;

public sealed class AnalyzerConfiguration
{
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [".cs", ".cshtml.cs"];

    public IReadOnlyList<string> ExcludeDirectoryNames { get; init; } =
    [
        "bin",
        "obj",
        ".git",
        ".vs",
        "node_modules",
        "Migrations"
    ];

    public IReadOnlyList<SqlExecutionMethodSpec> SqlExecutionMethods { get; init; } =
    [
        new("Execute", 0),
        new("ExecuteAsync", 0),
        new("Query", 0),
        new("QueryAsync", 0),
        new("QueryFirst", 0),
        new("QueryFirstOrDefault", 0),
        new("QuerySingle", 0),
        new("QuerySingleOrDefault", 0),
        new("ExecuteSqlRaw", 0),
        new("ExecuteSqlRawAsync", 0),
        new("FromSqlRaw", 0),
        new("FromSqlRawInterpolated", 0),
        new("SqlQueryRaw", 0),
        new("SqlQuery", 0),
        new("SqlCommand", 0)
    ];

    public int MaxCallDepth { get; init; } = 8;

    public int MaxCandidatesPerExpression { get; init; } = 50;
}
