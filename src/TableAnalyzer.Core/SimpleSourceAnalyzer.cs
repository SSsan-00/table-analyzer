using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TableAnalyzer.Core;

public sealed class SimpleSourceAnalyzer
{
    public AnalysisResult Analyze(IReadOnlyList<SourceFile> files, AnalyzerConfiguration configuration)
    {
        return Analyze(files, configuration, progress: null);
    }

    public AnalysisResult Analyze(IReadOnlyList<SourceFile> files, AnalyzerConfiguration configuration, IProgress<AnalysisProgress>? progress)
    {
        var result = new AnalysisResult();
        var reader = new SourceTextReader();
        var ids = new IdSequence();
        var completed = 0;

        progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, ""));
        foreach (var file in files)
        {
            var read = reader.Read(file.FullPath);
            if (!read.Success)
            {
                result.Warnings.Add(new WarningRow(ids.NextWarningId(), "Medium", "FILE_READ_FAILED", file.RelativePath, 0, "", read.ErrorMessage ?? "Failed to read file.", "", ""));
                completed++;
                progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, file.RelativePath));
                continue;
            }

            AnalyzeFile(file, read.Text, configuration, result, ids);
            completed++;
            progress?.Report(new AnalysisProgress("analyzing", completed, files.Count, file.RelativePath));
        }

        return result;
    }

    private static void AnalyzeFile(SourceFile file, string source, AnalyzerConfiguration configuration, AnalysisResult result, IdSequence ids)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MethodDeclarationSyntax>)group.ToArray(), StringComparer.Ordinal);

        var evaluator = new ExpressionEvaluator(methods, configuration);
        foreach (var invocation in FindSqlInvocations(root, configuration).OrderBy(invocation => invocation.Syntax.SpanStart))
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

    private static IReadOnlyList<SqlInvocation> FindSqlInvocations(CompilationUnitSyntax root, AnalyzerConfiguration configuration)
    {
        var invocations = new List<SqlInvocation>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetCallableName(invocation.Expression);
            if (methodName is null)
            {
                continue;
            }

            var spec = configuration.SqlExecutionMethods.FirstOrDefault(item =>
                string.Equals(item.Name, methodName, StringComparison.Ordinal));
            if (spec is null || spec.SqlArgumentIndex >= invocation.ArgumentList.Arguments.Count)
            {
                continue;
            }

            invocations.Add(new SqlInvocation(methodName, invocation.ArgumentList.Arguments[spec.SqlArgumentIndex].Expression, invocation));
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = GetTypeName(creation.Type);
            var spec = configuration.SqlExecutionMethods.FirstOrDefault(item =>
                string.Equals(item.Name, typeName, StringComparison.Ordinal));
            if (spec is null || creation.ArgumentList is null || spec.SqlArgumentIndex >= creation.ArgumentList.Arguments.Count)
            {
                continue;
            }

            invocations.Add(new SqlInvocation(typeName, creation.ArgumentList.Arguments[spec.SqlArgumentIndex].Expression, creation));
        }

        return invocations;
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
        IReadOnlyDictionary<string, IReadOnlyList<MethodDeclarationSyntax>> methods,
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

            var localExpression = FindLocalValue(scope, name, identifier.SpanStart);
            if (localExpression is not null)
            {
                var value = Evaluate(localExpression, scope, parameters, depth, activeMethods);
                return value with
                {
                    ResolutionPath = string.IsNullOrEmpty(value.ResolutionPath) ? name : $"{name} -> {value.ResolutionPath}"
                };
            }

            return Unknown(identifier);
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

            if (string.Equals(methodName, "Format", StringComparison.Ordinal) ||
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                string.Equals(memberAccess.Name.Identifier.ValueText, "Format", StringComparison.Ordinal) &&
                string.Equals(memberAccess.Expression.ToString(), "string", StringComparison.Ordinal))
            {
                return EvaluateStringFormat(invocation, scope, parameters, depth, activeMethods);
            }

            if (!methods.TryGetValue(methodName, out var candidates))
            {
                return Unknown(invocation);
            }

            var evaluatedReturns = new List<SymbolicValue>();
            foreach (var method in candidates.Where(method => method.ParameterList.Parameters.Count <= invocation.ArgumentList.Arguments.Count))
            {
                var signature = $"{method.Identifier.ValueText}/{method.ParameterList.Parameters.Count}";
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

        private SymbolicValue EvaluateStringFormat(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax? scope,
            IReadOnlyDictionary<string, SymbolicValue> parameters,
            int depth,
            HashSet<string> activeMethods)
        {
            if (invocation.ArgumentList.Arguments.Count == 0)
            {
                return Unknown(invocation);
            }

            var format = Evaluate(invocation.ArgumentList.Arguments[0].Expression, scope, parameters, depth, activeMethods);
            var values = invocation.ArgumentList.Arguments.Skip(1)
                .Select(argument => Evaluate(argument.Expression, scope, parameters, depth, activeMethods))
                .ToArray();
            var current = format.Candidates.Count > 0 ? format.Candidates.ToList() : [format.Pattern];
            for (var index = 0; index < values.Length; index++)
            {
                current = ReplacePlaceholder(current, "{" + index + "}", values[index].Candidates.Count > 0 ? values[index].Candidates : [values[index].Pattern]);
            }

            return FromCandidates(current, format.Pattern, "string.Format");
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

        private static ExpressionSyntax? FindLocalValue(MethodDeclarationSyntax? scope, string name, int beforePosition)
        {
            if (scope?.Body is null)
            {
                return null;
            }

            ExpressionSyntax? value = null;
            foreach (var declarator in scope.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.Identifier.ValueText == name))
            {
                if (declarator.Initializer?.Value is not null)
                {
                    value = declarator.Initializer.Value;
                }
            }

            foreach (var assignment in scope.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(item => item.SpanStart < beforePosition && item.IsKind(SyntaxKind.SimpleAssignmentExpression)))
            {
                if (assignment.Left is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal))
                {
                    value = assignment.Right;
                }
            }

            return value;
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
    }

    private sealed record SqlObject(string ObjectType, string ObjectName, string FullName, string Operation, string SqlRole);

    private static class SqlObjectExtractor
    {
        private const string NamePattern = @"(?<name>(?:[#@]?\[[^\]]+\]|[#@]?[A-Za-z_][A-Za-z0-9_]*|\{[^}]+\})(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*|\{[^}]+\}))*)";

        public static IReadOnlyList<SqlObject> Extract(string sql)
        {
            var ctes = CollectCteNames(sql);
            var objects = new List<SqlObject>();
            AddMatches(objects, sql, ctes, @"\bFROM\s+" + NamePattern, "SELECT", "Source");
            AddMatches(objects, sql, ctes, @"\bJOIN\s+" + NamePattern, "SELECT", "Join");
            AddMatches(objects, sql, ctes, @"\bUPDATE\s+" + NamePattern, "UPDATE", "Target");
            AddMatches(objects, sql, ctes, @"\bINSERT\s+INTO\s+" + NamePattern, "INSERT", "Target");
            AddMatches(objects, sql, ctes, @"\bDELETE\s+FROM\s+" + NamePattern, "DELETE", "Target");
            AddMatches(objects, sql, ctes, @"\bMERGE\s+(?:INTO\s+)?" + NamePattern, "MERGE", "Target");
            AddMatches(objects, sql, ctes, @"\bEXEC(?:UTE)?\s+" + NamePattern, "EXEC", "Procedure");
            return objects;
        }

        private static void AddMatches(List<SqlObject> objects, string sql, HashSet<string> ctes, string pattern, string operation, string role)
        {
            foreach (Match match in Regex.Matches(sql, pattern, RegexOptions.IgnoreCase))
            {
                if (operation == "SELECT" && role == "Source" && Regex.IsMatch(sql[..match.Index].TrimEnd(), @"\bDELETE\s*$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                var fullName = NormalizeName(match.Groups["name"].Value);
                if (ctes.Contains(fullName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var objectName = fullName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
                var objectType = operation == "EXEC"
                    ? "Procedure"
                    : fullName.StartsWith('#') ? "TempTable"
                    : fullName.StartsWith('@') ? "TableVariable"
                    : fullName.Contains('{', StringComparison.Ordinal) ? "Unknown"
                    : "TableOrView";

                objects.Add(new SqlObject(objectType, objectName, fullName, operation, role));
            }
        }

        private static HashSet<string> CollectCteNames(string sql)
        {
            var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(sql, @"\bWITH\s+(?<name>\[?[A-Za-z_][A-Za-z0-9_]*\]?)\s+AS\s*\(", RegexOptions.IgnoreCase))
            {
                ctes.Add(NormalizeName(match.Groups["name"].Value));
            }

            return ctes;
        }

        private static string NormalizeName(string name)
        {
            return Regex.Replace(name, @"\s+", "")
                .Replace("[", "", StringComparison.Ordinal)
                .Replace("]", "", StringComparison.Ordinal);
        }
    }
}
