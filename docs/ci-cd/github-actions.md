# GitHub Actions CI/CD

## Overview

GitHub Actions validates the backend and frontend and deploys each backend service to Azure App Service. There are two backend environments:

| Branch | Environment | Workflows | Azure App Services |
|---|---|---|---|
| `dev` | **Staging** | `staging-*.yml` | `staging-identity-service`, `staging-workflow-service`, `staging-document-service`, `staging-audit-service` |
| `main` | **Production** | `deploy-*.yml` | the four production App Services |

The workflows read the target App Service name from a secret (`AZURE_<SERVICE>_STAGING_APP_NAME` / `AZURE_<SERVICE>_APP_NAME`), so the names above are set in GitHub, not in the repository.

The frontend is deployed by Vercel's Git integration, not by GitHub Actions (see [vercel-frontend.md](../deployment/vercel-frontend.md)).

## Workflow Structure

```text
.github/workflows/
├── ci.yml                 # backend CI (dev, main)
├── frontend-ci.yml        # frontend CI (dev, main)
├── deploy-audit.yml       # production (main)
├── deploy-document.yml
├── deploy-identity.yml
├── deploy-workflow.yml
├── staging-audit.yml      # staging (dev)
├── staging-document.yml
├── staging-identity.yml
└── staging-workflow.yml
```

## Backend CI — `ci.yml`

- **Triggers:** push and pull request to `dev` or `main`, when `backend/**` or `ci.yml` changes.
- **Steps:** checkout → setup .NET 9 → `dotnet restore` → `dotnet build -c Release` → `dotnet test backend/Custodian.sln`.
- Runs every test project in the solution (Shared, Audit, Documents, Workflow, Identity).

## Frontend CI — `frontend-ci.yml`

- **Triggers:** push and pull request to `dev` or `main`, when `frontend/**` or the workflow changes.
- **Steps:** checkout → setup Node.js 20 → `npm ci` → type check → build.

## Production deployment — `deploy-*.yml`

- **Trigger:** push to `main` when any of these change:
  - the service folder (`backend/src/services/<Service>/**`)
  - the shared libraries (`backend/src/shared/**`)
  - the workflow file itself
- **Steps:** checkout → setup .NET 9 → restore → build (Release) → **test the service's test project** → publish → `azure/webapps-deploy@v3`.

| Workflow | Test project |
|---|---|
| `deploy-identity.yml` | `backend/tests/Identity.Tests` |
| `deploy-workflow.yml` | `backend/tests/Custodian.Workflow.Tests` |
| `deploy-document.yml` | `backend/tests/Custodian.Documents.Tests` |
| `deploy-audit.yml` | `backend/tests/Custodian.Audit.Tests` |

A failing test stops the deployment.

## Staging deployment — `staging-*.yml`

- **Trigger:** push to `dev` when either of these change:
  - the service folder (`backend/src/services/<Service>/**`)
  - the **production** workflow file `.github/workflows/deploy-<service>.yml`
- **Steps:** checkout → setup .NET 9 → restore → build (Release) → test → publish → `azure/webapps-deploy@v3` to the staging App Service.

Current behaviour to be aware of:

1. **Staging runs no tests.** Its Test step is `dotnet test backend/src/services/<Service> --no-build`, which points at the service project (it has no tests), not the test project. Tests on `dev` only run through `ci.yml`, which does not block the deployment.
2. **Shared-library changes do not redeploy staging.** `backend/src/shared/**` is not in the staging path filter, unlike production.
3. **Editing a `staging-*.yml` file does not trigger it.** The staging path filter watches `deploy-<service>.yml` instead.

`ci.yml` is a separate workflow in both environments; deployments do not wait for it.

## Secrets

GitHub Secrets (Settings → Secrets and variables → Actions). Names only; values are never committed.

| Service | Production | Staging |
|---|---|---|
| Identity | `AZURE_IDENTITY_APP_NAME`, `AZURE_IDENTITY_PUBLISH_PROFILE` | `AZURE_IDENTITY_STAGING_APP_NAME`, `AZURE_IDENTITY_STAGING_PUBLISH_PROFILE` |
| Workflow | `AZURE_WORKFLOW_APP_NAME`, `AZURE_WORKFLOW_PUBLISH_PROFILE` | `AZURE_WORKFLOW_STAGING_APP_NAME`, `AZURE_WORKFLOW_STAGING_PUBLISH_PROFILE` |
| Documents | `AZURE_DOCUMENT_APP_NAME`, `AZURE_DOCUMENT_PUBLISH_PROFILE` | `AZURE_DOCUMENT_STAGING_APP_NAME`, `AZURE_DOCUMENT_STAGING_PUBLISH_PROFILE` |
| Audit | `AZURE_AUDIT_APP_NAME`, `AZURE_AUDIT_PUBLISH_PROFILE` | `AZURE_AUDIT_STAGING_APP_NAME`, `AZURE_AUDIT_STAGING_PUBLISH_PROFILE` |

The CI workflows use no secrets. Application configuration (database, JWT, Kafka, etc.) is not passed by the workflows; it lives in each App Service's settings (see [azure-backend.md](../deployment/azure-backend.md)).

## Branch and CI/CD Flow

```text
feature/*  ──PR──>  dev  ──PR──>  main
                     │              │
          ci.yml + frontend-ci.yml  ci.yml + frontend-ci.yml
                     │              │
            staging-*.yml      deploy-*.yml
                     │              │
          staging App Services  production App Services
```
