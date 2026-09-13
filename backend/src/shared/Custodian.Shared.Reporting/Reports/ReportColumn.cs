namespace Custodian.Shared.Reporting.Reports;

public sealed record ReportColumn<T>(string Header, Func<T, object?> Value);