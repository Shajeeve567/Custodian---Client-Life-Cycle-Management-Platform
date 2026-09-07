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
VITE_IDENTITY_API_URL
VITE_WORKFLOW_API_URL
VITE_AUDIT_API_URL
VITE_DOCUMENTS_API_URL
```

These values should point to the appropriate local or deployed backend services.

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
