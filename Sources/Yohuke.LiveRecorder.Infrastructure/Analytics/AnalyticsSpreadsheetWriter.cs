using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsSpreadsheetWriter : IDisposable
{
    private const int MAX_SHEET_ROWS = 1048576;
    private const double MAX_ROW_HEIGHT = 409;
    private readonly SpreadsheetDocument document;
    private readonly WorkbookPart workbook;
    private readonly Sheets sheets;
    private readonly AnalyticsExportLabels labels;
    private readonly CancellationToken cancellationToken;
    private readonly int maximumRows;

    internal AnalyticsSpreadsheetWriter(Stream stream, AnalyticsExportLabels labels, CancellationToken cancellationToken, int maximumRows = MAX_SHEET_ROWS)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, MAX_SHEET_ROWS);
        this.labels = labels;
        this.cancellationToken = cancellationToken;
        this.maximumRows = maximumRows;
        document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook(new BookViews(new WorkbookView()));
        sheets = workbook.Workbook.AppendChild(new Sheets());
        workbook.AddNewPart<WorkbookStylesPart>().Stylesheet = AnalyticsSpreadsheetStyles.Create();
    }

    internal void WriteTable(string name, IReadOnlyList<AnalyticsExportColumn> columns, IEnumerable<object?[]> rows)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AnalyticsExportColumn> allColumns = [new(labels["recordNumber"], 13, AnalyticsSpreadsheetStyles.NUMBER), ..columns,
            new(labels["textPart"], 12, AnalyticsSpreadsheetStyles.NUMBER), new(labels["textParts"], 12, AnalyticsSpreadsheetStyles.NUMBER)];
        var part = 1;
        var rowNumber = 1U;
        var recordNumber = 0L;
        OpenXmlWriter? writer = null;
        try
        {
            writer = StartSheet(name, part, allColumns);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Length != columns.Count)
                {
                    throw new InvalidOperationException("The export row does not match its columns.");
                }
                recordNumber++;
                var strings = row.Select(value => Value(value) is string text ? AnalyticsSpreadsheetText.Split(text) : null).ToArray();
                var fragments = strings.Select(value => value?.Count ?? 1).DefaultIfEmpty(1).Max();
                for (var fragment = 0; fragment < fragments; fragment++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rowNumber == maximumRows)
                    {
                        FinishSheet(writer, allColumns.Count, rowNumber);
                        writer.Dispose();
                        writer = null;
                        writer = StartSheet(name, ++part, allColumns);
                        rowNumber = 1;
                    }
                    var values = row.Select((value, column) => strings[column] is not { } textParts
                        ? fragment == 0 ? Value(value) : null
                        : textParts.Count == 1 ? textParts[0] : fragment < textParts.Count ? textParts[fragment] : null).ToArray();
                    writer.WriteStartElement(new Row { RowIndex = ++rowNumber, Height = RowHeight(values, columns), CustomHeight = true });
                    WriteCell(writer, 0, rowNumber, recordNumber, AnalyticsSpreadsheetStyles.NUMBER);
                    for (var column = 0; column < row.Length; column++)
                    {
                        var style = row[column] is AnalyticsExportCell cell ? cell.StyleIndex : columns[column].StyleIndex;
                        WriteCell(writer, column + 1, rowNumber, values[column], style);
                    }
                    WriteCell(writer, allColumns.Count - 2, rowNumber, fragment + 1, AnalyticsSpreadsheetStyles.NUMBER);
                    WriteCell(writer, allColumns.Count - 1, rowNumber, fragments, AnalyticsSpreadsheetStyles.NUMBER);
                    writer.WriteEndElement();
                }
            }
            FinishSheet(writer, allColumns.Count, rowNumber);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>完成工作簿包；调用方继续持有输出流。</summary>
    public void Dispose()
    {
        document.Dispose();
    }

    private OpenXmlWriter StartSheet(string name, int part, IReadOnlyList<AnalyticsExportColumn> columns)
    {
        var suffix = part == 1 ? "" : " (" + part.ToString(CultureInfo.InvariantCulture) + ")";
        var sheetName = name[..Math.Min(name.Length, 31 - suffix.Length)] + suffix;
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        sheets.AppendChild(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = (uint)sheets.ChildElements.Count + 1, Name = sheetName });
        var writer = OpenXmlWriter.Create(worksheet);
        writer.WriteStartElement(new Worksheet());
        writer.WriteElement(new SheetViews(new SheetView(
            new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
            new Selection { Pane = PaneValues.BottomLeft, ActiveCell = "A2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "A2" } })
        { WorkbookViewId = 0, ShowGridLines = false }));
        writer.WriteElement(new SheetFormatProperties { DefaultRowHeight = 18 });
        writer.WriteStartElement(new Columns());
        for (var index = 0; index < columns.Count; index++)
        {
            writer.WriteElement(new Column
            {
                Min = (uint)index + 1, Max = (uint)index + 1, Width = columns[index].Width, CustomWidth = true,
                Hidden = index == 0 || index >= columns.Count - 2
            });
        }
        writer.WriteEndElement();
        writer.WriteStartElement(new SheetData());
        writer.WriteStartElement(new Row { RowIndex = 1, Height = 34, CustomHeight = true });
        for (var index = 0; index < columns.Count; index++)
        {
            WriteCell(writer, index, 1, columns[index].Label, AnalyticsSpreadsheetStyles.HEADER);
        }
        writer.WriteEndElement();
        return writer;
    }

    private static void FinishSheet(OpenXmlWriter writer, int columns, uint rowNumber)
    {
        writer.WriteEndElement();
        writer.WriteElement(new AutoFilter { Reference = "A1:" + ColumnName(columns - 1) + rowNumber.ToString(CultureInfo.InvariantCulture) });
        writer.WriteEndElement();
    }

    private static object? Value(object? value) => value is AnalyticsExportCell cell ? cell.Value : value;

    private static void WriteCell(OpenXmlWriter writer, int column, uint row, object? value, uint style)
    {
        var cell = new Cell { CellReference = ColumnName(column) + row.ToString(CultureInfo.InvariantCulture), StyleIndex = style };
        switch (value)
        {
            case null:
                break;
            case string text:
                if (style != AnalyticsSpreadsheetStyles.HEADER)
                {
                    cell.StyleIndex = AnalyticsSpreadsheetStyles.TEXT;
                }
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                break;
            case DateTime date:
                cell.StyleIndex = AnalyticsSpreadsheetStyles.DATE;
                cell.DataType = CellValues.Number;
                cell.CellValue = new(date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                break;
            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new(flag ? "1" : "0");
                break;
            default:
                if (style == AnalyticsSpreadsheetStyles.NUMBER && HasFraction(value))
                {
                    cell.StyleIndex = AnalyticsSpreadsheetStyles.DECIMAL;
                }
                cell.DataType = CellValues.Number;
                cell.CellValue = new(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
        writer.WriteElement(cell);
    }

    private static bool HasFraction(object value) => value switch
    {
        decimal number => number % 1 != 0,
        double number => number % 1 != 0,
        float number => number % 1 != 0,
        _ => false
    };

    private static double RowHeight(IReadOnlyList<object?> values, IReadOnlyList<AnalyticsExportColumn> columns)
    {
        var maximumLines = 1;
        for (var column = 0; column < values.Count; column++)
        {
            if (values[column] is not string text)
            {
                continue;
            }
            var width = Math.Max(1, columns[column].Width - 2);
            var lines = 1;
            var used = 0;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    lines++;
                    used = 0;
                    continue;
                }
                var characterWidth = character == '\t' ? 4 : character >= '\u2E80' && !char.IsSurrogate(character) ? 2 : 1;
                if (used + characterWidth > width)
                {
                    lines++;
                    used = 0;
                }
                used += characterWidth;
                if (lines * 18 + 4 >= MAX_ROW_HEIGHT)
                {
                    return MAX_ROW_HEIGHT;
                }
            }
            maximumLines = Math.Max(maximumLines, lines);
        }
        return Math.Min(MAX_ROW_HEIGHT, maximumLines * 18 + 4);
    }

    private static string ColumnName(int index)
    {
        var value = "";
        do
        {
            value = (char)('A' + index % 26) + value;
            index = index / 26 - 1;
        }
        while (index >= 0);
        return value;
    }
}
