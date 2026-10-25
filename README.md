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
| Messaging            | Apache Kafka (local), Azure Event Hubs Kafka endpoint (cloud) |
| File Storage         | Azure Blob Storage      |
| Local Infrastructure | Docker Compose          |
| Backend Hosting      | Azure App Services      |
| Frontend Hosting     | Vercel                  |
| CI/CD                | GitHub Actions          |
| API Documentation    | OpenAPI, Swagger UI, Scalar |
| Monitoring           | Application Insights, Prometheus, Grafana |

## Live Environment

| Component | URL |
| --------- | --- |
| Frontend | https://custodian-blue.vercel.app |

The backend API addresses are not listed here; see the production section of [Azure Backend Deployment](docs/deployment/azure-backend.md#production-environment) for the App Service names. Each API serves Swagger UI at `/swagger`, Scalar at `/scalar/v1` and the OpenAPI document at `/openapi/v1.json` when `ApiDocs__Enabled=true` is set on its App Service (always in local Development). Protected endpoints need a JWT from `POST /api/auth/login`; use **Authorize** in either UI.

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
│   ├── jmeter/             # Load & performance testing (JMeter)
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
cd Custodian---Client-Life-Cycle-Management-Platform
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

Each backend microservice has independent deployment workflows and two environments:

| Branch | Environment | Workflows |
| ------ | ----------- | --------- |
| `dev`  | Staging (`staging-*-service` App Services) | `staging-*.yml` |
| `main` | Production | `deploy-*.yml` |

Production runs in its own Azure subscription with two App Service plans (Workflow + Documents, Identity + Audit), Azure Blob Storage for document files, and Application Insights plus Prometheus/Grafana for monitoring. See [Azure Backend Deployment](docs/deployment/azure-backend.md#production-environment).

Deployment secrets are GitHub Secrets (`AZURE_<SERVICE>_APP_NAME` / `_PUBLISH_PROFILE`, and `AZURE_<SERVICE>_STAGING_*` for staging). Application configuration lives in each App Service's settings. See [GitHub Actions CI/CD](docs/ci-cd/github-actions.md).

The test workflows (unit, mutation, E2E and load) are described in [Testing Pipeline (SE3112)](#testing-pipeline-se3112).

The frontend is deployed through Vercel.

```text
GitHub
   │
   ├── Backend ──→ GitHub Actions ──→ Azure App Services
   │
   └── Frontend ─────────────────────→ Vercel
```

### Event Messaging (Azure Event Hubs)

Asynchronous event streaming between microservices uses Azure Event Hubs via its Kafka-compatible endpoint, powered by `Confluent.Kafka`:
* **Workflow** and **Documents** act as Kafka producers.
* **Audit** consumes every event and adds it to a per-engagement SHA-256 hash chain; **Workflow** consumes document verification events.
* **Shared Topic / Event Hub:** `custodian.events`.
* **Broker Endpoint:** Azure Event Hubs (production namespace `ehns-custodian-prod-gr`) on port `9093` with `SASL_SSL` (`PLAIN`).
* **Consumer groups:** `custodian-audit`, `custodian-workflow`, `custodian-identity-notifications-group`; each receives every event.
* Application code continues using standard `Confluent.Kafka` without SDK changes.
* Secrets are supplied exclusively via runtime environment variables and are never committed.
* The codebase remains compatible with local Kafka brokers for development when SASL configuration is omitted.

```text
Workflow
→ Azure Event Hubs
→ custodian.events
→ Audit
→ audit_db
```

For detailed setup, configuration, and troubleshooting guides, see:
* [Azure Event Hubs Kafka Integration](docs/deployment/event-hubs-kafka.md)
* [Troubleshooting Guide](docs/troubleshooting.md)

## Testing Pipeline (SE3112)

Four test layers, each owned by one team member, all run in GitHub Actions. Pull requests run the unit and mutation
tests; pushes run the unit tests and the E2E smoke test; load tests and full runs are started from the **Run workflow**
button.

### Team and roles

| Role | Owner | Tools | Where |
| ---- | ----- | ----- | ----- |
| Unit Testing & Mocking | Rithish | xUnit, Moq, EF Core InMemory | `backend/tests/` (5 test projects) |
| Mutation Testing | Shajeeve | Stryker.NET 4.16.0 | `backend/tests/*/stryker-config.json`, `mutation-tests.yml` |
| UI / E2E Testing | Mohamed Ilzam | Selenium WebDriver 4.48, xUnit, PdfPig | [`tests/selenium/`](tests/selenium/README.md) |
| Load & Performance Testing | Gunitha Rathnayake | Apache JMeter 5.6.3 | [`tests/jmeter/`](tests/jmeter/README.md) |

### Tool setup

* **Unit and mutation tests:** .NET 9 SDK. `dotnet tool restore` installs Stryker.NET from `.config/dotnet-tools.json`.
* **E2E tests:** .NET 10 SDK and Google Chrome. Copy `tests/selenium/.env.example` to `tests/selenium/.env.e2e.local` (git-ignored) and fill in the four QA accounts: owner, staff, client and Stage 2 client (see the [E2E README](tests/selenium/README.md)).
* **Load tests:** Java 17+, Apache JMeter 5.6.3 (`brew install jmeter`) and Node.js 20. The QA login goes in `tests/jmeter/data/accounts.csv`, copied from `accounts.example.csv` (git-ignored).

### Run locally

Unit tests (all five test projects):

```bash
dotnet test backend/Custodian.sln
```

Mutation tests for one service (repeat for any test project; the HTML report is written to `StrykerOutput/`):

```bash
cd backend/tests/Custodian.Workflow.Tests && dotnet tool restore && dotnet stryker
```

E2E tests, headless:

```bash
SELENIUM_HEADLESS=true DOTNET_ROLL_FORWARD=Major dotnet test tests/selenium/Custodian.System.Tests/Custodian.System.Tests.csproj --filter "Category=CI"
```

On Windows, `tests/selenium/run-demo.ps1` runs the same suite with visible Chrome for demos.

Load test (smoke profile; also `load`, `stress` and `spike`):

```bash
cd tests/jmeter && ./run.sh smoke --props qa.properties
```

### In GitHub Actions

| Workflow (Actions tab name) | Layer | Pull request to `dev` / `main` | Push to `dev` / `main` | Run workflow button |
| --------------------------- | ----- | ------------------------------ | ---------------------- | ------------------- |
| `ci.yml` (CI — Build & Test) | Unit | Yes, backend changes | Yes, backend changes | No |
| `mutation-tests.yml` (Mutation Tests — Backend) | Mutation | Yes, backend changes: changed code only, fails below `BREAK_AT` (75%) | No | Yes: full run of every project; also Mondays 02:00 UTC |
| `e2e-pipeline.yml` (CI — E2E Testing Pipeline (Role D)) | E2E | Build check only (browser tests need the account secrets) | Yes: smoke test (APPR01) | Yes: smoke or full suite (11 tests) |
| `load-test.yml` (Load Test — JMeter (manual)) | Load | No | No | Yes: redeploys the four services, then runs the chosen profile |
| `deploy-*.yml` (Deploy — *Service*) | Unit, then deploy | No | `main`: build, unit tests, deploy | Yes |

Load tests are manual on purpose: they measure the deployed environment, they would overlap the E2E tests and
deployments that run on every push, and they put real load on the shared QA apps. A run can also be started from a
terminal with the GitHub CLI:

```bash
gh workflow run load-test.yml --ref main -f run_load_test=true -f profile=smoke
```

**Results:** every workflow writes its verdict to the run's summary page (test totals, mutation score per project,
SLA table) and keeps its reports as artifacts (Stryker HTML reports, the JMeter dashboard and evidence). A failed
check fails the job.

**Secrets** (GitHub → Settings → Secrets and variables → Actions):

* `QA_EMAIL` / `QA_PASSWORD`: the QA-only login for the load test.
* `E2E_OWNER_*`, `E2E_STAFF_*`, `E2E_CLIENT_*`, `E2E_STAGE2_CLIENT_*` (`_EMAIL` and `_PASSWORD` each, 8 in total): the E2E accounts. The E2E job fails straight away if any is missing.
* The existing `AZURE_*` deployment secrets.

**Mutation score thresholds:** Stryker rates a score of 85% or more as high; pull requests fail below 75%, just under
the lowest current service score (Documents, 76%).

## Sprint 3 Features (16 Sep – 30 Sep 2026)

| Story | Feature |
| ----- | ------- |
| CSTD-19 | Next-action orchestration: one deterministic "what happens next" per engagement |
| CSTD-21 | Client action model: create, edit, cancel, standard checklist |
| CSTD-24 | Engagement conditions (Approval / Payment) that gate a later stage |
| CSTD-33 | Action SLA and stall detection, computed on read |
| CSTD-34 | Staff stall queue sorted by urgency; owner reassigns responsible staff |
| CSTD-35 | Interventions and recovery, with an idempotent recheck |
| CSTD-36 | Shared report infrastructure (PDF / CSV) |
| CSTD-37 | SLA Performance report (in progress) |
| CSTD-40 | SHA-256 hash chain for audit events, with a verify endpoint |

Details: [Workflow Engine](docs/architecture/workflow-engine.md), [Workflow API](docs/api/workflow-api.md), [Audit API](docs/api/audit-api.md), [Reporting](docs/reporting.md).

## Documentation

Detailed project documentation is available in the [`docs/`](docs/) directory.

| Documentation                                                           | Description                                 |
| ----------------------------------------------------------------------- | ------------------------------------------- |
| [Deployment Architecture](docs/architecture/deployment-architecture.md) | System architecture and deployment overview |
| [Workflow Engine](docs/architecture/workflow-engine.md)                 | Next action, conditions, SLA/stall, interventions, reports, hash chain |
| [Workflow API](docs/api/workflow-api.md)                                | Workflow endpoints added in Sprint 3        |
| [Audit API](docs/api/audit-api.md)                                      | Audit endpoints and chain verification      |
| [Reporting](docs/reporting.md)                                          | Report conventions and the SLA Performance report |
| [Development Setup](docs/development/development-setup.md)              | Local development and setup instructions    |
| [Azure Backend Deployment](docs/deployment/azure-backend.md)            | Backend deployment to Azure App Services    |
| [Azure Event Hubs Kafka Integration](docs/deployment/event-hubs-kafka.md) | Event Hubs Kafka messaging and runtime setup |
| [Vercel Frontend Deployment](docs/deployment/vercel-frontend.md)        | Frontend deployment and configuration       |
| [GitHub Actions CI/CD](docs/ci-cd/github-actions.md)                    | CI/CD workflows and deployment automation   |
| [Monitoring](monitoring/README.md)                                      | Prometheus + Grafana setup and `/metrics` protection |
| [Load & Performance Testing](tests/jmeter/README.md)                    | JMeter smoke / load / stress / spike tests, SLA gate, manual CI workflow (owner: Gunitha Rathnayake) |
| [UI / E2E Testing](tests/selenium/README.md)                            | Selenium suite (11 tests), setup and demo runner (owner: Mohamed Ilzam) |
| [Troubleshooting](docs/troubleshooting.md)                              | Common development and deployment issues    |

## Security

Sensitive configuration such as:

* Database connection strings
* JWT signing keys
* Azure deployment credentials

must not be committed to the repository.

Environment-specific configuration should be provided through environment variables, GitHub Secrets, or Azure App Service application settings as appropriate.
