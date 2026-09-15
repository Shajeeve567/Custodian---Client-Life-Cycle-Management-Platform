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

## 9. Known Deferred Reliability Item: Audit Consumer Offset Commit on Processing Failure

> **Status:** Known reliability hardening item / deferred to next sprint.

**Issue Description:**
In the current implementation of `KafkaAuditEventConsumer`, the message processing call `ProcessMessageAsync` internally catches general exceptions (`catch (Exception ex)`), logs the error, and returns without rethrowing. As a result, the outer consumption loop in `ExecuteAsync` considers message execution complete and proceeds to call `consumer.Commit(consumeResult)`.

**Impact & Risk:**
If a transient downstream processing failure occurs (such as a temporary Azure MySQL database connection timeout or transient deadlock), the error is logged, but the Kafka consumer offset is still advanced and committed to Azure Event Hubs. Consequently, the affected audit event will not be redelivered or retried by the same consumer group once the database recovers, presenting a risk of missing records in the audit log during outages.

**Current State & Scope:**
* Basic poison message / malformed JSON handling is present (unparseable envelopes are logged and discarded without crashing the service).
* Dedicated dead-letter queue (DLQ) behavior and secondary retry topics are not yet implemented.
* No separate failure topic exists.
* Remediation (propagating transient DB exceptions, deferring commit, and applying consumer retry/backoff policies) is tracked as a planned reliability hardening item for the upcoming sprint.

