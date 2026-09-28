# Reporting

Custodian reports are PDF (and CSV) files generated on request from live service data (CSTD-36).
This page is the convention every report follows. The SLA Performance report (CSTD-37) is the first one.

## Where things live

| Concern | Lives in | Why |
|---|---|---|
| Data queries and numbers | The service that owns the data (Workflow: SLA, readiness; Audit: proof of delivery; Documents: validation) | Each service keeps its own database; no report reads another service's tables |
| Report model, layout, PDF/CSV output, errors | `backend/src/shared/Custodian.Shared.Reporting` | One look and one error contract for every report. No database access |

## The report model

A report is pure data: `ReportMetadata` plus an ordered list of sections (`Custodian.Shared.Reporting.Models`).

**`ReportMetadata`**
- `ReportCode`: upper case, digits, underscores (`SLA_PERFORMANCE`). It is used in the file name.
- `Title`.
- `TenantId` / `TenantDisplayName` (optional).
- `GeneratedAtUtc`.
- `GeneratedBy`.
- `AppliedFilters`: printed in the order given.
- `DataSource`, e.g. "Workflow service live database".

**Sections:**

| Section | Use |
|---|---|
| `KeyValueSection(title, pairs)` | Summary figures: "Total actions" → 42 |
| `TableSection(title, columns, rows, footnote?)` | Breakdowns. One cell per column; `ReportColumnAlignment.Right` for numbers |
| `TextSection(title, paragraphs)` | Explanations, e.g. metric definitions |
| `EmptySection(title, message?)` | A section with no matching data. Default message: "No records match the selected filters." |

**Rules:**
- **Live data only.** Every number in a report is computed from data the service passes into the model at request time. There is no caching, and templates hold labels only, never numbers or business text.
- **Empty data is not an error.** When nothing matches, the report is still generated, with `EmptySection`s and zero totals.
- **One "now" per report.** The service takes it once from `TimeProvider` and uses it for every calculation and for `GeneratedAtUtc`.
- **Keep values raw.** Put numbers and dates into cells as values, not pre-formatted strings, so CSV stays machine-readable. `ReportValue.Format` is the single text form:
    - invariant numbers (`1234.5`);
    - ISO-8601 UTC dates (`2026-09-28T08:05:00Z`);
    - `Yes`/`No` for booleans.
    
    Round in the builder (e.g. hours to 1 decimal). Supported cell types: string, bool, numbers, `DateTime`, `DateTimeOffset`, `DateOnly`, `Guid`, enums, null. Convert a `TimeSpan` to a number of hours first.
- **Separate the maths from the model.** Compute a plain result object (e.g. `SlaPerformanceData`) first, then build the model from it, so tests can check the numbers directly.

## PDF output

`PdfReportRenderer` (`IReportRenderer.RenderPdf(model)`) draws every report with one A4 layout:

- **Header:** "Custodian", the report title, the tenant, "Generated {UTC time} UTC by {actor}", and the data source.
- **Filters block:** the applied filters in order, or "No filters applied".
- **Sections:**
    - summaries as label/value rows;
    - tables with the header row repeated on every page;
    - text paragraphs;
    - empty sections as their message.
- **Footer:** "Generated from live data at time of request", the report code, and "Page X of Y".

The PDF's title, subject (report code) and creation date come from the model, and there are no random ids. The same model always gives the same pages.

**Library: [QuestPDF](https://www.questpdf.com)** (`2026.9.1`, in `Custodian.Shared.Reporting`), under its **Community licence**. The package's `LICENSE.md` allows free use for:

- learning and evaluation;
- academic institutions;
- OSI-licensed open-source projects;
- organisations under USD 1M annual revenue.

This university case-study project qualifies. A commercial deployment above that threshold would need a paid licence, or the renderer could be swapped for PdfSharp/MigraDoc (MIT) behind the same `IReportRenderer`. To be confirmed by the BA/team (CSTD-36-N1).

**Fonts:** only QuestPDF's bundled Lato is used; `UseSystemFonts` is off. The output therefore doesn't depend on fonts installed in the Docker image or on App Service Linux, and QuestPDF ships its native libraries for linux-x64. Text in scripts Lato lacks (e.g. Sinhala, Tamil names) renders as replacement glyphs instead of failing the report.

**Tests** read PDFs back with [PdfPig](https://www.nuget.org/packages/PdfPig) (Apache-2.0, tests only). The official package id is `PdfPig`. The similarly named `UglyToad.PdfPig` package on NuGet is not the official release and must not be used.

## CSV output

`CsvExporter` (`ICsvExporter.ToCsv(tableSection)`) writes one `TableSection` as a CSV file, usually a report's per-row detail table:

- **Encoding:** UTF-8 with a BOM, so Excel reads non-ASCII names correctly.
- **Format:** RFC 4180. Comma separated, CRLF row endings, and the header row first. A field is quoted when it contains a comma, quote or line break, or starts or ends with a space; quotes inside are doubled.
- **Values:** `ReportValue.Format`, i.e. invariant numbers (`1234.5`), ISO-8601 UTC dates, and `Yes`/`No`.
- **Formula protection:** text starting with `=`, `+`, `-`, `@`, tab or CR gets a leading `'`, so Excel shows it instead of running it as a formula. Task titles and file names are user-entered. Numbers, including negative ones, are written unchanged.
- **Only the data:** the table title and footnote are not written.

## Adding a report

1. **Compute the numbers.** In the owning service, query live data for the caller's tenant (`ReportAuthorization.RequireTenantId(User)`) and compute a plain result object.
2. **Build the model.** Build a `ReportModel` subclass from it (a *model builder*): metadata + sections. Take `GeneratedBy` from `ReportAuthorization.ResolveActor(User)`.
3. **Render it** inside `ReportTelemetry.MeasureAsync` (see Telemetry). Use `IReportRenderer` for PDF, or `ICsvExporter` to export a `TableSection` as CSV. Register these once per service with `builder.Services.AddCustodianReporting()`; Workflow already does this.
4. **Return it** with `ReportResults.File(ReportResults.Output(metadata, format, bytes))`.

A report endpoint looks like this:

```csharp
[ApiController]
[Route("api/reports")]
[Authorize(Roles = "Owner,Staff")]
[ReportErrors("SLA_PERFORMANCE")]          // error contract below
public class ReportsController : ControllerBase
{
    [HttpGet("sla-performance")]
    public async Task<IActionResult> SlaPerformance([FromQuery] string? format, ...)
    {
        var reportFormat = ReportFormats.Parse(format);                  // 400 on anything but pdf/csv
        var tenantId = ReportAuthorization.RequireTenantId(User);        // JWT only

        var output = await _telemetry.MeasureAsync("SLA_PERFORMANCE", reportFormat, async () =>
        {
            var data = await _service.ComputeAsync(tenantId, filter);        // plain numbers
            var model = _builder.Build(data, ReportAuthorization.ResolveActor(User));
            var bytes = reportFormat == ReportFormat.Csv ? _csv.ToCsv(model.Detail) : _renderer.RenderPdf(model);
            return new ReportRun(ReportResults.Output(model.Metadata, reportFormat, bytes), IsEmpty: data.TotalActions == 0);
        });
        return ReportResults.File(output);
    }
}
```

`tests/Custodian.Workflow.Tests/Integration/ReportPipelineTests.cs` runs exactly this shape, as a test-only endpoint, through the real Workflow pipeline.

## Endpoint convention

`GET api/reports/{report-slug}?format=pdf|csv&{filters}`

- **Format:** `format` defaults to `pdf`. Any other value → 400.
- **Download headers** (`ReportResults.File`):
    - `Content-Disposition: attachment; filename="custodian-{reportcode-lower}-{yyyyMMdd-HHmm}Z.{ext}"`, e.g. `custodian-sla_performance-20260928-1030Z.pdf`. The timestamp is the report's own "now".
    - `Content-Type` `application/pdf` or `text/csv; charset=utf-8`.
    - `Cache-Control: no-store`, because reports hold client data and must not be cached.
    - `X-Content-Type-Options: nosniff`.
- **Cross-origin:** CORS already exposes `Content-Disposition`, so the browser frontend can read the file name.
- **Roles:** `[Authorize(Roles = "Owner,Staff")]` unless the report is client-facing (below).

## Tenant and role rules

- **Tenant from the JWT only.** The tenant always comes from the JWT `tenant_id` claim (the `TenantMember` policy already enforces it on every endpoint). Never take it from a query string or header.
- **Staff reports:** Owner and Staff only; a Client gets 403.
- **Client-facing reports** (e.g. CSTD-14, Sprint 4) must also check that the requested engagement belongs to the calling client, the same `client_id` check the portal uses. Otherwise return 404, so the engagement's existence isn't revealed.

## Errors

Put `[ReportErrors("REPORT_CODE")]` on the report controller or action. Throw `ReportGenerationException` for expected failures:

- `ReportGenerationException.InvalidFilter("from", "…")`
- `.SubjectNotFound("…")`
- `.DataSourceUnavailable("…", inner)`
- `.Forbidden("…")`

Any other exception becomes a 500. The response is ASP.NET `ProblemDetails` (`application/problem+json`) with extensions:

- `reportCode`;
- `correlationId`;
- `field`, for filter errors.

The message is shown to the user for every kind except a 500, so keep ids and internals out of it. A 500 never includes the exception text or a stack trace; its detail asks the user to quote the correlation id, which is logged with the exception.

| Situation | Status | Title |
|---|---|---|
| Invalid filter (from > to, bad date, unknown stage, unsupported format) | 400 | Invalid report filter (+ `field`) |
| Role not allowed | 403 | standard from `[Authorize]`; "Forbidden" from `ReportAuthorization` |
| Engagement or other subject not in the tenant | 404 | Report subject not found |
| A service the report depends on is down | 503 | Report data source unavailable — try again |
| Rendering or unexpected failure | 500 | Report could not be generated (logged with the correlation id) |

Logs record the report code, error kind and correlation id, never the filter values, which can identify clients. A cancelled request (the user closed the page) is not treated as a failure.

## Frontend

`frontend/src/services/api.ts` exports `downloadReport(url, params)`. It:

- sends the request with the user's token;
- leaves out empty parameters;
- saves the file under the name from `Content-Disposition`;
- throws a `ReportDownloadError` whose `problem` holds the ProblemDetails fields (`title`, `detail`, `field`, `reportCode`, `correlationId`). A network failure reads as "Report data source unavailable — try again".

`<ReportDownloadButton url format params />` (`frontend/src/components/reports/`) wraps it:

- a "Generating…" spinner while the report is built;
- the error title and detail under the button;
- the reference id to quote for a 500.

```tsx
<ReportDownloadButton
    url={`${API_BASE.WORKFLOW}/api/reports/sla-performance`}
    format="pdf"
    params={{ from, to, stage, engagementId }}
/>
```

The shared `authorizedFetch` now also reads `application/problem+json` error bodies. Previously the raw JSON text became the error message for every ProblemDetails response.

## Telemetry

Wrap each generation in `ReportTelemetry.MeasureAsync(reportCode, format, generate)`. It records one entry per run:

| Where | What |
|---|---|
| Log line (`ILogger`, category `ReportTelemetry`) | `Report SLA_PERFORMANCE (pdf) finished in 412.3 ms: success, 58210 bytes.` |
| Meter `Custodian.Reporting` | `custodian.report.generation.duration` (ms) and `custodian.report.generation.size` (bytes), tagged `report_code`, `format`, `outcome` |
| Activity source `Custodian.Reporting` | `report.generate` span with the same tags |

- **Outcomes:**
    - `success`;
    - `empty` (the run reported no matching records);
    - `invalid_filter`, `forbidden`, `subject_not_found`, `data_source_unavailable` or `generation_failed`. For these the exception is rethrown unchanged for `[ReportErrors]`.
- **Privacy:** report contents, filter values and exception messages are never recorded, because they can identify clients.
- **Collection:** no exporter (App Insights, Prometheus) is wired in any service yet. That matches the existing next-action telemetry. Today the log line is what App Service's log stream shows, and the meter and activity are ready for an OpenTelemetry exporter when one is added.

## CI

`dotnet test backend/Custodian.sln` (the `ci.yml` backend job, on `ubuntu-latest`) runs the reporting tests:

- `SampleReportTests` render a test-only sample report (`SampleReportModelBuilder`) as PDF and CSV, so a renderer that throws fails the build;
- `ReportPipelineTests` run a test-only endpoint through the real Workflow pipeline.

QuestPDF brings its own linux-x64 native library and the bundled Lato font, so no extra CI setup is needed.

Still open for DevOps (CSTD-36-5): render a PDF inside the Workflow Docker image, and on App Service Linux once CSTD-37's endpoint is deployed.

## Status

| Part | Milestone | State |
|---|---|---|
| Report model + value rules | CSTD-36-M1 | Done |
| PDF renderer | CSTD-36-M2 | Done |
| CSV exporter | CSTD-36-M3 | Done |
| `ReportResults`, error contract, role helper, Workflow wiring | CSTD-36-M4 | Done |
| Telemetry, sample report, CI check | CSTD-36-M5 | Done |
| Frontend download helper + button | CSTD-36-M6 | Done |
