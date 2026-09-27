# Azure Backend Deployment

## Overview

The Custodian backend consists of four independently deployable .NET 9 services hosted on Azure App Services:

* Identity
* Workflow
* Documents
* Audit

Each service has its own GitHub Actions deployment workflow.

## Deployment Flow

```text
Push to main
     |
     v
GitHub Actions
     |
     +--> Restore
     |
     +--> Build
     |
     +--> Test
     |
     +--> Publish
     |
     v
Azure App Service
```

Deployment workflows are triggered when changes are pushed to `main` that affect the corresponding service.

## Deployment Workflows

The workflows are located in:

```text
.github/workflows/
├── deploy-identity.yml
├── deploy-workflow.yml
├── deploy-document.yml
└── deploy-audit.yml
```

Each workflow builds and publishes only its corresponding service.

For example:

```text
backend/src/services/Identity
        |
        v
Restore → Build → Test → Publish
        |
        v
Azure Identity App Service
```

This allows the services to be deployed independently.

## Azure Configuration

Each deployment workflow uses GitHub Secrets for Azure configuration.

The required secrets follow this pattern:

```text
AZURE_<SERVICE>_APP_NAME
AZURE_<SERVICE>_PUBLISH_PROFILE
```

For example:

```text
AZURE_IDENTITY_APP_NAME
AZURE_IDENTITY_PUBLISH_PROFILE
```

Sensitive configuration values such as database connection strings and JWT signing keys should be configured through the Azure App Service environment rather than committed to the repository.

### Required application settings

Set these in each App Service under **Settings → Environment variables**. The repository is public, so anything committed is known to everyone.

| Setting | Services | Notes |
|---|---|---|
| `Jwt__SigningKey` | Identity, Workflow, Documents, Audit | The same random secret on all four (e.g. `openssl rand -base64 64`). **Startup fails** if it is missing, shorter than 32 bytes, the `.env.example` placeholder, or (outside Development) the development key committed in `appsettings.json`. Changing it signs everyone out. |
| `ConnectionStrings__AzureMySqlConnection` | all four | Per-service database. Never commit it; if one leaks, rotate the MySQL password. |
| `AuditIngestion__ApiKey` | Audit, Workflow, Documents | The same random value, at least 32 characters (e.g. `openssl rand -hex 32`). Without it, HTTP audit writes are refused (fail closed). |
| `Services__AuditUrl`, `Services__DocumentsUrl` | Workflow | Staging URLs of the Audit and Documents apps. |
| `Services__AuditUrl`, `Services__WorkflowUrl` | Documents | `WorkflowUrl` is required for the client ownership check; without it Client document requests get 503. |

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
