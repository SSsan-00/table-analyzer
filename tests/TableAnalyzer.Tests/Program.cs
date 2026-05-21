using System.Text;
using System.IO.Compression;
using TableAnalyzer.Core;

var tests = new (string Name, Action Body)[]
{
    ("directory scan is recursive and excludes generated folders", Tests.DirectoryScanIsRecursiveAndExcludesGeneratedFolders),
    ("single file input is narrowed to that file", Tests.SingleFileInputIsNarrowedToThatFile),
    ("output under input is rejected by default", Tests.OutputUnderInputIsRejectedByDefault),
    ("report folder name uses timestamp and input name", Tests.ReportFolderNameUsesTimestampAndInputName),
    ("report folder collision fails", Tests.ReportFolderCollisionFails),
    ("source reader supports shift-jis cp932", Tests.SourceReaderSupportsShiftJisCp932),
    ("analyzer extracts direct sql usage and duplicate appearances", Tests.AnalyzerExtractsDirectSqlUsageAndDuplicateAppearances),
    ("analyzer resolves string format sql", Tests.AnalyzerResolvesStringFormatSql),
    ("analyzer follows helper method return values recursively", Tests.AnalyzerFollowsHelperMethodReturnValuesRecursively),
    ("analyzer follows helper method from context files", Tests.AnalyzerFollowsHelperMethodFromContextFiles),
    ("analyzer resolves helper method by using namespace", Tests.AnalyzerResolvesHelperMethodByUsingNamespace),
    ("analyzer resolves helper method overload by argument type", Tests.AnalyzerResolvesHelperMethodOverloadByArgumentType),
    ("analyzer resolves helper method optional argument default", Tests.AnalyzerResolvesHelperMethodOptionalArgumentDefault),
    ("analyzer resolves helper method named arguments", Tests.AnalyzerResolvesHelperMethodNamedArguments),
    ("analyzer resolves member constants and properties", Tests.AnalyzerResolvesMemberConstantsAndProperties),
    ("analyzer resolves branch and loop assignment candidates", Tests.AnalyzerResolvesBranchAndLoopAssignmentCandidates),
    ("analyzer drops overwritten value after complete branch assignment", Tests.AnalyzerDropsOverwrittenValueAfterCompleteBranchAssignment),
    ("analyzer ignores assignments from terminating branch", Tests.AnalyzerIgnoresAssignmentsFromTerminatingBranch),
    ("analyzer ignores shadowed local values", Tests.AnalyzerIgnoresShadowedLocalValues),
    ("analyzer resolves simple object state", Tests.AnalyzerResolvesSimpleObjectState),
    ("analyzer resolves string builder sql", Tests.AnalyzerResolvesStringBuilderSql),
    ("analyzer resolves string builder branch candidates", Tests.AnalyzerResolvesStringBuilderBranchCandidates),
    ("analyzer extracts dynamic table placeholders with t-sql ast", Tests.AnalyzerExtractsDynamicTablePlaceholdersWithTsqlAst),
    ("analyzer emits candidates from conditional helper method", Tests.AnalyzerEmitsCandidatesFromConditionalHelperMethod),
    ("analyzer ignores sql-looking calls in comments", Tests.AnalyzerIgnoresSqlLookingCallsInComments),
    ("analyzer handles semicolons inside sql string literals", Tests.AnalyzerHandlesSemicolonsInsideSqlStringLiterals),
    ("analyzer detects sql command object creation", Tests.AnalyzerDetectsSqlCommandObjectCreation),
    ("analyzer ignores sql method name on non sql receiver", Tests.AnalyzerIgnoresSqlMethodNameOnNonSqlReceiver),
    ("analyzer resolves configured sql execution method", Tests.AnalyzerResolvesConfiguredSqlExecutionMethod),
    ("analyzer resolves configured sql argument index", Tests.AnalyzerResolvesConfiguredSqlArgumentIndex),
    ("analyzer auto detects configured sql argument", Tests.AnalyzerAutoDetectsConfiguredSqlArgument),
    ("analyzer records unresolved likely sql argument for auto detected method", Tests.AnalyzerRecordsUnresolvedLikelySqlArgumentForAutoDetectedMethod),
    ("analyzer keeps sql snippet when auto detected sql has no objects", Tests.AnalyzerKeepsSqlSnippetWhenAutoDetectedSqlHasNoObjects),
    ("analyzer resolves caller argument into sql wrapper", Tests.AnalyzerResolvesCallerArgumentIntoSqlWrapper),
    ("analyzer resolves mixed named caller argument into sql wrapper", Tests.AnalyzerResolvesMixedNamedCallerArgumentIntoSqlWrapper),
    ("analyzer resolves caller argument into context sql wrapper", Tests.AnalyzerResolvesCallerArgumentIntoContextSqlWrapper),
    ("analyzer resolves multiple caller argument candidates", Tests.AnalyzerResolvesMultipleCallerArgumentCandidates),
    ("analyzer reports dynamic caller argument candidate", Tests.AnalyzerReportsDynamicCallerArgumentCandidate),
    ("analyzer reports insert select target and source", Tests.AnalyzerReportsInsertSelectTargetAndSource),
    ("analyzer parses merge source and target with t-sql ast", Tests.AnalyzerParsesMergeSourceAndTargetWithTsqlAst),
    ("analyzer ignores source local execute method", Tests.AnalyzerIgnoresSourceLocalExecuteMethod),
    ("analyzer reports file progress", Tests.AnalyzerReportsFileProgress),
    ("runner analyzes single target file with project context", Tests.RunnerAnalyzesSingleTargetFileWithProjectContext),
    ("runner writes xlsx only when requested", Tests.RunnerWritesXlsxOnlyWhenRequested),
    ("xlsx table usages includes sql text", Tests.XlsxTableUsagesIncludesSqlText),
    ("csv writer writes query and source crud summaries", Tests.CsvWriterWritesQueryAndSourceCrudSummaries),
    ("csv writer writes all expected files with bom", Tests.CsvWriterWritesAllExpectedFilesWithBom),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

return failed == 0 ? 0 : 1;

internal static class Tests
{
    public static void DirectoryScanIsRecursiveAndExcludesGeneratedFolders()
    {
        using var temp = TempWorkspace.Create();
        temp.Write("Pages/Index.cshtml.cs", "class IndexModel {}");
        temp.Write("Services/UserService.cs", "class UserService {}");
        temp.Write("bin/Generated.cs", "class Generated {}");
        temp.Write("obj/Generated.cs", "class Generated {}");
        temp.Write("Migrations/20260101_Init.cs", "class Migration {}");
        temp.Write("Notes/readme.txt", "ignore");

        var files = new FileSystemScanner().GetSourceFiles(temp.Root, new AnalyzerConfiguration());

        Assert.Equal(2, files.Count);
        Assert.Contains(files.Select(x => x.RelativePath), "Pages/Index.cshtml.cs");
        Assert.Contains(files.Select(x => x.RelativePath), "Services/UserService.cs");
    }

    public static void SingleFileInputIsNarrowedToThatFile()
    {
        using var temp = TempWorkspace.Create();
        var target = temp.Write("Pages/Index.cshtml.cs", "class IndexModel {}");
        temp.Write("Pages/Other.cs", "class Other {}");

        var files = new FileSystemScanner().GetSourceFiles(target, new AnalyzerConfiguration());

        Assert.Equal(1, files.Count);
        Assert.Equal("Index.cshtml.cs", files[0].RelativePath);
    }

    public static void OutputUnderInputIsRejectedByDefault()
    {
        using var temp = TempWorkspace.Create();
        var output = Path.Combine(temp.Root, "reports");

        Assert.Throws<InvalidOperationException>(() => new RunOptionsValidator().Validate(temp.Root, output));
    }

    public static void ReportFolderNameUsesTimestampAndInputName()
    {
        using var temp = TempWorkspace.Create();
        var input = temp.CreateDirectory("My.App");
        var output = temp.CreateDirectory("out");

        var report = new ReportDirectoryFactory().Create(output, input, new DateTime(2026, 5, 10, 14, 30, 12));

        Assert.Equal("20260510-143012_My_App", Path.GetFileName(report));
        Assert.True(Directory.Exists(report));
    }

    public static void ReportFolderCollisionFails()
    {
        using var temp = TempWorkspace.Create();
        var input = temp.CreateDirectory("MyApp");
        var output = temp.CreateDirectory("out");
        Directory.CreateDirectory(Path.Combine(output, "20260510-143012_MyApp"));

        Assert.Throws<IOException>(() => new ReportDirectoryFactory().Create(output, input, new DateTime(2026, 5, 10, 14, 30, 12)));
    }

    public static void SourceReaderSupportsShiftJisCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = TempWorkspace.Create();
        var path = Path.Combine(temp.Root, "Japanese.cs");
        File.WriteAllBytes(path, Encoding.GetEncoding(932).GetBytes("// テーブル\r\nclass A {}"));

        var result = new SourceTextReader().Read(path);

        Assert.True(result.Success);
        Assert.Equal("cp932", result.EncodingName);
        Assert.Contains(result.Text, "テーブル");
    }

    public static void AnalyzerExtractsDirectSqlUsageAndDuplicateAppearances()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    var sql = "SELECT * FROM dbo.Users u JOIN dbo.Users manager ON manager.Id = u.ManagerId";
                    db.Query(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);
        Assert.All(result.TableUsages, row => Assert.Equal("TableOrView", row.ObjectType));
        Assert.All(result.TableUsages, row => Assert.Equal("dbo.Users", row.FullName));
        Assert.Single(result.SqlSnippets);
        Assert.Equal("S000001", result.TableUsages[0].SqlId);
    }

    public static void AnalyzerResolvesStringFormatSql()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            class OrderService
            {
                void Run()
                {
                    var table = "Orders";
                    var sql = string.Format("UPDATE dbo.{0} SET Status = @status", table);
                    db.Execute(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Orders", result.TableUsages[0].FullName);
        Assert.Equal("UPDATE", result.TableUsages[0].Operation);
        Assert.Equal("Target", result.TableUsages[0].SqlRole);
    }

    public static void AnalyzerFollowsHelperMethodReturnValuesRecursively()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                string GetTable()
                {
                    return "Users";
                }

                string BuildSql(string table)
                {
                    return "SELECT * FROM dbo." + table;
                }

                void Run()
                {
                    var sql = BuildSql(GetTable());
                    db.Query(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("certain", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerFollowsHelperMethodFromContextFiles()
    {
        using var temp = TempWorkspace.Create();
        var page = temp.Write("Pages/Index.cshtml.cs", """
            class IndexModel
            {
                void OnGet()
                {
                    var sql = SqlFactory.BuildSelect("Users");
                    db.Query(sql);
                }
            }
            """);
        var helper = temp.Write("Infrastructure/SqlFactory.cs", """
            static class SqlFactory
            {
                public static string BuildSelect(string table)
                {
                    return "SELECT * FROM dbo." + table;
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze(
            [new SourceFile(page, "Index.cshtml.cs")],
            [
                new SourceFile(page, "Pages/Index.cshtml.cs"),
                new SourceFile(helper, "Infrastructure/SqlFactory.cs")
            ],
            new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("Index.cshtml.cs", result.TableUsages[0].SourceFile);
    }

    public static void AnalyzerResolvesHelperMethodByUsingNamespace()
    {
        using var temp = TempWorkspace.Create();
        var page = temp.Write("Pages/Index.cshtml.cs", """
            using App.Sql;

            namespace App.Pages
            {
                class IndexModel
                {
                    void OnGet()
                    {
                        db.Query(SqlFactory.Build("Users"));
                    }
                }
            }
            """);
        var selectedHelper = temp.Write("Sql/SqlFactory.cs", """
            namespace App.Sql
            {
                static class SqlFactory
                {
                    public static string Build(string table) => "SELECT * FROM dbo." + table;
                }
            }
            """);
        var otherHelper = temp.Write("Other/SqlFactory.cs", """
            namespace App.Other
            {
                static class SqlFactory
                {
                    public static string Build(string table) => "SELECT * FROM dbo.WrongUsers";
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze(
            [new SourceFile(page, "Pages/Index.cshtml.cs")],
            [
                new SourceFile(page, "Pages/Index.cshtml.cs"),
                new SourceFile(selectedHelper, "Sql/SqlFactory.cs"),
                new SourceFile(otherHelper, "Other/SqlFactory.cs")
            ],
            new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesHelperMethodOverloadByArgumentType()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query(SqlFactory.Build("Users"));
                }
            }

            static class SqlFactory
            {
                public static string Build(string table) => "SELECT * FROM dbo." + table;

                public static string Build(int tableId) => "SELECT * FROM dbo.NumericUsers";
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesHelperMethodOptionalArgumentDefault()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query(BuildSql("Users"));
                }

                string BuildSql(string table, string schema = "dbo")
                {
                    return "SELECT * FROM " + schema + "." + table;
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesHelperMethodNamedArguments()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query(BuildSql(table: "Users", schema: "dbo"));
                }

                string BuildSql(string schema, string table)
                {
                    return "SELECT * FROM " + schema + "." + table;
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesMemberConstantsAndProperties()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                private const string Schema = "dbo";
                private static readonly string Table = "Users";
                private string SelectPrefix => "SELECT * FROM " + Schema + ".";

                void Run()
                {
                    db.Query(SelectPrefix + Table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesBranchAndLoopAssignmentCandidates()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            class OrderService
            {
                void Run(bool archive, string[] names)
                {
                    var table = "Orders";
                    if (archive)
                    {
                        table = "OrdersArchive";
                    }
                    else
                    {
                        table = "OrdersCurrent";
                    }

                    foreach (var name in names)
                    {
                        table = "OrdersLoop";
                    }

                    db.Query("SELECT * FROM dbo." + table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());
        var names = result.TableUsages.Select(row => row.FullName).ToArray();

        Assert.Equal(3, result.TableUsages.Count);
        Assert.Contains(names, "dbo.OrdersArchive");
        Assert.Contains(names, "dbo.OrdersCurrent");
        Assert.Contains(names, "dbo.OrdersLoop");
        Assert.All(result.TableUsages, row => Assert.Equal("probable", row.Confidence));
    }

    public static void AnalyzerDropsOverwrittenValueAfterCompleteBranchAssignment()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            class OrderService
            {
                void Run(bool archive)
                {
                    var table = "Orders";
                    if (archive)
                    {
                        table = "OrdersArchive";
                    }
                    else
                    {
                        table = "OrdersCurrent";
                    }

                    db.Query("SELECT * FROM dbo." + table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());
        var names = result.TableUsages.Select(row => row.FullName).ToArray();

        Assert.Equal(2, result.TableUsages.Count);
        Assert.DoesNotContain(names, "dbo.Orders");
        Assert.Contains(names, "dbo.OrdersArchive");
        Assert.Contains(names, "dbo.OrdersCurrent");
    }

    public static void AnalyzerIgnoresShadowedLocalValues()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                private const string Table = "Users";

                void Run(bool diagnostic)
                {
                    if (diagnostic)
                    {
                        var Table = "DebugOnly";
                        System.Console.WriteLine(Table);
                    }

                    db.Query("SELECT * FROM dbo." + Table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerIgnoresAssignmentsFromTerminatingBranch()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            class OrderService
            {
                void Run(bool archive)
                {
                    var table = "Orders";
                    if (archive)
                    {
                        table = "OrdersArchive";
                        return;
                    }

                    db.Query("SELECT * FROM dbo." + table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Orders", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesSimpleObjectState()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    var target = new SqlTarget { Table = "Users" };
                    target.Schema = "dbo";
                    db.Query("SELECT * FROM " + target.Schema + "." + target.Table);
                }
            }

            class SqlTarget
            {
                public string Schema { get; set; }
                public string Table { get; set; }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
    }

    public static void AnalyzerResolvesStringBuilderSql()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            using System.Text;

            class UserService
            {
                void Run()
                {
                    var table = "Users";
                    var builder = new StringBuilder("SELECT *");
                    builder.Append(" FROM dbo.");
                    builder.AppendLine(table);
                    builder.AppendFormat(" WHERE Status = {0}", "@status");
                    db.Query(builder.ToString());
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("certain", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerResolvesStringBuilderBranchCandidates()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            using System.Text;

            class OrderService
            {
                void Run(bool archive)
                {
                    var builder = new StringBuilder();
                    builder.Append("SELECT * FROM dbo.");
                    if (archive)
                    {
                        builder.Append("OrdersArchive");
                    }
                    else
                    {
                        builder.Append("Orders");
                    }

                    db.Query(builder.ToString());
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.Orders");
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.OrdersArchive");
        Assert.All(result.TableUsages, row => Assert.Equal("probable", row.Confidence));
    }

    public static void AnalyzerExtractsDynamicTablePlaceholdersWithTsqlAst()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run(string table)
                {
                    db.Query("SELECT * FROM dbo." + table);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.{table}", result.TableUsages[0].FullName);
        Assert.Equal("Unknown", result.TableUsages[0].ObjectType);
        Assert.Equal("dynamic", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerEmitsCandidatesFromConditionalHelperMethod()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/OrderService.cs", """
            class OrderService
            {
                string GetTable(bool archive)
                {
                    return archive ? "OrdersArchive" : "Orders";
                }

                string BuildSql(string table)
                {
                    return "SELECT * FROM dbo." + table;
                }

                void Run(bool archive)
                {
                    var sql = BuildSql(GetTable(archive));
                    db.Query(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/OrderService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.Orders");
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.OrdersArchive");
        Assert.All(result.TableUsages, row => Assert.Equal("probable", row.Confidence));
        Assert.Single(result.DynamicSql);
    }

    public static void AnalyzerIgnoresSqlLookingCallsInComments()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CommentedService.cs", """
            class CommentedService
            {
                void Run()
                {
                    // db.Query("SELECT * FROM dbo.CommentOnly");
                    var sql = "SELECT * FROM dbo.RealUsers";
                    db.Query(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CommentedService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.RealUsers", result.TableUsages[0].FullName);
    }

    public static void AnalyzerHandlesSemicolonsInsideSqlStringLiterals()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/MultiStatementService.cs", """
            class MultiStatementService
            {
                void Run()
                {
                    var sql = "SELECT * FROM dbo.Users; SELECT * FROM dbo.Roles";
                    db.Query(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/MultiStatementService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.Users");
        Assert.Contains(result.TableUsages.Select(row => row.FullName), "dbo.Roles");
    }

    public static void AnalyzerDetectsSqlCommandObjectCreation()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/AdoService.cs", """
            class AdoService
            {
                void Run()
                {
                    var sql = "DELETE FROM dbo.Sessions WHERE ExpiresAt < @now";
                    using var command = new SqlCommand(sql, connection);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/AdoService.cs")], new AnalyzerConfiguration());

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Sessions", result.TableUsages[0].FullName);
        Assert.Equal("DELETE", result.TableUsages[0].Operation);
    }

    public static void AnalyzerIgnoresSqlMethodNameOnNonSqlReceiver()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/TextService.cs", """
            class TextService
            {
                void Run()
                {
                    var text = "not a database connection";
                    text.Query("SELECT * FROM dbo.Users");
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/TextService.cs")], new AnalyzerConfiguration());

        Assert.Equal(0, result.TableUsages.Count);
        Assert.Equal(0, result.SqlSnippets.Count);
    }

    public static void AnalyzerResolvesConfiguredSqlExecutionMethod()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CustomDbService.cs", """
            class CustomDbService
            {
                void Run(CustomDb db)
                {
                    var sql = "SELECT * FROM dbo.Users";
                    db.ExecDb(sql);
                }
            }

            class CustomDb
            {
                public void ExecDb(string sql) {}
            }
            """);
        var configuration = new AnalyzerConfiguration
        {
            SqlExecutionMethods = new AnalyzerConfiguration().SqlExecutionMethods
                .Concat([new SqlExecutionMethodSpec("ExecDb", 0, AllowAnyReceiver: true)])
                .ToArray()
        };

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CustomDbService.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("ExecDb", result.TableUsages[0].SqlExecutionMethod);
    }

    public static void AnalyzerResolvesConfiguredSqlArgumentIndex()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CustomDbService.cs", """
            class CustomDbService
            {
                void Run(CustomDb db)
                {
                    db.ExecDb(connectionName: "main", sql: "DELETE FROM dbo.Sessions WHERE ExpiresAt < @now");
                }
            }

            class CustomDb
            {
                public void ExecDb(string connectionName, string sql) {}
            }
            """);
        var configuration = new AnalyzerConfiguration
        {
            SqlExecutionMethods = new AnalyzerConfiguration().SqlExecutionMethods
                .Concat([new SqlExecutionMethodSpec("ExecDb", 1, AllowAnyReceiver: true)])
                .ToArray()
        };

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CustomDbService.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Sessions", result.TableUsages[0].FullName);
        Assert.Equal("DELETE", result.TableUsages[0].Operation);
    }

    public static void AnalyzerAutoDetectsConfiguredSqlArgument()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CustomDbService.cs", """
            class CustomDbService
            {
                void Run(CustomDb db)
                {
                    db.ExecDb("main", "DELETE FROM dbo.Sessions WHERE ExpiresAt < @now");
                }
            }

            class CustomDb
            {
                public void ExecDb(string connectionName, string sql) {}
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CustomDbService.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Single(result.SqlSnippets);
        Assert.Equal("dbo.Sessions", result.TableUsages[0].FullName);
        Assert.Equal("DELETE", result.TableUsages[0].Operation);
    }

    public static void AnalyzerRecordsUnresolvedLikelySqlArgumentForAutoDetectedMethod()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CustomDbService.cs", """
            class CustomDbService
            {
                void Run(string connectionName, string sql)
                {
                    db.ExecDb(connectionName);
                    db.ExecDb(sql);
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CustomDbService.cs")], configuration);

        Assert.Equal(0, result.TableUsages.Count);
        Assert.Single(result.UnresolvedSql);
        Assert.Single(result.SqlSnippets);
        Assert.Equal("sql", result.UnresolvedSql[0].Expression);
        Assert.Equal(6, result.UnresolvedSql[0].Line);
    }

    public static void AnalyzerKeepsSqlSnippetWhenAutoDetectedSqlHasNoObjects()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/CustomDbService.cs", """
            class CustomDbService
            {
                void Run()
                {
                    db.ExecDb("SELECT 1");
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/CustomDbService.cs")], configuration);

        Assert.Equal(0, result.TableUsages.Count);
        Assert.Single(result.UnresolvedSql);
        Assert.Single(result.SqlSnippets);
        Assert.Equal("NoSqlObjectsFound", result.UnresolvedSql[0].Reason);
        Assert.Equal("SELECT 1", result.SqlSnippets[0].SqlText);
        Assert.Equal(5, result.SqlSnippets[0].Line);
    }

    public static void AnalyzerResolvesCallerArgumentIntoSqlWrapper()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Pages/Users.cshtml.cs", """
            class UsersModel
            {
                void OnGet()
                {
                    SearchUsers("Users");
                }

                void SearchUsers(string table)
                {
                    var sql = "SELECT * FROM dbo." + table;
                    db.ExecDb(sql);
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Pages/Users.cshtml.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("certain", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerResolvesMixedNamedCallerArgumentIntoSqlWrapper()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Pages/Users.cshtml.cs", """
            class UsersModel
            {
                void OnGet()
                {
                    SearchUsers(schema: "dbo", "Users");
                }

                void SearchUsers(string schema, string table)
                {
                    var sql = "SELECT * FROM " + schema + "." + table;
                    db.ExecDb(sql);
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Pages/Users.cshtml.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.Equal("certain", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerResolvesCallerArgumentIntoContextSqlWrapper()
    {
        using var temp = TempWorkspace.Create();
        var page = temp.Write("Pages/Users.cshtml.cs", """
            class UsersModel
            {
                void OnGet()
                {
                    new UserRepository().SearchUsers("Users");
                }
            }
            """);
        var repository = temp.Write("Infrastructure/UserRepository.cs", """
            class UserRepository
            {
                public void SearchUsers(string table)
                {
                    var sql = "SELECT * FROM dbo." + table;
                    db.ExecDb(sql);
                }
            }
            """);
        var otherPage = temp.Write("Pages/Other.cshtml.cs", """
            class OtherModel
            {
                void OnGet()
                {
                    new UserRepository().SearchUsers("OtherUsers");
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze(
            [new SourceFile(page, "Pages/Users.cshtml.cs")],
            [
                new SourceFile(page, "Pages/Users.cshtml.cs"),
                new SourceFile(repository, "Infrastructure/UserRepository.cs"),
                new SourceFile(otherPage, "Pages/Other.cshtml.cs")
            ],
            configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.Users", result.TableUsages[0].FullName);
        Assert.DoesNotContain(result.TableUsages.Select(row => row.FullName), "dbo.OtherUsers");
        Assert.Equal("Infrastructure/UserRepository.cs", result.TableUsages[0].SourceFile);
    }

    public static void AnalyzerResolvesMultipleCallerArgumentCandidates()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Pages/Users.cshtml.cs", """
            class UsersModel
            {
                void OnGet(bool archive)
                {
                    SearchUsers(archive ? "UserArchive" : "Users");
                }

                void SearchUsers(string table)
                {
                    var sql = "SELECT * FROM dbo." + table;
                    db.ExecDb(sql);
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Pages/Users.cshtml.cs")], configuration);
        var names = result.TableUsages.Select(row => row.FullName).ToArray();

        Assert.Equal(2, result.TableUsages.Count);
        Assert.Contains(names, "dbo.Users");
        Assert.Contains(names, "dbo.UserArchive");
        Assert.All(result.TableUsages, row => Assert.Equal("probable", row.Confidence));
    }

    public static void AnalyzerReportsDynamicCallerArgumentCandidate()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Pages/Users.cshtml.cs", """
            class UsersModel
            {
                void OnGet(string table)
                {
                    SearchUsers(table);
                }

                void SearchUsers(string table)
                {
                    var sql = "SELECT * FROM dbo." + table;
                    db.ExecDb(sql);
                }
            }
            """);
        var configuration = ConfigurationWithAutoMethod("ExecDb");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Pages/Users.cshtml.cs")], configuration);

        Assert.Single(result.TableUsages);
        Assert.Equal("dbo.{table}", result.TableUsages[0].FullName);
        Assert.Equal("Unknown", result.TableUsages[0].ObjectType);
        Assert.Equal("dynamic", result.TableUsages[0].Confidence);
    }

    public static void AnalyzerReportsInsertSelectTargetAndSource()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/ArchiveService.cs", """
            class ArchiveService
            {
                void Run()
                {
                    var sql = "INSERT INTO dbo.UserArchive (Id, Name) SELECT Id, Name FROM dbo.Users";
                    db.Execute(sql);
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/ArchiveService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);

        var target = result.TableUsages.Single(row => row.FullName == "dbo.UserArchive");
        Assert.Equal("INSERT", target.Operation);
        Assert.Equal("Target", target.SqlRole);

        var source = result.TableUsages.Single(row => row.FullName == "dbo.Users");
        Assert.Equal("SELECT", source.Operation);
        Assert.Equal("Source", source.SqlRole);
        Assert.Equal(target.SqlId, source.SqlId);
    }

    public static void AnalyzerParsesMergeSourceAndTargetWithTsqlAst()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/MergeService.cs", """"
            class MergeService
            {
                void Run()
                {
                    var sql = @"MERGE INTO dbo.TargetUsers AS target
                        USING dbo.SourceUsers AS source
                        ON target.Id = source.Id
                        WHEN MATCHED THEN
                            UPDATE SET Name = source.Name
                        WHEN NOT MATCHED THEN
                            INSERT (Id, Name) VALUES (source.Id, source.Name);";
                    db.Execute(sql);
                }
            }
            """");

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/MergeService.cs")], new AnalyzerConfiguration());

        Assert.Equal(2, result.TableUsages.Count);

        var target = result.TableUsages.Single(row => row.FullName == "dbo.TargetUsers");
        Assert.Equal("MERGE", target.Operation);
        Assert.Equal("Target", target.SqlRole);

        var source = result.TableUsages.Single(row => row.FullName == "dbo.SourceUsers");
        Assert.Equal("MERGE", source.Operation);
        Assert.Equal("Source", source.SqlRole);
    }

    public static void AnalyzerIgnoresSourceLocalExecuteMethod()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/Worker.cs", """
            class Worker
            {
                string Execute(string value) => value;

                void Run()
                {
                    Execute("SELECT * FROM dbo.NotSql");
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/Worker.cs")], new AnalyzerConfiguration());

        Assert.Equal(0, result.TableUsages.Count);
        Assert.Equal(0, result.SqlSnippets.Count);
    }

    public static void AnalyzerReportsFileProgress()
    {
        using var temp = TempWorkspace.Create();
        var first = temp.Write("Services/One.cs", """
            class One
            {
                void Run()
                {
                    db.Query("SELECT * FROM dbo.One");
                }
            }
            """);
        var second = temp.Write("Services/Two.cs", """
            class Two
            {
                void Run()
                {
                    db.Query("SELECT * FROM dbo.Two");
                }
            }
            """);
        var progress = new CapturingProgress();

        new SimpleSourceAnalyzer().Analyze(
            [
                new SourceFile(first, "Services/One.cs"),
                new SourceFile(second, "Services/Two.cs")
            ],
            new AnalyzerConfiguration(),
            progress);

        var indexing = progress.Items.Where(item => item.Stage == "indexing").ToArray();
        var analyzing = progress.Items.Where(item => item.Stage == "analyzing").ToArray();

        Assert.Equal(3, indexing.Length);
        Assert.Equal(0, indexing[0].Completed);
        Assert.Equal(2, indexing[0].Total);
        Assert.Equal(2, indexing[2].Completed);

        Assert.Equal(3, analyzing.Length);
        Assert.Equal(0, analyzing[0].Completed);
        Assert.Equal(2, analyzing[0].Total);
        Assert.Equal(1, analyzing[1].Completed);
        Assert.Equal("Services/One.cs", analyzing[1].CurrentFile);
        Assert.Equal(2, analyzing[2].Completed);
        Assert.Equal("Services/Two.cs", analyzing[2].CurrentFile);
    }

    public static void RunnerAnalyzesSingleTargetFileWithProjectContext()
    {
        using var temp = TempWorkspace.Create();
        var target = temp.Write("Pages/Target.cshtml.cs", """
            class TargetModel
            {
                void OnGet()
                {
                    db.Query(SqlFactory.BuildSelect("TargetUsers"));
                }
            }
            """);
        temp.Write("Pages/Other.cshtml.cs", """
            class OtherModel
            {
                void OnGet()
                {
                    db.Query("SELECT * FROM dbo.OtherUsers");
                }
            }
            """);
        temp.Write("Shared/SqlFactory.cs", """
            static class SqlFactory
            {
                public static string BuildSelect(string table) => "SELECT * FROM dbo." + table;
            }
            """);
        var output = Path.Combine(Path.GetTempPath(), "table-analyzer-output-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var run = new AnalysisRunner().Run(
                new AnalysisRunRequest(temp.Root, Path.Combine(temp.Root, "Pages"), target, output),
                new AnalyzerConfiguration(),
                new DateTime(2026, 5, 12, 9, 0, 0));

            Assert.Equal(1, run.AnalysisFiles.Count);
            Assert.True(run.ContextFiles.Count >= 3);
            Assert.Single(run.AnalysisResult.TableUsages);
            Assert.Equal("dbo.TargetUsers", run.AnalysisResult.TableUsages[0].FullName);
            Assert.True(!run.AnalysisResult.TableUsages.Any(row => row.FullName == "dbo.OtherUsers"));
            Assert.True(File.Exists(Path.Combine(run.ReportDirectory, "table-usages.csv")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    public static void RunnerWritesXlsxOnlyWhenRequested()
    {
        using var temp = TempWorkspace.Create();
        temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query("SELECT * FROM dbo.Users");
                }
            }
            """);
        var output = Path.Combine(Path.GetTempPath(), "table-analyzer-output-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var run = new AnalysisRunner().Run(
                new AnalysisRunRequest(temp.Root, temp.Root, null, output, ReportOutputFormat.Xlsx),
                new AnalyzerConfiguration(),
                new DateTime(2026, 5, 12, 9, 0, 0));

            Assert.True(File.Exists(Path.Combine(run.ReportDirectory, "table-analysis.xlsx")));
            Assert.True(!File.Exists(Path.Combine(run.ReportDirectory, "table-usages.csv")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    public static void XlsxTableUsagesIncludesSqlText()
    {
        using var temp = TempWorkspace.Create();
        temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query("SELECT * FROM dbo.Users");
                }
            }
            """);
        var output = Path.Combine(Path.GetTempPath(), "table-analyzer-output-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var run = new AnalysisRunner().Run(
                new AnalysisRunRequest(temp.Root, temp.Root, null, output, ReportOutputFormat.Xlsx),
                new AnalyzerConfiguration(),
                new DateTime(2026, 5, 12, 9, 0, 0));

            var xlsxPath = Path.Combine(run.ReportDirectory, "table-analysis.xlsx");
            using var archive = ZipFile.OpenRead(xlsxPath);
            var sheet = archive.GetEntry("xl/worksheets/sheet1.xml") ?? throw new Exception("table-usages worksheet was not found.");
            using var stream = sheet.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();

            Assert.Contains(xml, "SqlText");
            Assert.Contains(xml, "SELECT * FROM dbo.Users");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    public static void CsvWriterWritesQueryAndSourceCrudSummaries()
    {
        using var temp = TempWorkspace.Create();
        var path = temp.Write("Services/UserService.cs", """
            class UserService
            {
                void Run()
                {
                    db.Query("SELECT * FROM dbo.Users");
                    db.Execute("INSERT INTO dbo.Users (Name) VALUES (@name)");
                    db.Execute("UPDATE dbo.Users SET Name = @name WHERE Id = @id");
                    db.Execute("DELETE FROM dbo.Sessions WHERE ExpiresAt < @now");
                }
            }
            """);

        var result = new SimpleSourceAnalyzer().Analyze([new SourceFile(path, "Services/UserService.cs")], new AnalyzerConfiguration());
        new CsvReportWriter().Write(temp.Root, result);

        var queryLines = File.ReadAllLines(Path.Combine(temp.Root, "query-crud-summary.csv"), Encoding.UTF8);
        Assert.Contains(queryLines[0], "CrudFlags");
        Assert.True(queryLines.Any(line => line.Contains("S000001", StringComparison.Ordinal) && line.Contains("Read", StringComparison.Ordinal) && line.Contains("dbo.Users", StringComparison.Ordinal)));
        Assert.True(queryLines.Any(line => line.Contains("S000002", StringComparison.Ordinal) && line.Contains("Create", StringComparison.Ordinal) && line.Contains("dbo.Users", StringComparison.Ordinal)));
        Assert.True(queryLines.Any(line => line.Contains("S000003", StringComparison.Ordinal) && line.Contains("Update", StringComparison.Ordinal) && line.Contains("dbo.Users", StringComparison.Ordinal)));
        Assert.True(queryLines.Any(line => line.Contains("S000004", StringComparison.Ordinal) && line.Contains("Delete", StringComparison.Ordinal) && line.Contains("dbo.Sessions", StringComparison.Ordinal)));

        var sourceLines = File.ReadAllLines(Path.Combine(temp.Root, "source-crud-summary.csv"), Encoding.UTF8);
        Assert.Contains(sourceLines[0], "ReadTables,CreateTables,UpdateTables,DeleteTables");
        Assert.Single(sourceLines.Skip(1).ToArray());
        Assert.Contains(sourceLines[1], "Create|Read|Update|Delete");
        Assert.Contains(sourceLines[1], "dbo.Users");
        Assert.Contains(sourceLines[1], "dbo.Sessions");
    }

    public static void CsvWriterWritesAllExpectedFilesWithBom()
    {
        using var temp = TempWorkspace.Create();
        var result = new AnalysisResult();
        result.TableUsages.Add(new TableUsageRow("U000001", "S000001", "TableOrView", "Users", "dbo.Users", "SELECT", "Source", "certain", "A.cs", 1, 1, "A.Run", "Query", "Run", 0, "", "", ""));
        result.SqlSnippets.Add(new SqlSnippetRow("S000001", "A.cs", 1, 1, "A.Run", "Query", "certain", "SELECT * FROM dbo.Users", "SELECT * FROM dbo.Users", "Run", ""));

        new CsvReportWriter().Write(temp.Root, result);

        var expected = new[]
        {
            "table-usages.csv",
            "query-crud-summary.csv",
            "source-crud-summary.csv",
            "table-summary.csv",
            "dynamic-sql.csv",
            "unresolved-sql.csv",
            "sql-snippets.csv",
            "warnings.csv",
            "run-summary.txt"
        };
        foreach (var file in expected)
        {
            Assert.True(File.Exists(Path.Combine(temp.Root, file)), file);
        }

        var bytes = File.ReadAllBytes(Path.Combine(temp.Root, "table-usages.csv"));
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "table-usages.csv"), Encoding.UTF8);
        Assert.Contains(lines[0], "ExecutionSourceFile,ExecutionLine,ExecutionColumn");
        Assert.True(lines[0].EndsWith(",SqlText", StringComparison.Ordinal), lines[0]);
        Assert.True(lines[1].EndsWith(",SELECT * FROM dbo.Users", StringComparison.Ordinal), lines[1]);
    }

    private static AnalyzerConfiguration ConfigurationWithAutoMethod(string methodName)
    {
        return new AnalyzerConfiguration
        {
            SqlExecutionMethods = new AnalyzerConfiguration().SqlExecutionMethods
                .Concat([new SqlExecutionMethodSpec(methodName, 0, AllowAnyReceiver: true, AutoDetectSqlArgument: true)])
                .ToArray()
        };
    }
}

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; }

    private TempWorkspace(string root)
    {
        Root = root;
    }

    public static TempWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "table-analyzer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TempWorkspace(root);
    }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class CapturingProgress : IProgress<AnalysisProgress>
{
    public List<AnalysisProgress> Items { get; } = [];

    public void Report(AnalysisProgress value)
    {
        Items.Add(value);
    }
}

internal static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new Exception(message ?? "Expected true.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected <{expected}> but got <{actual}>.");
        }
    }

    public static void Single<T>(IReadOnlyCollection<T> values)
    {
        Equal(1, values.Count);
    }

    public static void Contains(IEnumerable<string> values, string expected)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
        {
            throw new Exception($"Expected collection to contain <{expected}>.");
        }
    }

    public static void DoesNotContain(IEnumerable<string> values, string expected)
    {
        if (values.Contains(expected, StringComparer.Ordinal))
        {
            throw new Exception($"Expected collection not to contain <{expected}>.");
        }
    }

    public static void Contains(string actual, string expected)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new Exception($"Expected string to contain <{expected}>.");
        }
    }

    public static void All<T>(IEnumerable<T> values, Action<T> assertion)
    {
        foreach (var value in values)
        {
            assertion(value);
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected exception {typeof(TException).Name}.");
    }
}
