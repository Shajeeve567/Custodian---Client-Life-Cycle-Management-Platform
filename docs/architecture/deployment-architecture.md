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
       +------+------+------+------+
       |      |      |      |
    Identity Workflow Documents Audit
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

Deployment workflows run when relevant service changes are pushed to `main`, allowing services to be deployed independently.

The frontend is deployed through Vercel.

## Infrastructure

The local development environment uses Docker Compose and includes the supporting infrastructure required by the application, including:

* MySQL
* Kafka
* Kafka UI
* Backend services

Production backend services are hosted using Azure App Services, while the frontend is hosted on Vercel.
