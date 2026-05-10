namespace TableAnalyzer.Core;

public sealed record SqlExecutionMethodSpec(string Name, int SqlArgumentIndex, string? TypeName = null);
