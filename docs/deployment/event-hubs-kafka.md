# Azure Event Hubs Kafka Integration

## Overview

Custodian uses Azure Event Hubs as its cloud message broker via the Azure Event Hubs Kafka-compatible endpoint. This enables standard Apache Kafka client protocols (`Confluent.Kafka`) to stream lifecycle and audit events in the cloud without requiring custom Azure SDKs or dedicated virtual machine broker infrastructure.

* **Event Hubs Namespace:** `custodian-events`
* **Event Hub / Topic:** `custodian.events`
* **Partition Count:** 1
* **Producer:** `Workflow` microservice publishes lifecycle events.
* **Consumer:** `Audit` microservice consumes events and persists them to `audit_db`.

---

## Architecture

```text
Workflow API
    ↓
KafkaAuditPublisher
    ↓
Confluent.Kafka (Producer)
    ↓ [TLS / SASL_SSL:9093]
Azure Event Hubs Kafka Endpoint (custodian-events.servicebus.windows.net:9093)
    ↓
custodian.events (Event Hub / Topic)
    ↓ [TLS / SASL_SSL:9093]
Confluent.Kafka (Consumer Group: custodian-audit)
    ↓
KafkaAuditEventConsumer
    ↓
AuditEventService
    ↓
audit_db (events table)
```

---

## Supported Events in Workflow / Audit Scope

The following event types are defined for the shared topic:

| Event Type | Producer Action | Audit Handling | Status in Current Task |
| :--- | :--- | :--- | :--- |
| `Genesis` | Engagement creation (`POST /api/Engagements`) | Inserts initial audit record | End-to-end verified via Azure Event Hubs |
| `StatusChange` | Status transition (`PUT /api/Engagements/{id}/status`) | Records status change and payload | End-to-end verified locally |
| `StageChange` | Stage progression (`PUT /api/Engagements/{id}/stage`) | Records stage update and timestamps | End-to-end verified locally |
| `RequirementRequested` | Guided requirement setup | Records requested requirement | Wired in codebase (not cloud-tested) |
| `RequirementSubmitted` | Client submission of requirement | Records submission event | Wired in codebase (not cloud-tested) |

> **Verification Scope:**
> * `Genesis`, `StatusChange`, and `StageChange` lifecycle transitions were manually verified end-to-end.
> * `Genesis` was verified against live Azure Event Hubs through to `audit_db`.
> * `RequirementRequested` and `RequirementSubmitted` events are implemented and handled in the consumer, but were not independently smoke-tested against Azure Event Hubs during this sprint.

---

## Azure Event Hubs Configuration

Azure Event Hubs exposes a Kafka 1.0+ compatible endpoint on port 9093. Standard SASL/PLAIN authentication is used where the username is `$ConnectionString` and the password is the shared access policy connection string.

| Setting | Value |
| :--- | :--- |
| `BootstrapServers` | `custodian-events.servicebus.windows.net:9093` |
| `Topic` | `custodian.events` |
| `SecurityProtocol` | `SaslSsl` |
| `SaslMechanism` | `Plain` |
| `SaslUsername` | `$ConnectionString` |
| `SaslPassword` | `<runtime Event Hubs connection string>` |

---

## Service Runtime Configuration

All secrets and connection details are provided via runtime environment variables and must never be committed to source code or configuration files.

### Workflow Service (Producer)

Set the following runtime environment variables when running the Workflow service:

```bash
Audit__Transport=Kafka
Kafka__BootstrapServers=custodian-events.servicebus.windows.net:9093
Kafka__Topic=custodian.events
Kafka__SecurityProtocol=SaslSsl
Kafka__SaslMechanism=Plain
Kafka__SaslUsername=$ConnectionString
Kafka__SaslPassword=<EVENT_HUBS_CONNECTION_STRING>
```

### Audit Service (Consumer)

Set the following runtime environment variables when running the Audit service:

```bash
Kafka__Enabled=true
Kafka__BootstrapServers=custodian-events.servicebus.windows.net:9093
Kafka__Topic=custodian.events
Kafka__GroupId=custodian-audit
Kafka__SecurityProtocol=SaslSsl
Kafka__SaslMechanism=Plain
Kafka__SaslUsername=$ConnectionString
Kafka__SaslPassword=<EVENT_HUBS_CONNECTION_STRING>
```

---

## Local Kafka Compatibility

The application code remains compatible with local Kafka for development when SASL configuration is omitted.

---

## Manual Verification Procedure

Follow this step-by-step procedure to perform an end-to-end verification through the API UI:

1. **Start the Audit service** with the Azure Event Hubs runtime variables configured.
2. **Start the Workflow service** with the Azure Event Hubs runtime variables configured.
3. Open the Workflow API documentation (e.g. `http://localhost:5225/scalar/v1` or Swagger UI).
4. Authenticate using a valid bearer token:
   ```text
   Authorization: Bearer <JWT_TOKEN>
   ```
5. Submit a `POST /api/Engagements` request with a valid payload:
   ```json
   {
     "tenantId": "<TENANT_ID>",
     "clientId": "<CLIENT_ID>",
     "staffId": "staff-admin"
   }
   ```
6. Confirm the HTTP response returns `201 Created`.
7. Copy the returned `engagementId`.
8. Open the **Azure Portal** → navigate to the `custodian-events` namespace → `custodian.events` Event Hub → select **Data Explorer**.
9. Confirm the message appears with `EventType = Genesis` and the matching `EngagementId`.
10. Connect to `audit_db` using your MySQL client and verify the event was consumed and persisted.

### Verification SQL Query

```sql
SELECT
    sequence_number,
    event_id,
    engagement_id,
    tenant_id,
    actor,
    type,
    timestamp,
    payload
FROM events
WHERE engagement_id = '<ENGAGEMENT_ID>'
ORDER BY sequence_number DESC;
```

---

## Verification Evidence Completed

A full live smoke test was conducted against live Azure Event Hubs infrastructure:

```text
Workflow
→ Azure Event Hubs
→ custodian.events
→ Audit
→ audit_db
```

Test results confirmed:
* **TLS connection:** PASS
* **SASL authentication:** PASS
* **Kafka produce:** PASS
* **Audit consume:** PASS
* **Audit DB row count:** Increased by exactly one row
* **Event Type:** `Genesis`
* **Idempotency & Isolation:** No secrets logged or committed to the repository
