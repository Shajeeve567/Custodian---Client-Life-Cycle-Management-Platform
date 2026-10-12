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
| `main` | Production | `deploy-<service>.yml` | the four production App Services |

Staging and production are separate App Services with their own application settings (database, Kafka, CORS, and so on), so the same code runs against different configuration. See [github-actions.md](../ci-cd/github-actions.md) for triggers and the current differences between the two pipelines (staging runs no tests and ignores `backend/src/shared/**` changes).

## Deployment Flow

```text
Push to dev ──> staging-<service>.yml ──> Restore → Build → Test* → Publish ──> staging App Service
Push to main ─> deploy-<service>.yml ───> Restore → Build → Test  → Publish ──> production App Service

* the staging Test step targets the service project and runs no tests
```

A workflow only runs when files under its service folder (and, for production, `backend/src/shared/**`) change, so services are deployed independently.

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
| `Cors__AllowedOrigins__0` (`__1`, … for more) | all four | The frontend origin(s), e.g. `https://<app>.vercel.app`. Outside Development no other origin is allowed; with none set, **the browser frontend is blocked** and startup logs a warning. |
| `Audit__Transport` + `Kafka__*` | Workflow, Documents (publish); Audit, Identity (consume) | `Kafka` (the default in `appsettings.json`) is required for notifications and document → task sync. Startup logs which transport is active. See `event-hubs-kafka.md`. |

### Settings per service (key names only)

Set the same keys on the staging and the production App Service; only the values differ. Values are never committed.

| Service | Keys |
|---|---|
| **Identity** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`, `Jwt__ExpiryMinutes`, `Resend__ApiKey`, `Resend__FromEmail`, `Resend__ApiUrl`, `Kafka__Enabled`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0` |
| **Workflow** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*` (as above), `Services__AuditUrl`, `Services__DocumentsUrl`, `AuditIngestion__ApiKey`, `Audit__Transport`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__ClientId`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Kafka__ConsumerEnabled`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Sla__DefaultOverdueHours`, `Sla__StageOverdueHours__<n>`, `Sla__Urgency__GatingWeight`, `Sla__Urgency__DefaultWeight`, `Cors__AllowedOrigins__0` |
| **Documents** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*`, `Storage__UploadPath`, `ComplianceRules__*`, `Services__AuditUrl`, `Services__WorkflowUrl`, `AuditIngestion__ApiKey`, `Audit__Transport`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__ClientId`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0` |
| **Audit** | `ConnectionStrings__AzureMySqlConnection`, `Jwt__*`, `Services__WorkflowUrl`, `AuditIngestion__ApiKey`, `Kafka__Enabled`, `Kafka__BootstrapServers`, `Kafka__Topic`, `Kafka__GroupId`, `Kafka__AutoOffsetReset`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Kafka__SaslUsername`, `Kafka__SaslPassword`, `Cors__AllowedOrigins__0` |

The `Sla__*` keys are optional (defaults: 72 hours, weights 2.0 / 1.0). `ComplianceRules__*` is optional; the defaults are in the Documents `appsettings.json`. Kafka values for Event Hubs are in [event-hubs-kafka.md](event-hubs-kafka.md).

> **Identity and Event Hubs.** Identity supports Azure Event Hubs via `SecurityProtocol` / `Sasl*` settings (CSTD-144). When configured with `SaslSsl` and SASL credentials, its notification consumer authenticates to Azure Event Hubs; when omitted, it works against a local Kafka broker.

`ASPNETCORE_ENVIRONMENT` must not be `Development` on a deployed App Service: that environment accepts the public development JWT key.

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

## Rollback

The current deployment process does not use an automated rollback mechanism.

If a deployment introduces an issue, the previous known-good version can be redeployed by reverting the problematic change in Git and allowing the corresponding GitHub Actions deployment workflow to run again.

The first places to investigate a failed or problematic deployment are:

* GitHub Actions workflow logs
* Azure App Service application and deployment logs
