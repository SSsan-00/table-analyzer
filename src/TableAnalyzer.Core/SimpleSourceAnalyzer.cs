using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TableAnalyzer.Core;

public sealed class SimpleSourceAnalyzer
{
    public AnalysisResult Analyze(IReadOnlyList<SourceFile> files, AnalyzerConfiguration configuration)
    {
        return Analyze(files, configuration, progress: null);
    }

    public AnalysisResult Analyze(IReadOnlyList<SourceFile> files, AnalyzerConfiguration configuration, IProgress<AnalysisProgress>? progress)
    {
        return Analyze(files, files, configuration, progress);
    }

    public AnalysisResult Analyze(
        IReadOnlyList<SourceFile> files,
        IReadOnlyList<SourceFile> contextFiles,
        AnalyzerConfiguration configuration,
        IProgress<AnalysisProgress>? progress = null)
    {
        var result = new AnalysisResult();
        var reader = new SourceTextReader();
        var ids = new IdSequence();
        var parsedFiles = ReadAndParseFiles(MergeFiles(files, contextFiles), reader, result, ids, progress);
        var semanticContext = SemanticAnalysisContext.Create(parsedFiles.Values.Select(file => file.Root.SyntaxTree));
        var methods = BuildMethodIndex(parsedFiles.Values, semanticContext);
        var evaluator = new ExpressionEvaluator(methods, semanticContext, configuration);
        var callContexts = new MethodCallContextResolver(parsedFiles.Values, methods, evaluator);
        var sourceFilesByTree = parsedFiles.Values.ToDictionary(file => file.Root.SyntaxTree, file => file.SourceFile);
        var completed = 0;

        progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, ""));
        foreach (var file in files)
        {
            if (!parsedFiles.TryGetValue(Path.GetFullPath(file.FullPath), out var parsed))
            {
                completed++;
                progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, file.RelativePath));
                continue;
            }

            AnalyzeFile(file, parsed.Root, methods, semanticContext, configuration, evaluator, callContexts, sourceFilesByTree, result, ids);
            completed++;
            progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, file.RelativePath));
        }

        return result;
    }

    private static Dictionary<string, ParsedSourceFile> ReadAndParseFiles(
        IReadOnlyList<SourceFile> files,
        SourceTextReader reader,
        AnalysisResult result,
        IdSequence ids,
        IProgress<AnalysisProgress>? progress)
    {
        var parsedFiles = new Dictionary<string, ParsedSourceFile>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        progress?.Report(new AnalysisProgress("indexing", completed, files.Count, ""));
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file.FullPath);
            if (parsedFiles.ContainsKey(fullPath))
            {
                completed++;
                progress?.Report(new AnalysisProgress("indexing", completed, files.Count, file.RelativePath));
                continue;
            }

            var read = reader.Read(fullPath);
            if (!read.Success)
            {
                result.Warnings.Add(new WarningRow(ids.NextWarningId(), "Medium", "FILE_READ_FAILED", file.RelativePath, 0, "", read.ErrorMessage ?? "Failed to read file.", "", ""));
                completed++;
                progress?.Report(new AnalysisProgress("indexing", completed, files.Count, file.RelativePath));
                continue;
            }

            try
            {
                var tree = CSharpSyntaxTree.ParseText(read.Text, path: fullPath);
                parsedFiles[fullPath] = new ParsedSourceFile(file, tree.GetCompilationUnitRoot());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                result.Warnings.Add(new WarningRow(ids.NextWarningId(), "Medium", "FILE_PARSE_FAILED", file.RelativePath, 0, "", ex.Message, "", ""));
            }

            completed++;
            progress?.Report(new AnalysisProgress("indexing", completed, files.Count, file.RelativePath));
        }

        return parsedFiles;
    }

    private static IReadOnlyList<SourceFile> MergeFiles(IReadOnlyList<SourceFile> first, IReadOnlyList<SourceFile> second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SourceFile>();
        foreach (var file in first.Concat(second))
        {
            if (seen.Add(Path.GetFullPath(file.FullPath)))
            {
                merged.Add(file);
            }
        }

        return merged;
    }

    private static MethodIndex BuildMethodIndex(IEnumerable<ParsedSourceFile> parsedFiles, SemanticAnalysisContext semanticContext)
    {
        var methods = parsedFiles
            .SelectMany(file => file.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .ToArray();
        return MethodIndex.Create(methods, semanticContext);
    }

    private static void AnalyzeFile(
        SourceFile file,
        CompilationUnitSyntax root,
        MethodIndex methods,
        SemanticAnalysisContext semanticContext,
        AnalyzerConfiguration configuration,
        ExpressionEvaluator evaluator,
        MethodCallContextResolver callContexts,
        IReadOnlyDictionary<SyntaxTree, SourceFile> sourceFilesByTree,
        AnalysisResult result,
        IdSequence ids)
    {
        var reachable = FindReachableSqlInvocations(root, file, methods, configuration, semanticContext, sourceFilesByTree);
        var emittedSqlByMethod = new HashSet<string>(StringComparer.Ordinal);
        AnalyzeInvocations(reachable.Invocations, skipDuplicateSqlStrings: false);
        AnalyzeInvocations(FindSqlStringInvocations(reachable.Methods, file, sourceFilesByTree), skipDuplicateSqlStrings: true);

        void AnalyzeInvocations(IEnumerable<SqlInvocation> invocations, bool skipDuplicateSqlStrings)
        {
            foreach (var invocation in invocations
                         .OrderBy(invocation => invocation.SourceFile.RelativePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(invocation => invocation.Syntax.SpanStart)
                         .ThenBy(invocation => invocation.SqlExpression.SpanStart))
            {
                var sqlId = ids.NextSqlId();
                var location = GetLocation(invocation.Syntax);
                var containingMethod = invocation.Syntax.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var containingSymbol = GetContainingSymbol(containingMethod);
                var sourceFile = invocation.SourceFile;
                var parameterContexts = containingMethod is null
                    ? [new Dictionary<string, SymbolicValue>(StringComparer.Ordinal)]
                    : callContexts.GetParameterContexts(containingMethod, reachable.Methods);
                var evaluated = evaluator.Evaluate(invocation.SqlExpression, containingMethod, parameterContexts);

                if (evaluated.Candidates.Count == 0)
                {
                    if ((invocation.AutoDetectSqlArgument || invocation.IsSqlStringCandidate) &&
                        !LooksLikeSqlCarrier(invocation.SqlExpression, invocation.ArgumentName))
                    {
                        continue;
                    }

                    var pattern = string.IsNullOrEmpty(evaluated.Pattern) ? $"{{{invocation.SqlExpression}}}" : evaluated.Pattern;
                    if (skipDuplicateSqlStrings && emittedSqlByMethod.Contains(CreateEmittedSqlKey(sourceFile, pattern)))
                    {
                        continue;
                    }

                    emittedSqlByMethod.Add(CreateEmittedSqlKey(sourceFile, pattern));
                    result.UnresolvedSql.Add(new UnresolvedSqlRow(sqlId, sourceFile.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "RuntimeValue", invocation.SqlExpression.ToString(), containingSymbol, evaluated.Notes));
                    result.SqlSnippets.Add(new SqlSnippetRow(sqlId, sourceFile.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "unknown", pattern, NormalizeSql(pattern), containingSymbol, evaluated.Notes));
                    continue;
                }

                var candidates = BuildSqlCandidates(evaluated, invocation.AutoDetectSqlArgument);
                if (candidates.Count == 0)
                {
                    continue;
                }

                if (skipDuplicateSqlStrings)
                {
                    candidates = candidates
                        .Where(candidate => !emittedSqlByMethod.Contains(CreateEmittedSqlKey(sourceFile, candidate.Sql)))
                        .ToArray();
                    if (candidates.Count == 0)
                    {
                        continue;
                    }
                }

                var confidence = DetermineConfidence(candidates.Select(candidate => candidate.Sql).ToArray(), evaluated.Confidence);
                string candidateGroupId = "";
                if (candidates.Count > 1 || confidence is "dynamic" or "unknown")
                {
                    candidateGroupId = ids.NextCandidateGroupId();
                    result.DynamicSql.Add(new DynamicSqlRow(
                        candidateGroupId,
                        sourceFile.RelativePath,
                        location.Line,
                        containingSymbol,
                        evaluated.Pattern,
                        candidates.Count,
                        string.Join("|", candidates.Select(candidate => candidate.Sql)),
                        confidence,
                        evaluated.ResolutionPath,
                        evaluated.Notes));
                }

                foreach (var candidate in candidates)
                {
                    emittedSqlByMethod.Add(CreateEmittedSqlKey(sourceFile, candidate.Sql));

                    var currentSqlId = candidates.Count == 1 ? sqlId : ids.NextSqlId();
                    result.SqlSnippets.Add(new SqlSnippetRow(currentSqlId, sourceFile.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, confidence, candidate.Sql, NormalizeSql(candidate.Sql), containingSymbol, evaluated.Notes));

                    if (candidate.Objects.Count == 0)
                    {
                        result.UnresolvedSql.Add(new UnresolvedSqlRow(currentSqlId, sourceFile.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "NoSqlObjectsFound", invocation.SqlExpression.ToString(), containingSymbol, ""));
                        continue;
                    }

                    foreach (var sqlObject in candidate.Objects)
                    {
                        result.TableUsages.Add(new TableUsageRow(
                            ids.NextUsageId(),
                            currentSqlId,
                            sqlObject.ObjectType,
                            sqlObject.ObjectName,
                            sqlObject.FullName,
                            sqlObject.Operation,
                            sqlObject.SqlRole,
                            confidence,
                            sourceFile.RelativePath,
                            location.Line,
                            location.Column,
                            containingSymbol,
                            invocation.MethodName,
                            containingSymbol,
                            0,
                            candidateGroupId.Length > 0 ? evaluated.Pattern : "",
                            candidateGroupId,
                            evaluated.Notes));
                    }
                }
            }
        }
    }

    private static string CreateEmittedSqlKey(SourceFile sourceFile, string sql)
    {
        return $"{sourceFile.RelativePath}\n{NormalizeSql(sql)}";
    }

    private static ReachableSqlInvocations FindReachableSqlInvocations(
        CompilationUnitSyntax root,
        SourceFile rootSourceFile,
        MethodIndex methods,
        AnalyzerConfiguration configuration,
        SemanticAnalysisContext semanticContext,
        IReadOnlyDictionary<SyntaxTree, SourceFile> sourceFilesByTree)
    {
        var invocations = new List<SqlInvocation>();
        var seenMethods = new HashSet<MethodDeclarationSyntax>();
        var seenInvocations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            VisitMethod(method);
        }

        return new ReachableSqlInvocations(invocations, seenMethods);

        void VisitMethod(MethodDeclarationSyntax method)
        {
            if (!seenMethods.Add(method))
            {
                return;
            }

            var sourceFile = sourceFilesByTree.TryGetValue(method.SyntaxTree, out var found)
                ? found
                : rootSourceFile;

            foreach (var sqlInvocation in FindSqlInvocations(method, sourceFile, configuration, semanticContext))
            {
                if (seenInvocations.Add(CreateSqlInvocationKey(sqlInvocation)))
                {
                    invocations.Add(sqlInvocation);
                }
            }

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                foreach (var target in methods.ResolveInvocationCandidates(invocation))
                {
                    VisitMethod(target);
                }
            }
        }
    }

    private static string CreateSqlInvocationKey(SqlInvocation invocation)
    {
        return $"{invocation.Syntax.SyntaxTree.FilePath}:{invocation.Syntax.SpanStart}:{invocation.SqlExpression.SpanStart}";
    }

    private static IReadOnlyList<SqlInvocation> FindSqlStringInvocations(
        IEnumerable<MethodDeclarationSyntax> methods,
        SourceFile rootSourceFile,
        IReadOnlyDictionary<SyntaxTree, SourceFile> sourceFilesByTree)
    {
        var invocations = new List<SqlInvocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            var sourceFile = sourceFilesByTree.TryGetValue(method.SyntaxTree, out var found)
                ? found
                : rootSourceFile;
            foreach (var variable in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (variable.Initializer?.Value is not null)
                {
                    AddCandidate(variable.Initializer.Value, variable, variable.Identifier.ValueText, sourceFile);
                }
            }

            foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    AddCandidate(assignment.Right, assignment, GetAssignedName(assignment.Left), sourceFile);
                }
            }

            foreach (var returnStatement in method.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (returnStatement.Expression is not null)
                {
                    AddCandidate(returnStatement.Expression, returnStatement, "", sourceFile);
                }
            }

            foreach (var arrowExpression in method.DescendantNodes().OfType<ArrowExpressionClauseSyntax>())
            {
                AddCandidate(arrowExpression.Expression, arrowExpression, "", sourceFile);
            }

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsStringConstructionInvocation(invocation))
                {
                    continue;
                }

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    AddCandidate(argument.Expression, invocation, GetArgumentName(argument), sourceFile);
                }
            }

            foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (creation.ArgumentList is null)
                {
                    continue;
                }

                foreach (var argument in creation.ArgumentList.Arguments)
                {
                    AddCandidate(argument.Expression, creation, GetArgumentName(argument), sourceFile);
                }
            }
        }

        return invocations;

        void AddCandidate(ExpressionSyntax expression, SyntaxNode syntax, string contextName, SourceFile sourceFile)
        {
            if (!ShouldCollectSqlStringExpression(expression, contextName))
            {
                return;
            }

            var key = $"{expression.SyntaxTree.FilePath}:{expression.SpanStart}:{expression.Span.End}";
            if (!seen.Add(key))
            {
                return;
            }

            invocations.Add(new SqlInvocation(
                "SqlString",
                expression,
                syntax,
                sourceFile,
                contextName,
                AutoDetectSqlArgument: false,
                IsSqlStringCandidate: true));
        }
    }

    private static bool ShouldCollectSqlStringExpression(ExpressionSyntax expression, string contextName)
    {
        return LooksLikeSqlCarrier(expression, contextName) ||
               ContainsSqlKeyword(expression.ToString());
    }

    private static bool IsStringConstructionInvocation(InvocationExpressionSyntax invocation)
    {
        var methodName = GetCallableName(invocation.Expression);
        return methodName is "Format" or "Append" or "AppendLine" or "AppendFormat";
    }

    private static string GetAssignedName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            ElementAccessExpressionSyntax elementAccess => elementAccess.Expression.ToString(),
            _ => ""
        };
    }

    private static IReadOnlyList<SqlCandidate> BuildSqlCandidates(SymbolicValue evaluated, bool onlySqlObjects)
    {
        var candidates = new List<SqlCandidate>();
        foreach (var candidate in evaluated.Candidates)
        {
            if (onlySqlObjects && !LooksLikeSqlStatement(candidate))
            {
                continue;
            }

            var objects = SqlObjectExtractor.Extract(candidate);
            candidates.Add(new SqlCandidate(candidate, objects));
        }

        return candidates;
    }

    private static bool LooksLikeSqlCarrier(ExpressionSyntax expression, string argumentName)
    {
        if (LooksLikeSqlCarrierName(argumentName))
        {
            return true;
        }

        return expression switch
        {
            IdentifierNameSyntax identifier => LooksLikeSqlCarrierName(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess => LooksLikeSqlCarrierName(memberAccess.Name.Identifier.ValueText),
            ConditionalAccessExpressionSyntax conditionalAccess => LooksLikeSqlCarrier(conditionalAccess.WhenNotNull, argumentName),
            InvocationExpressionSyntax invocation => LooksLikeSqlCarrierName(GetCallableName(invocation.Expression) ?? ""),
            _ => false
        };
    }

    private static bool LooksLikeSqlCarrierName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized.Contains("connection", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("sql", StringComparison.Ordinal) ||
               normalized.Contains("query", StringComparison.Ordinal) ||
               normalized.Contains("commandtext", StringComparison.Ordinal) ||
               normalized.Contains("cmdtext", StringComparison.Ordinal) ||
               normalized.Contains("statement", StringComparison.Ordinal);
    }

    private static bool ContainsSqlKeyword(string text)
    {
        return Regex.IsMatch(text, @"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|WITH)\b", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeSqlStatement(string sql)
    {
        var normalized = NormalizeSql(sql).TrimStart('(', ' ');
        if (normalized.Length == 0)
        {
            return false;
        }

        var firstToken = normalized.Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        return firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("INSERT", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("MERGE", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("EXEC", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase) ||
               firstToken.Equals("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetermineConfidence(IReadOnlyList<string> candidates, string fallback)
    {
        if (candidates.Count == 0)
        {
            return fallback;
        }

        if (candidates.Any(candidate => candidate.Contains('{', StringComparison.Ordinal) && candidate.Contains('}', StringComparison.Ordinal)))
        {
            return "dynamic";
        }

        return candidates.Count == 1 ? "certain" : "probable";
    }

    private static IReadOnlyList<SqlInvocation> FindSqlInvocations(
        SyntaxNode root,
        SourceFile sourceFile,
        AnalyzerConfiguration configuration,
        SemanticAnalysisContext semanticContext)
    {
        var invocations = new List<SqlInvocation>();
        var model = semanticContext.GetModel(root.SyntaxTree);
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetCallableName(invocation.Expression);
            if (methodName is null)
            {
                continue;
            }

            var spec = ResolveSqlExecutionInvocation(invocation, methodName, configuration, model);
            if (spec is null || spec.SqlArgumentIndex >= invocation.ArgumentList.Arguments.Count)
            {
                continue;
            }

            if (spec.AutoDetectSqlArgument)
            {
                invocations.AddRange(invocation.ArgumentList.Arguments
                    .Select(argument => new SqlInvocation(
                        methodName,
                        argument.Expression,
                        invocation,
                        sourceFile,
                        GetArgumentName(argument),
                        AutoDetectSqlArgument: true)));
                continue;
            }

            var sqlArgument = invocation.ArgumentList.Arguments[spec.SqlArgumentIndex];
            invocations.Add(new SqlInvocation(
                methodName,
                sqlArgument.Expression,
                invocation,
                sourceFile,
                GetArgumentName(sqlArgument),
                AutoDetectSqlArgument: false));
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = GetTypeName(creation.Type);
            var spec = ResolveSqlCommandCreation(creation, typeName, configuration, model);
            if (spec is null || creation.ArgumentList is null || spec.SqlArgumentIndex >= creation.ArgumentList.Arguments.Count)
            {
                continue;
            }

            var sqlArgument = creation.ArgumentList.Arguments[spec.SqlArgumentIndex];
            invocations.Add(new SqlInvocation(
                typeName,
                sqlArgument.Expression,
                creation,
                sourceFile,
                GetArgumentName(sqlArgument),
                AutoDetectSqlArgument: false));
        }

        return invocations;
    }

    private static string GetArgumentName(ArgumentSyntax argument)
    {
        return argument.NameColon?.Name.Identifier.ValueText ?? "";
    }

    private static SqlExecutionMethodSpec? ResolveSqlExecutionInvocation(
        InvocationExpressionSyntax invocation,
        string methodName,
        AnalyzerConfiguration configuration,
        SemanticModel model)
    {
        var specs = configuration.SqlExecutionMethods
            .Where(item => string.Equals(item.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        if (specs.Length == 0)
        {
            return null;
        }

        var symbolInfo = model.GetSymbolInfo(invocation);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (methodSymbol is not null)
        {
            var configuredSpec = specs.FirstOrDefault(spec => IsConfiguredTypeMatch(methodSymbol, spec) || spec.AllowAnyReceiver);
            if (configuredSpec is not null)
            {
                return configuredSpec;
            }

            if (IsKnownSqlExecutionMethod(methodSymbol))
            {
                return specs[0];
            }

            return null;
        }

        var customSpec = specs.FirstOrDefault(spec => spec.AllowAnyReceiver);
        if (customSpec is not null)
        {
            return customSpec;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
            if (IsResolvedNonDynamicType(receiverType))
            {
                var typedSpec = specs.FirstOrDefault(spec => IsConfiguredReceiverTypeMatch(receiverType, spec));
                if (typedSpec is not null)
                {
                    return typedSpec;
                }

                return IsKnownSqlReceiverType(receiverType)
                    ? specs[0]
                    : null;
            }
        }

        return IsUnqualifiedInvocation(invocation)
            ? null
            : specs[0];
    }

    private static SqlExecutionMethodSpec? ResolveSqlCommandCreation(
        ObjectCreationExpressionSyntax creation,
        string typeName,
        AnalyzerConfiguration configuration,
        SemanticModel model)
    {
        var spec = configuration.SqlExecutionMethods.FirstOrDefault(item =>
            string.Equals(item.Name, typeName, StringComparison.Ordinal));
        if (spec is null)
        {
            return null;
        }

        var type = model.GetTypeInfo(creation.Type).Type;
        if (type is null)
        {
            return spec;
        }

        return IsKnownSqlCommandType(type)
            ? spec
            : null;
    }

    private static bool IsConfiguredTypeMatch(IMethodSymbol method, SqlExecutionMethodSpec spec)
    {
        return !string.IsNullOrWhiteSpace(spec.TypeName) &&
               IsTypeNameMatch(method.ContainingType, spec.TypeName!);
    }

    private static bool IsConfiguredReceiverTypeMatch(ITypeSymbol? type, SqlExecutionMethodSpec spec)
    {
        return !string.IsNullOrWhiteSpace(spec.TypeName) &&
               IsTypeNameMatch(type, spec.TypeName!);
    }

    private static bool IsKnownSqlExecutionMethod(IMethodSymbol method)
    {
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        var containingType = original.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        var typeName = containingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return typeName is "Dapper.SqlMapper" ||
               typeName is "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions" ||
               typeName is "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions";
    }

    private static bool IsKnownSqlReceiverType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return typeName is "System.Data.IDbConnection" ||
               typeName is "System.Data.Common.DbConnection" ||
               typeName is "System.Data.SqlClient.SqlConnection" ||
               typeName is "Microsoft.Data.SqlClient.SqlConnection" ||
               typeName is "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade" ||
               typeName is "Microsoft.EntityFrameworkCore.DbSet" ||
               typeName.StartsWith("Microsoft.EntityFrameworkCore.DbSet<", StringComparison.Ordinal);
    }

    private static bool IsKnownSqlCommandType(ITypeSymbol type)
    {
        var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return typeName is "System.Data.SqlClient.SqlCommand" or "Microsoft.Data.SqlClient.SqlCommand";
    }

    private static bool IsResolvedNonDynamicType(ITypeSymbol? type)
    {
        return type is not null &&
               type.TypeKind is not TypeKind.Error and not TypeKind.Dynamic;
    }

    private static bool IsTypeNameMatch(ITypeSymbol? type, string expectedTypeName)
    {
        if (type is null)
        {
            return false;
        }

        var displayName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return string.Equals(displayName, expectedTypeName, StringComparison.Ordinal) ||
               string.Equals(type.Name, expectedTypeName, StringComparison.Ordinal);
    }

    private static bool IsUnqualifiedInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is IdentifierNameSyntax or GenericNameSyntax;
    }

    private static string? GetCallableName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => GetCallableName(member.Name),
            MemberBindingExpressionSyntax binding => GetCallableName(binding.Name),
            _ => null
        };
    }

    private static string GetTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => GetTypeName(qualified.Right),
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
            _ => type.ToString()
        };
    }

    private static SourceLocation GetLocation(SyntaxNode node)
    {
        var position = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
        return new SourceLocation(position.Line + 1, position.Character + 1);
    }

    private static string GetContainingSymbol(MethodDeclarationSyntax? method)
    {
        if (method is null)
        {
            return "";
        }

        var type = method.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        return type is null
            ? method.Identifier.ValueText
            : $"{type.Identifier.ValueText}.{method.Identifier.ValueText}";
    }

    private static string NormalizeSql(string sql)
    {
        return Regex.Replace(sql, @"\s+", " ").Trim();
    }

    private sealed record SqlCandidate(string Sql, IReadOnlyList<SqlObject> Objects);

    private sealed record SqlInvocation(
        string MethodName,
        ExpressionSyntax SqlExpression,
        SyntaxNode Syntax,
        SourceFile SourceFile,
        string ArgumentName,
        bool AutoDetectSqlArgument,
        bool IsSqlStringCandidate = false);

    private sealed record ReachableSqlInvocations(
        IReadOnlyList<SqlInvocation> Invocations,
        IReadOnlySet<MethodDeclarationSyntax> Methods);

    private sealed record SourceLocation(int Line, int Column);

    private sealed record ParsedSourceFile(SourceFile SourceFile, CompilationUnitSyntax Root);

    private sealed class SemanticAnalysisContext
    {
        private readonly CSharpCompilation _compilation;
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();

        private SemanticAnalysisContext(CSharpCompilation compilation)
        {
            _compilation = compilation;
        }

        public static SemanticAnalysisContext Create(IEnumerable<SyntaxTree> trees)
        {
            var compilation = CSharpCompilation.Create(
                "TableAnalyzerInput",
                trees,
                CreateMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            return new SemanticAnalysisContext(compilation);
        }

        public SemanticModel GetModel(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var model))
            {
                model = _compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                _models[tree] = model;
            }

            return model;
        }

        private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
        {
            var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
            {
                return trustedPlatformAssemblies
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => MetadataReference.CreateFromFile(path))
                    .ToArray();
            }

            return
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location)
            ];
        }
    }

    private sealed class MethodIndex
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<MethodDeclarationSyntax>> _byName;
        private readonly Dictionary<IMethodSymbol, MethodDeclarationSyntax> _bySymbol;
        private readonly Dictionary<MethodDeclarationSyntax, string> _signatures;
        private readonly SemanticAnalysisContext _semanticContext;

        private MethodIndex(
            IReadOnlyDictionary<string, IReadOnlyList<MethodDeclarationSyntax>> byName,
            Dictionary<IMethodSymbol, MethodDeclarationSyntax> bySymbol,
            Dictionary<MethodDeclarationSyntax, string> signatures,
            SemanticAnalysisContext semanticContext)
        {
            _byName = byName;
            _bySymbol = bySymbol;
            _signatures = signatures;
            _semanticContext = semanticContext;
        }

        public static MethodIndex Create(IReadOnlyList<MethodDeclarationSyntax> methods, SemanticAnalysisContext semanticContext)
        {
            var byName = methods
                .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<MethodDeclarationSyntax>)group.ToArray(), StringComparer.Ordinal);
            var bySymbol = new Dictionary<IMethodSymbol, MethodDeclarationSyntax>(SymbolEqualityComparer.Default);
            var signatures = new Dictionary<MethodDeclarationSyntax, string>();

            foreach (var method in methods)
            {
                var symbol = semanticContext.GetModel(method.SyntaxTree).GetDeclaredSymbol(method);
                if (symbol is null)
                {
                    signatures[method] = CreateSyntaxSignature(method);
                    continue;
                }

                signatures[method] = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                AddSymbol(bySymbol, symbol, method);
                AddSymbol(bySymbol, symbol.OriginalDefinition, method);
                AddSymbol(bySymbol, symbol.PartialDefinitionPart, method);
                AddSymbol(bySymbol, symbol.PartialImplementationPart, method);
            }

            return new MethodIndex(byName, bySymbol, signatures, semanticContext);
        }

        public IReadOnlyList<MethodDeclarationSyntax> ResolveInvocationCandidates(InvocationExpressionSyntax invocation)
        {
            var model = _semanticContext.GetModel(invocation.SyntaxTree);
            var symbolInfo = model.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol resolvedMethod &&
                TryResolveSymbol(resolvedMethod, out var resolvedDeclaration))
            {
                return [resolvedDeclaration];
            }

            var candidates = new List<MethodDeclarationSyntax>();
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (TryResolveSymbol(candidate, out var method) && !candidates.Contains(method))
                {
                    candidates.Add(method);
                }
            }

            return candidates;
        }

        public IReadOnlyList<MethodDeclarationSyntax> GetFallbackCandidates(string methodName, int argumentCount)
        {
            if (!_byName.TryGetValue(methodName, out var candidates))
            {
                return [];
            }

            return candidates
                .Where(method => IsArgumentCountCompatible(method, argumentCount))
                .ToArray();
        }

        public string GetSignature(MethodDeclarationSyntax method)
        {
            return _signatures.TryGetValue(method, out var signature)
                ? signature
                : CreateSyntaxSignature(method);
        }

        private bool TryResolveSymbol(IMethodSymbol symbol, out MethodDeclarationSyntax method)
        {
            foreach (var lookupSymbol in GetLookupSymbols(symbol))
            {
                if (lookupSymbol is not null && _bySymbol.TryGetValue(lookupSymbol, out method!))
                {
                    return true;
                }
            }

            method = null!;
            return false;
        }

        private static IEnumerable<IMethodSymbol?> GetLookupSymbols(IMethodSymbol symbol)
        {
            yield return symbol;
            yield return symbol.ReducedFrom;
            yield return symbol.OriginalDefinition;
            yield return symbol.ReducedFrom?.OriginalDefinition;
            yield return symbol.PartialDefinitionPart;
            yield return symbol.PartialImplementationPart;
        }

        private static void AddSymbol(
            Dictionary<IMethodSymbol, MethodDeclarationSyntax> bySymbol,
            IMethodSymbol? symbol,
            MethodDeclarationSyntax method)
        {
            if (symbol is null)
            {
                return;
            }

            bySymbol[symbol] = method;
        }

        private static string CreateSyntaxSignature(MethodDeclarationSyntax method)
        {
            var containingType = method.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText ?? "";
            return $"{containingType}.{method.Identifier.ValueText}/{method.ParameterList.Parameters.Count}";
        }

        private static int GetRequiredParameterCount(MethodDeclarationSyntax method)
        {
            return method.ParameterList.Parameters.Count(parameter =>
                parameter.Default is null &&
                !parameter.Modifiers.Any(SyntaxKind.ParamsKeyword));
        }

        private static bool IsArgumentCountCompatible(MethodDeclarationSyntax method, int argumentCount)
        {
            var parameters = method.ParameterList.Parameters;
            var hasParamsParameter = parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ParamsKeyword));
            return GetRequiredParameterCount(method) <= argumentCount &&
                   (hasParamsParameter || argumentCount <= parameters.Count);
        }
    }

    private sealed class MethodCallContextResolver
    {
        private readonly MethodIndex _methods;
        private readonly ExpressionEvaluator _evaluator;
        private readonly Dictionary<MethodDeclarationSyntax, IReadOnlyList<InvocationExpressionSyntax>> _callers = new();
        private readonly Dictionary<MethodDeclarationSyntax, IReadOnlyList<IReadOnlyDictionary<string, SymbolicValue>>> _cache = new();

        public MethodCallContextResolver(
            IEnumerable<ParsedSourceFile> parsedFiles,
            MethodIndex methods,
            ExpressionEvaluator evaluator)
        {
            _methods = methods;
            _evaluator = evaluator;
            BuildCallerIndex(parsedFiles);
        }

        public IReadOnlyList<IReadOnlyDictionary<string, SymbolicValue>> GetParameterContexts(
            MethodDeclarationSyntax method,
            IReadOnlySet<MethodDeclarationSyntax>? allowedMethods = null)
        {
            var contexts = ResolveParameterContexts(method, new HashSet<string>(StringComparer.Ordinal), allowedMethods);
            return contexts.Count == 0
                ? [new Dictionary<string, SymbolicValue>(StringComparer.Ordinal)]
                : contexts;
        }

        private void BuildCallerIndex(IEnumerable<ParsedSourceFile> parsedFiles)
        {
            foreach (var invocation in parsedFiles.SelectMany(file => file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()))
            {
                foreach (var target in _methods.ResolveInvocationCandidates(invocation))
                {
                    if (!_callers.TryGetValue(target, out var existing))
                    {
                        _callers[target] = [invocation];
                        continue;
                    }

                    _callers[target] = existing.Concat([invocation]).ToArray();
                }
            }
        }

        private IReadOnlyList<IReadOnlyDictionary<string, SymbolicValue>> ResolveParameterContexts(
            MethodDeclarationSyntax method,
            HashSet<string> activeMethods,
            IReadOnlySet<MethodDeclarationSyntax>? allowedMethods)
        {
            if (allowedMethods is null && _cache.TryGetValue(method, out var cached))
            {
                return cached;
            }

            var signature = _methods.GetSignature(method);
            if (!activeMethods.Add(signature))
            {
                return [];
            }

            var contexts = new List<IReadOnlyDictionary<string, SymbolicValue>>();
            if (_callers.TryGetValue(method, out var callers))
            {
                foreach (var invocation in callers)
                {
                    var callerMethod = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    if (ReferenceEquals(callerMethod, method))
                    {
                        continue;
                    }

                    var callerContexts = callerMethod is null
                        ? [new Dictionary<string, SymbolicValue>(StringComparer.Ordinal)]
                        : allowedMethods is not null && !allowedMethods.Contains(callerMethod)
                            ? []
                            : ResolveParameterContexts(callerMethod, activeMethods, allowedMethods);
                    if (callerContexts.Count == 0)
                    {
                        if (allowedMethods is not null && callerMethod is not null && !allowedMethods.Contains(callerMethod))
                        {
                            continue;
                        }

                        callerContexts = [new Dictionary<string, SymbolicValue>(StringComparer.Ordinal)];
                    }

                    foreach (var callerContext in callerContexts)
                    {
                        contexts.Add(CreateParameterContext(method, invocation, callerMethod, callerContext));
                    }
                }
            }

            activeMethods.Remove(signature);
            var distinct = DistinctParameterContexts(contexts);
            if (allowedMethods is null)
            {
                _cache[method] = distinct;
            }

            return distinct;
        }

        private IReadOnlyDictionary<string, SymbolicValue> CreateParameterContext(
            MethodDeclarationSyntax target,
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? callerMethod,
            IReadOnlyDictionary<string, SymbolicValue> callerContext)
        {
            var mapped = new Dictionary<string, SymbolicValue>(StringComparer.Ordinal);
            var parameters = target.ParameterList.Parameters;
            var parameterByName = parameters.ToDictionary(parameter => parameter.Identifier.ValueText, StringComparer.Ordinal);
            var positionalIndex = 0;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                ParameterSyntax? parameter = null;
                if (argument.NameColon is not null)
                {
                    parameterByName.TryGetValue(argument.NameColon.Name.Identifier.ValueText, out parameter);
                }
                else
                {
                    while (positionalIndex < parameters.Count &&
                           mapped.ContainsKey(parameters[positionalIndex].Identifier.ValueText))
                    {
                        positionalIndex++;
                    }

                    if (positionalIndex < parameters.Count)
                    {
                        parameter = parameters[positionalIndex];
                        positionalIndex++;
                    }
                }

                if (parameter is null)
                {
                    continue;
                }

                mapped[parameter.Identifier.ValueText] = _evaluator.Evaluate(argument.Expression, callerMethod, callerContext);
            }

            foreach (var parameter in parameters)
            {
                var parameterName = parameter.Identifier.ValueText;
                if (!mapped.ContainsKey(parameterName) && parameter.Default?.Value is not null)
                {
                    mapped[parameterName] = _evaluator.Evaluate(parameter.Default.Value, target, new Dictionary<string, SymbolicValue>(StringComparer.Ordinal));
                }
            }

            return mapped;
        }

        private static IReadOnlyList<IReadOnlyDictionary<string, SymbolicValue>> DistinctParameterContexts(
            IEnumerable<IReadOnlyDictionary<string, SymbolicValue>> contexts)
        {
            return contexts
                .GroupBy(CreateContextKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        private static string CreateContextKey(IReadOnlyDictionary<string, SymbolicValue> context)
        {
            return string.Join(";", context
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={string.Join("|", item.Value.Candidates)}:{item.Value.Pattern}"));
        }
    }

    private sealed class IdSequence
    {
        private int _usage;
        private int _sql;
        private int _candidateGroup;
        private int _warning;

        public string NextUsageId() => $"U{++_usage:000000}";

        public string NextSqlId() => $"S{++_sql:000000}";

        public string NextCandidateGroupId() => $"C{++_candidateGroup:000000}";

        public string NextWarningId() => $"W{++_warning:000000}";
    }

    private sealed record SymbolicValue(IReadOnlyList<string> Candidates, string Pattern, string Confidence, string ResolutionPath, string Notes);

    private sealed class ExpressionEvaluator(
        MethodIndex methods,
        SemanticAnalysisContext semanticContext,
        AnalyzerConfiguration configuration)
    {
        public SymbolicValue Evaluate(ExpressionSyntax expression, MethodDeclarationSyntax? scope)
        {
            return Evaluate(expression, scope, new Dictionary<string, SymbolicValue>(StringComparer.Ordinal), 0, new HashSet<string>(StringComparer.Ordinal));
        }

        public SymbolicValue Evaluate(
            ExpressionSyntax expression,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters)
        {
            return Evaluate(expression, scope, parameters, 0, new HashSet<string>(StringComparer.Ordinal));
        }

        public SymbolicValue Evaluate(
            ExpressionSyntax expression,
            MethodDeclarationSyntax? scope,
            IReadOnlyList<IReadOnlyDictionary<string, SymbolicValue>> parameterContexts)
        {
            if (parameterContexts.Count == 0)
            {
                return Evaluate(expression, scope);
            }

            var values = parameterContexts
                .Select(parameters => Evaluate(expression, scope, parameters, 0, new HashSet<string>(StringComparer.Ordinal)))
                .ToArray();
            return values.Length == 1
                ? values[0]
                : CombineAlternatives(values, "call contexts");
        }

        private SymbolicValue Evaluate(
            ExpressionSyntax expression,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (depth > configuration.MaxCallDepth)
            {
                return new SymbolicValue([], $"{{{expression}}}", "unresolved", "", "max call depth exceeded");
            }

            expression = Unwrap(expression);
            return expression switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                    Literal(literal.Token.ValueText),
                InterpolatedStringExpressionSyntax interpolated =>
                    EvaluateInterpolatedString(interpolated, scope, parameters, depth, activeMethods),
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                    Combine(
                    [
                        Evaluate(binary.Left, scope, parameters, depth, activeMethods),
                        Evaluate(binary.Right, scope, parameters, depth, activeMethods)
                    ], "concatenation"),
                IdentifierNameSyntax identifier =>
                    EvaluateIdentifier(identifier, scope, parameters, depth, activeMethods),
                MemberAccessExpressionSyntax memberAccess =>
                    EvaluateMemberAccess(memberAccess, scope, parameters, depth, activeMethods),
                InvocationExpressionSyntax invocation =>
                    EvaluateInvocation(invocation, scope, parameters, depth, activeMethods),
                ConditionalExpressionSyntax conditional =>
                    CombineAlternatives(
                    [
                        Evaluate(conditional.WhenTrue, scope, parameters, depth, activeMethods),
                        Evaluate(conditional.WhenFalse, scope, parameters, depth, activeMethods)
                    ], "conditional"),
                SwitchExpressionSyntax switchExpression =>
                    CombineAlternatives(switchExpression.Arms.Select(arm => Evaluate(arm.Expression, scope, parameters, depth, activeMethods)).ToArray(), "switch"),
                _ => Unknown(expression)
            };
        }

        private SymbolicValue EvaluateIdentifier(
            IdentifierNameSyntax identifier,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            var name = identifier.Identifier.ValueText;
            if (parameters.TryGetValue(name, out var parameterValue))
            {
                return parameterValue;
            }

            var localExpressions = FindLocalValues(scope, identifier);
            if (localExpressions.Count > 0)
            {
                var value = CombineAlternatives(
                    localExpressions.Select(expression => Evaluate(expression, scope, parameters, depth, activeMethods)).ToArray(),
                    name);
                return value with
                {
                    ResolutionPath = string.IsNullOrEmpty(value.ResolutionPath) ? name : $"{name} -> {value.ResolutionPath}"
                };
            }

            if (TryEvaluateSymbol(identifier, scope, parameters, depth, activeMethods, out var symbolValue))
            {
                return symbolValue;
            }

            return Unknown(identifier);
        }

        private SymbolicValue EvaluateMemberAccess(
            MemberAccessExpressionSyntax memberAccess,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (memberAccess.Expression is IdentifierNameSyntax receiver)
            {
                var memberExpressions = FindObjectMemberValues(scope, receiver, memberAccess.Name.Identifier.ValueText, memberAccess.SpanStart);
                if (memberExpressions.Count > 0)
                {
                    return CombineAlternatives(
                        memberExpressions.Select(expression => Evaluate(expression, scope, parameters, depth, activeMethods)).ToArray(),
                        memberAccess.ToString());
                }
            }

            if (TryEvaluateSymbol(memberAccess, scope, parameters, depth, activeMethods, out var symbolValue))
            {
                return symbolValue;
            }

            return Unknown(memberAccess);
        }

        private SymbolicValue EvaluateInvocation(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            var methodName = GetCallableName(invocation.Expression);
            if (methodName is null)
            {
                return Unknown(invocation);
            }

            if (TryEvaluateStringBuilderToString(invocation, scope, parameters, depth, activeMethods, out var stringBuilderValue))
            {
                return stringBuilderValue;
            }

            if (string.Equals(methodName, "Format", StringComparison.Ordinal) ||
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                string.Equals(memberAccess.Name.Identifier.ValueText, "Format", StringComparison.Ordinal) &&
                string.Equals(memberAccess.Expression.ToString(), "string", StringComparison.Ordinal))
            {
                return EvaluateStringFormat(invocation, scope, parameters, depth, activeMethods);
            }

            var candidates = methods.ResolveInvocationCandidates(invocation);
            if (candidates.Count == 0)
            {
                candidates = methods.GetFallbackCandidates(methodName, invocation.ArgumentList.Arguments.Count);
            }

            if (candidates.Count == 0)
            {
                return Unknown(invocation);
            }

            var evaluatedReturns = new List<SymbolicValue>();
            foreach (var method in candidates)
            {
                var signature = methods.GetSignature(method);
                if (!activeMethods.Add(signature))
                {
                    continue;
                }

                var nextParameters = CreateParameterValues(method, invocation, scope, parameters, depth, activeMethods);

                foreach (var returnExpression in GetReturnExpressions(method))
                {
                    evaluatedReturns.Add(Evaluate(returnExpression, method, nextParameters, depth + 1, activeMethods));
                }

                activeMethods.Remove(signature);
            }

            if (evaluatedReturns.Count == 0)
            {
                return Unknown(invocation);
            }

            return CombineAlternatives(evaluatedReturns, methodName);
        }

        private IReadOnlyDictionary<string, SymbolicValue> CreateParameterValues(
            MethodDeclarationSyntax target,
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? callerScope,
            IReadOnlyDictionary<string, SymbolicValue> callerParameters,
            int depth,
            HashSet<string> activeMethods)
        {
            var mapped = new Dictionary<string, SymbolicValue>(StringComparer.Ordinal);
            var parameters = target.ParameterList.Parameters;
            var parameterByName = parameters.ToDictionary(parameter => parameter.Identifier.ValueText, StringComparer.Ordinal);
            var positionalIndex = 0;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                ParameterSyntax? parameter = null;
                if (argument.NameColon is not null)
                {
                    parameterByName.TryGetValue(argument.NameColon.Name.Identifier.ValueText, out parameter);
                }
                else
                {
                    while (positionalIndex < parameters.Count &&
                           mapped.ContainsKey(parameters[positionalIndex].Identifier.ValueText))
                    {
                        positionalIndex++;
                    }

                    if (positionalIndex < parameters.Count)
                    {
                        parameter = parameters[positionalIndex];
                        positionalIndex++;
                    }
                }

                if (parameter is null)
                {
                    continue;
                }

                mapped[parameter.Identifier.ValueText] = Evaluate(argument.Expression, callerScope, callerParameters, depth, activeMethods);
            }

            foreach (var parameter in parameters)
            {
                var parameterName = parameter.Identifier.ValueText;
                if (!mapped.ContainsKey(parameterName) && parameter.Default?.Value is not null)
                {
                    mapped[parameterName] = Evaluate(parameter.Default.Value, target, new Dictionary<string, SymbolicValue>(StringComparer.Ordinal), depth, activeMethods);
                }
            }

            return mapped;
        }

        private bool TryEvaluateStringBuilderToString(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods,
            out SymbolicValue value)
        {
            value = null!;
            if (invocation.ArgumentList.Arguments.Count != 0 ||
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                !string.Equals(memberAccess.Name.Identifier.ValueText, "ToString", StringComparison.Ordinal) ||
                memberAccess.Expression is not IdentifierNameSyntax receiver)
            {
                return false;
            }

            var stringBuilderValue = EvaluateStringBuilderValue(receiver, scope, invocation.SpanStart, parameters, depth, activeMethods);
            if (stringBuilderValue is null)
            {
                return false;
            }

            value = stringBuilderValue with
            {
                ResolutionPath = string.IsNullOrEmpty(stringBuilderValue.ResolutionPath)
                    ? "StringBuilder.ToString"
                    : $"StringBuilder.ToString -> {stringBuilderValue.ResolutionPath}"
            };
            return true;
        }

        private SymbolicValue? EvaluateStringBuilderValue(
            IdentifierNameSyntax receiver,
            MethodDeclarationSyntax? scope,
            int beforePosition,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (scope?.Body is null)
            {
                return null;
            }

            var builderSymbol = GetSymbol(receiver);
            if (builderSymbol is null)
            {
                return null;
            }

            var creation = FindStringBuilderCreation(scope, receiver.Identifier.ValueText, builderSymbol, beforePosition);
            if (creation is null)
            {
                return null;
            }

            var current = EvaluateStringBuilderInitialValue(creation, scope, parameters, depth, activeMethods);
            var mutations = FindStringBuilderMutations(scope.Body, receiver.Identifier.ValueText, builderSymbol, creation.Position, beforePosition);
            for (var index = 0; index < mutations.Count;)
            {
                var controlNode = mutations[index].ControlNode;
                if (controlNode is null)
                {
                    current = ApplyStringBuilderMutation(current, mutations[index], scope, parameters, depth, activeMethods);
                    index++;
                    continue;
                }

                var group = new List<StringBuilderMutation>();
                while (index < mutations.Count && ReferenceEquals(mutations[index].ControlNode, controlNode))
                {
                    group.Add(mutations[index]);
                    index++;
                }

                current = ApplyControlledStringBuilderMutations(current, controlNode, group, scope, parameters, depth, activeMethods);
            }

            return current;
        }

        private SymbolicValue EvaluateStringFormat(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            return EvaluateFormatArguments(invocation.ArgumentList.Arguments.ToArray(), scope, parameters, depth, activeMethods, "string.Format");
        }

        private SymbolicValue EvaluateFormatArguments(
            IReadOnlyList<ArgumentSyntax> arguments,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods,
            string path)
        {
            if (arguments.Count == 0)
            {
                return new SymbolicValue([], "", "unknown", path, "format string is missing");
            }

            var format = Evaluate(arguments[0].Expression, scope, parameters, depth, activeMethods);
            var values = arguments.Skip(1)
                .Select(argument => Evaluate(argument.Expression, scope, parameters, depth, activeMethods))
                .ToArray();
            var current = format.Candidates.Count > 0 ? format.Candidates.ToList() : [format.Pattern];
            for (var index = 0; index < values.Length; index++)
            {
                current = ReplacePlaceholder(current, "{" + index + "}", values[index].Candidates.Count > 0 ? values[index].Candidates : [values[index].Pattern]);
            }

            return FromCandidates(current, format.Pattern, path);
        }

        private SymbolicValue EvaluateInterpolatedString(
            InterpolatedStringExpressionSyntax interpolated,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            var parts = new List<SymbolicValue>();
            foreach (var content in interpolated.Contents)
            {
                switch (content)
                {
                    case InterpolatedStringTextSyntax text:
                        parts.Add(Literal(text.TextToken.ValueText));
                        break;
                    case InterpolationSyntax interpolation:
                        var value = Evaluate(interpolation.Expression, scope, parameters, depth, activeMethods);
                        parts.Add(value.Candidates.Count == 0
                            ? value
                            : value with { Pattern = "{" + interpolation.Expression + "}" });
                        break;
                }
            }

            return Combine(parts, "interpolated string");
        }

        private static IEnumerable<ExpressionSyntax> GetReturnExpressions(MethodDeclarationSyntax method)
        {
            if (method.ExpressionBody?.Expression is not null)
            {
                yield return method.ExpressionBody.Expression;
            }

            if (method.Body is null)
            {
                yield break;
            }

            foreach (var returnStatement in method.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (returnStatement.Expression is not null)
                {
                    yield return returnStatement.Expression;
                }
            }
        }

        private IReadOnlyList<ExpressionSyntax> FindLocalValues(MethodDeclarationSyntax? scope, IdentifierNameSyntax identifier)
        {
            if (scope?.Body is null)
            {
                return [];
            }

            var targetSymbol = GetSymbol(identifier);
            if (targetSymbol is not ILocalSymbol)
            {
                return [];
            }

            return TrackLocalValueStatements(scope.Body.Statements, targetSymbol, identifier.SpanStart, [])
                .DistinctBy(expression => expression.ToString())
                .ToArray();
        }

        private IReadOnlyList<ExpressionSyntax> TrackLocalValueStatements(
            IEnumerable<StatementSyntax> statements,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            var values = current.ToList();
            foreach (var statement in statements.OrderBy(statement => statement.SpanStart))
            {
                if (statement.SpanStart >= beforePosition)
                {
                    continue;
                }

                values = TrackLocalValueStatement(statement, targetSymbol, beforePosition, values).ToList();
            }

            return values;
        }

        private IReadOnlyList<ExpressionSyntax> TrackLocalValueStatement(
            StatementSyntax statement,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            switch (statement)
            {
                case BlockSyntax block:
                    return TrackLocalValueStatements(block.Statements, targetSymbol, beforePosition, current);
                case LocalDeclarationStatementSyntax localDeclaration:
                    return TrackLocalDeclaration(localDeclaration, targetSymbol, beforePosition, current);
                case ExpressionStatementSyntax expressionStatement:
                    return TryGetLocalAssignment(expressionStatement.Expression, targetSymbol, beforePosition, out var assigned)
                        ? [assigned]
                        : current;
                case IfStatementSyntax ifStatement:
                    return TrackIfLocalValues(ifStatement, targetSymbol, beforePosition, current);
                case SwitchStatementSyntax switchStatement:
                    return TrackSwitchLocalValues(switchStatement, targetSymbol, beforePosition, current);
                case ForStatementSyntax forStatement:
                    return TrackLoopLocalValues(forStatement.Statement, targetSymbol, beforePosition, current);
                case ForEachStatementSyntax forEachStatement:
                    return TrackLoopLocalValues(forEachStatement.Statement, targetSymbol, beforePosition, current);
                case WhileStatementSyntax whileStatement:
                    return TrackLoopLocalValues(whileStatement.Statement, targetSymbol, beforePosition, current);
                case DoStatementSyntax doStatement:
                    return TrackLoopLocalValues(doStatement.Statement, targetSymbol, beforePosition, current);
                default:
                    return current;
            }
        }

        private IReadOnlyList<ExpressionSyntax> TrackLocalDeclaration(
            LocalDeclarationStatementSyntax localDeclaration,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            var values = current;
            foreach (var declarator in localDeclaration.Declaration.Variables)
            {
                if (declarator.SpanStart >= beforePosition ||
                    declarator.Initializer?.Value is null ||
                    !IsTargetDeclaration(declarator, targetSymbol))
                {
                    continue;
                }

                values = [declarator.Initializer.Value];
            }

            return values;
        }

        private IReadOnlyList<ExpressionSyntax> TrackIfLocalValues(
            IfStatementSyntax ifStatement,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            var thenValues = TrackLocalValueBranch(ifStatement.Statement, targetSymbol, beforePosition, current);
            var thenContinues = !AlwaysTerminates(ifStatement.Statement);
            if (ifStatement.Else?.Statement is not { } elseStatement)
            {
                return thenContinues
                    ? MergeExpressionValues(current, thenValues)
                    : current;
            }

            var elseValues = TrackLocalValueBranch(elseStatement, targetSymbol, beforePosition, current);
            var elseContinues = !AlwaysTerminates(elseStatement);
            return (thenContinues, elseContinues) switch
            {
                (true, true) => MergeExpressionValues(thenValues, elseValues),
                (true, false) => thenValues,
                (false, true) => elseValues,
                _ => []
            };
        }

        private IReadOnlyList<ExpressionSyntax> TrackSwitchLocalValues(
            SwitchStatementSyntax switchStatement,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            var values = current.ToList();
            foreach (var section in switchStatement.Sections)
            {
                var sectionValues = TrackLocalValueStatements(section.Statements, targetSymbol, beforePosition, current);
                values.AddRange(sectionValues);
            }

            return DistinctExpressionValues(values);
        }

        private IReadOnlyList<ExpressionSyntax> TrackLoopLocalValues(
            StatementSyntax loopBody,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            var once = TrackLocalValueBranch(loopBody, targetSymbol, beforePosition, current);
            return MergeExpressionValues(current, once);
        }

        private IReadOnlyList<ExpressionSyntax> TrackLocalValueBranch(
            StatementSyntax statement,
            ISymbol targetSymbol,
            int beforePosition,
            IReadOnlyList<ExpressionSyntax> current)
        {
            return statement is BlockSyntax block
                ? TrackLocalValueStatements(block.Statements, targetSymbol, beforePosition, current)
                : TrackLocalValueStatement(statement, targetSymbol, beforePosition, current);
        }

        private bool TryGetLocalAssignment(ExpressionSyntax expression, ISymbol targetSymbol, int beforePosition, out ExpressionSyntax value)
        {
            value = null!;
            if (expression is not AssignmentExpressionSyntax assignment ||
                !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                assignment.SpanStart >= beforePosition ||
                assignment.Left is not IdentifierNameSyntax identifier ||
                !IsTargetReference(identifier, targetSymbol))
            {
                return false;
            }

            value = assignment.Right;
            return true;
        }

        private static IReadOnlyList<ExpressionSyntax> MergeExpressionValues(
            IReadOnlyList<ExpressionSyntax> first,
            IReadOnlyList<ExpressionSyntax> second)
        {
            return DistinctExpressionValues(first.Concat(second));
        }

        private static IReadOnlyList<ExpressionSyntax> DistinctExpressionValues(IEnumerable<ExpressionSyntax> values)
        {
            return values
                .DistinctBy(expression => expression.ToString())
                .ToArray();
        }

        private bool IsTargetDeclaration(VariableDeclaratorSyntax declarator, ISymbol targetSymbol)
        {
            var declaredSymbol = semanticContext.GetModel(declarator.SyntaxTree).GetDeclaredSymbol(declarator);
            return SymbolEqualityComparer.Default.Equals(declaredSymbol, targetSymbol);
        }

        private bool IsTargetReference(ExpressionSyntax expression, ISymbol targetSymbol)
        {
            return SymbolEqualityComparer.Default.Equals(GetSymbol(expression), targetSymbol);
        }

        private ISymbol? GetSymbol(ExpressionSyntax expression)
        {
            var symbolInfo = semanticContext.GetModel(expression.SyntaxTree).GetSymbolInfo(expression);
            return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        }

        private StringBuilderCreation? FindStringBuilderCreation(MethodDeclarationSyntax scope, string name, ISymbol targetSymbol, int beforePosition)
        {
            var creations = new List<StringBuilderCreation>();
            foreach (var declarator in scope.Body?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == name && item.Initializer?.Value is not null) ?? [])
            {
                var declaredType = declarator.Parent is VariableDeclarationSyntax declaration
                    ? declaration.Type
                    : null;
                if (IsTargetDeclaration(declarator, targetSymbol) &&
                    TryGetStringBuilderCreation(declarator.Initializer!.Value, declaredType, out var arguments))
                {
                    creations.Add(new StringBuilderCreation(declarator.Initializer.Value, declarator.SpanStart, arguments));
                }
            }

            foreach (var assignment in scope.Body?.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.IsKind(SyntaxKind.SimpleAssignmentExpression)) ?? [])
            {
                if (assignment.Left is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal) &&
                    IsTargetReference(identifier, targetSymbol) &&
                    TryGetStringBuilderCreation(assignment.Right, null, out var arguments))
                {
                    creations.Add(new StringBuilderCreation(assignment.Right, assignment.SpanStart, arguments));
                }
            }

            return creations
                .OrderBy(creation => creation.Position)
                .LastOrDefault();
        }

        private bool TryGetStringBuilderCreation(ExpressionSyntax expression, TypeSyntax? declaredType, out IReadOnlyList<ArgumentSyntax> arguments)
        {
            expression = Unwrap(expression);
            if (expression is ObjectCreationExpressionSyntax objectCreation)
            {
                arguments = objectCreation.ArgumentList?.Arguments.ToArray() ?? [];
                var type = semanticContext.GetModel(expression.SyntaxTree).GetTypeInfo(expression).Type;
                return IsStringBuilderType(type) ||
                       IsUnresolvedType(type) && IsStringBuilderTypeName(GetTypeName(objectCreation.Type));
            }

            if (expression is ImplicitObjectCreationExpressionSyntax implicitCreation)
            {
                arguments = implicitCreation.ArgumentList.Arguments.ToArray();
                var type = semanticContext.GetModel(expression.SyntaxTree).GetTypeInfo(expression).Type;
                return IsStringBuilderType(type) ||
                       IsUnresolvedType(type) && declaredType is not null && IsStringBuilderTypeName(GetTypeName(declaredType));
            }

            arguments = [];
            return false;
        }

        private SymbolicValue EvaluateStringBuilderInitialValue(
            StringBuilderCreation creation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (creation.Arguments.Count == 0 || !IsStringLikeExpression(creation.Arguments[0].Expression))
            {
                return Literal("");
            }

            return Evaluate(creation.Arguments[0].Expression, scope, parameters, depth, activeMethods);
        }

        private IReadOnlyList<StringBuilderMutation> FindStringBuilderMutations(BlockSyntax body, string builderName, ISymbol targetSymbol, int afterPosition, int beforePosition)
        {
            return body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.SpanStart > afterPosition && invocation.SpanStart < beforePosition)
                .Select(invocation => TryCreateStringBuilderMutation(body, builderName, targetSymbol, invocation, out var mutation) ? mutation : null)
                .Where(mutation => mutation is not null)
                .Select(mutation => mutation!)
                .OrderBy(mutation => mutation.Invocation.SpanStart)
                .ToArray();
        }

        private bool TryCreateStringBuilderMutation(
            BlockSyntax body,
            string builderName,
            ISymbol targetSymbol,
            InvocationExpressionSyntax invocation,
            out StringBuilderMutation mutation)
        {
            mutation = null!;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax receiver ||
                !string.Equals(receiver.Identifier.ValueText, builderName, StringComparison.Ordinal) ||
                !IsTargetReference(receiver, targetSymbol))
            {
                return false;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (methodName is not ("Append" or "AppendLine" or "AppendFormat" or "Clear"))
            {
                return false;
            }

            mutation = new StringBuilderMutation(invocation, methodName, invocation.ArgumentList.Arguments.ToArray(), GetStringBuilderControlNode(body, invocation));
            return true;
        }

        private SymbolicValue ApplyControlledStringBuilderMutations(
            SymbolicValue current,
            SyntaxNode controlNode,
            IReadOnlyList<StringBuilderMutation> mutations,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (controlNode is IfStatementSyntax ifStatement)
            {
                var branchValues = new List<SymbolicValue>();
                var thenMutations = mutations.Where(mutation => IsWithin(mutation.Invocation, ifStatement.Statement)).ToArray();
                if (thenMutations.Length > 0)
                {
                    branchValues.Add(ApplyStringBuilderMutationSequence(current, thenMutations, scope, parameters, depth, activeMethods));
                }
                else
                {
                    branchValues.Add(current);
                }

                if (ifStatement.Else?.Statement is { } elseStatement)
                {
                    var elseMutations = mutations.Where(mutation => IsWithin(mutation.Invocation, elseStatement)).ToArray();
                    branchValues.Add(elseMutations.Length > 0
                        ? ApplyStringBuilderMutationSequence(current, elseMutations, scope, parameters, depth, activeMethods)
                        : current);
                }
                else
                {
                    branchValues.Add(current);
                }

                return CombineAlternatives(branchValues, "StringBuilder.if");
            }

            if (controlNode is SwitchStatementSyntax switchStatement)
            {
                var branchValues = switchStatement.Sections
                    .Select(section =>
                    {
                        var sectionMutations = mutations.Where(mutation => IsWithin(mutation.Invocation, section)).ToArray();
                        return sectionMutations.Length > 0
                            ? ApplyStringBuilderMutationSequence(current, sectionMutations, scope, parameters, depth, activeMethods)
                            : current;
                    })
                    .ToArray();
                return branchValues.Length > 0
                    ? CombineAlternatives(branchValues, "StringBuilder.switch")
                    : current;
            }

            var once = ApplyStringBuilderMutationSequence(current, mutations, scope, parameters, depth, activeMethods);
            return CombineAlternatives([current, once], "StringBuilder.loop");
        }

        private SymbolicValue ApplyStringBuilderMutationSequence(
            SymbolicValue current,
            IReadOnlyList<StringBuilderMutation> mutations,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            foreach (var mutation in mutations)
            {
                current = ApplyStringBuilderMutation(current, mutation, scope, parameters, depth, activeMethods);
            }

            return current;
        }

        private SymbolicValue ApplyStringBuilderMutation(
            SymbolicValue current,
            StringBuilderMutation mutation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            return mutation.MethodName switch
            {
                "Append" => mutation.Arguments.Count == 0
                    ? current
                    : Combine([current, Evaluate(mutation.Arguments[0].Expression, scope, parameters, depth, activeMethods)], "StringBuilder.Append"),
                "AppendLine" => Combine(
                [
                    current,
                    mutation.Arguments.Count == 0 ? Literal("") : Evaluate(mutation.Arguments[0].Expression, scope, parameters, depth, activeMethods),
                    Literal("\n")
                ], "StringBuilder.AppendLine"),
                "AppendFormat" => mutation.Arguments.Count == 0
                    ? current
                    : Combine([current, EvaluateFormatArguments(mutation.Arguments, scope, parameters, depth, activeMethods, "StringBuilder.AppendFormat")], "StringBuilder.AppendFormat"),
                "Clear" => Literal(""),
                _ => current
            };
        }

        private static SyntaxNode? GetStringBuilderControlNode(BlockSyntax body, SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors().TakeWhile(ancestor => ancestor != body))
            {
                if (ancestor is IfStatementSyntax or SwitchStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax)
                {
                    return ancestor;
                }
            }

            return null;
        }

        private static bool IsWithin(SyntaxNode node, SyntaxNode ancestor)
        {
            return node == ancestor || node.Ancestors().Any(item => item == ancestor);
        }

        private static bool AlwaysTerminates(StatementSyntax statement)
        {
            return statement switch
            {
                ReturnStatementSyntax => true,
                ThrowStatementSyntax => true,
                BlockSyntax block => block.Statements.Count > 0 && AlwaysTerminates(block.Statements.Last()),
                IfStatementSyntax ifStatement when ifStatement.Else?.Statement is { } elseStatement =>
                    AlwaysTerminates(ifStatement.Statement) && AlwaysTerminates(elseStatement),
                _ => false
            };
        }

        private bool IsStringLikeExpression(ExpressionSyntax expression)
        {
            var typeInfo = semanticContext.GetModel(expression.SyntaxTree).GetTypeInfo(expression);
            var type = typeInfo.ConvertedType ?? typeInfo.Type;
            return type is null || type.SpecialType == SpecialType.System_String;
        }

        private static bool IsStringBuilderType(ITypeSymbol? type)
        {
            return type is not null &&
                   !IsUnresolvedType(type) &&
                   string.Equals(type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), "System.Text.StringBuilder", StringComparison.Ordinal);
        }

        private static bool IsUnresolvedType(ITypeSymbol? type)
        {
            return type is null || type.TypeKind == TypeKind.Error;
        }

        private static bool IsStringBuilderTypeName(string typeName)
        {
            return string.Equals(typeName, "StringBuilder", StringComparison.Ordinal) ||
                   string.Equals(typeName, "System.Text.StringBuilder", StringComparison.Ordinal);
        }

        private IReadOnlyList<ExpressionSyntax> FindObjectMemberValues(MethodDeclarationSyntax? scope, IdentifierNameSyntax objectIdentifier, string memberName, int beforePosition)
        {
            if (scope?.Body is null)
            {
                return [];
            }

            var targetSymbol = GetSymbol(objectIdentifier);
            if (targetSymbol is null)
            {
                return [];
            }

            var values = new List<ExpressionSyntax>();
            foreach (var declarator in scope.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == objectIdentifier.Identifier.ValueText))
            {
                if (IsTargetDeclaration(declarator, targetSymbol) &&
                    declarator.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
                    creation.Initializer is not null)
                {
                    values.AddRange(GetObjectInitializerMemberValues(creation.Initializer, memberName));
                }
            }

            foreach (var assignment in scope.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.IsKind(SyntaxKind.SimpleAssignmentExpression)))
            {
                if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Expression is IdentifierNameSyntax receiver &&
                    string.Equals(receiver.Identifier.ValueText, objectIdentifier.Identifier.ValueText, StringComparison.Ordinal) &&
                    IsTargetReference(receiver, targetSymbol) &&
                    string.Equals(memberAccess.Name.Identifier.ValueText, memberName, StringComparison.Ordinal))
                {
                    values.Add(assignment.Right);
                }
            }

            return values.ToArray();
        }

        private static IEnumerable<ExpressionSyntax> GetObjectInitializerMemberValues(InitializerExpressionSyntax initializer, string memberName)
        {
            foreach (var expression in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                var name = expression.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                    _ => ""
                };
                if (string.Equals(name, memberName, StringComparison.Ordinal))
                {
                    yield return expression.Right;
                }
            }
        }
        private bool TryEvaluateSymbol(
            ExpressionSyntax expression,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods,
            out SymbolicValue value)
        {
            var symbol = semanticContext.GetModel(expression.SyntaxTree).GetSymbolInfo(expression).Symbol;
            if (symbol is IFieldSymbol field)
            {
                return TryEvaluateField(field, scope, parameters, depth, activeMethods, out value);
            }

            if (symbol is IPropertySymbol property)
            {
                return TryEvaluateProperty(property, scope, parameters, depth, activeMethods, out value);
            }

            if (symbol is ILocalSymbol { HasConstantValue: true, ConstantValue: string constant })
            {
                value = Literal(constant);
                return true;
            }

            value = null!;
            return false;
        }

        private bool TryEvaluateField(
            IFieldSymbol field,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods,
            out SymbolicValue value)
        {
            if (field.HasConstantValue && field.ConstantValue is string constant)
            {
                value = Literal(constant);
                return true;
            }

            foreach (var syntaxReference in field.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is not VariableDeclaratorSyntax declarator ||
                    declarator.Initializer?.Value is null)
                {
                    continue;
                }

                value = Evaluate(declarator.Initializer.Value, scope, parameters, depth, activeMethods);
                return true;
            }

            value = null!;
            return false;
        }

        private bool TryEvaluateProperty(
            IPropertySymbol property,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods,
            out SymbolicValue value)
        {
            foreach (var syntaxReference in property.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is not PropertyDeclarationSyntax declaration)
                {
                    continue;
                }

                if (declaration.Initializer?.Value is not null)
                {
                    value = Evaluate(declaration.Initializer.Value, scope, parameters, depth, activeMethods);
                    return true;
                }

                if (declaration.ExpressionBody?.Expression is not null)
                {
                    value = Evaluate(declaration.ExpressionBody.Expression, scope, parameters, depth, activeMethods);
                    return true;
                }

                var returns = declaration.AccessorList?.Accessors
                    .Where(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                    .SelectMany(GetAccessorReturnExpressions)
                    .ToArray() ?? [];
                if (returns.Length > 0)
                {
                    value = CombineAlternatives(returns.Select(expression => Evaluate(expression, scope, parameters, depth, activeMethods)).ToArray(), property.Name);
                    return true;
                }
            }

            value = null!;
            return false;
        }

        private static IEnumerable<ExpressionSyntax> GetAccessorReturnExpressions(AccessorDeclarationSyntax accessor)
        {
            if (accessor.ExpressionBody?.Expression is not null)
            {
                yield return accessor.ExpressionBody.Expression;
            }

            if (accessor.Body is null)
            {
                yield break;
            }

            foreach (var returnStatement in accessor.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (returnStatement.Expression is not null)
                {
                    yield return returnStatement.Expression;
                }
            }
        }

        private SymbolicValue Combine(IReadOnlyList<SymbolicValue> parts, string path)
        {
            var current = new List<string> { "" };
            foreach (var part in parts)
            {
                var additions = part.Candidates.Count > 0 ? part.Candidates : [part.Pattern];
                var next = new List<string>();
                foreach (var prefix in current)
                {
                    foreach (var addition in additions)
                    {
                        if (next.Count >= configuration.MaxCandidatesPerExpression)
                        {
                            return new SymbolicValue(next, string.Concat(parts.Select(item => item.Pattern)), "dynamic", path, "candidate limit exceeded");
                        }

                        next.Add(prefix + addition);
                    }
                }

                current = next;
            }

            return FromCandidates(current, string.Concat(parts.Select(part => part.Pattern)), path);
        }

        private SymbolicValue CombineAlternatives(IReadOnlyList<SymbolicValue> alternatives, string path)
        {
            return FromCandidates(
                alternatives.SelectMany(item => item.Candidates.Count > 0 ? item.Candidates : [item.Pattern])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                string.Join("|", alternatives.Select(item => item.Pattern).Where(item => item.Length > 0)),
                path);
        }

        private SymbolicValue FromCandidates(IReadOnlyList<string> candidates, string pattern, string path)
        {
            var distinct = candidates.Where(candidate => candidate.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(configuration.MaxCandidatesPerExpression + 1)
                .ToArray();
            if (distinct.Length > configuration.MaxCandidatesPerExpression)
            {
                return new SymbolicValue(distinct.Take(configuration.MaxCandidatesPerExpression).ToArray(), pattern, "dynamic", path, "candidate limit exceeded");
            }

            var hasDynamicCandidate = distinct.Any(candidate => candidate.Contains('{', StringComparison.Ordinal) && candidate.Contains('}', StringComparison.Ordinal));
            var confidence = distinct.Length switch
            {
                0 => "unknown",
                _ when hasDynamicCandidate => "dynamic",
                1 => "certain",
                _ => "probable"
            };

            return new SymbolicValue(distinct, pattern, confidence, path, "");
        }

        private static SymbolicValue Literal(string value)
        {
            return new SymbolicValue([value], value, "certain", "literal", "");
        }

        private static SymbolicValue Unknown(ExpressionSyntax expression)
        {
            return new SymbolicValue([], $"{{{expression}}}", "unknown", expression.ToString(), "runtime value");
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    case CastExpressionSyntax cast:
                        expression = cast.Expression;
                        continue;
                    default:
                        return expression;
                }
            }
        }

        private static List<string> ReplacePlaceholder(IEnumerable<string> source, string placeholder, IReadOnlyList<string> replacements)
        {
            var next = new List<string>();
            foreach (var item in source)
            {
                foreach (var replacement in replacements)
                {
                    next.Add(item.Replace(placeholder, replacement, StringComparison.Ordinal));
                }
            }

            return next;
        }

        private sealed record StringBuilderCreation(ExpressionSyntax Expression, int Position, IReadOnlyList<ArgumentSyntax> Arguments);

        private sealed record StringBuilderMutation(InvocationExpressionSyntax Invocation, string MethodName, IReadOnlyList<ArgumentSyntax> Arguments, SyntaxNode? ControlNode);
    }

    private sealed record SqlObject(string ObjectType, string ObjectName, string FullName, string Operation, string SqlRole);

    private static class SqlObjectExtractor
    {
        public static IReadOnlyList<SqlObject> Extract(string sql)
        {
            var normalized = PlaceholderSqlNormalizer.Normalize(sql);
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(normalized.Sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                return [];
            }

            var visitor = new TsqlObjectVisitor(normalized.Placeholders);
            fragment.Accept(visitor);
            return visitor.Objects;
        }

        private sealed class TsqlObjectVisitor : TSqlFragmentVisitor
        {
            private readonly IReadOnlyDictionary<string, string> _placeholders;
            private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
            private readonly Stack<SqlObjectContext> _contexts = new();
            private readonly List<SqlObject> _objects = [];

            public TsqlObjectVisitor(IReadOnlyDictionary<string, string> placeholders)
            {
                _placeholders = placeholders;
            }

            public IReadOnlyList<SqlObject> Objects => _objects;

            public override void ExplicitVisit(CommonTableExpression node)
            {
                if (node.ExpressionName is not null)
                {
                    _cteNames.Add(node.ExpressionName.Value);
                }

                node.QueryExpression?.Accept(this);
            }

            public override void ExplicitVisit(InsertStatement node)
            {
                var specification = node.InsertSpecification;
                VisitTableReference(specification.Target, "INSERT", "Target");
                specification.InsertSource?.Accept(this);
                specification.OutputClause?.Accept(this);
                specification.OutputIntoClause?.Accept(this);
            }

            public override void ExplicitVisit(UpdateStatement node)
            {
                var specification = node.UpdateSpecification;
                VisitTableReference(specification.Target, "UPDATE", "Target");
                specification.FromClause?.Accept(this);
                specification.WhereClause?.Accept(this);
                specification.OutputClause?.Accept(this);
                specification.OutputIntoClause?.Accept(this);
                foreach (var setClause in specification.SetClauses)
                {
                    setClause.Accept(this);
                }
            }

            public override void ExplicitVisit(DeleteStatement node)
            {
                var specification = node.DeleteSpecification;
                VisitTableReference(specification.Target, "DELETE", "Target");
                specification.FromClause?.Accept(this);
                specification.WhereClause?.Accept(this);
                specification.OutputClause?.Accept(this);
                specification.OutputIntoClause?.Accept(this);
            }

            public override void ExplicitVisit(MergeStatement node)
            {
                var specification = node.MergeSpecification;
                VisitTableReference(specification.Target, "MERGE", "Target");
                VisitTableReference(specification.TableReference, "MERGE", "Source");
                specification.SearchCondition?.Accept(this);
                foreach (var actionClause in specification.ActionClauses)
                {
                    actionClause.Accept(this);
                }
                specification.OutputClause?.Accept(this);
                specification.OutputIntoClause?.Accept(this);
            }

            public override void ExplicitVisit(FromClause node)
            {
                foreach (var tableReference in node.TableReferences)
                {
                    VisitTableReference(tableReference, "SELECT", "Source");
                }

                foreach (var predictTableReference in node.PredictTableReference)
                {
                    predictTableReference.Accept(this);
                }
            }

            public override void ExplicitVisit(NamedTableReference node)
            {
                if (_contexts.TryPeek(out var context))
                {
                    AddSchemaObject(node.SchemaObject, context.Operation, context.Role, "TableOrView");
                }
            }

            public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
            {
                if (_contexts.TryPeek(out var context))
                {
                    AddSchemaObject(node.SchemaObject, context.Operation, context.Role, "TableOrView");
                }
            }

            public override void ExplicitVisit(VariableTableReference node)
            {
                if (_contexts.TryPeek(out var context))
                {
                    AddObject(node.Variable.Name, context.Operation, context.Role, "TableVariable");
                }
            }

            public override void ExplicitVisit(ExecuteStatement node)
            {
                if (node.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference procedureReference &&
                    procedureReference.ProcedureReference?.ProcedureReference?.Name is not null)
                {
                    AddObject(FormatSchemaObjectName(procedureReference.ProcedureReference.ProcedureReference.Name), "EXEC", "Procedure", "Procedure");
                }

                node.ExecuteSpecification.AcceptChildren(this);
            }

            private void VisitTableReference(TableReference? tableReference, string operation, string role)
            {
                switch (tableReference)
                {
                    case null:
                        return;
                    case JoinTableReference join:
                        VisitTableReference(join.FirstTableReference, operation, role);
                        VisitTableReference(join.SecondTableReference, operation, "Join");
                        if (join is QualifiedJoin qualifiedJoin)
                        {
                            qualifiedJoin.SearchCondition?.Accept(this);
                        }
                        return;
                    default:
                        WithContext(operation, role, () => tableReference.Accept(this));
                        return;
                }
            }

            private void WithContext(string operation, string role, Action action)
            {
                _contexts.Push(new SqlObjectContext(operation, role));
                try
                {
                    action();
                }
                finally
                {
                    _contexts.Pop();
                }
            }

            private void AddSchemaObject(SchemaObjectName schemaObject, string operation, string role, string objectType)
            {
                AddObject(FormatSchemaObjectName(schemaObject), operation, role, objectType);
            }

            private void AddObject(string fullName, string operation, string role, string objectType)
            {
                fullName = RestorePlaceholders(fullName);
                if (string.IsNullOrWhiteSpace(fullName) || _cteNames.Contains(fullName))
                {
                    return;
                }

                var resolvedObjectType = objectType == "TableOrView"
                    ? ClassifyTableObject(fullName)
                    : objectType;
                var objectName = fullName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
                _objects.Add(new SqlObject(resolvedObjectType, objectName, fullName, operation, role));
            }

            private static string ClassifyTableObject(string fullName)
            {
                if (fullName.StartsWith('#'))
                {
                    return "TempTable";
                }

                if (fullName.StartsWith('@'))
                {
                    return "TableVariable";
                }

                if (fullName.Contains('{', StringComparison.Ordinal))
                {
                    return "Unknown";
                }

                return "TableOrView";
            }

            private string RestorePlaceholders(string value)
            {
                foreach (var placeholder in _placeholders)
                {
                    value = value.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal);
                }

                return value;
            }

            private static string FormatSchemaObjectName(SchemaObjectName name)
            {
                return string.Join(".", name.Identifiers.Select(identifier => identifier.Value));
            }
        }

        private sealed record SqlObjectContext(string Operation, string Role);

        private sealed record NormalizedSql(string Sql, IReadOnlyDictionary<string, string> Placeholders);

        private static class PlaceholderSqlNormalizer
        {
            private static readonly Regex PlaceholderPattern = new(@"\{[^}]+\}", RegexOptions.Compiled);

            public static NormalizedSql Normalize(string sql)
            {
                var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                var index = 0;
                var normalized = PlaceholderPattern.Replace(sql, match =>
                {
                    var token = $"__ta_dynamic_{index++}__";
                    replacements[token] = match.Value;
                    return token;
                });

                return new NormalizedSql(normalized, replacements);
            }
        }
    }
}
