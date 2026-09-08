# Troubleshooting

This document records issues encountered during the development and deployment of Custodian and their solutions.

---

## 1. Frontend Could Not Communicate with Backend

**Problem:**
The React frontend could not successfully make requests to backend services because the backend services did not have the required CORS configuration.

**Solution:**
Configured CORS in the affected backend services and enabled the CORS middleware in the request pipeline.

---

## 2. JWT Signing Key Configuration

**Problem:**
The Identity service requires a JWT signing key for authentication, but storing the signing key directly in the repository would expose a sensitive credential.

**Solution:**
Configured the JWT signing key through environment-specific configuration instead of committing it to source control. Deployment environments can provide the value through their application settings or secrets.

---

## 3. Vercel Environment Variables Not Taking Effect

**Problem:**
Changes to the frontend API URL environment variables in Vercel were not reflected in the already deployed application.

**Solution:**
Redeployed the frontend after changing the Vercel environment variables. Vite environment variables are injected during the build process, so changes require a new build/deployment.

---

## 4. Local and Deployed API Configuration Differences

**Problem:**
The frontend uses different backend API URLs when running locally compared with the deployed application.

**Solution:**
Configured the API URLs using Vite environment variables:

```text
VITE_IDENTITY_API_URL
VITE_WORKFLOW_API_URL
VITE_AUDIT_API_URL
VITE_DOCUMENTS_API_URL
```

Local development uses local service URLs, while the deployed frontend uses the corresponding Azure App Service URLs.

---

## 5. Azure Deployment Configuration

**Problem:**
Each backend microservice is deployed independently to Azure App Service, requiring service-specific deployment configuration.

**Solution:**
Created separate GitHub Actions deployment workflows for each service and configured the required Azure App Service name and publish profile as GitHub Secrets.

---

## 6. Docker Compose Infrastructure Issues

**Problem:**
Backend services could not communicate with required local infrastructure when the MySQL or Kafka containers were not running.

**Solution:**
Verified the Docker Compose services using:

```bash
docker compose ps
```

and restarted the local infrastructure when necessary:

```bash
docker compose down
docker compose up -d
```
