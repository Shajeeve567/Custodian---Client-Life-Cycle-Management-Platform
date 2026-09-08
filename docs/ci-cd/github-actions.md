# GitHub Actions CI/CD

## Overview

GitHub Actions is used to automate validation and deployment of the Custodian application.

The repository contains separate workflows for backend CI, frontend CI, and backend service deployment.

## Workflow Structure

```text
.github/workflows/
├── ci.yml
├── frontend-ci.yml
├── deploy-audit.yml
├── deploy-document.yml
├── deploy-identity.yml
└── deploy-workflow.yml
```

## Backend CI

`ci.yml` validates the backend when changes are pushed to `dev` or `main`, or when a pull request targets these branches.

The workflow performs:

```text
Checkout
   ↓
Setup .NET 9
   ↓
Restore
   ↓
Build
   ↓
Test
```

The workflow is limited to changes under `backend/` and changes to the workflow itself.

## Frontend CI

`frontend-ci.yml` validates the frontend when relevant changes are pushed to `dev` or `main`, or when a pull request targets these branches.

The workflow performs:

```text
Checkout
   ↓
Setup Node.js 20
   ↓
npm ci
   ↓
Type Check
   ↓
Build
```

The workflow is limited to changes under `frontend/` and changes to the frontend workflow.

## Backend Deployment

Each backend service has its own deployment workflow:

```text
deploy-audit.yml
deploy-document.yml
deploy-identity.yml
deploy-workflow.yml
```

These workflows run when relevant service changes are pushed to `main`.

The deployment process is:

```text
Restore
   ↓
Build
   ↓
Test
   ↓
Publish
   ↓
Azure App Service
```

This allows each backend service to be deployed independently.

## Secrets

Azure deployment workflows use GitHub Secrets for service-specific Azure configuration.

Examples include:

```text
AZURE_IDENTITY_APP_NAME
AZURE_IDENTITY_PUBLISH_PROFILE
AZURE_WORKFLOW_APP_NAME
AZURE_WORKFLOW_PUBLISH_PROFILE
AZURE_DOCUMENT_APP_NAME
AZURE_DOCUMENT_PUBLISH_PROFILE
AZURE_AUDIT_APP_NAME
AZURE_AUDIT_PUBLISH_PROFILE
```

Secrets and sensitive application configuration must not be committed to the repository.

## Branch and CI/CD Flow

The project follows:

```text
feature/*
    ↓
   dev
    ↓
  main
```

CI validation runs against changes targeting `dev` and `main`.

Production backend deployment workflows are triggered from `main`.

This provides automated validation before changes reach the deployment stage.
