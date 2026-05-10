using System.Text.RegularExpressions;

namespace TableAnalyzer.Core;

public sealed class SimpleSourceAnalyzer
{
    public AnalysisResult Analyze(IReadOnlyList<SourceFile> files, AnalyzerConfiguration configuration)
    {
        var result = new AnalysisResult();
        var reader = new SourceTextReader();
        var ids = new IdSequence();

        foreach (var file in files)
        {
            var read = reader.Read(file.FullPath);
            if (!read.Success)
            {
                result.Warnings.Add(new WarningRow(ids.NextWarningId(), "Medium", "FILE_READ_FAILED", file.RelativePath, 0, "", read.ErrorMessage ?? "Failed to read file.", "", ""));
                continue;
            }

            AnalyzeFile(file, read.Text, configuration, result, ids);
        }

        return result;
    }

    private static void AnalyzeFile(SourceFile file, string source, AnalyzerConfiguration configuration, AnalysisResult result, IdSequence ids)
    {
        var variableExpressions = CollectVariableExpressions(source);
        var methods = CollectMethods(source);
        var invocations = FindSqlInvocations(source, configuration);

        foreach (var invocation in invocations)
        {
            var sqlId = ids.NextSqlId();
            var location = GetLocation(source, invocation.Position);
            var containingSymbol = FindContainingSymbol(source, invocation.Position);
            var evaluator = new ExpressionEvaluator(variableExpressions, methods, configuration);
            var evaluated = evaluator.Evaluate(invocation.SqlExpression);

            if (evaluated.Candidates.Count == 0)
            {
                var pattern = string.IsNullOrEmpty(evaluated.Pattern) ? $"{{{invocation.SqlExpression}}}" : evaluated.Pattern;
                result.UnresolvedSql.Add(new UnresolvedSqlRow(sqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "RuntimeValue", invocation.SqlExpression, containingSymbol, evaluated.Notes));
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
                    result.UnresolvedSql.Add(new UnresolvedSqlRow(currentSqlId, file.RelativePath, location.Line, location.Column, containingSymbol, invocation.MethodName, "NoSqlObjectsFound", invocation.SqlExpression, containingSymbol, ""));
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

    private static Dictionary<string, string> CollectVariableExpressions(string source)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var regex = new Regex(@"\b(?:const\s+)?(?:var|string)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<expr>.*?);", RegexOptions.Singleline);
        foreach (Match match in regex.Matches(source))
        {
            variables[match.Groups["name"].Value] = match.Groups["expr"].Value.Trim();
        }

        return variables;
    }

    private static Dictionary<string, MethodDefinition> CollectMethods(string source)
    {
        var methods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var regex = new Regex(@"(?:^|[;{}\r\n])\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|partial)\s+)*[\w<>\[\]\?]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^)]*)\)\s*\{", RegexOptions.Singleline);
        foreach (Match match in regex.Matches(source))
        {
            var openBrace = match.Index + match.Value.LastIndexOf('{');
            var closeBrace = FindClosing(source, openBrace, '{', '}');
            if (openBrace < 0 || closeBrace < 0)
            {
                continue;
            }

            var parameters = match.Groups["params"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(parameter => parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
                .Where(parameter => parameter.Length > 0)
                .ToArray();
            methods[match.Groups["name"].Value] = new MethodDefinition(match.Groups["name"].Value, parameters, source[(openBrace + 1)..closeBrace]);
        }

        return methods;
    }

    private static IReadOnlyList<SqlInvocation> FindSqlInvocations(string source, AnalyzerConfiguration configuration)
    {
        var invocations = new List<SqlInvocation>();
        foreach (var spec in configuration.SqlExecutionMethods)
        {
            var regex = new Regex(@"\b" + Regex.Escape(spec.Name) + @"\s*\(");
            foreach (Match match in regex.Matches(source))
            {
                var openParen = source.IndexOf('(', match.Index);
                var closeParen = FindClosing(source, openParen, '(', ')');
                if (openParen < 0 || closeParen < 0)
                {
                    continue;
                }

                var args = SplitTopLevel(source[(openParen + 1)..closeParen], ',');
                if (spec.SqlArgumentIndex >= args.Count)
                {
                    continue;
                }

                invocations.Add(new SqlInvocation(spec.Name, args[spec.SqlArgumentIndex].Trim(), match.Index));
            }
        }

        return invocations.OrderBy(invocation => invocation.Position).ToArray();
    }

    private static SourceLocation GetLocation(string source, int position)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < position && index < source.Length; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new SourceLocation(line, column);
    }

    private static string FindContainingSymbol(string source, int position)
    {
        var before = source[..Math.Min(position, source.Length)];
        var regex = new Regex(@"(?:^|[;{}\r\n])\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|partial)\s+)*[\w<>\[\]\?]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^)]*\)\s*\{", RegexOptions.Singleline);
        var match = regex.Matches(before).Cast<Match>().LastOrDefault();
        return match?.Groups["name"].Value ?? "";
    }

    private static int FindClosing(string text, int openIndex, char open, char close)
    {
        if (openIndex < 0)
        {
            return -1;
        }

        var depth = 0;
        var inString = false;
        var inChar = false;
        var verbatimString = false;
        for (var index = openIndex; index < text.Length; index++)
        {
            var ch = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (inString)
            {
                if (verbatimString && ch == '"' && next == '"')
                {
                    index++;
                    continue;
                }

                if (ch == '"' && (verbatimString || !IsEscaped(text, index)))
                {
                    inString = false;
                    verbatimString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (ch == '\'' && !IsEscaped(text, index))
                {
                    inChar = false;
                }

                continue;
            }

            if (ch == '@' && next == '"')
            {
                inString = true;
                verbatimString = true;
                index++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '\'')
            {
                inChar = true;
                continue;
            }

            if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            slashCount++;
        }

        return slashCount % 2 == 1;
    }

    internal static IReadOnlyList<string> SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        var verbatimString = false;

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (inString)
            {
                if (verbatimString && ch == '"' && next == '"')
                {
                    index++;
                    continue;
                }

                if (ch == '"' && (verbatimString || !IsEscaped(text, index)))
                {
                    inString = false;
                    verbatimString = false;
                }

                continue;
            }

            if (ch == '@' && next == '"')
            {
                inString = true;
                verbatimString = true;
                index++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch is '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is ')' or ']' or '}')
            {
                depth--;
            }
            else if (ch == delimiter && depth == 0)
            {
                result.Add(text[start..index]);
                start = index + 1;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    private static string NormalizeSql(string sql)
    {
        return Regex.Replace(sql, @"\s+", " ").Trim();
    }

    private sealed record SqlInvocation(string MethodName, string SqlExpression, int Position);

    private sealed record SourceLocation(int Line, int Column);

    private sealed record MethodDefinition(string Name, IReadOnlyList<string> Parameters, string Body);

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
        IReadOnlyDictionary<string, string> variableExpressions,
        IReadOnlyDictionary<string, MethodDefinition> methods,
        AnalyzerConfiguration configuration)
    {
        public SymbolicValue Evaluate(string expression)
        {
            return Evaluate(expression, new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, SymbolicValue>(StringComparer.Ordinal));
        }

        private SymbolicValue Evaluate(string expression, HashSet<string> visitedVariables, IReadOnlyDictionary<string, SymbolicValue> parameterValues)
        {
            expression = TrimOuterParentheses(expression.Trim());
            if (parameterValues.TryGetValue(expression, out var parameterValue))
            {
                return parameterValue;
            }

            var plusParts = SplitTopLevel(expression, '+');
            if (plusParts.Count > 1)
            {
                return Combine(plusParts.Select(part => Evaluate(part, visitedVariables, parameterValues)).ToArray());
            }

            if (TryEvaluateInterpolatedString(expression, visitedVariables, parameterValues, out var interpolated))
            {
                return interpolated;
            }

            if (IsStringLiteral(expression))
            {
                var literal = ParseStringLiteral(expression);
                return new SymbolicValue([literal], literal, "certain", "literal", "");
            }

            if (TryEvaluateStringFormat(expression, visitedVariables, parameterValues, out var formatted))
            {
                return formatted;
            }

            if (TryEvaluateConditional(expression, visitedVariables, parameterValues, out var conditional))
            {
                return conditional;
            }

            if (Regex.IsMatch(expression, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                if (variableExpressions.TryGetValue(expression, out var variableExpression) && visitedVariables.Add(expression))
                {
                    var value = Evaluate(variableExpression, visitedVariables, parameterValues);
                    visitedVariables.Remove(expression);
                    return value with
                    {
                        ResolutionPath = string.IsNullOrEmpty(value.ResolutionPath) ? expression : $"{expression} -> {value.ResolutionPath}"
                    };
                }
            }

            if (TryEvaluateMethodCall(expression, visitedVariables, parameterValues, out var methodValue))
            {
                return methodValue;
            }

            return new SymbolicValue([], $"{{{expression}}}", "unknown", expression, "runtime value");
        }

        private bool TryEvaluateStringFormat(string expression, HashSet<string> visitedVariables, IReadOnlyDictionary<string, SymbolicValue> parameterValues, out SymbolicValue value)
        {
            value = EmptyValue;
            var match = Regex.Match(expression, @"^(?:string\.)?Format\s*\((?<args>.*)\)$", RegexOptions.Singleline);
            if (!match.Success)
            {
                return false;
            }

            var args = SplitTopLevel(match.Groups["args"].Value, ',');
            if (args.Count == 0)
            {
                return false;
            }

            var format = Evaluate(args[0], visitedVariables, parameterValues);
            var argValues = args.Skip(1).Select(arg => Evaluate(arg, visitedVariables, parameterValues)).ToArray();
            var candidates = new List<string>();
            foreach (var formatCandidate in format.Candidates)
            {
                var current = new List<string> { formatCandidate };
                for (var index = 0; index < argValues.Length; index++)
                {
                    current = ReplacePlaceholder(current, "{" + index + "}", argValues[index].Candidates.Count > 0 ? argValues[index].Candidates : [argValues[index].Pattern]);
                }

                candidates.AddRange(current);
            }

            value = FromCandidates(candidates, expression, "string.Format");
            return true;
        }

        private bool TryEvaluateInterpolatedString(string expression, HashSet<string> visitedVariables, IReadOnlyDictionary<string, SymbolicValue> parameterValues, out SymbolicValue value)
        {
            value = EmptyValue;
            if (!expression.StartsWith("$\"", StringComparison.Ordinal) && !expression.StartsWith("$@\"", StringComparison.Ordinal) && !expression.StartsWith("@$\"", StringComparison.Ordinal))
            {
                return false;
            }

            var content = ParseStringLiteral(expression.Replace("$@", "@", StringComparison.Ordinal).Replace("@$", "@", StringComparison.Ordinal).TrimStart('$'));
            var current = new List<string> { content };
            foreach (Match match in Regex.Matches(content, @"\{(?<expr>[^{}]+)\}"))
            {
                var placeholder = match.Value;
                var innerValue = Evaluate(match.Groups["expr"].Value, visitedVariables, parameterValues);
                current = ReplacePlaceholder(current, placeholder, innerValue.Candidates.Count > 0 ? innerValue.Candidates : [innerValue.Pattern]);
            }

            value = FromCandidates(current, expression, "interpolated string");
            return true;
        }

        private bool TryEvaluateConditional(string expression, HashSet<string> visitedVariables, IReadOnlyDictionary<string, SymbolicValue> parameterValues, out SymbolicValue value)
        {
            value = EmptyValue;
            var questionIndex = FindTopLevel(expression, '?');
            if (questionIndex < 0)
            {
                return false;
            }

            var colonIndex = FindTopLevel(expression[(questionIndex + 1)..], ':');
            if (colonIndex < 0)
            {
                return false;
            }

            colonIndex += questionIndex + 1;
            var whenTrue = Evaluate(expression[(questionIndex + 1)..colonIndex], visitedVariables, parameterValues);
            var whenFalse = Evaluate(expression[(colonIndex + 1)..], visitedVariables, parameterValues);
            value = FromCandidates(whenTrue.Candidates.Concat(whenFalse.Candidates).Distinct(StringComparer.Ordinal).ToArray(), expression, "conditional");
            return true;
        }

        private bool TryEvaluateMethodCall(string expression, HashSet<string> visitedVariables, IReadOnlyDictionary<string, SymbolicValue> parameterValues, out SymbolicValue value)
        {
            value = EmptyValue;
            var match = Regex.Match(expression, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>.*)\)$", RegexOptions.Singleline);
            if (!match.Success || !methods.TryGetValue(match.Groups["name"].Value, out var method))
            {
                return false;
            }

            var args = SplitTopLevel(match.Groups["args"].Value, ',');
            var nextParameters = new Dictionary<string, SymbolicValue>(StringComparer.Ordinal);
            for (var index = 0; index < method.Parameters.Count && index < args.Count; index++)
            {
                nextParameters[method.Parameters[index]] = Evaluate(args[index], visitedVariables, parameterValues);
            }

            var returns = Regex.Matches(method.Body, @"\breturn\s+(?<expr>.*?);", RegexOptions.Singleline)
                .Select(returnMatch => Evaluate(returnMatch.Groups["expr"].Value, visitedVariables, nextParameters))
                .ToArray();
            if (returns.Length == 0)
            {
                return false;
            }

            value = FromCandidates(returns.SelectMany(item => item.Candidates).Distinct(StringComparer.Ordinal).ToArray(), expression, method.Name);
            return true;
        }

        private SymbolicValue Combine(IReadOnlyList<SymbolicValue> parts)
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
                            return new SymbolicValue(next, string.Concat(parts.Select(item => item.Pattern)), "dynamic", "concatenation", "candidate limit exceeded");
                        }

                        next.Add(prefix + addition);
                    }
                }

                current = next;
            }

            return FromCandidates(current, string.Concat(parts.Select(part => part.Pattern)), "concatenation");
        }

        private SymbolicValue FromCandidates(IReadOnlyList<string> candidates, string pattern, string path)
        {
            var distinct = candidates.Where(candidate => candidate.Length > 0).Distinct(StringComparer.Ordinal).Take(configuration.MaxCandidatesPerExpression + 1).ToArray();
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

        private static bool IsStringLiteral(string expression)
        {
            return expression.StartsWith('"') || expression.StartsWith("@\"") || expression.StartsWith("$\"") || expression.StartsWith("$@\"") || expression.StartsWith("@$\"");
        }

        private static string ParseStringLiteral(string expression)
        {
            expression = expression.Trim();
            var isVerbatim = expression.StartsWith("@\"", StringComparison.Ordinal);
            if (expression.StartsWith("$@\"", StringComparison.Ordinal) || expression.StartsWith("@$\"", StringComparison.Ordinal))
            {
                isVerbatim = true;
                expression = "@\"" + expression[3..];
            }
            else if (expression.StartsWith("$\"", StringComparison.Ordinal))
            {
                expression = expression[1..];
            }

            if (isVerbatim)
            {
                var content = expression[2..^1];
                return content.Replace("\"\"", "\"", StringComparison.Ordinal);
            }

            return Regex.Unescape(expression[1..^1]);
        }

        private static string TrimOuterParentheses(string expression)
        {
            while (expression.StartsWith('(') && expression.EndsWith(')') && FindClosing(expression, 0, '(', ')') == expression.Length - 1)
            {
                expression = expression[1..^1].Trim();
            }

            return expression;
        }

        private static int FindTopLevel(string text, char target)
        {
            var depth = 0;
            var inString = false;
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (inString)
                {
                    if (ch == '"' && !IsEscaped(text, index))
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    depth--;
                }
                else if (ch == target && depth == 0)
                {
                    return index;
                }
            }

            return -1;
        }

        private static readonly SymbolicValue EmptyValue = new([], "", "unknown", "", "");
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
