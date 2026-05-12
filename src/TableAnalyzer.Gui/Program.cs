using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using TableAnalyzer.Core;

namespace TableAnalyzer.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly PathInputRow _projectFolderRow;
    private readonly PathInputRow _analysisFolderRow;
    private readonly PathInputRow _analysisFileRow;
    private readonly PathInputRow _outputRootRow;
    private readonly ComboBox _outputFormatComboBox = new();
    private readonly Button _runButton = new();
    private readonly Button _openReportButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressLabel = new();
    private readonly TextBox _resultTextBox = new();
    private string _lastReportDirectory = "";

    public MainForm()
    {
        Text = "Table Analyzer";
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var settings = GuiSettingsStore.Load();
        _projectFolderRow = PathInputRow.ForFolder("解析対象プロジェクトフォルダ", settings.ProjectFolder);
        _analysisFolderRow = PathInputRow.ForFolder("解析対象フォルダ", settings.AnalysisFolder);
        _analysisFileRow = PathInputRow.ForFile("解析対象ファイル", settings.AnalysisFile);
        _outputRootRow = PathInputRow.ForFolder("出力先フォルダ", settings.OutputRoot);
        ConfigureOutputFormat(settings.OutputFormat);

        _projectFolderRow.PathSelected += path =>
        {
            if (string.IsNullOrWhiteSpace(_analysisFolderRow.PathValue))
            {
                _analysisFolderRow.PathValue = path;
            }
        };
        _analysisFolderRow.PathSelected += path =>
        {
            if (string.IsNullOrWhiteSpace(_projectFolderRow.PathValue))
            {
                _projectFolderRow.PathValue = path;
            }
        };
        _analysisFileRow.PathSelected += path =>
        {
            if (string.IsNullOrWhiteSpace(_analysisFolderRow.PathValue))
            {
                _analysisFolderRow.PathValue = Path.GetDirectoryName(path) ?? "";
            }
        };

        ConfigureButtons();
        ConfigureProgress();
        ConfigureResultBox();
        BuildLayout();

        FormClosing += (_, _) => SaveSettings();
    }

    private void ConfigureButtons()
    {
        _runButton.Text = "解析を実行";
        _runButton.AutoSize = true;
        _runButton.MinimumSize = new Size(120, 36);
        _runButton.Click += async (_, _) => await RunAnalysisAsync();

        _openReportButton.Text = "出力先を開く";
        _openReportButton.AutoSize = true;
        _openReportButton.MinimumSize = new Size(120, 36);
        _openReportButton.Enabled = false;
        _openReportButton.Click += (_, _) => OpenLastReportDirectory();
    }

    private void ConfigureOutputFormat(string value)
    {
        _outputFormatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _outputFormatComboBox.Items.AddRange(["csv", "xlsx"]);
        _outputFormatComboBox.SelectedItem = string.Equals(value, "xlsx", StringComparison.OrdinalIgnoreCase)
            ? "xlsx"
            : "csv";
    }

    private void ConfigureProgress()
    {
        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Height = 18;

        _progressLabel.AutoEllipsis = true;
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        _progressLabel.Text = "待機中";
    }

    private void ConfigureResultBox()
    {
        _resultTextBox.Dock = DockStyle.Fill;
        _resultTextBox.Multiline = true;
        _resultTextBox.ReadOnly = true;
        _resultTextBox.ScrollBars = ScrollBars.Vertical;
        _resultTextBox.Font = new Font(FontFamily.GenericMonospace, 9);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var inputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true
        };
        inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputPanel.Controls.Add(_projectFolderRow, 0, 0);
        inputPanel.Controls.Add(_analysisFolderRow, 0, 1);
        inputPanel.Controls.Add(_analysisFileRow, 0, 2);
        inputPanel.Controls.Add(_outputRootRow, 0, 3);
        inputPanel.Controls.Add(BuildOutputFormatRow(), 0, 4);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 12, 0, 6)
        };
        buttonPanel.Controls.Add(_runButton);
        buttonPanel.Controls.Add(_openReportButton);

        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 10)
        };
        progressPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progressPanel.Controls.Add(_progressLabel, 0, 0);
        progressPanel.Controls.Add(_progressBar, 0, 1);

        root.Controls.Add(inputPanel, 0, 0);
        root.Controls.Add(buttonPanel, 0, 1);
        root.Controls.Add(progressPanel, 0, 2);
        root.Controls.Add(_resultTextBox, 0, 3);
        Controls.Add(root);
    }

    private async Task RunAnalysisAsync()
    {
        SaveSettings();
        var request = new AnalysisRunRequest(
            _projectFolderRow.PathValue,
            _analysisFolderRow.PathValue,
            string.IsNullOrWhiteSpace(_analysisFileRow.PathValue) ? null : _analysisFileRow.PathValue,
            _outputRootRow.PathValue,
            SelectedOutputFormat);

        if (!ValidateRequest(request))
        {
            return;
        }

        SetBusy(true);
        _lastReportDirectory = "";
        _openReportButton.Enabled = false;
        _resultTextBox.Clear();
        _progressBar.Value = 0;
        _progressLabel.Text = "解析準備中";

        var progress = new Progress<AnalysisProgress>(UpdateProgress);
        try
        {
            var run = await Task.Run(() => new AnalysisRunner().Run(request, new AnalyzerConfiguration(), progress));
            _lastReportDirectory = run.ReportDirectory;
            _openReportButton.Enabled = true;
            _progressBar.Value = 100;
            _progressLabel.Text = "完了";
            _resultTextBox.Text = BuildResultText(run);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _progressLabel.Text = "エラー";
            _resultTextBox.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "解析できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool ValidateRequest(AnalysisRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectFolder) ||
            string.IsNullOrWhiteSpace(request.AnalysisFolder) ||
            string.IsNullOrWhiteSpace(request.OutputRoot))
        {
            MessageBox.Show(this, "解析対象プロジェクトフォルダ、解析対象フォルダ、出力先フォルダは必須です。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void UpdateProgress(AnalysisProgress progress)
    {
        var percent = progress.Total <= 0
            ? 0
            : Math.Clamp((int)Math.Floor(progress.Completed * 100.0 / progress.Total), 0, 100);
        _progressBar.Value = percent;

        var stage = progress.Stage switch
        {
            "indexing" => "索引作成",
            "analyzing" => "解析",
            _ => "処理"
        };
        var file = string.IsNullOrWhiteSpace(progress.CurrentFile)
            ? ""
            : $"  {progress.CurrentFile}";
        _progressLabel.Text = $"{stage}: {progress.Completed}/{progress.Total} ({percent}%){file}";
    }

    private void SetBusy(bool busy)
    {
        _runButton.Enabled = !busy;
        _projectFolderRow.Enabled = !busy;
        _analysisFolderRow.Enabled = !busy;
        _analysisFileRow.Enabled = !busy;
        _outputRootRow.Enabled = !busy;
        _outputFormatComboBox.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void OpenLastReportDirectory()
    {
        if (string.IsNullOrWhiteSpace(_lastReportDirectory) || !Directory.Exists(_lastReportDirectory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _lastReportDirectory,
            UseShellExecute = true
        });
    }

    private void SaveSettings()
    {
        GuiSettingsStore.Save(new GuiSettings(
            _projectFolderRow.PathValue,
            _analysisFolderRow.PathValue,
            _analysisFileRow.PathValue,
            _outputRootRow.PathValue,
            _outputFormatComboBox.SelectedItem?.ToString() ?? "csv"));
    }

    private static string BuildResultText(AnalysisRunResult run)
    {
        return string.Join(Environment.NewLine,
        [
            $"出力先: {run.ReportDirectory}",
            $"出力形式: {run.OutputFormat.ToString().ToLowerInvariant()}",
            $"解析ファイル数: {run.AnalysisFiles.Count}",
            $"索引ファイル数: {run.ContextFiles.Count}",
            $"SQLスニペット: {run.AnalysisResult.SqlSnippets.Count}",
            $"テーブル利用: {run.AnalysisResult.TableUsages.Count}",
            $"動的SQL: {run.AnalysisResult.DynamicSql.Count}",
            $"未解決SQL: {run.AnalysisResult.UnresolvedSql.Count}",
            $"警告: {run.AnalysisResult.Warnings.Count}"
        ]);
    }

    private ReportOutputFormat SelectedOutputFormat =>
        string.Equals(_outputFormatComboBox.SelectedItem?.ToString(), "xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReportOutputFormat.Xlsx
            : ReportOutputFormat.Csv;

    private Control BuildOutputFormatRow()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        var label = new Label
        {
            Text = "出力形式",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _outputFormatComboBox.Dock = DockStyle.Fill;

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_outputFormatComboBox, 1, 0);
        return layout;
    }
}

internal sealed class PathInputRow : UserControl
{
    private readonly TextBox _textBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _clearButton = new();
    private readonly bool _isFolder;

    public event Action<string>? PathSelected;

    private PathInputRow(string label, string value, bool isFolder)
    {
        _isFolder = isFolder;
        Dock = DockStyle.Top;
        AutoSize = true;
        Padding = new Padding(0, 0, 0, 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));

        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _textBox.Text = value;
        _textBox.Dock = DockStyle.Fill;
        _textBox.AllowDrop = true;
        _textBox.DragEnter += TextBoxDragEnter;
        _textBox.DragDrop += TextBoxDragDrop;

        _browseButton.Text = "選択";
        _browseButton.Dock = DockStyle.Fill;
        _browseButton.Click += (_, _) => Browse();

        _clearButton.Text = "クリア";
        _clearButton.Dock = DockStyle.Fill;
        _clearButton.Click += (_, _) => PathValue = "";

        layout.Controls.Add(labelControl, 0, 0);
        layout.Controls.Add(_textBox, 1, 0);
        layout.Controls.Add(_browseButton, 2, 0);
        layout.Controls.Add(_clearButton, 3, 0);

        Controls.Add(layout);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PathValue
    {
        get => _textBox.Text.Trim();
        set => _textBox.Text = value.Trim();
    }

    public static PathInputRow ForFolder(string label, string value)
    {
        return new PathInputRow(label, value, isFolder: true);
    }

    public static PathInputRow ForFile(string label, string value)
    {
        return new PathInputRow(label, value, isFolder: false);
    }

    private void Browse()
    {
        if (_isFolder)
        {
            using var dialog = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(PathValue) ? PathValue : "",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                PathValue = dialog.SelectedPath;
                PathSelected?.Invoke(PathValue);
            }

            return;
        }

        using var fileDialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "C# / Razor Pages (*.cs;*.cshtml.cs)|*.cs;*.cshtml.cs|All files (*.*)|*.*",
            FileName = File.Exists(PathValue) ? PathValue : ""
        };
        if (fileDialog.ShowDialog(this) == DialogResult.OK)
        {
            PathValue = fileDialog.FileName;
            PathSelected?.Invoke(PathValue);
        }
    }

    private void TextBoxDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void TextBoxDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        var path = files[0];
        if (_isFolder && File.Exists(path))
        {
            path = Path.GetDirectoryName(path) ?? path;
        }

        PathValue = path;
        PathSelected?.Invoke(PathValue);
    }
}

internal sealed record GuiSettings(
    string ProjectFolder = "",
    string AnalysisFolder = "",
    string AnalysisFile = "",
    string OutputRoot = "",
    string OutputFormat = "csv");

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
