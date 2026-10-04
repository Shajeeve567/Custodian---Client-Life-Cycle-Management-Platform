# Validation & Verification Report API (CSTD-31 / CSTD-182)

The Validation & Verification Report is generated on demand from live Documents service data. It provides compliance validation and human verification metrics, totals, document type breakdowns, and rejection reasons.

Local base URL: `http://localhost:5171`.

## Endpoint

`GET /api/reports/validation-verification`

### Authorization & Tenant Scope
- **Roles**: `[Authorize(Roles = "Owner,Staff")]`. Client role callers receive `403 Forbidden`.
- **Tenant**: Strictly extracted from the authenticated JWT `tenant_id` claim. Caller-controlled tenant parameters are ignored for tenant boundary enforcement.
- **Owner Scope**: Aggregates active documents across the entire authenticated tenant workspace (`allowedEngagementIds = null`).
- **Staff Scope**: Restricted strictly to engagements assigned to the calling staff member in the Workflow service. Documents queries Workflow `GET /api/engagements` using the caller's JWT.
- **Fail-Closed Security**: If the Workflow dependency is down, unreachable, or returns a non-success status, the Staff request fails closed with `503 Service Unavailable` (`ReportErrorKind.DataSourceUnavailable`).

### Query Parameters

| Parameter | Type | Required | Description |
|---|---|---|---|
| `from` | `string` (`yyyy-MM-dd`) | No | Inclusive lower upload date boundary (`UploadedAt >= From 00:00:00 UTC`). |
| `to` | `string` (`yyyy-MM-dd`) | No | Inclusive upper upload calendar day (`UploadedAt < day-after-To 00:00:00 UTC`). |
| `engagementId` | `Guid` | No | Restricts aggregation to a specific engagement within the caller's scope. |
| `format` | `string` | No | `pdf` (default) or `csv`. |

### Date Semantics
- Date boundaries follow calendar-day UTC:
  - `from`: `UploadedAt >= From 00:00:00 UTC` (`FromUtc`).
  - `to`: `UploadedAt < day-after-To 00:00:00 UTC` (`ToUtcExclusive`). The `to` date is user-inclusive because queries apply an exclusive next-day midnight boundary.
- If `from > to`, the API returns `400 Bad Request` naming field `from`.

### Data Rules
- **Live Data Source**: Aggregates directly from the live `Documents` database table.
- **Soft-Deleted Documents**: Excluded (`IsDeleted == true` documents are never counted).
- **Separation of Concerns**: Automatic compliance (`Compliant`, `Rejected`, `Pending`) and Human verification (`Verified`, `Rejected`, `Unverified`, `Pending`) are tracked and reported separately. Human verification counts only include automatically `Compliant` documents.
- **Empty Datasets**: An empty dataset is not an error; the API returns HTTP 200 with zero summary counts and `EmptySection`s ("No ... recorded") for breakdowns. A Staff member with zero assigned engagements receives a valid empty report.

### Outputs

- **PDF (`format=pdf`)**:
  - `Content-Type`: `application/pdf`
  - `Content-Disposition`: `attachment; filename="custodian-validation_verification-{yyyyMMdd-HHmm}Z.pdf"`
  - Sections: Summary, Automatic compliance, Human verification, Document type breakdown, Automatic rejection reasons, Human verification rejection reasons.
- **CSV (`format=csv`)**:
  - `Content-Type`: `text/csv; charset=utf-8`
  - `Content-Disposition`: `attachment; filename="custodian-validation_verification-{yyyyMMdd-HHmm}Z.csv"`
  - Structure: Deterministic aggregate-detail table with columns `Category,Metric,Count`.

### Error Contract (RFC 7807 ProblemDetails)

| Status | Title | Cause |
|---|---|---|
| `400` | `Invalid report filter` | Malformed date, `from > to`, malformed `engagementId`, or invalid `format`. Extension field `field` specifies the parameter. |
| `403` | `Forbidden` | Caller lacks `Owner` or `Staff` role, or JWT lacks `tenant_id` claim. |
| `503` | `Report data source unavailable — try again` | Workflow service dependency unreachable or failing during Staff scope resolution. |
| `500` | `Report could not be generated` | Unexpected server error. Detail includes correlation ID for support. |
