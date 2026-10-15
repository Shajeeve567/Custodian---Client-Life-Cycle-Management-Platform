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
VITE_IDENTITY_URL
VITE_WORKFLOW_URL
VITE_AUDIT_URL
VITE_DOCUMENTS_URL
```

The older names `VITE_<SERVICE>_API_URL` are still read as a fallback (`frontend/src/services/api.ts`); without either, the frontend uses the local ports (`5281`, `5225`, `5051`, `5171`). Copy `frontend/.env.example` to `frontend/.env` to start locally.

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

---

## 10. Log Stream Says `<Service>.dll` Does Not Exist

**Problem:**
Right after creating a new App Service, the log stream reports that `Workflow.dll` does not exist.

**Cause:**
The log lines are from before the first deployment: the startup command is set, but `wwwroot` is still empty.

**Solution:**
Run the service's `deploy-*.yml` workflow (it can be started manually with **Run workflow**) and check the log stream again.

---

## 11. Kafka SASL Error: "connection string is not of the expected format"

**Problem:**
A service fails to connect to Event Hubs at startup with this message.

**Cause:**
`Kafka__SaslPassword` contains a different connection string (for example the storage account's) instead of the Event Hubs one.

**Solution:**
Copy the connection string from **Event Hubs namespace → Shared access policies → custodian-apps** (it starts with `Endpoint=sb://`) and keep `Kafka__SaslUsername=$ConnectionString`.

---

## 12. New Settings Not Picked Up After Saving App Settings

**Problem:**
After changing App Settings, the service kept the old behaviour.

**Cause:**
On a B1 plan the new container took longer than the startup probe allowed (about 78 seconds in our case), so Azure kept the old container running.

**Solution:**
Restart the web app (**Overview → Restart**) and watch the log stream until the new container reports it has started.

---

## 13. Prometheus Target Shows `401 Unauthorized`

**Problem:**
On Prometheus's **Status → Target health** page a service shows `401`.

**Cause:**
The key in `monitoring/prometheus/scrape_key` does not match `Metrics__ScrapeKey` on the App Service. In our case extra characters were pasted (67 characters instead of 64).

**Solution:**
Rewrite the file without a trailing newline (`printf '%s'`, see `monitoring/README.md`), compare only the lengths of the two values (never print them), and restart Prometheus.

---

## 14. Grafana or Prometheus Unreachable

**Problem:**
`http://<vm-ip>:3000` or `:9090` times out, although the VM is running.

**Cause:**
The VM's network security group only allows these ports from the operator's IP, and that home IP changed.

**Solution:**
Check the current IP (`curl -s https://api.ipify.org`) and update the source of the SSH, Grafana and Prometheus rules on `vm-custodian-monitoring-nsg`.

---

## 15. Empty "5xx Ratio" Panel in Grafana

**Problem:**
The 5xx panel shows "No data".

**Cause:**
No request has returned a 5xx status yet, so the metric series does not exist. This is expected.

**Solution:**
Nothing to fix. The panel fills as soon as a 5xx response is recorded.

---

## 16. Swagger UI or Scalar Returns 404 on Azure

**Problem:**
`/swagger` or `/scalar/v1` works locally but returns 404 on the App Service.

**Cause:**
The API docs are only served in Development unless `ApiDocs__Enabled=true`.

**Solution:**
Set `ApiDocs__Enabled=true` in the web app's environment variables and save (the app restarts).
