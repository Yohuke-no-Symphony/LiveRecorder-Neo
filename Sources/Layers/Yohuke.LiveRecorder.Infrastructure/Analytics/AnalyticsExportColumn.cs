namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed record AnalyticsExportColumn(string Label, double Width = 20, uint StyleIndex = AnalyticsSpreadsheetStyles.DEFAULT);
