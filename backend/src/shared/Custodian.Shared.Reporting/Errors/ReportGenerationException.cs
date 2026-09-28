namespace Custodian.Shared.Reporting.Errors;

/// <summary>CSTD-36-3: why a report could not be produced. Each kind has one HTTP status and title.</summary>
public enum ReportErrorKind
{
    /// <summary>400 "Invalid report filter": bad date, from &gt; to, unknown stage, unsupported format.</summary>
    InvalidFilter,

    /// <summary>403: the caller's role may not run this report, or the token has no tenant.</summary>
    Forbidden,

    /// <summary>404 "Report subject not found": e.g. an engagement outside the caller's tenant.</summary>
    SubjectNotFound,

    /// <summary>503 "Report data source unavailable — try again": a service the report reads is down.</summary>
    DataSourceUnavailable,

    /// <summary>500 "Report could not be generated": rendering or an unexpected failure.</summary>
    GenerationFailed
}

/// <summary>
/// CSTD-36-3: thrown by report code for an expected failure; <see cref="ReportExceptionFilter"/> turns
/// it into ProblemDetails. The message is shown to the user for every kind except
/// <see cref="ReportErrorKind.GenerationFailed"/>, so write it for them and keep ids and internals out.
/// Empty data is not an error: return a report with EmptySections instead.
/// </summary>
public sealed class ReportGenerationException : Exception
{
    public ReportGenerationException(
        ReportErrorKind kind,
        string message,
        string? field = null,
        string? reportCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Field = field;
        ReportCode = reportCode;
    }

    public ReportErrorKind Kind { get; }

    /// <summary>The query parameter at fault, for <see cref="ReportErrorKind.InvalidFilter"/>.</summary>
    public string? Field { get; }

    /// <summary>Overrides the report code set on the endpoint, when one endpoint serves several reports.</summary>
    public string? ReportCode { get; }

    public static ReportGenerationException InvalidFilter(string field, string message) =>
        new(ReportErrorKind.InvalidFilter, message, field);

    public static ReportGenerationException Forbidden(string message) =>
        new(ReportErrorKind.Forbidden, message);

    public static ReportGenerationException SubjectNotFound(string message) =>
        new(ReportErrorKind.SubjectNotFound, message);

    public static ReportGenerationException DataSourceUnavailable(string message, Exception? innerException = null) =>
        new(ReportErrorKind.DataSourceUnavailable, message, innerException: innerException);
}
