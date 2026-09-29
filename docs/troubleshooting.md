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

---

## 7. Workflow Service Fails to Start or MySQL Access Denied

**Problem:**
Workflow service terminates on startup with database connection exceptions or MySQL `Access denied for user` errors.

**Cause:**
If database connection string environment variables are not supplied at runtime, the application falls back to default development or localhost database connection settings in `appsettings.json`, which may not be accessible or may lack correct credentials for the target Azure MySQL database.

**Solution:**
Ensure the appropriate database connection string environment variable is explicitly provided at runtime before starting the service:

```bash
ConnectionStrings__Default="Server=<MYSQL_HOST>;Port=3306;Database=workflow_db;User=<USER>;Password=<PASSWORD>;SslMode=Required;"
ConnectionStrings__AzureMySqlConnection="Server=<MYSQL_HOST>;Port=3306;Database=workflow_db;User=<USER>;Password=<PASSWORD>;SslMode=Required;"
```

*(Never commit actual database credentials or connection strings to source control.)*

---

## 8. Azure Event Hubs TLS Certificate Verification Failure

**Problem:**
The Kafka producer or consumer fails to establish a secure connection to Azure Event Hubs, logging an SSL handshake error:

```text
SSL handshake failed: error:0A000086:SSL routines::certificate verify failed: broker certificate could not be verified
```

**Cause:**
In certain network environments (e.g., enterprise, campus, or firewall-monitored Wi-Fi networks), a deep-packet SSL inspection firewall (such as Fortinet) intercepts outbound port 9093 TLS connections and presents a custom or self-signed proxy certificate that is not trusted by the underlying `librdkafka` OpenSSL trust store.

**Solution:**
1. Switch to an unintercepted network connection (e.g., standard cellular hotspot or trusted external network).
2. Keep TLS certificate verification enabled.
3. Do not permanently disable SSL certificate verification in production or application configuration, as this circumvents transit encryption validation.

---

## 9. Kafka Consumers: Retry Instead of Committing Failed Messages

> **Status:** Fixed (Audit `KafkaAuditEventConsumer` and Workflow `DocumentEventsConsumer`).

Both consumers commit a message's offset only when it is done:

* **Recorded / applied**, or a **duplicate** (idempotent redelivery): committed.
* **Cannot succeed on retry** (unparseable JSON, missing ids, an event the service rejects as invalid, or one that conflicts with another tenant's chain): logged at Warning, skipped and committed, so it does not block the partition.
* **Any other failure** (for example Azure MySQL unavailable or a timeout): logged at Error and **not committed**. The consumer seeks back to the message and retries it after 1s, 2s, 4s … capped at 60s, until it succeeds.

A message that keeps failing therefore holds up its partition (with a log entry every retry) instead of being lost. There is still no dead-letter topic: if a message fails permanently for a reason not listed above, fix the cause or skip it deliberately.

