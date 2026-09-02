using System.Text.Json;
using Custodian.Identity.Services.Notifications.Mappers;
using Custodian.Shared.Messaging;
using Xunit;

namespace Identity.Tests.Notifications;

public class EventToClientSafeMessageMapperTests
{
    private readonly EventToClientSafeMessageMapper _mapper = new();

    private static KafkaEnvelope CreateEnvelope(string eventType, object payload, string? tenantId = null)
    {
        return new KafkaEnvelope(
            EventId: Guid.NewGuid().ToString("N"),
            EventType: eventType,
            TenantId: tenantId ?? Guid.NewGuid().ToString(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Payload: JsonSerializer.SerializeToElement(payload)
        );
    }

    [Fact]
    public void Map_EngagementStarted_ShouldReturnWelcomeMessage()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("engagement.started", new
        {
            clientId,
            clientEmail = "client@example.com",
            title = "Alpha Project"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Equal("client@example.com", result.ClientEmail);
        Assert.Contains("Welcome", result.Subject);
        Assert.Contains("onboarding engagement is now active", result.Message);
    }

    [Fact]
    public void Map_DocumentVerified_ShouldIncludeDocumentName()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("document.verified", new
        {
            clientId,
            documentName = "Passport Copy"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Passport Copy", result.Subject);
        Assert.Contains("Passport Copy", result.Message);
        Assert.Contains("successfully reviewed and verified", result.Message);
    }

    [Fact]
    public void Map_DocumentRejected_ShouldIncludeReasonWithoutTechnicalJargon()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("document.rejected", new
        {
            clientId,
            documentName = "Proof of Address",
            reason = "The image is blurry and expired."
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Action Required", result.Subject);
        Assert.Contains("Proof of Address", result.Message);
        Assert.Contains("The image is blurry and expired.", result.Message);
    }

    [Fact]
    public void Map_DocumentRejected_WithTechnicalException_ShouldSanitizeJargon()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("document.rejected", new
        {
            clientId,
            documentName = "Bank Statement",
            reason = "SqlException: Table dbo.DocumentVerifications failed with NullReferenceException stack trace"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.DoesNotContain("SqlException", result.Message);
        Assert.DoesNotContain("stack trace", result.Message);
        Assert.Contains("does not meet compliance standards", result.Message);
    }

    [Fact]
    public void Map_RequirementRequested_ShouldIncludeRequirementName()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("requirement.requested", new
        {
            clientId,
            requirementName = "Tax Declaration Form"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Tax Declaration Form", result.Subject);
        Assert.Contains("Tax Declaration Form", result.Message);
        Assert.Contains("requested for your engagement", result.Message);
    }

    [Fact]
    public void Map_ConditionAttached_ShouldReturnApprovalRequestMessage()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("condition.attached", new
        {
            clientId,
            title = "Final Deliverable Sign-off"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Final Deliverable Sign-off", result.Subject);
        Assert.Contains("awaiting your review on the portal", result.Message);
    }

    [Fact]
    public void Map_PaymentConditionAttached_ShouldReturnPaymentMilestoneMessage()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("payment.condition.attached", new
        {
            clientId,
            title = "50% Initial Deposit"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("50% Initial Deposit", result.Subject);
        Assert.Contains("milestone", result.Message);
    }

    [Fact]
    public void Map_ActionOverdue_ShouldReturnFriendlyReminder()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("action.overdue", new
        {
            clientId,
            actionTitle = "Identity Verification Step"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Reminder", result.Subject);
        Assert.Contains("Identity Verification Step", result.Message);
    }

    [Fact]
    public void Map_InterventionRecovered_ShouldReturnStageProgressionMessage()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("intervention.recovered", new
        {
            clientId,
            stageName = "Compliance Review"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Contains("Progressing to Next Stage", result.Subject);
        Assert.Contains("Compliance Review", result.Message);
    }

    [Fact]
    public void Map_UnknownEvent_ShouldReturnSafeGenericMessageWithoutLeakingPayload()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("internal.system.metric.recorded", new
        {
            clientId,
            internalMetricId = 9999,
            stackTrace = "secret server information"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.Equal(clientId, result.ClientId);
        Assert.Equal("Update on your Custodian Engagement", result.Subject);
        Assert.DoesNotContain("secret server information", result.Message);
        Assert.Contains("update was made to your onboarding engagement", result.Message);
    }

    [Fact]
    public void Map_NullEnvelope_ShouldReturnSafeDefaultWithoutThrowing()
    {
        var result = _mapper.MapToClientSafeMessage(null!);

        Assert.NotNull(result);
        Assert.Equal("Update on your Custodian Engagement", result.Subject);
        Assert.Contains("An update was made", result.Message);
    }
}
