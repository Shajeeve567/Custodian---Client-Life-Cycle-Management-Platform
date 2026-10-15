# Azure Backend Deployment

## Overview

The Custodian backend consists of four independently deployable .NET 9 services hosted on Azure App Services:

* Identity
* Workflow
* Documents
* Audit

Each service is deployed to two environments by its own GitHub Actions workflows:

| Branch | Environment | Workflow | App Services |
|---|---|---|---|
| `dev` | Staging | `staging-<service>.yml` | `staging-identity-service`, `staging-workflow-service`, `staging-document-service`, `staging-audit-service` |
| `main` | Production | `deploy-<service>.yml` | `custodian-identity-prod-gr`, `custodian-workflow-prod-gr`, `custodian-documents-prod-gr`, `custodian-audit-prod-gr` |

Staging and production are separate App Services with their own application settings (database, Kafka, CORS, and so on), so the same code runs against different configuration. Both pipelines run the service's test project before publishing; see [github-actions.md](../ci-cd/github-actions.md) for triggers.

## Production environment

Production was rebuilt in Sprint 4 in its own subscription, `custodian-prod`. Staging stays in the original team subscription.

**Why:** in Sprint 3 all four services shared one App Service plan, so a load spike on one service took all four down. The new layout isolates services into two plans and moves document files off the App Service disk.

```text
                         Vercel (custodian-blue.vercel.app)
                                     │ HTTPS + JWT
              ┌──────────────────────┴───────────────────────┐
              ▼                                              ▼
  asp-custodian-idaudit (B1 Linux)             asp-custodian-wfdocs (B1 Linux)
   ├─ custodian-identity-prod-gr                ├─ custodian-workflow-prod-gr
   └─ custodian-audit-prod-gr                   └─ custodian-documents-prod-gr ──► Blob: stcustodianprodgr
              │                                              │                       /custodian-documents
              └──────────────► Event Hubs ◄──────────────────┘
                    ehns-custodian-prod-gr / custodian.events (Kafka :9093)
              │                                              │
              └────────────► Azure MySQL (custodian-mysql) ◄─┘
                     identity_db, workflow_db, document_db, audit_db

  App Insights appi-custodian-prod (all four apps)      Prometheus + Grafana VM (scrapes /metrics)
```

| Resource | Name | Notes |
|---|---|---|
| Resource groups | `rg-custodian-prod` (Southeast Asia), `rg-custodian-monitoring` | Monitoring is separate so it can be removed or changed without touching production |
| App Service plans | `asp-custodian-wfdocs`, `asp-custodian-idaudit` | B1 Linux. Workflow and Documents call each other over HTTP and carry most load; Identity and Audit are lighter |
| Web apps | `custodian-<service>-prod-gr` | .NET 9, startup command `dotnet <Service>.dll` |
| Event Hubs | namespace `ehns-custodian-prod-gr`, hub `custodian.events` | Standard tier (Basic has no Kafka endpoint); see [event-hubs-kafka.md](event-hubs-kafka.md) |
| Storage | account `stcustodianprodgr`, container `custodian-documents` | LRS, Hot tier, private container, 7-day soft delete |
| Application Insights | `appi-custodian-prod` on Log Analytics `log-custodian-prod` | Codeless agent enabled per app; no SDK in code |
| Database | `custodian-mysql` | Azure Database for MySQL in the team subscription, one database per service |
| Monitoring | VM `vm-custodian-monitoring` | Prometheus + Grafana; see [monitoring/README.md](../../monitoring/README.md) |

The backend URL of each app is shown on its **Overview** page in the portal (`https://<app-name>-<suffix>.southeastasia-01.azurewebsites.net`). They are not repeated in these docs (only the Prometheus targets in `monitoring/prometheus/prometheus.yml` need them); the frontend is at https://custodian-blue.vercel.app.

### Web app configuration

Set on every production web app (**Settings → Configuration → General settings**):

| Setting | Value | Why |
|---|---|---|
| Always On | On | Keeps the Kafka consumers (Audit, Identity, Workflow) running when no HTTP traffic arrives |
| HTTPS Only | On | JWTs and documents are never sent over plain HTTP |
| Minimum TLS version | 1.2 | |
| FTP state | Disabled | Deployments go through the publish profile, not FTP |
| Session affinity (ARR) | Off | The services are stateless (JWT), so any instance can serve any request |
| SCM basic auth | On | Required by `azure/webapps-deploy` with a publish profile |

### Scaling and alerts

B1 has no autoscale. Each plan can be scaled out manually up to 3 instances (**App Service plan → Scale out**). A CPU alert (average > 70% for 5 minutes) on each plan emails the team through action group `ag-custodian-email`, as the signal to scale out or up. A subscription budget (`custodian-prod-budget`) alerts at 50%, 80% and 100% of actual spend and at 100% of forecast.

## Deployment Flow

```text
Push to dev ──> staging-<service>.yml ──> Restore → Build → Test → Publish ──> staging App Service
Push to main ─> deploy-<service>.yml ───> Restore → Build → Test → Publish ──> production App Service
```

A workflow only runs when files under its service folder, `backend/src/shared/**` or its own workflow file change, so services are deployed independently. A production workflow can also be started manually from the Actions tab.

## Deployment Workflows

```text
.github/workflows/
├── deploy-identity.yml    staging-identity.yml
├── deploy-workflow.yml    staging-workflow.yml
├── deploy-document.yml    staging-document.yml
└── deploy-audit.yml       staging-audit.yml
```

## Azure Configuration

The workflows get the target App Service from GitHub Secrets:

```text
Production: AZURE_<SERVICE>_APP_NAME           AZURE_<SERVICE>_PUBLISH_PROFILE
Staging:    AZURE_<SERVICE>_STAGING_APP_NAME   AZURE_<SERVICE>_STAGING_PUBLISH_PROFILE

<SERVICE> = IDENTITY | WORKFLOW | DOCUMENT | AUDIT
```

To point production at different App Services, replace the eight production secrets with the new app names and publish profiles (**web app → Overview → Download publish profile**). Nothing in the repository changes.

Sensitive configuration values such as database connection strings and JWT signing keys should be configured through the Azure App Service environment rather than committed to the repository.

### Required application settings

Set these in each App Service under **Settings → Environment variables**. The repository is public, so anything committed is known to everyone.

| Setting | Services | Notes |
|---|---|---|
| `Jwt__SigningKey` | Identity, Workflow, Documents, Audit | The same random secret on all four (e.g. `openssl rand -base64 64`). **Startup fails** if it is missing, shorter than 32 bytes, the `.env.example` placeholder, or (outside Development) the development key committed in `appsettings.json`. Changing it signs everyone out. |
| `ConnectionStrings__AzureMySqlConnection` | all four | Per-service database. Never commit it; if one leaks, rotate the MySQL password. |
| `AuditIngestion__ApiKey` | Audit, Workflow, Documents | The same random value, at least 32 characters (e.g. `openssl rand -hex 32`). Without it, HTTP audit writes are refused (fail closed). |
| `Services__AuditUrl`, `Services__DocumentsUrl` | Workflow | URLs of the Audit and Documents apps **in the same environment** (staging → staging, production → production). |
| `Services__AuditUrl`, `Services__WorkflowUrl` | Documents | `WorkflowUrl` is required for the engagement access check (client ownership, staff assignment); without it Client and Staff document requests get 503. |
| `Services__WorkflowUrl` | Audit | Audit asks Workflow which engagements a Staff member is responsible for; without it Staff audit requests get 503 (Owners are unaffected). |
| `Cors__AllowedOrigins__0` (`__1`, … for more) | all four | The frontend origin(s), e.g. `https://custodian-blue.vercel.app`. Outside Development no other origin is allowed; with none set, **the browser frontend is blocked** and startup logs a warning. |
| `Audit__Transport` + `Kafka__*` | Workflow, Documents (publish); Audit, Identity, Workflow (consume) | `Kafka` (the default in `appsettings.json`) is required for notifications and document → task sync. Startup logs which transport is active. See `event-hubs-kafka.md`. |
| `Storage__Provider`, `AzureBlob__ConnectionString`, `AzureBlob__ContainerName` | Documents | `Blob` stores uploaded files in Azure Blob Storage. Any other value (default `Local`) writes them to the App Service disk, which is wiped on redeploy and not shared between instances. With `Blob` and no connection string, **startup fails**. The container must already exist (default `custodian-documents`). |
| `Metrics__ScrapeKey` | all four | Enables `GET /metrics` for Prometheus with HTTP Basic auth (user `prometheus`, this key as password). Without it `/metrics` returns 404 outside Development. Same value on all four and in the monitoring VM's `prometheus/scrape_key`. |
| `ApiDocs__Enabled` | all four (optional) | `true` serves Swagger UI, Scalar and the OpenAPI document outside Development; see [API documentation](#api-documentation). |
| `Resend__ApiKey`, `Resend__FromEmail` | Identity (optional) | Email notifications. Without a key, emails are only logged; in-app notifications still work. |

### Settings per service (key names only)

Set the same keys on the staging and the production App Service; only the values differ. Values are never committed.

| Service | Keys |
|---|---|
| **Identity** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`, `Jwt__ExpiryMinutes`, `Resend__ApiKey`, `Resend__FromEmail`, `Resend__ApiUrl`, `Kafka__Enabled`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0`, `Metrics__ScrapeKey`, `ApiDocs__Enabled` |
| **Workflow** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*` (as above), `Services__AuditUrl`, `Services__DocumentsUrl`, `AuditIngestion__ApiKey`, `Audit__Transport`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__ClientId`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Kafka__ConsumerEnabled`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Sla__DefaultOverdueHours`, `Sla__StageOverdueHours__<n>`, `Sla__Urgency__GatingWeight`, `Sla__Urgency__DefaultWeight`, `Cors__AllowedOrigins__0`, `Metrics__ScrapeKey`, `ApiDocs__Enabled` |
| **Documents** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*`, `Storage__Provider`, `AzureBlob__ConnectionString`, `AzureBlob__ContainerName`, `Storage__UploadPath` (Local only), `ComplianceRules__*`, `Services__AuditUrl`, `Services__WorkflowUrl`, `AuditIngestion__ApiKey`, `Audit__Transport`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__ClientId`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0`, `Metrics__ScrapeKey`, `ApiDocs__Enabled` |
| **Audit** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*`, `Services__WorkflowUrl`, `AuditIngestion__ApiKey`, `Kafka__Enabled`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0`, `Metrics__ScrapeKey`, `ApiDocs__Enabled` |

The `Sla__*` keys are optional (defaults: 72 hours, weights 2.0 / 1.0). `ComplianceRules__*` is optional; the defaults are in the Documents `appsettings.json`. Kafka values for Event Hubs are in [event-hubs-kafka.md](event-hubs-kafka.md).

`ASPNETCORE_ENVIRONMENT` must not be `Development` on a deployed App Service: that environment accepts the public development JWT key.

## API documentation

Each service serves an OpenAPI document and two interactive UIs:

| Path | Content |
|---|---|
| `/swagger` | Swagger UI |
| `/scalar/v1` | Scalar API reference |
| `/openapi/v1.json` | OpenAPI document (read by both UIs) |

They are always on in Development. On a deployed App Service they are served only when `ApiDocs__Enabled=true`, so an environment publishes its API description only on purpose. The endpoints themselves still require a JWT: in either UI, choose **Authorize** and paste a workspace token from Identity (`POST /api/auth/login`, then `POST /api/auth/select-workspace/{tenantId}`). Set the flag back to `false` (or remove it) to hide the docs again; the app restarts when the setting changes.

## Application Deployment

The services are deployed as published .NET applications using `azure/webapps-deploy@v3`.

The deployment process is:

```text
Source Code
    ↓
dotnet restore
    ↓
dotnet build
    ↓
dotnet test
    ↓
dotnet publish
    ↓
Azure Web App
```

Each service is published to its own output directory before deployment.

## Deployment Requirements

Before deploying a backend service, ensure that:

1. The service builds successfully.
2. Automated tests pass.
3. Required Azure App Service secrets are configured in GitHub.
4. Required application settings and connection strings are configured in Azure.
5. The deployment workflow targets the correct App Service.

After a deployment, check:

1. **Log stream** (web app → Monitoring → Log stream) shows the startup lines for the audit transport and CORS origins, and no exceptions.
2. A request without a token to a protected endpoint returns `401` (the app is up and JWT is enforced).
3. Prometheus **Status → Target health** shows the service as `UP`.

## Rollback

The current deployment process does not use an automated rollback mechanism.

If a deployment introduces an issue, the previous known-good version can be redeployed by reverting the problematic change in Git and allowing the corresponding GitHub Actions deployment workflow to run again (or running it manually with **Run workflow**).

The first places to investigate a failed or problematic deployment are:

* GitHub Actions workflow logs
* Azure App Service **Log stream** and **Diagnose and solve problems**
* Application Insights (failures, live metrics, application map)
* The Grafana **Custodian services** dashboard

## Known limitations

These are accepted trade-offs of the current production setup, with the next step for each:

| Limitation | Improvement |
|---|---|
| One MySQL server (Burstable B1ms) in another subscription is a single point of failure | Zone-redundant MySQL or a read replica in the production subscription |
| One region (Southeast Asia) | A second region behind Azure Front Door |
| B1 plans have no autoscale | Standard (S1) or Premium plans with CPU-based autoscale rules |
| Secrets live in App Settings | Azure Key Vault references with managed identity |
| Deployments use publish profiles | GitHub OIDC federated credentials (no long-lived secrets) |
| MySQL firewall allows all Azure services | Private endpoint / VNet integration |
| No dedicated `/health` endpoint | ASP.NET Core health checks wired to App Service health check |
| Resources were created in the portal | Bicep or Terraform templates |
