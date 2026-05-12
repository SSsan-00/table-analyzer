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

            AnalyzeFile(file, parsed.Root, methods, semanticContext, configuration, result, ids);
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
                parsedFiles[fullPath] = new ParsedSourceFile(tree.GetCompilationUnitRoot());
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
        AnalysisResult result,
        IdSequence ids)
    {
        var evaluator = new ExpressionEvaluator(methods, semanticContext, configuration);
        foreach (var invocation in FindSqlInvocations(root, configuration, semanticContext).OrderBy(invocation => invocation.Syntax.SpanStart))
        {
            var sqlId = ids.NextSqlId();
            var location = GetLocation(invocation.Syntax);
            var containingMethod = invocation.Syntax.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            var containingSymbol = GetContainingSymbol(containingMethod);
            var evaluated = evaluator.Evaluate(invocation.SqlExpression, containingMethod);

            if (evaluated.Candidates.Count == 0)
            {
                var pattern = string.IsNullOrEmpty(evaluated.Pattern) ? $"{{{invocation.SqlExpression}}}" : evaluated.Pattern;
                result.UnresolvedSql.Add(new UnresolvedSqlRow(sqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "RuntimeValue", invocation.SqlExpression.ToString(), containingSymbol, evaluated.Notes));
                result.SqlSnippets.Add(new SqlSnippetRow(sqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "unknown", pattern, NormalizeSql(pattern), containingSymbol, evaluated.Notes));
                continue;
            }

            string candidateGroupId = "";
            if (evaluated.Candidates.Count > 1 || evaluated.Confidence is "dynamic" or "unknown")
            {
                candidateGroupId = ids.NextCandidateGroupId();
                result.DynamicSql.Add(new DynamicSqlRow(
                    candidateGroupId,
                    file.RelativePath,
                    location.Line,
                    containingSymbol,
                    evaluated.Pattern,
                    evaluated.Candidates.Count,
                    string.Join("|", evaluated.Candidates),
                    evaluated.Confidence,
                    evaluated.ResolutionPath,
                    evaluated.Notes));
            }

            foreach (var candidate in evaluated.Candidates)
            {
                var currentSqlId = evaluated.Candidates.Count == 1 ? sqlId : ids.NextSqlId();
                result.SqlSnippets.Add(new SqlSnippetRow(currentSqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, evaluated.Confidence, candidate, NormalizeSql(candidate), containingSymbol, evaluated.Notes));

                var objects = SqlObjectExtractor.Extract(candidate);
                if (objects.Count == 0)
                {
                    result.UnresolvedSql.Add(new UnresolvedSqlRow(currentSqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "NoSqlObjectsFound", invocation.SqlExpression.ToString(), containingSymbol, ""));
                    continue;
                }

                foreach (var sqlObject in objects)
                {
                    result.TableUsages.Add(new TableUsageRow(
                        ids.NextUsageId(),
                        currentSqlId,
                        sqlObject.ObjectType,
                        sqlObject.ObjectName,
                        sqlObject.FullName,
                        sqlObject.Operation,
                        sqlObject.SqlRole,
                        evaluated.Confidence,
                        file.RelativePath,
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

    private static IReadOnlyList<SqlInvocation> FindSqlInvocations(
        CompilationUnitSyntax root,
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

            invocations.Add(new SqlInvocation(methodName, invocation.ArgumentList.Arguments[spec.SqlArgumentIndex].Expression, invocation));
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = GetTypeName(creation.Type);
            var spec = ResolveSqlCommandCreation(creation, typeName, configuration, model);
            if (spec is null || creation.ArgumentList is null || spec.SqlArgumentIndex >= creation.ArgumentList.Arguments.Count)
            {
                continue;
            }

            invocations.Add(new SqlInvocation(typeName, creation.ArgumentList.Arguments[spec.SqlArgumentIndex].Expression, creation));
        }

        return invocations;
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
            if (specs.Any(spec => IsConfiguredTypeMatch(methodSymbol, spec)) ||
                IsKnownSqlExecutionMethod(methodSymbol))
            {
                return specs[0];
            }

            return null;
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

    private static bool IsKnownSqlCommandType(ITypeSymbol type)
    {
        var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return typeName is "System.Data.SqlClient.SqlCommand" or "Microsoft.Data.SqlClient.SqlCommand";
    }

    private static bool IsTypeNameMatch(INamedTypeSymbol? type, string expectedTypeName)
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

    private sealed record SqlInvocation(string MethodName, ExpressionSyntax SqlExpression, SyntaxNode Syntax);

    private sealed record SourceLocation(int Line, int Column);

    private sealed record ParsedSourceFile(CompilationUnitSyntax Root);

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
                .Where(method => method.ParameterList.Parameters.Count <= argumentCount)
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

            var localExpressions = FindLocalValues(scope, name, identifier.SpanStart);
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
                var memberExpressions = FindObjectMemberValues(scope, receiver.Identifier.ValueText, memberAccess.Name.Identifier.ValueText, memberAccess.SpanStart);
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

                var nextParameters = new Dictionary<string, SymbolicValue>(StringComparer.Ordinal);
                for (var index = 0; index < method.ParameterList.Parameters.Count; index++)
                {
                    var parameterName = method.ParameterList.Parameters[index].Identifier.ValueText;
                    nextParameters[parameterName] = Evaluate(invocation.ArgumentList.Arguments[index].Expression, scope, parameters, depth, activeMethods);
                }

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

            var stringBuilderValue = EvaluateStringBuilderValue(receiver.Identifier.ValueText, scope, invocation.SpanStart, parameters, depth, activeMethods);
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
            string builderName,
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

            var creation = FindStringBuilderCreation(scope, builderName, beforePosition);
            if (creation is null)
            {
                return null;
            }

            var current = EvaluateStringBuilderInitialValue(creation, scope, parameters, depth, activeMethods);
            var mutations = FindStringBuilderMutations(scope.Body, builderName, creation.Position, beforePosition);
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

        private static IReadOnlyList<ExpressionSyntax> FindLocalValues(MethodDeclarationSyntax? scope, string name, int beforePosition)
        {
            if (scope?.Body is null)
            {
                return [];
            }

            ExpressionSyntax? current = null;
            var branched = new List<ExpressionSyntax>();
            foreach (var assignment in EnumerateLocalAssignments(scope.Body, name, beforePosition))
            {
                if (assignment.IsConditional)
                {
                    branched.Add(assignment.Expression);
                    continue;
                }

                current = assignment.Expression;
                branched.Clear();
            }

            var values = new List<ExpressionSyntax>();
            if (current is not null)
            {
                values.Add(current);
            }

            values.AddRange(branched);
            return values
                .Distinct()
                .ToArray();
        }

        private StringBuilderCreation? FindStringBuilderCreation(MethodDeclarationSyntax scope, string name, int beforePosition)
        {
            var creations = new List<StringBuilderCreation>();
            foreach (var declarator in scope.Body?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == name && item.Initializer?.Value is not null) ?? [])
            {
                var declaredType = declarator.Parent is VariableDeclarationSyntax declaration
                    ? declaration.Type
                    : null;
                if (TryGetStringBuilderCreation(declarator.Initializer!.Value, declaredType, out var arguments))
                {
                    creations.Add(new StringBuilderCreation(declarator.Initializer.Value, declarator.SpanStart, arguments));
                }
            }

            foreach (var assignment in scope.Body?.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.IsKind(SyntaxKind.SimpleAssignmentExpression)) ?? [])
            {
                if (assignment.Left is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal) &&
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

        private IReadOnlyList<StringBuilderMutation> FindStringBuilderMutations(BlockSyntax body, string builderName, int afterPosition, int beforePosition)
        {
            return body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.SpanStart > afterPosition && invocation.SpanStart < beforePosition)
                .Select(invocation => TryCreateStringBuilderMutation(body, builderName, invocation, out var mutation) ? mutation : null)
                .Where(mutation => mutation is not null)
                .Select(mutation => mutation!)
                .OrderBy(mutation => mutation.Invocation.SpanStart)
                .ToArray();
        }

        private static bool TryCreateStringBuilderMutation(
            BlockSyntax body,
            string builderName,
            InvocationExpressionSyntax invocation,
            out StringBuilderMutation mutation)
        {
            mutation = null!;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax receiver ||
                !string.Equals(receiver.Identifier.ValueText, builderName, StringComparison.Ordinal))
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

        private static IReadOnlyList<ExpressionSyntax> FindObjectMemberValues(MethodDeclarationSyntax? scope, string objectName, string memberName, int beforePosition)
        {
            if (scope?.Body is null)
            {
                return [];
            }

            var values = new List<ExpressionSyntax>();
            foreach (var declarator in scope.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == objectName))
            {
                if (declarator.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
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
                    string.Equals(receiver.Identifier.ValueText, objectName, StringComparison.Ordinal) &&
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

        private static IEnumerable<TrackedAssignment> EnumerateLocalAssignments(BlockSyntax body, string name, int beforePosition)
        {
            foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == name && item.Initializer?.Value is not null)
                         .OrderBy(item => item.SpanStart))
            {
                yield return new TrackedAssignment(declarator.Initializer!.Value, IsConditionalAssignment(body, declarator));
            }

            foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.IsKind(SyntaxKind.SimpleAssignmentExpression))
                         .OrderBy(item => item.SpanStart))
            {
                if (assignment.Left is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal))
                {
                    yield return new TrackedAssignment(assignment.Right, IsConditionalAssignment(body, assignment));
                }
            }
        }

        private static bool IsConditionalAssignment(BlockSyntax body, SyntaxNode node)
        {
            return node.Ancestors()
                .TakeWhile(ancestor => ancestor != body)
                .Any(ancestor => ancestor is IfStatementSyntax or ElseClauseSyntax or SwitchStatementSyntax or SwitchExpressionSyntax
                    or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
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

            var confidence = distinct.Length switch
            {
                0 => "unknown",
                1 => distinct[0].Contains('{', StringComparison.Ordinal) && distinct[0].Contains('}', StringComparison.Ordinal) ? "dynamic" : "certain",
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

        private sealed record TrackedAssignment(ExpressionSyntax Expression, bool IsConditional);

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
