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

1. In the owning service, query live data for the caller's tenant and compute a plain result object.
2. Build a `ReportModel` subclass from it (a *model builder*): metadata + sections.
3. Render it with `IReportRenderer` (PDF), or export a `TableSection` with `ICsvExporter` (CSV).
4. Return it with `ReportResults.File(...)` from `GET api/reports/{report-slug}`.

## Endpoint convention

`GET api/reports/{report-slug}?format=pdf|csv&{filters}`

- **Format:** `format` defaults to `pdf`. Any other value → 400.
- **Download headers:**
    - file name `custodian-{reportcode-lower}-{yyyyMMdd-HHmm}Z.{ext}`;
    - `Content-Type` `application/pdf` or `text/csv`;
    - `Cache-Control: no-store`.
- **Roles:** `[Authorize(Roles = "Owner,Staff")]` unless the report is client-facing (below).

## Tenant and role rules

- **Tenant from the JWT only.** The tenant always comes from the JWT `tenant_id` claim (the `TenantMember` policy already enforces it on every endpoint). Never take it from a query string or header.
- **Staff reports:** Owner and Staff only; a Client gets 403.
- **Client-facing reports** (e.g. CSTD-14, Sprint 4) must also check that the requested engagement belongs to the calling client, the same `client_id` check the portal uses. Otherwise return 404, so the engagement's existence isn't revealed.

## Errors

Errors are ASP.NET `ProblemDetails` with a `reportCode` extension. Stack traces are never returned.

| Situation | Status | Title |
|---|---|---|
| Invalid filter (from > to, bad date, unknown stage, unsupported format) | 400 | Invalid report filter (+ field detail) |
| Role not allowed | 403 | standard |
| Engagement or other subject not in the tenant | 404 | Report subject not found |
| A service the report depends on is down | 503 | Report data source unavailable — try again |
| Rendering failure | 500 | Report could not be generated (logged with a correlation id) |

## Status

| Part | Milestone | State |
|---|---|---|
| Report model + value rules | CSTD-36-M1 | Done |
| PDF renderer | CSTD-36-M2 | Done |
| CSV exporter | CSTD-36-M3 | Done |
| `ReportResults`, error contract, role helper | CSTD-36-M4 | Planned |
| Telemetry, sample report | CSTD-36-M5 | Planned |
| Frontend download helper | CSTD-36-M6 | Planned |
