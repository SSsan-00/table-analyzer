# Table Analyzer

Table Analyzer は、C# / Razor Pages のソースコードを読み取り専用で解析し、SQLで利用しているテーブル候補をCSVまたはXLSXに出力するツールです。CLI と Windows GUI を用意しています。

C#ソースは Roslyn の構文木と SemanticModel で解析します。SQL本文は SQL Server / T-SQL 前提で AST 解析します。コメント内の `db.Query(...)` のような文字列は実行コードとして扱いません。

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
- SQL実行メソッドの引数やソース中で構築されたSQL文字列候補を追跡
- SQL実行メソッドを内部で呼ぶメソッドは、呼び出し元の実引数を仮引数へ流して再評価
- SQL実行メソッド検出では、SemanticModelで解決できる非SQLの通常メソッド呼び出しを除外
- 実行メソッド指定に依存せず、ソース中で構築されたSQLらしい文字列も `SqlString` として解析
- 文字列リテラル、文字列連結、補間文字列、`string.Format` を解析
- `StringBuilder` の初期値、`Append`、`AppendLine`、`AppendFormat`、`ToString()` を解析
- 配列や辞書などの `queries[1]` / `queries["key"]` 形式のインデクサを、同一メソッド内の初期化や代入から解決
- `targets[1].TableName` のようなコレクション要素のプロパティを、ソース上のオブジェクト初期化子や代入から解決
- ローカル変数、クラス定数、`static readonly`、フィールド初期化、プロパティ、単純なオブジェクト初期化/プロパティ代入を追跡
- `if` / ループ内代入 / 三項演算子 / switch式などから候補を複数出力
- 変数のシンボル一致、完全な `if/else` 上書き、`return` / `throw` 枝を考慮して過剰候補を抑制
- T-SQL ASTから `SELECT` / `JOIN` / `INSERT` / `UPDATE` / `DELETE` / `MERGE` / `EXEC` の対象を抽出
- `SELECT` などの単語を含むだけの通常メッセージは、T-SQL ASTでテーブル/ビュー等を抽出できない限りSQL候補にしない
- 動的テーブル名の `{table}` 形式プレースホルダを保持したままT-SQL AST解析
- 未解決の `WHERE` / `ON` 句断片は、解析内部でT-SQLパーサーが読める述語に置換して、静的に分かるテーブル抽出を継続
- SQL実行メソッドの実行箇所を `ExecutionSourceFile` / `ExecutionLine` / `ExecutionColumn` として出力
- クエリ単位・ソースファイル単位で CRUD 観点のサマリを出力
- UTF-8 / Shift-JIS(CP932) のソースを読み取り
- 出力形式は CSV または XLSX を選択可能。CSVは UTF-8 BOM付きで出力

## 重要な方針

解析対象プロジェクトには一切書き込みません。

出力先フォルダは、解析対象プロジェクトフォルダや解析対象フォルダの外側を指定してください。入力配下を出力先にするとエラーになります。

外部ライブラリ内の関数本体、環境変数、DB設定値など実行時にしか決まらない値は、静的解析では確定扱いにしません。解決不能または動的候補として出力します。

## WinForms GUIの使い方

Windowsで実行します。

```powershell
dotnet run --project src/TableAnalyzer.Gui/TableAnalyzer.Gui.csproj
```

画面では次を入力します。

- 解析対象プロジェクトフォルダ: メソッド解決用に索引化するプロジェクトルート
- 解析対象フォルダ: 実際にCSV出力対象として解析するフォルダ
- 解析対象ファイル: 任意。指定した場合はこの1ファイルだけ解析
- 出力先フォルダ: レポートの出力先
- 出力形式: `csv` または `xlsx`
- 解析範囲: `対象ファイルのみ解析` または `関連ファイルも再帰的に解析`
- 候補上限数: 1つのSQL文字列式から展開する候補SQL数の上限。既定値は `50`
- 呼び出し深度上限: helperメソッドや別メソッド呼び出しを再帰的に値解決する深さの上限。既定値は `8`

各パスは `選択` ボタンでフォルダ/ファイル選択できます。各入力欄の `クリア` ボタンで空に戻せます。テキストボックスへのドラッグ&ドロップにも対応しています。

解析範囲で `対象ファイルのみ解析` を選ぶと、解析対象ファイルを指定した場合はそのファイルだけ、解析対象ファイルを省略した場合は解析対象フォルダ配下だけを索引化します。関連ファイルのhelperやRepositoryには入りません。

`関連ファイルも再帰的に解析` を選ぶと、解析対象プロジェクトフォルダ全体を索引化し、解析対象から呼ばれる別ファイルのメソッドも再帰的に追跡します。単一ファイルから関連Repositoryまで見たい場合はこちらを使います。既定値は `関連ファイルも再帰的に解析` です。

実行メソッドの指定は不要です。漏れ防止のため、`var sql = ...`、`return "SELECT ..."`、任意メソッドへのSQL文字列引数など、ソース中でSQL文として構築された文字列があれば `SqlExecutionMethod=SqlString` として解析します。SQL候補かどうかは単純な文字列検索ではなく、T-SQL ASTでオブジェクトを抽出できるかで判定します。`関連ファイルも再帰的に解析` の場合は、別メソッドや別ファイルの呼び出し先にも到達できる範囲で、呼び出し元の実引数を仮引数へ流して再評価します。

入力値はアプリ終了後も保持されます。保存先はユーザーのアプリケーションデータ配下の `TableAnalyzer/gui-settings.json` です。

## CLIの使い方

解析対象フォルダを再帰的に解析する場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --out /path/to/table-analysis \
  --format csv
```

単一ファイルだけ解析しつつ、プロジェクト内の別ファイルにある helper メソッドもリンクする場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --analysis-file /path/to/MyApp/Pages/Users/Index.cshtml.cs \
  --out /path/to/table-analysis \
  --format xlsx \
  --analysis-scope related-files
```

単一ファイル内だけをExcel出力する場合:

```bash
dotnet run --project src/TableAnalyzer.Cli/TableAnalyzer.Cli.csproj -- \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --analysis-file /path/to/MyApp/Pages/Users/Index.cshtml.cs \
  --out /path/to/table-analysis \
  --format xlsx \
  --analysis-scope target-only
```

ビルド済みDLLを実行する場合:

```bash
dotnet src/TableAnalyzer.Cli/bin/Release/net9.0/TableAnalyzer.Cli.dll \
  analyze \
  --project-folder /path/to/MyApp \
  --analysis-folder /path/to/MyApp/Pages \
  --out /path/to/table-analysis \
  --format csv
```

従来互換の短縮指定も使えます。この場合、`--input` がプロジェクトフォルダ兼解析対象になります。

```bash
dotnet src/TableAnalyzer.Cli/bin/Release/net9.0/TableAnalyzer.Cli.dll \
  analyze \
  --input /path/to/project-or-file \
  --out /path/to/table-analysis \
  --format xlsx
```

解析中は標準エラーに進捗が表示されます。

```text
Project folder: C:\src\MyApp
Analysis folder: C:\src\MyApp\Pages
Analysis scope: related-files
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

## リリース用単一ファイルexe

Windows x64 向けの自己完結型単一ファイル exe を作成する場合:

```powershell
.\bootstrap.ps1 publish
```

出力先:

```text
publish\TableAnalyzer.Gui\win-x64\TableAnalyzer.Gui.exe
publish\TableAnalyzer.Cli\win-x64\TableAnalyzer.Cli.exe
```

Windows ARM64 向けに作成する場合:

```powershell
.\bootstrap.ps1 publish -Runtime win-arm64
```

GUIを配布する場合は `TableAnalyzer.Gui.exe` を、CLIを配布する場合は `TableAnalyzer.Cli.exe` をリリース成果物として使います。`--self-contained true` で発行するため、利用者端末に .NET ランタイムを別途インストールしなくても起動できます。

作成したGUIを起動する場合:

```powershell
.\publish\TableAnalyzer.Gui\win-x64\TableAnalyzer.Gui.exe
```

作成したCLIで解析する場合:

```powershell
.\publish\TableAnalyzer.Cli\win-x64\TableAnalyzer.Cli.exe analyze `
  --project-folder "C:\src\MyApp" `
  --analysis-folder "C:\src\MyApp\Pages" `
  --out "C:\work\table-analysis" `
  --format xlsx
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

CSV形式を選んだ場合:

```text
table-usages.csv      SQL内のテーブル/ビュー/Procedure出現ごとの詳細。実行箇所とSQL全文を出力
query-crud-summary.csv  SQL文字列ごとのCRUDサマリ
source-crud-summary.csv ソースファイルごとのCRUDサマリ
table-summary.csv     FullName単位の集計
dynamic-sql.csv       動的SQLや候補展開の詳細
unresolved-sql.csv    SQL文字列やテーブル名を解決できなかった箇所
sql-snippets.csv      SQL本文、正規化SQL、実行箇所
warnings.csv          読み込み失敗などの警告
run-summary.txt       実行サマリ
```

XLSX形式を選んだ場合:

```text
table-analysis.xlsx   上記CSV相当の内容をシート分割した1ブック
```

`table-usages` と `sql-snippets` の `ExecutionSourceFile` / `ExecutionLine` / `ExecutionColumn` は、SQL実行メソッド呼び出しまたはSQL文字列候補を検出した位置です。既存互換のため `SourceFile` / `Line` / `Column` も同じ位置を保持しています。

`query-crud-summary` は `SqlId` 単位で、1つのSQL文字列がどのテーブルを `Create` / `Read` / `Update` / `Delete` するかを出力します。`source-crud-summary` は `SourceFile` 単位で集計し、そのソースファイルの機能をCRUD視点で確認できるようにします。`MERGE` と `EXEC` はCRUDに単純分類しきれないため、`MergeTables` / `ExecuteProcedures` として別列にも出力します。

## ヒット件数が少ないとき

まず `sql-snippets`、`unresolved-sql`、`warnings` を確認してください。

- `sql-snippets` が少ない: SQL文字列の構築自体を検出できていない可能性があります。解析対象ファイルを指定している場合は、解析対象ファイルを省略してフォルダ全体を解析してください。
- `unresolved-sql` が多い: 実行箇所は検出できていますが、SQL文字列やテーブル名を静的に確定できていません。
- `warnings` がある: ファイル読み込みや構文解析で除外されたファイルがあります。
- 解析対象ファイルを指定していて `target-only` を使っている: 指定ファイル内だけを解析します。関連Repositoryやhelperも見る場合は `related-files` を使ってください。
- 解析対象ファイルを指定していて `related-files` でも少ない: そのファイルから到達できる呼び出しを中心に解析します。別ファイル単体で実行されるSQLも拾う場合は、解析対象ファイルを省略してフォルダ全体を解析してください。
- 対象外拡張子や除外フォルダにSQLがある: 既定では `.cs` / `.cshtml.cs` を解析し、`bin`、`obj`、`Migrations` などは除外します。

静的解析のため、外部ライブラリ内部、DB設定値、環境変数、実行時に組み立てられる値は完全には確定できません。その場合も、実行箇所を検出できたものは `unresolved-sql` に残す方針です。

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

空フォルダに `bootstrap.ps1` だけを置いて、リリース用 exe まで作成する場合:

```powershell
.\bootstrap.ps1 publish
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
  -Out "C:\work\table-analysis" `
  -Format csv `
  -AnalysisScope related-files
```

単一ファイルだけ解析する場合:

```powershell
.\bootstrap.ps1 analyze `
  -ProjectFolder "C:\src\MyApp" `
  -AnalysisFolder "C:\src\MyApp\Pages" `
  -AnalysisFile "C:\src\MyApp\Pages\Users\Index.cshtml.cs" `
  -Out "C:\work\table-analysis" `
  -Format xlsx
```

既存の `src` を上書きして展開したい場合:

```powershell
.\bootstrap.ps1 init -Force
```
