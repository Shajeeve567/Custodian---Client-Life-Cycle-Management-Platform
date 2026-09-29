# Deployment Architecture

## Overview

Custodian uses a microservices-based backend with a separate React frontend. The backend services are independently built, tested, and deployed.

## System Architecture

```text
                    GitHub Repository
                           |
                    feature/* → dev → main
                           |
              +------------+------------+
              |                         |
        GitHub Actions                Vercel
              |                         |
              |                  React + TypeScript
              |                         |
              |                  Backend REST APIs
              |                         |
              ▼                         ▼
       Azure App Services  <------------+
              |
     +--------+---------+
     |                  |
  Staging (dev)    Production (main)
     |                  |
  Identity, Workflow, Documents, Audit (one App Service each, per environment)
     |                  |
     +---- Azure Event Hubs (Kafka, SASL_SSL :9093) ----+
     +---- Azure Database for MySQL (one DB per service)
```

The frontend is hosted on Vercel and communicates with the backend services deployed on Azure App Services.

## Backend Services

The backend contains four independently deployable .NET 9 services:

```text
backend/src/services/
├── Audit/
├── Documents/
├── Identity/
└── Workflow/
```

Each service maintains its own controllers, application logic, data access, and service-specific components. Some services also contain repositories, domain models, events, or database migrations depending on their implementation.

## CI/CD and Deployment

GitHub Actions is used for automated validation and deployment.

Backend CI performs:

```text
Restore → Build → Test
```

Each backend service has its own deployment workflow:

```text
Build → Test → Publish → Azure App Service
```

Each service is deployed to two environments:

| Branch | Environment | Workflows | App Services |
|---|---|---|---|
| `dev` | Staging | `staging-*.yml` | `staging-identity-service`, `staging-workflow-service`, `staging-document-service`, `staging-audit-service` |
| `main` | Production | `deploy-*.yml` | production App Services |

A workflow runs only when its service's files change, so services are deployed independently. Each environment has its own App Settings (database, JWT, Kafka topic and consumer groups, CORS). See [GitHub Actions CI/CD](../ci-cd/github-actions.md) and [Azure Backend Deployment](../deployment/azure-backend.md).

The frontend is deployed through Vercel.

## Infrastructure

The local development environment uses Docker Compose and includes the supporting infrastructure required by the application, including:

* MySQL
* Kafka
* Kafka UI
* Backend services

Staging and production backend services are hosted on Azure App Services; they exchange events through Azure Event Hubs' Kafka endpoint. The frontend is hosted on Vercel.

## Service internals

The Workflow engine added in Sprint 3 (next action, conditions, SLA and stall detection, stall queue, interventions, reports) and the Audit SHA-256 hash chain are described in [workflow-engine.md](workflow-engine.md).
