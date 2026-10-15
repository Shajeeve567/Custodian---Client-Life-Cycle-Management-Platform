# Development Setup

## Prerequisites

Install the following before starting development:

* Git
* .NET 9 SDK
* Node.js 20
* Docker Desktop
* MySQL and Kafka are provided through Docker Compose

## Clone the Repository

```bash
git clone https://github.com/Shajeeve567/Custodian---Client-Life-Cycle-Management-Platform.git
cd Custodian---Client-Life-Cycle-Management-Platform
```

## Branching Strategy

Development follows the following branch flow:

```text
feature/* → dev → main
```

Create feature branches from `dev`:

```bash
git checkout dev
git pull origin dev
git checkout -b feature/<feature-name>
```

## Backend Setup

The backend contains four .NET services:

```text
backend/src/services/
├── Audit/
├── Documents/
├── Identity/
└── Workflow/
```

Restore and build the solution:

```bash
dotnet restore backend/Custodian.sln
dotnet build backend/Custodian.sln
```

Run the required infrastructure and services using Docker Compose.

Backend configuration such as database connection strings and JWT settings should be provided through environment-specific configuration or environment variables. Secrets must not be committed to the repository.

## Frontend Setup

Navigate to the frontend:

```bash
cd frontend
```

Install dependencies:

```bash
npm ci
```

Run the development server:

```bash
npm run dev
```

The frontend uses Vite environment variables for backend API URLs:

```text
VITE_IDENTITY_URL
VITE_WORKFLOW_URL
VITE_AUDIT_URL
VITE_DOCUMENTS_URL
```

The older names `VITE_<SERVICE>_API_URL` are still read as a fallback (`frontend/src/services/api.ts`); without either, the frontend uses the local ports (`5281`, `5225`, `5051`, `5171`). Copy `frontend/.env.example` to `frontend/.env` to start locally.

These values should point to the appropriate local or deployed backend services.

## Local Service URLs and API Documentation

When run with `dotnet run`, each service listens on the port from its `Properties/launchSettings.json`. With `docker compose up`, the services are published on ports 5001–5004 instead.

| Service | `dotnet run` | Docker Compose |
|---|---|---|
| Identity | http://localhost:5281 | http://localhost:5001 |
| Workflow | http://localhost:5225 | http://localhost:5002 |
| Documents | http://localhost:5171 | http://localhost:5003 |
| Audit | http://localhost:5051 | http://localhost:5004 |

In Development every service serves Swagger UI at `/swagger`, Scalar at `/scalar/v1` and the OpenAPI document at `/openapi/v1.json`. Use **Authorize** with a token from `POST /api/auth/login` (then `POST /api/auth/select-workspace/{tenantId}` for tenant APIs). Deployed environments serve them only with `ApiDocs__Enabled=true` (see `docs/deployment/azure-backend.md`).

Uploaded documents are written to the local `uploads` folder unless `Storage__Provider=Blob` and `AzureBlob__ConnectionString` are set.

## Running Tests

Backend:

```bash
dotnet test backend/Custodian.sln
```

Frontend type checking:

```bash
npm run type-check
```

Frontend build:

```bash
npm run build
```

## Local Development

Docker Compose provides the main supporting infrastructure for local development, including:

* MySQL
* Kafka
* Kafka UI
* Backend services

Developers can run and test individual services while making changes on their feature branches.

### Kafka is required for the full flow

Workflow and Documents publish their events to Kafka (`Audit:Transport=Kafka` in `appsettings.json`). Audit records them, Identity turns client-facing ones into notifications, and Workflow's consumer applies document verification results to the linked tasks. When running services with `dotnet run`, start a broker first:

```bash
docker compose up -d kafka kafka-ui
```

Without a broker, each event fails after the producer timeout (about 10 seconds, which also delays the request that produced it) and is logged as an error. Each service logs its audit transport and allowed CORS origins at startup; check those lines first when events or browser calls go missing. `Audit__Transport=Http` (with `AuditIngestion__ApiKey` set on Audit and the publisher) still records audit events without Kafka, but notifications and document sync then receive nothing.

In Development any browser origin is allowed. Deployed environments must list the frontend origin in `Cors__AllowedOrigins__0` (see `docs/deployment/azure-backend.md`).
