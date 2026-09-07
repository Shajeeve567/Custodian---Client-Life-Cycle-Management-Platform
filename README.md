# Custodian

Custodian is a Client Life Cycle Management Platform built using a microservices-based backend and a React frontend.

## Project Overview

The system consists of four independently deployable backend services:

* **Identity** — Authentication and identity management
* **Workflow** — Workflow and client lifecycle operations
* **Documents** — Document-related operations
* **Audit** — Auditing and activity tracking

The frontend is built with React, TypeScript, and Vite.

### Technology Stack

| Component            | Technology              |
| -------------------- | ----------------------- |
| Frontend             | React, TypeScript, Vite |
| Backend              | .NET 9                  |
| Database             | MySQL                   |
| Messaging            | Apache Kafka            |
| Local Infrastructure | Docker Compose          |
| Backend Hosting      | Azure App Services      |
| Frontend Hosting     | Vercel                  |
| CI/CD                | GitHub Actions          |

## Repository Structure

```text
Custodian/
├── .github/
│   └── workflows/          # CI/CD workflows
│
├── backend/
│   ├── src/
│   │   ├── services/       # Backend microservices
│   │   └── shared/         # Shared backend components
│   └── tests/              # Backend tests
│
├── frontend/
│   └── src/                # React frontend source
│
├── tests/
│   ├── jmeter/             # Performance testing
│   └── selenium/           # End-to-end testing
│
└── docs/                   # Project documentation
```

## Getting Started

### Prerequisites

Install:

* Git
* .NET 9 SDK
* Node.js 20
* Docker Desktop

### Clone the Repository

```bash
git clone https://github.com/Shajeeve567/Custodian---Client-Life-Cycle-Management-Platform.git
cd Client-Life-Cycle-Management-Platform
```

### Backend

Restore, build, and test the backend:

```bash
dotnet restore backend/Custodian.sln
dotnet build backend/Custodian.sln
dotnet test backend/Custodian.sln
```

### Frontend

```bash
cd frontend
npm ci
npm run dev
```

For frontend validation:

```bash
npm run type-check
npm run build
```

### Local Infrastructure

Docker Compose is used to provide the local infrastructure required by the application, including MySQL and Kafka.

```bash
docker compose up -d
```

Check running containers with:

```bash
docker compose ps
```

## Branching Strategy

Development follows:

```text
feature/*
    ↓
   dev
    ↓
  main
```

Feature branches should be created from `dev`:

```bash
git checkout dev
git pull origin dev
git checkout -b feature/<feature-name>
```

## CI/CD

GitHub Actions is used for automated validation and backend deployment.

### Continuous Integration

Backend and frontend changes are automatically validated through separate workflows.

```text
Backend:
Restore → Build → Test

Frontend:
Install → Type Check → Build
```

### Deployment

Each backend microservice has an independent deployment workflow and is deployed to its corresponding Azure App Service.

The frontend is deployed through Vercel.

```text
GitHub
   │
   ├── Backend ──→ GitHub Actions ──→ Azure App Services
   │
   └── Frontend ─────────────────────→ Vercel
```

## Documentation

Detailed project documentation is available in the [`docs/`](docs/) directory.

| Documentation                                                           | Description                                 |
| ----------------------------------------------------------------------- | ------------------------------------------- |
| [Deployment Architecture](docs/architecture/deployment-architecture.md) | System architecture and deployment overview |
| [Development Setup](docs/development/development-setup.md)              | Local development and setup instructions    |
| [Azure Backend Deployment](docs/deployment/azure-backend.md)            | Backend deployment to Azure App Services    |
| [Vercel Frontend Deployment](docs/deployment/vercel-frontend.md)        | Frontend deployment and configuration       |
| [GitHub Actions CI/CD](docs/ci-cd/github-actions.md)                    | CI/CD workflows and deployment automation   |
| [Troubleshooting](docs/troubleshooting.md)                              | Common development and deployment issues    |

## Security

Sensitive configuration such as:

* Database connection strings
* JWT signing keys
* Azure deployment credentials

must not be committed to the repository.

Environment-specific configuration should be provided through environment variables, GitHub Secrets, or Azure App Service application settings as appropriate.
