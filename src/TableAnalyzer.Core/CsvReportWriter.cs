using System.Text;

namespace TableAnalyzer.Core;

public sealed class CsvReportWriter
{
    public void Write(string reportDirectory, AnalysisResult result)
    {
        Directory.CreateDirectory(reportDirectory);
        var data = ReportDataBuilder.Build(result);

        foreach (var table in data.Tables)
        {
            WriteCsv(Path.Combine(reportDirectory, table.Name + ".csv"), table.Headers, table.Rows);
        }

        WriteText(Path.Combine(reportDirectory, "run-summary.txt"), string.Join(Environment.NewLine, data.SummaryLines) + Environment.NewLine);
    }

    private static void WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        WriteText(path, builder.ToString().ReplaceLineEndings("\r\n"));
    }

    private static void WriteText(string path, string text)
    {
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
