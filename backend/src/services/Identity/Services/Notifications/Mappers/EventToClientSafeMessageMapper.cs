using System.Text.Json;
using Custodian.Shared.Messaging;

namespace Custodian.Identity.Services.Notifications.Mappers;

/// <summary>
/// Turns events from the shared custodian.events topic into client-safe notifications.
///
/// Event names are matched case-insensitively and in both spellings in use: Workflow publishes
/// PascalCase names (RequirementRequested, ConditionAttached, StageChange, StatusChange), Documents
/// and the stall detector publish dot-case names (document.verified, action.overdue). The names are
/// not renamed at the source because Audit's hash chain and its consumer depend on them.
///
/// Only client-facing events produce a message. Everything else on the topic (task edits, checklist
/// changes, condition updates, audit-only document events, ...) returns null and is not sent: a
/// generic "an update was made" message for every internal event would flood the client.
/// </summary>
public sealed class EventToClientSafeMessageMapper : IEventToMessageMapper
{
    public ClientSafeMessageResult? MapToClientSafeMessage(KafkaEnvelope envelope)
    {
        if (envelope == null)
        {
            return null;
        }

        var clientId = ExtractClientId(envelope.Payload);
        var clientEmail = ExtractStringProperty(envelope.Payload, "clientEmail", "email", "Email");

        var eventType = envelope.EventType?.Trim().ToLowerInvariant() ?? string.Empty;

        ClientSafeMessageResult Message(string subject, string message) => new()
        {
            Subject = subject,
            Message = message,
            ClientId = clientId,
            ClientEmail = clientEmail
        };

        string? Field(params string[] names) => ExtractStringProperty(envelope.Payload, names);

        switch (eventType)
        {
            case "engagement.started" or "engagement.created":
                return Welcome();

            // Workflow: Draft -> Started. Genesis (creation) is not notified: the engagement is still a draft.
            case "statuschange":
                return string.Equals(Field("toStatus"), "Started", StringComparison.OrdinalIgnoreCase) ? Welcome() : null;

            case "document.verified":
                {
                    var name = Field("documentName", "title", "fileName") ?? "Your document";
                    return Message(
                        $"Document Verified: {name}",
                        $"Great news! Your submitted document '{name}' has been successfully reviewed and verified.");
                }

            // document.verification_rejected is what Documents publishes; document.rejected is kept for compatibility.
            case "document.verification_rejected" or "document.rejected":
                return Message(
                    "Action Required: Document Update Needed",
                    $"Your uploaded document '{Field("documentName", "title", "fileName") ?? "document"}' requires revision: {SanitizeReason(Field("reason", "rejectionReason") ?? "Please re-upload a clear copy.")}");

            case "requirementrequested" or "requirement.requested" or "document.requested":
                {
                    // Requirements can also be assigned to staff; only the client's own are notified.
                    var assignedTo = Field("assignedToRole");
                    if (assignedTo != null && !string.Equals(assignedTo, "Client", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    var name = Field("requirementName", "title", "requirementType", "documentType");
                    return Message(
                        $"Document Requested: {name ?? "New Requirement"}",
                        $"A required document ('{name ?? "document"}') has been requested for your engagement. Please log into your portal to upload it.");
                }

            case "payment.condition.attached":
                return PaymentCondition();

            case "conditionattached" or "condition.attached" or "approval.requested":
                return string.Equals(Field("type", "conditionType"), "Payment", StringComparison.OrdinalIgnoreCase)
                    ? PaymentCondition()
                    : Message(
                        $"Approval Required: {Field("title", "conditionType") ?? "Engagement Step"}",
                        $"A new approval item ('{Field("title", "conditionType") ?? "approval request"}') is awaiting your review on the portal to proceed to the next stage.");

            case "action.overdue":
                return Message(
                    "Reminder: Action awaiting your input",
                    $"Friendly reminder: An action on your engagement ('{Field("actionTitle", "title") ?? "pending step"}') is awaiting your input to keep your onboarding moving smoothly.");

            case "stagechange" or "engagement.ready":
                return Message(
                    "Engagement Update: Progressing to Next Stage",
                    $"Great news! Your engagement has been updated and has progressed to the '{Humanize(Field("stageName", "stage", "toStage")) ?? "next"}' stage.");
            
            // CSTD-35: an intervention was recorded
            // Only client positive outcome notify
            case "intervention.recovered":
                {
                    var outcome = Field("outcome");
                    if (!string.Equals(outcome, "Recovered", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(outcome, "Progressing", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    return Message(
                        "Engagement Update: Back on track",
                        "Good news! Your onboarding team has resolved a blocker and your engagement is moving forward again. Please check your portal for any next steps"
                    );
                }

            default:
                return null;
        }

        ClientSafeMessageResult Welcome() => Message(
            "Welcome! Your onboarding engagement has started",
            "Your onboarding engagement is now active. Please log into your client portal to review your onboarding plan and next steps.");

        ClientSafeMessageResult PaymentCondition() => Message(
            $"Payment Condition Update: {Field("title") ?? "Milestone"}",
            $"A payment condition milestone ('{Field("title") ?? "milestone"}') is now active for your engagement.");
    }

    /// <summary>"DocumentCollection" -> "Document Collection"; readable names pass through unchanged.</summary>
    private static string? Humanize(string? name) =>
        name == null ? null : System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>
    /// Produces the payload elements to search for fields, in preference order.
    /// Engagement lifecycle events are wrapped in EngagementEventPayload
    /// ({EngagementId, Actor, Data}), so the real fields live inside Data.
    /// Fall back to the top-level payload for events that don't wrap.
    /// </summary>
    private static IEnumerable<JsonElement> CandidatePayloads(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (payload.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            yield return data;
        }

        yield return payload;
    }

    private static Guid ExtractClientId(JsonElement payload)
    {
        foreach (var candidate in CandidatePayloads(payload))
        {
            if (candidate.TryGetProperty("clientId", out var prop) && prop.TryGetGuid(out var id))
            {
                return id;
            }
            if (candidate.TryGetProperty("ClientId", out var prop2) && prop2.TryGetGuid(out var id2))
            {
                return id2;
            }
        }

        return Guid.Empty;
    }

    private static string? ExtractStringProperty(JsonElement payload, params string[] propertyNames)
    {
        foreach (var candidate in CandidatePayloads(payload))
        {
            foreach (var name in propertyNames)
            {
                if (candidate.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }
            }
        }

        return null;
    }

    private static string SanitizeReason(string reason)
    {
        if (reason.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Sql", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Stack", StringComparison.OrdinalIgnoreCase))
        {
            return "The document does not meet compliance standards. Please re-upload a clean, valid copy.";
        }

        return reason;
    }
}