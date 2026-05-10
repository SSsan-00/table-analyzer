# Table Analyzer

Table Analyzer は、C# / Razor Pages のソースコードを読み取り専用で解析し、SQLで利用しているテーブル候補をCSVに出力するCLIツールです。

C#ソースは Roslyn の構文木で解析します。コメント内の `db.Query(...)` のような文字列は実行コードとして扱いません。

## できること

- フォルダ配下の `.cs` / `.cshtml.cs` を再帰的に解析
- 単一ファイルだけを指定して解析
- SQL実行メソッドの引数からSQL文字列を追跡
- 文字列リテラル、文字列連結、補間文字列、`string.Format` を解析
- helperメソッドの戻り値を再帰的に追跡
- `if` / 三項演算子 / switch式などから候補を複数出力
- UTF-8 / Shift-JIS(CP932) のソースを読み取り
- CSVは UTF-8 BOM付きで出力

## 重要な方針

解析対象プロジェクトには一切書き込みません。

`--out` は必ず `--input` の外側を指定してください。入力フォルダ配下を出力先にするとエラーになります。

```bash
# OK
TableAnalyzer analyze --input C:\src\MyApp --out C:\work\table-analysis

# NG: input配下に出力しようとしている
TableAnalyzer analyze --input C:\src\MyApp --out C:\src\MyApp\table-analysis
```

## ビルド

```bash
dotnet build src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -c Release
```

## テスト

```bash
dotnet run --project tests/TableAnalyzer.Tests/TableAnalyzer.Tests.csproj
```

## 基本的な使い方

開発中に直接実行する場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --input /path/to/project-or-file \
  --out /path/to/report-root
```

ビルド済みDLLを実行する場合:

```bash
dotnet src/TableAnalyzer.Cli/bin/Release/net10.0/TableAnalyzer.Cli.dll \
  analyze \
  --input /path/to/project-or-file \
  --out /path/to/report-root
```

Windows例:

```powershell
dotnet .\src\TableAnalyzer.Cli\bin\Release\net10.0\TableAnalyzer.Cli.dll `
  analyze `
  --input "C:\src\MyApp" `
  --out "C:\work\table-analysis"
```

解析中は標準エラーに進捗が表示されます。

```text
Scanning input: C:\src\MyApp
Files queued: 1840
Analyzing: 0/1840 (0%)
Analyzing: 25/1840 (1%) Services\UserService.cs
Analyzing: 50/1840 (2%) Pages\Users\Index.cshtml.cs
...
Analyzing: 1840/1840 (100%) Services\LastFile.cs
Writing reports...
```

進捗表示を抑止したい場合は `--quiet` を指定します。

```powershell
dotnet .\src\TableAnalyzer.Cli\bin\Release\net10.0\TableAnalyzer.Cli.dll `
  analyze `
  --input "C:\src\MyApp" `
  --out "C:\work\table-analysis" `
  --quiet
```

単一ファイルだけ解析する場合:

```powershell
dotnet .\src\TableAnalyzer.Cli\bin\Release\net10.0\TableAnalyzer.Cli.dll `
  analyze `
  --input "C:\src\MyApp\Pages\Users\Index.cshtml.cs" `
  --out "C:\work\table-analysis"
```

## 出力先

`--out` で指定したフォルダの下に、実行ごとのレポートフォルダを作ります。

```text
{out}/yyyyMMdd-HHmmss_{inputName}/
```

例:

```text
C:\work\table-analysis\20260510-143012_MyApp\
```

同じ名前のレポートフォルダが既にある場合はエラーになります。

## 出力ファイル

```text
table-usages.csv      SQL内のテーブル/ビュー/Procedure出現ごとの詳細
table-summary.csv     FullName単位の集計
dynamic-sql.csv       動的SQLや候補展開の詳細
unresolved-sql.csv    SQL文字列やテーブル名を解決できなかった箇所
sql-snippets.csv      SQL本文と正規化SQL
warnings.csv          読み込み失敗などの警告
run-summary.txt       実行サマリ
```

## `INSERT ... SELECT` の出力

次のSQLを解析した場合:

```sql
INSERT INTO dbo.UserArchive (Id, Name)
SELECT Id, Name FROM dbo.Users
```

`table-usages.csv` には、同じ `SqlId` で2行出力されます。

```csv
ObjectName,FullName,Operation,SqlRole
UserArchive,dbo.UserArchive,INSERT,Target
Users,dbo.Users,SELECT,Source
```

つまり、INSERT先は `Operation=INSERT`, `SqlRole=Target`、SELECT元は `Operation=SELECT`, `SqlRole=Source` として扱います。

## Bootstrap

`bootstrap.ps1` には、ツール実行に必要な `src` 配下のソースだけを埋め込んでいます。テスト、サンプル、ビルド成果物は含めません。

空フォルダに `bootstrap.ps1` だけを置いて展開する場合:

```powershell
.\bootstrap.ps1 init
.\bootstrap.ps1 build
```

解析まで一括で行う場合:

```powershell
.\bootstrap.ps1 all `
  -Input "C:\src\MyApp" `
  -Out "C:\work\table-analysis"
```

既存の `src` を上書きして展開したい場合:

```powershell
.\bootstrap.ps1 init -Force
```
