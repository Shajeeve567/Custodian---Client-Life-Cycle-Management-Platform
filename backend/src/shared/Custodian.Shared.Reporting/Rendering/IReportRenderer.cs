using Custodian.Shared.Reporting.Models;

namespace Custodian.Shared.Reporting.Rendering;

/// <summary>CSTD-36-2: turns a report model into a downloadable file. Layout only, no data access.</summary>
public interface IReportRenderer
{
    /// <summary>Renders the model as a PDF. The same model always gives the same pages.</summary>
    byte[] RenderPdf(ReportModel model);
}
