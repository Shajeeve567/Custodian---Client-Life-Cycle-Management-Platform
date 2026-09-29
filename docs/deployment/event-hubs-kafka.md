# Azure Event Hubs Kafka Integration

## Overview

Custodian uses Azure Event Hubs as its cloud message broker via the Azure Event Hubs Kafka-compatible endpoint. This enables standard Apache Kafka client protocols (`Confluent.Kafka`) to stream lifecycle and audit events in the cloud without requiring custom Azure SDKs or dedicated virtual machine broker infrastructure.

* **Event Hubs Namespace:** `custodian-events`
* **Event Hub / Topic:** `custodian.events`
* **Partition Count:** 1
* **Producers:** `Workflow` (lifecycle events) and `Documents` (document events) when `Audit__Transport=Kafka`.
* **Consumers:**
  * `Audit` (group `custodian-audit`) persists every event to `audit_db` and links it into the engagement's SHA-256 hash chain (CSTD-40).
  * `Workflow` (group `custodian-workflow`) reacts to `document.verified` / `document.verification_rejected` by updating the linked client action.
  * `Identity` (group `custodian-identity-notifications-group`) turns events into client notifications. It has no SASL settings, so it only works against a plain local broker (see below).

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

### Events added in Sprint 3 (Workflow → `custodian.events`)

| Event Type | Raised by |
| :--- | :--- |
| `ClientActionCreated`, `ClientActionUpdated`, `ClientActionStatusChanged` | Client action create, edit, cancel/complete/review (CSTD-21) |
| `ConditionAttached`, `ConditionUpdated`, `ConditionDeactivated` | Engagement conditions (CSTD-24). `InternalNote` is never included |
| `action.overdue` | First read that finds a newly overdue blocker; once per stall episode (CSTD-33) |
| `StallResolved` | A stall episode closes (CSTD-33) |
| `ResponsibleStaffChanged` | `PUT /api/Engagements/{id}/staff` (CSTD-34) |
| `intervention.recovered` | Recording an intervention (CSTD-35); the payload carries the type and outcome |

All of these go through the same `Audit__Transport` (Kafka or HTTP) and are hash-chained by Audit.

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

### Workflow Service (Document Events Consumer)

Workflow also consumes document verification events (`document.verified`, `document.verification_rejected`) to update the linked client action (CSTD-19, 19-N5). It reuses the producer's broker and SASL variables above and adds:

```bash
Kafka__ConsumerEnabled=true    # the appsettings.json default; the code default is false
Kafka__GroupId=custodian-workflow
```

The Documents service now publishes these events to Kafka (`KafkaAuditPublisher`, selected with `Audit__Transport=Kafka`), so the consumer is enabled by default.

### Documents Service (Producer)

```bash
Audit__Transport=Kafka
Kafka__BootstrapServers=<namespace>.servicebus.windows.net:9093
Kafka__Topic=custodian.events
Kafka__ClientId=documents-service
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

## Staging vs Production

Staging and production are separate App Services and read their Kafka settings from their own application settings. Nothing in the repository or the GitHub workflows sets a staging-specific topic or consumer group: `appsettings.json` has one topic (`custodian.events`) and the group defaults above. To keep staging traffic out of production, set these keys differently on the staging App Services:

| Key | Set on | Purpose |
|---|---|---|
| `Kafka__Topic` | all four services | Staging topic (Event Hub) name. Producers and consumers in one environment must use the same value |
| `Kafka__GroupId` | Audit, Workflow, Identity | Staging consumer group names, so staging consumers never take production messages |
| `Kafka__BootstrapServers`, `Kafka__SaslPassword` | all producers/consumers | Only if staging uses a different Event Hubs namespace or access policy |

The Event Hub and its consumer groups must also exist in Azure (Event Hubs namespace → Event Hubs → *hub* → Consumer groups).

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
