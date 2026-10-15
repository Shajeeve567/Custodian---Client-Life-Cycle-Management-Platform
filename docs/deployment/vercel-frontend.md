# Vercel Frontend Deployment

## Overview

The Custodian frontend is a React application written in TypeScript and built using Vite. It is deployed to Vercel and communicates with the backend services hosted on Azure App Services.

## Deployment Flow

```text
GitHub Repository
       |
       v
    Vercel
       |
       v
Vite Build
       |
       v
Deployed Frontend
       |
       v
Azure Backend APIs
```

## Frontend Configuration

The frontend uses Vite environment variables to configure the backend API URLs:

```text
VITE_IDENTITY_URL
VITE_WORKFLOW_URL
VITE_AUDIT_URL
VITE_DOCUMENTS_URL
```

The older names `VITE_<SERVICE>_API_URL` are still read as a fallback (`frontend/src/services/api.ts`); without either, the frontend uses the local ports (`5281`, `5225`, `5051`, `5171`). Copy `frontend/.env.example` to `frontend/.env` to start locally.

For local development, these variables point to the local backend services.

For the deployed application, they point to the corresponding Azure App Service URLs.

## Environment Variables

Vite environment variables are injected during the frontend build.

Therefore, when an environment variable is changed in Vercel, the application must be redeployed for the new value to take effect.

Environment-specific configuration should not contain sensitive secrets in the frontend, since Vite variables are exposed to the client-side application.

## Deployment

The Vercel project is connected to the GitHub repository and is served at https://custodian-blue.vercel.app. Its four `VITE_*_URL` variables point at the production App Services described in [azure-backend.md](azure-backend.md#production-environment), and that origin is set as `Cors__AllowedOrigins__0` on each of them.

When the frontend is deployed, Vercel builds the application and serves the generated production files.

The frontend can also be validated locally using:

```bash
npm run type-check
npm run build
```

## Troubleshooting

If the deployed frontend cannot communicate with the backend:

1. Check that the Vercel environment variables contain the correct Azure API URLs.
2. Redeploy the frontend after changing environment variables.
3. Check browser developer tools for failed API requests.
4. Check the backend service logs and CORS configuration.
