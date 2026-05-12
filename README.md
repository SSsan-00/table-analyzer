# Table Analyzer

Table Analyzer は、C# / Razor Pages のソースコードを読み取り専用で解析し、SQLで利用しているテーブル候補をCSVに出力するツールです。CLI と Windows GUI を用意しています。

C#ソースは Roslyn の構文木と SemanticModel で解析します。コメント内の `db.Query(...)` のような文字列は実行コードとして扱いません。

## 対象環境

- .NET SDK 9.0
- Windows 10/11 (WinForms GUI)
- SQL Server 向けSQL文字列
- C# / Razor Pages の `.cs` / `.cshtml.cs`

## できること

- 解析対象フォルダ配下の `.cs` / `.cshtml.cs` を再帰的に解析
- 単一ファイルだけを指定して解析
- 解析対象外のプロジェクト内ファイルにある helper メソッドの戻り値をリンクして解析
- `using` / namespace / overload を考慮して helper メソッド呼び出しを優先解決
- SQL実行メソッドの引数からSQL文字列を追跡
- 文字列リテラル、文字列連結、補間文字列、`string.Format` を解析
- `if` / 三項演算子 / switch式などから候補を複数出力
- UTF-8 / Shift-JIS(CP932) のソースを読み取り
- CSVは UTF-8 BOM付きで出力

## 重要な方針

解析対象プロジェクトには一切書き込みません。

出力先フォルダは、解析対象プロジェクトフォルダや解析対象フォルダの外側を指定してください。入力配下を出力先にするとエラーになります。

## WinForms GUIの使い方

Windowsで実行します。

```powershell
dotnet run --project src/TableAnalyzer.Gui/TableAnalyzer.Gui.csproj
```

画面では次を入力します。

- 解析対象プロジェクトフォルダ: メソッド解決用に索引化するプロジェクトルート
- 解析対象フォルダ: 実際にCSV出力対象として解析するフォルダ
- 解析対象ファイル: 任意。指定した場合はこの1ファイルだけ解析
- 出力先フォルダ: CSVレポートの出力先

各パスは `選択` ボタンでフォルダ/ファイル選択できます。テキストボックスへのドラッグ&ドロップにも対応しています。

入力値はアプリ終了後も保持されます。保存先はユーザーのアプリケーションデータ配下の `TableAnalyzer/gui-settings.json` です。

## CLIの使い方

解析対象フォルダを再帰的に解析する場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --out /path/to/table-analysis
```

単一ファイルだけ解析しつつ、プロジェクト内の別ファイルにある helper メソッドもリンクする場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --analysis-file /path/to/MyApp/Pages/Users/Index.cshtml.cs \
  --out /path/to/table-analysis
```

ビルド済みDLLを実行する場合:

```bash
dotnet src/TableAnalyzer.Cli/bin/Release/net9.0/TableAnalyzer.Cli.dll \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --out /path/to/table-analysis
```

従来互換の短縮指定も使えます。この場合、`--input` がプロジェクトフォルダ兼解析対象になります。

```bash
dotnet src/TableAnalyzer.Cli/bin/Release/net9.0/TableAnalyzer.Cli.dll \
  analyze \
  --input /path/to/project-or-file \
  --out /path/to/table-analysis
```

解析中は標準エラーに進捗が表示されます。

```text
Project folder: C:\src\MyApp
Analysis folder: C:\src\MyApp\Pages
Scanning project context and analysis targets...
Indexing: 0/1840 (0%)
Indexing: 25/1840 (1%) Services\UserService.cs
...
Analyzing: 0/320 (0%)
Analyzing: 25/320 (7%) Users\Index.cshtml.cs
...
Analyzing: 320/320 (100%) Users\Details.cshtml.cs
```

進捗表示を抑止する場合は `--quiet` を指定します。

## ビルド

```bash
dotnet build src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -c Release
dotnet build src/TableAnalyzer.Gui/TableAnalyzer.Gui.csproj -c Release
```

## テスト

```bash
dotnet run --project tests/TableAnalyzer.Tests/TableAnalyzer.Tests.csproj
```

## 出力先

`--out` またはGUIの出力先フォルダの下に、実行ごとのレポートフォルダを作ります。

```text
{out}/yyyyMMdd-HHmmss_{inputName}/
```

例:

```text
C:\work\table-analysis\20260512-143012_Pages\
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

INSERT先は `Operation=INSERT`, `SqlRole=Target`、SELECT元は `Operation=SELECT`, `SqlRole=Source` として扱います。

## Bootstrap

`bootstrap.ps1` には、ツール実行に必要な `src` 配下のソースだけを埋め込んでいます。テスト、サンプル、ビルド成果物は含めません。

空フォルダに `bootstrap.ps1` だけを置いて展開する場合:

```powershell
.\bootstrap.ps1 init
.\bootstrap.ps1 build
```

WinForms GUIを起動する場合:

```powershell
.\bootstrap.ps1 gui
```

解析する場合:

```powershell
.\bootstrap.ps1 analyze `
  -ProjectFolder "C:\src\MyApp" `
  -AnalysisFolder "C:\src\MyApp\Pages" `
  -Out "C:\work\table-analysis"
```

単一ファイルだけ解析する場合:

```powershell
.\bootstrap.ps1 analyze `
  -ProjectFolder "C:\src\MyApp" `
  -AnalysisFolder "C:\src\MyApp\Pages" `
  -AnalysisFile "C:\src\MyApp\Pages\Users\Index.cshtml.cs" `
  -Out "C:\work\table-analysis"
```

既存の `src` を上書きして展開したい場合:

```powershell
.\bootstrap.ps1 init -Force
```
