using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Custodian.Shared.Reporting.Errors;

/// <summary>
/// CSTD-36-3: put on a report endpoint or controller to get the report error contract:
/// <code>[ReportErrors("SLA_PERFORMANCE")]</code>
/// A <see cref="ReportGenerationException"/> maps to its kind; any other exception is a 500
/// "Report could not be generated", logged with the correlation id and never echoed to the caller.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReportErrorsAttribute : TypeFilterAttribute
{
    public ReportErrorsAttribute(string reportCode) : base(typeof(ReportExceptionFilter))
    {
        Arguments = [reportCode];
    }
}

public sealed class ReportExceptionFilter : IExceptionFilter
{
    private readonly string _reportCode;
    private readonly ILogger<ReportExceptionFilter> _logger;

    public ReportExceptionFilter(string reportCode, ILogger<ReportExceptionFilter> logger)
    {
        _reportCode = reportCode;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        // The caller went away: nothing to answer, and not a failure of the report.
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var correlationId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        var reportException = context.Exception as ReportGenerationException;
        var kind = reportException?.Kind ?? ReportErrorKind.GenerationFailed;
        var reportCode = reportException?.ReportCode ?? _reportCode;

        // Only the kind is logged for expected errors: messages can name filter values (client data).
        if (kind == ReportErrorKind.GenerationFailed)
        {
            _logger.LogError(context.Exception,
                "Report {ReportCode} could not be generated (correlation {CorrelationId}).", reportCode, correlationId);
        }
        else if (kind == ReportErrorKind.DataSourceUnavailable)
        {
            _logger.LogWarning(context.Exception,
                "Report {ReportCode}: data source unavailable (correlation {CorrelationId}).", reportCode, correlationId);
        }
        else
        {
            _logger.LogInformation(
                "Report {ReportCode} refused: {Kind} (correlation {CorrelationId}).", reportCode, kind, correlationId);
        }

        var problem = ReportProblemDetails.Create(kind, reportException?.Message, reportCode, correlationId, reportException?.Field);
        problem.Instance = context.HttpContext.Request.Path;

        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" }
        };
        context.ExceptionHandled = true;
    }
}
