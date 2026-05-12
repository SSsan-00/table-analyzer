using System.IO.Compression;
using System.Text;

namespace TableAnalyzer.Core;

public sealed class XlsxReportWriter
{
    public void Write(string reportDirectory, AnalysisResult result)
    {
        Directory.CreateDirectory(reportDirectory);
        var data = ReportDataBuilder.Build(result);
        var tables = data.Tables
            .Concat([BuildSummaryTable(data.SummaryLines)])
            .ToArray();

        var path = Path.Combine(reportDirectory, "table-analysis.xlsx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "[Content_Types].xml", BuildContentTypes(tables.Length));
        AddEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddEntry(archive, "xl/workbook.xml", BuildWorkbook(tables));
        AddEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships(tables.Length));
        AddEntry(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
            </styleSheet>
            """);

        for (var index = 0; index < tables.Length; index++)
        {
            AddEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheet(tables[index]));
        }
    }

    private static ReportTable BuildSummaryTable(IReadOnlyList<string> summaryLines)
    {
        var rows = summaryLines
            .Select(line =>
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator < 0)
                {
                    return (IReadOnlyList<string>)[line, ""];
                }

                return (IReadOnlyList<string>)[line[..separator], line[(separator + 1)..].TrimStart()];
            })
            .ToArray();
        return new ReportTable("run-summary", ["Name", "Value"], rows);
    }

    private static string BuildContentTypes(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        builder.AppendLine("""  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        builder.AppendLine("""  <Default Extension="xml" ContentType="application/xml"/>""");
        builder.AppendLine("""  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        builder.AppendLine("""  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.AppendLine($"""  <Override PartName="/xl/worksheets/sheet{index}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        builder.AppendLine("</Types>");
        return builder.ToString();
    }

    private static string BuildWorkbook(IReadOnlyList<ReportTable> tables)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        builder.AppendLine("  <sheets>");
        for (var index = 0; index < tables.Count; index++)
        {
            builder.AppendLine($"""    <sheet name="{XmlEscape(SanitizeSheetName(tables[index].Name))}" sheetId="{index + 1}" r:id="rId{index + 1}"/>""");
        }

        builder.AppendLine("  </sheets>");
        builder.AppendLine("</workbook>");
        return builder.ToString();
    }

    private static string BuildWorkbookRelationships(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.AppendLine($"""  <Relationship Id="rId{index}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index}.xml"/>""");
        }

        builder.AppendLine($"""  <Relationship Id="rId{sheetCount + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        builder.AppendLine("</Relationships>");
        return builder.ToString();
    }

    private static string BuildWorksheet(ReportTable table)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("  <sheetData>");
        WriteRow(builder, 1, table.Headers);
        for (var index = 0; index < table.Rows.Count; index++)
        {
            WriteRow(builder, index + 2, table.Rows[index]);
        }

        builder.AppendLine("  </sheetData>");
        builder.AppendLine("</worksheet>");
        return builder.ToString();
    }

    private static void WriteRow(StringBuilder builder, int rowNumber, IReadOnlyList<string> values)
    {
        builder.Append($"""    <row r="{rowNumber}">""");
        for (var index = 0; index < values.Count; index++)
        {
            var cellReference = GetCellReference(index, rowNumber);
            builder.Append($"""<c r="{cellReference}" t="inlineStr"><is><t xml:space="preserve">{XmlEscape(values[index])}</t></is></c>""");
        }

        builder.AppendLine("</row>");
    }

    private static string GetCellReference(int columnIndex, int rowNumber)
    {
        var dividend = columnIndex + 1;
        var columnName = "";
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = (char)('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName + rowNumber;
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.ReplaceLineEndings("\n"));
    }

    private static string SanitizeSheetName(string name)
    {
        var invalid = new HashSet<char>(['[', ']', '*', '?', '/', '\\', ':']);
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "sheet";
        }

        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
