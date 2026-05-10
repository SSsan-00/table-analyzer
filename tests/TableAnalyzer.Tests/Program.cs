using System.Text;
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
    ("analyzer emits candidates from conditional helper method", Tests.AnalyzerEmitsCandidatesFromConditionalHelperMethod),
    ("analyzer ignores sql-looking calls in comments", Tests.AnalyzerIgnoresSqlLookingCallsInComments),
    ("analyzer handles semicolons inside sql string literals", Tests.AnalyzerHandlesSemicolonsInsideSqlStringLiterals),
    ("analyzer detects sql command object creation", Tests.AnalyzerDetectsSqlCommandObjectCreation),
    ("analyzer reports insert select target and source", Tests.AnalyzerReportsInsertSelectTargetAndSource),
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
