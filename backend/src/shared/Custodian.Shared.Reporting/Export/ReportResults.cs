using System.Globalization;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Custodian.Shared.Reporting.Export;

/// <summary>
/// CSTD-36-3: how a rendered report leaves a service. A report endpoint ends with
/// <code>return ReportResults.File(ReportResults.Output(model.Metadata, format, bytes));</code>
/// </summary>
public static class ReportResults
{
    /// <summary><c>custodian-{reportcode-lower}-{yyyyMMdd-HHmm}Z.{ext}</c>, from the report's own "now".</summary>
    public static string FileName(ReportMetadata metadata, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var stamp = metadata.GeneratedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"custodian-{metadata.ReportCode.ToLowerInvariant()}-{stamp}Z.{ReportFormats.Extension(format)}";
    }

    /// <summary>
    /// <c>custodian-{reportcode-lower}-{subject}-{yyyyMMdd-HHmm}Z.{ext}</c>, for a report about one subject
    /// (e.g. an engagement code). The subject is lower-cased and reduced to letters, digits and dashes,
    /// so the name still needs no escaping in Content-Disposition.
    /// </summary>
    public static string FileName(ReportMetadata metadata, ReportFormat format, string subject)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var slug = new string(subject.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        if (slug.Length == 0) return FileName(metadata, format);

        var stamp = metadata.GeneratedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"custodian-{metadata.ReportCode.ToLowerInvariant()}-{slug}-{stamp}Z.{ReportFormats.Extension(format)}";
    }

    public static ReportOutput Output(ReportMetadata metadata, ReportFormat format, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ReportOutput(FileName(metadata, format), content, ReportFormats.ContentType(format));
    }

    public static ReportOutput Output(ReportMetadata metadata, ReportFormat format, byte[] content, string subject)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(subject);
        return new ReportOutput(FileName(metadata, format, subject), content, ReportFormats.ContentType(format));
    }

    /// <summary>
    /// The download response: the content type, <c>Content-Disposition: attachment; filename="…"</c>,
    /// and <c>Cache-Control: no-store</c> (reports hold client data and are live; nothing may cache them).
    /// </summary>
    public static IActionResult File(ReportOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.Name);
        return new ReportFileResult(output);
    }

    private sealed class ReportFileResult(ReportOutput output) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = output.ContentType;
            response.ContentLength = output.Content.Length;
            // File names are built from the report code and a timestamp only, so they need no escaping.
            response.Headers[HeaderNames.ContentDisposition] = $"attachment; filename=\"{output.Name}\"";
            response.Headers[HeaderNames.CacheControl] = "no-store";
            response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            await response.Body.WriteAsync(output.Content, context.HttpContext.RequestAborted);
        }
    }
}
