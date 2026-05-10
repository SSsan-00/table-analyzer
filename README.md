# Table Analyzer

C# / Razor Pages projects are scanned read-only and SQL table usages are exported as CSV.
C# source is parsed with Roslyn syntax trees, so comments and string literal contents are not treated as executable code.

## Build

```bash
dotnet build src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -c Release
```

## Test

```bash
dotnet run --project tests/TableAnalyzer.Tests/TableAnalyzer.Tests.csproj
```

## Run

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --input /path/to/project-or-file \
  --out /path/to/report-root
```

The output directory must be outside the input directory. The analyzer never writes to the analyzed project.

Reports are written under:

```text
{out}/yyyyMMdd-HHmmss_{inputName}/
```

Generated files:

- `table-usages.csv`
- `table-summary.csv`
- `dynamic-sql.csv`
- `unresolved-sql.csv`
- `sql-snippets.csv`
- `warnings.csv`
- `run-summary.txt`

## Bootstrap

`bootstrap.ps1` embeds only the runnable tool source, not tests or samples.

```powershell
.\bootstrap.ps1 init
.\bootstrap.ps1 build
.\bootstrap.ps1 run -Input "C:\src\MyApp" -Out "C:\work\table-analysis"
```
