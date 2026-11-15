using DocumentFormat.OpenXml.Spreadsheet;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class AnalyticsSpreadsheetStyles
{
    internal const uint DEFAULT = 0;
    internal const uint NUMBER = 1;
    internal const uint AMOUNT = 2;
    internal const uint DURATION = 3;
    internal const uint DATE = 4;
    internal const uint PERCENT = 5;
    internal const uint HEADER = 6;
    internal const uint TEXT = 7;
    internal const uint DECIMAL = 8;

    internal static Stylesheet Create()
    {
        var formats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "#,##0" },
            new NumberingFormat { NumberFormatId = 165, FormatCode = "#,##0.00########" },
            new NumberingFormat { NumberFormatId = 166, FormatCode = "[h]:mm:ss.000" },
            new NumberingFormat { NumberFormatId = 167, FormatCode = "yyyy-mm-dd hh:mm:ss.000\" UTC\"" },
            new NumberingFormat { NumberFormatId = 168, FormatCode = "0.0%" }) { Count = 5 };
        var fonts = new Fonts(
            new Font { FontSize = new() { Val = 11 }, FontName = new() { Val = "Aptos" } },
            new Font
            {
                FontSize = new() { Val = 11 },
                FontName = new() { Val = "Aptos" },
                Bold = new(),
                Color = new() { Rgb = "FFFFFFFF" }
            }) { Count = 2 };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF334155" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })) { Count = 3 };
        var borders = new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 };
        var cellFormats = new CellFormats(Format(0), Format(164), Format(165), Format(166), Format(167), Format(168),
            new CellFormat(new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center, WrapText = true })
            {
                FontId = 1, FillId = 2, BorderId = 0, FormatId = 0,
                ApplyFont = true, ApplyFill = true, ApplyAlignment = true
            },
            new CellFormat(new Alignment { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Top, WrapText = true })
            {
                NumberFormatId = 49, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0,
                ApplyNumberFormat = true, ApplyAlignment = true
            }, Format(165)) { Count = 9 };
        return new(formats, fonts, fills, borders,
            new CellStyleFormats(new CellFormat { FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            cellFormats, new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }

    private static CellFormat Format(uint format) => new(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true })
    {
        NumberFormatId = format, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0,
        ApplyNumberFormat = format != 0, ApplyAlignment = true
    };
}
