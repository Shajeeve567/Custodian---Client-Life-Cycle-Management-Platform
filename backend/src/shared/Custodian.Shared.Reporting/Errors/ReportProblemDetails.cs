using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Shared.Reporting.Errors;

/// <summary>
/// CSTD-36-3 error contract (AC5): one status and title per <see cref="ReportErrorKind"/>, plus
/// <c>reportCode</c>, <c>correlationId</c> and, for filter errors, <c>field</c> extensions.
/// A generation failure never shows its exception message; users quote the correlation id instead.
/// </summary>
public static class ReportProblemDetails
{
    public const string InvalidFilterTitle = "Invalid report filter";
    public const string ForbiddenTitle = "Forbidden";
    public const string SubjectNotFoundTitle = "Report subject not found";
    public const string DataSourceUnavailableTitle = "Report data source unavailable — try again";
    public const string GenerationFailedTitle = "Report could not be generated";

    public static int StatusFor(ReportErrorKind kind) => kind switch
    {
        ReportErrorKind.InvalidFilter => StatusCodes.Status400BadRequest,
        ReportErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ReportErrorKind.SubjectNotFound => StatusCodes.Status404NotFound,
        ReportErrorKind.DataSourceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    public static string TitleFor(ReportErrorKind kind) => kind switch
    {
        ReportErrorKind.InvalidFilter => InvalidFilterTitle,
        ReportErrorKind.Forbidden => ForbiddenTitle,
        ReportErrorKind.SubjectNotFound => SubjectNotFoundTitle,
        ReportErrorKind.DataSourceUnavailable => DataSourceUnavailableTitle,
        _ => GenerationFailedTitle
    };

    public static ProblemDetails Create(
        ReportErrorKind kind,
        string? message,
        string? reportCode,
        string correlationId,
        string? field = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusFor(kind),
            Title = TitleFor(kind),
            Detail = kind == ReportErrorKind.GenerationFailed
                ? $"Something went wrong while generating the report. Quote reference {correlationId} if you report this."
                : message
        };

        if (!string.IsNullOrWhiteSpace(reportCode)) problem.Extensions["reportCode"] = reportCode;
        problem.Extensions["correlationId"] = correlationId;
        if (kind == ReportErrorKind.InvalidFilter && !string.IsNullOrWhiteSpace(field)) problem.Extensions["field"] = field;

        return problem;
    }
}
