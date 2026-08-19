namespace Custodian.Shared.Reporting.Reports;

public sealed record ReportOutput(string Name, byte[] Content, string ContentType);