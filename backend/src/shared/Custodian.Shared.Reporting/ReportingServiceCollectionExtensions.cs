using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Shared.Reporting;

public static class ReportingServiceCollectionExtensions
{
    /// <summary>
    /// CSTD-36: registers the shared report renderer and CSV exporter (both stateless singletons).
    /// Report endpoints add <c>[ReportErrors("REPORT_CODE")]</c> for the error contract; see docs/reporting.md.
    /// </summary>
    public static IServiceCollection AddCustodianReporting(this IServiceCollection services)
    {
        services.AddSingleton<IReportRenderer, PdfReportRenderer>();
        services.AddSingleton<ICsvExporter, CsvExporter>();
        return services;
    }
}
