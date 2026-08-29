using System.Text.Json;
using Custodian.Shared.Messaging;

namespace Custodian.Identity.Services.Notifications.Mappers;

public sealed class EventToClientSafeMessageMapper : IEventToMessageMapper
{
    public ClientSafeMessageResult MapToClientSafeMessage(KafkaEnvelope envelope)
    {
        if (envelope == null)
        {
            return new ClientSafeMessageResult
            {
                Subject = "Update on your Custodian Engagement",
                Message = "An update was made to your engagement. Please visit your portal for details."
            };
        }

        var clientId = ExtractClientId(envelope.Payload);
        var clientEmail = ExtractStringProperty(envelope.Payload, "clientEmail", "email", "Email");

        var eventType = envelope.EventType?.Trim().ToLowerInvariant() ?? string.Empty;

        return eventType switch
        {
            "engagement.started" or "engagement.created" or "genesis" => new ClientSafeMessageResult
            {
                Subject = "Welcome! Your onboarding engagement has started",
                Message = "Your onboarding engagement is now active. Please log into your client portal to review your onboarding plan and next steps.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "document.verified" => new ClientSafeMessageResult
            {
                Subject = $"Document Verified: {ExtractStringProperty(envelope.Payload, "documentName", "title", "fileName") ?? "Your document"}",
                Message = $"Great news! Your submitted document '{ExtractStringProperty(envelope.Payload, "documentName", "title", "fileName") ?? "document"}' has been successfully reviewed and verified.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "document.rejected" => new ClientSafeMessageResult
            {
                Subject = $"Action Required: Document Update Needed",
                Message = $"Your uploaded document '{ExtractStringProperty(envelope.Payload, "documentName", "title", "fileName") ?? "document"}' requires revision: {SanitizeReason(ExtractStringProperty(envelope.Payload, "reason", "rejectionReason") ?? "Please re-upload a clear copy.")}",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "requirement.requested" or "document.requested" => new ClientSafeMessageResult
            {
                Subject = $"Document Requested: {ExtractStringProperty(envelope.Payload, "requirementName", "title", "documentType") ?? "New Requirement"}",
                Message = $"A required document ('{ExtractStringProperty(envelope.Payload, "requirementName", "title", "documentType") ?? "document"}') has been requested for your engagement. Please log into your portal to upload it.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "condition.attached" or "approval.requested" => new ClientSafeMessageResult
            {
                Subject = $"Approval Required: {ExtractStringProperty(envelope.Payload, "title", "conditionType") ?? "Engagement Step"}",
                Message = $"A new approval item ('{ExtractStringProperty(envelope.Payload, "title", "conditionType") ?? "approval request"}') is awaiting your review on the portal to proceed to the next stage.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "payment.condition.attached" => new ClientSafeMessageResult
            {
                Subject = $"Payment Condition Update: {ExtractStringProperty(envelope.Payload, "title") ?? "Milestone"}",
                Message = $"A payment condition milestone ('{ExtractStringProperty(envelope.Payload, "title") ?? "milestone"}') is now active for your engagement.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "action.overdue" => new ClientSafeMessageResult
            {
                Subject = "Reminder: Action awaiting your input",
                Message = $"Friendly reminder: An action on your engagement ('{ExtractStringProperty(envelope.Payload, "actionTitle", "title") ?? "pending step"}') is awaiting your input to keep your onboarding moving smoothly.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            "intervention.recovered" or "engagement.ready" => new ClientSafeMessageResult
            {
                Subject = $"Engagement Update: Progressing to Next Stage",
                Message = $"Great news! Your engagement has been updated and has progressed to the '{ExtractStringProperty(envelope.Payload, "stageName", "stage") ?? "next"}' stage.",
                ClientId = clientId,
                ClientEmail = clientEmail
            },

            _ => new ClientSafeMessageResult
            {
                Subject = "Update on your Custodian Engagement",
                Message = ExtractStringProperty(envelope.Payload, "clientMessage", "message") 
                          ?? "An update was made to your onboarding engagement. Please visit your client portal for details.",
                ClientId = clientId,
                ClientEmail = clientEmail
            }
        };
    }

    private static Guid ExtractClientId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Guid.Empty;
        }

        if (payload.TryGetProperty("clientId", out var prop) && prop.TryGetGuid(out var id))
        {
            return id;
        }

        if (payload.TryGetProperty("ClientId", out var prop2) && prop2.TryGetGuid(out var id2))
        {
            return id2;
        }

        return Guid.Empty;
    }

    private static string? ExtractStringProperty(JsonElement payload, params string[] propertyNames)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (payload.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val.Trim();
                }
            }
        }

        return null;
    }

    private static string SanitizeReason(string reason)
    {
        // Strip any technical stack traces, SQL errors, or internal exception strings
        if (reason.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Sql", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Stack", StringComparison.OrdinalIgnoreCase))
        {
            return "The document does not meet compliance standards. Please re-upload a clean, valid copy.";
        }

        return reason;
    }
}
