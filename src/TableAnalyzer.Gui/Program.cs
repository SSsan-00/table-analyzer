using System.Net;
using System.Text.Json;
using TableAnalyzer.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    app.Urls.Add("http://localhost:5123");
}

app.MapGet("/", () =>
{
    var settings = GuiSettingsStore.Load();
    return Html(RenderPage(settings, null, null));
});

app.MapPost("/analyze", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var settings = new GuiSettings(
        GetFormValue(form, "projectFolder"),
        GetFormValue(form, "analysisFolder"),
        GetFormValue(form, "analysisFile"),
        GetFormValue(form, "outputRoot"));
    GuiSettingsStore.Save(settings);

    if (string.IsNullOrWhiteSpace(settings.ProjectFolder) ||
        string.IsNullOrWhiteSpace(settings.AnalysisFolder) ||
        string.IsNullOrWhiteSpace(settings.OutputRoot))
    {
        return Html(RenderPage(settings, null, "解析対象プロジェクトフォルダ、解析対象フォルダ、出力先フォルダは必須です。"));
    }

    try
    {
        var run = new AnalysisRunner().Run(
            new AnalysisRunRequest(
                settings.ProjectFolder,
                settings.AnalysisFolder,
                settings.AnalysisFile,
                settings.OutputRoot),
            new AnalyzerConfiguration());
        return Html(RenderPage(settings, run, null));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
    {
        return Html(RenderPage(settings, null, ex.Message));
    }
});

app.Run();

static IResult Html(string html)
{
    return Results.Content(html, "text/html; charset=utf-8");
}

static string GetFormValue(IFormCollection form, string key)
{
    return form.TryGetValue(key, out var value) ? value.ToString().Trim() : "";
}

static string RenderPage(GuiSettings settings, AnalysisRunResult? result, string? error)
{
    var projectFolder = WebUtility.HtmlEncode(settings.ProjectFolder);
    var analysisFolder = WebUtility.HtmlEncode(settings.AnalysisFolder);
    var analysisFile = WebUtility.HtmlEncode(settings.AnalysisFile);
    var outputRoot = WebUtility.HtmlEncode(settings.OutputRoot);
    var status = RenderStatus(result, error);

    return $$"""
        <!doctype html>
        <html lang="ja">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Table Analyzer</title>
          <style>
            :root {
              color-scheme: light;
              --bg: #f5f7fa;
              --panel: #ffffff;
              --text: #1d2430;
              --muted: #667085;
              --line: #d8dee8;
              --accent: #0f766e;
              --accent-hover: #115e59;
              --danger: #b42318;
              --ok: #027a48;
            }
            * {
              box-sizing: border-box;
            }
            body {
              margin: 0;
              font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
              background: var(--bg);
              color: var(--text);
            }
            main {
              width: min(980px, calc(100vw - 32px));
              margin: 32px auto;
            }
            h1 {
              margin: 0 0 20px;
              font-size: 28px;
              line-height: 1.25;
              font-weight: 700;
            }
            form,
            .status {
              background: var(--panel);
              border: 1px solid var(--line);
              border-radius: 8px;
              padding: 24px;
            }
            form {
              display: grid;
              gap: 18px;
            }
            label {
              display: grid;
              gap: 8px;
              font-size: 14px;
              font-weight: 650;
            }
            input {
              width: 100%;
              min-height: 42px;
              border: 1px solid #b8c2d0;
              border-radius: 6px;
              padding: 9px 11px;
              font: inherit;
              background: #fff;
              color: var(--text);
            }
            input:focus {
              outline: 3px solid rgba(15, 118, 110, .18);
              border-color: var(--accent);
            }
            button {
              justify-self: start;
              min-height: 42px;
              border: 0;
              border-radius: 6px;
              padding: 10px 18px;
              font: inherit;
              font-weight: 700;
              color: #fff;
              background: var(--accent);
              cursor: pointer;
            }
            button:hover {
              background: var(--accent-hover);
            }
            .status {
              margin-top: 18px;
            }
            .status h2 {
              margin: 0 0 12px;
              font-size: 18px;
            }
            .error {
              border-color: rgba(180, 35, 24, .35);
              color: var(--danger);
            }
            .success {
              border-color: rgba(2, 122, 72, .35);
            }
            dl {
              display: grid;
              grid-template-columns: max-content 1fr;
              gap: 8px 18px;
              margin: 0;
            }
            dt {
              color: var(--muted);
            }
            dd {
              margin: 0;
              overflow-wrap: anywhere;
            }
          </style>
        </head>
        <body>
          <main>
            <h1>Table Analyzer</h1>
            <form method="post" action="/analyze">
              <label>
                解析対象プロジェクトフォルダ
                <input name="projectFolder" value="{{projectFolder}}" required>
              </label>
              <label>
                解析対象フォルダ
                <input name="analysisFolder" value="{{analysisFolder}}" required>
              </label>
              <label>
                解析対象ファイル
                <input name="analysisFile" value="{{analysisFile}}">
              </label>
              <label>
                出力先フォルダ
                <input name="outputRoot" value="{{outputRoot}}" required>
              </label>
              <button type="submit">解析を実行</button>
            </form>
            {{status}}
          </main>
        </body>
        </html>
        """;
}

static string RenderStatus(AnalysisRunResult? result, string? error)
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        return $$"""
            <section class="status error">
              <h2>解析できませんでした</h2>
              <div>{{WebUtility.HtmlEncode(error)}}</div>
            </section>
            """;
    }

    if (result is null)
    {
        return "";
    }

    return $$"""
        <section class="status success">
          <h2>解析が完了しました</h2>
          <dl>
            <dt>出力先</dt><dd>{{WebUtility.HtmlEncode(result.ReportDirectory)}}</dd>
            <dt>解析ファイル数</dt><dd>{{result.AnalysisFiles.Count}}</dd>
            <dt>索引ファイル数</dt><dd>{{result.ContextFiles.Count}}</dd>
            <dt>SQLスニペット</dt><dd>{{result.AnalysisResult.SqlSnippets.Count}}</dd>
            <dt>テーブル利用</dt><dd>{{result.AnalysisResult.TableUsages.Count}}</dd>
            <dt>動的SQL</dt><dd>{{result.AnalysisResult.DynamicSql.Count}}</dd>
            <dt>未解決SQL</dt><dd>{{result.AnalysisResult.UnresolvedSql.Count}}</dd>
            <dt>警告</dt><dd>{{result.AnalysisResult.Warnings.Count}}</dd>
          </dl>
        </section>
        """;
}

internal sealed record GuiSettings(
    string ProjectFolder = "",
    string AnalysisFolder = "",
    string AnalysisFile = "",
    string OutputRoot = "");

internal static class GuiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GuiSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new GuiSettings();
            }

            return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path), JsonOptions) ?? new GuiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GuiSettings();
        }
    }

    public static void Save(GuiSettings settings)
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence is best-effort; analysis can still run without it.
        }
    }

    private static string GetSettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(root, "TableAnalyzer", "gui-settings.json");
    }
}
