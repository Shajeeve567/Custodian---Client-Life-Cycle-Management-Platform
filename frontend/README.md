# Custodian Frontend

React + Vite + TypeScript frontend application for the **Custodian Operating System** client lifecycle management platform, maintaining pixel-accurate fidelity with Figma Dev Mode designs.

## Environment Variables

Create a `.env` file in the `frontend/` directory (or use default dev fallbacks):

```env
VITE_IDENTITY_URL=http://localhost:5281
VITE_WORKFLOW_URL=http://localhost:5225
VITE_AUDIT_URL=http://localhost:5051
VITE_DOCUMENTS_URL=http://localhost:5171
```

- **`VITE_IDENTITY_URL`**: URL of the Identity microservice (Authentication, Tenants, Clients, Team).
- **`VITE_WORKFLOW_URL`**: URL of the Workflow microservice (Engagements orchestration & stage transitions).
- **`VITE_AUDIT_URL`**: URL of the Audit microservice (Immutable event logs & cryptographic verification).
- **`VITE_DOCUMENTS_URL`**: URL of the Documents microservice (Evidence uploads & document metadata).

## Installation & Running

```bash
# Install dependencies
npm install

# Start local Vite development server
npm run dev

# Run TypeScript type check
npm run type-check

# Build for production
npm run build
```

## Features & Flows

1. **Create Account (`/register`)**:
   - Registers user identity node (`POST /api/Auth/register`).
   - Retrieves global security token (`POST /api/Auth/login`).
   - Initializes firm tenant workspace (`POST /api/Tenant`).
   - Exhanges for operational workspace token (`POST /api/Auth/select-workspace/{tenantId}`).
   - Redirects to Engagements dashboard.

2. **Login (`/login`)**:
   - Authenticates credentials (`POST /api/Auth/login`).
   - Discovers authorized workspaces (`GET /api/Tenant/mine`).
   - Auto-selects if single tenant, prompts creation if zero tenants, or presents workspace picker if multiple tenants.
   - Exchanges for workspace token (`POST /api/Auth/select-workspace/{tenantId}`).

3. **Engagements Dashboard (`/engagements`)**:
   - Protected route requiring valid workspace token with `tenant_id` claim.
   - Loads client profiles (`GET /api/Client`).
   - Supports inline client creation (`POST /api/Client`).
   - Creates new engagements (`POST /api/Engagements`).
   - Lists and filters active workspace engagements (`GET /api/Engagements?tenantId=...`).
