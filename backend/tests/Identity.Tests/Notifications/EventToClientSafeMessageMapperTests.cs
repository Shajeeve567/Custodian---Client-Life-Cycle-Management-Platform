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

    // CSTD-35: interventions notify the client only for a positive outcome, with a generic message
    // (never the staff-only reason).
    [Theory]
    [InlineData("Recovered")]
    [InlineData("Progressing")]
    public void Map_InterventionRecovered_PositiveOutcome_ShouldReturnBackOnTrackMessage(string outcome)
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("intervention.recovered", new
        {
            clientId,
            outcome,
            reason = "Internal: client ignored three emails"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Contains("Back on track", result.Subject);
        Assert.DoesNotContain("ignored", result.Message);
    }

    [Theory]
    [InlineData("NoChange")]
    [InlineData("Escalated")]
    [InlineData(null)]
    public void Map_InterventionRecovered_OtherOutcome_IsNotSent(string? outcome)
    {
        var envelope = CreateEnvelope("intervention.recovered", new { clientId = Guid.NewGuid(), outcome });

        Assert.Null(_mapper.MapToClientSafeMessage(envelope));
    }

    [Theory]
    [InlineData("internal.system.metric.recorded")]
    [InlineData("ClientActionUpdated")]
    [InlineData("StandardChecklistApplied")]
    [InlineData("ConditionUpdated")]
    [InlineData("document.metadata_updated")]
    [InlineData("Genesis")] // engagement created as a Draft: the client is welcomed when it starts
    public void Map_NonClientFacingEvent_IsNotSent(string eventType)
    {
        var envelope = CreateEnvelope(eventType, new
        {
            clientId = Guid.NewGuid(),
            internalMetricId = 9999,
            stackTrace = "secret server information"
        });

        Assert.Null(_mapper.MapToClientSafeMessage(envelope));
    }

    [Fact]
    public void Map_NullEnvelope_ReturnsNullWithoutThrowing()
    {
        Assert.Null(_mapper.MapToClientSafeMessage(null!));
    }

    // H2: the names Workflow and Documents actually publish, with fields wrapped in EngagementEventPayload.Data.
    private static KafkaEnvelope Wrapped(string eventType, object data) =>
        CreateEnvelope(eventType, new EngagementEventPayload(Guid.NewGuid(), "staff-1", JsonSerializer.SerializeToElement(data)));

    [Fact]
    public void Map_WorkflowRequirementRequested_ForClient_ReturnsRequestMessage()
    {
        var clientId = Guid.NewGuid();
        var result = _mapper.MapToClientSafeMessage(Wrapped("RequirementRequested", new
        {
            clientId,
            title = "Provide: Bank statement",
            assignedToRole = "Client",
            requirementType = "BankStatement"
        }));

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Contains("Provide: Bank statement", result.Subject);
    }

    [Fact]
    public void Map_WorkflowRequirementRequested_ForStaff_IsNotSent()
    {
        Assert.Null(_mapper.MapToClientSafeMessage(Wrapped("RequirementRequested", new
        {
            clientId = Guid.NewGuid(),
            title = "Internal review",
            assignedToRole = "Staff"
        })));
    }

    [Theory]
    [InlineData("Approval", "Approval Required")]
    [InlineData("Payment", "Payment Condition Update")]
    public void Map_WorkflowConditionAttached_UsesConditionType(string type, string expectedSubject)
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("ConditionAttached", new
        {
            clientId = Guid.NewGuid(),
            type,
            title = "Scope sign-off"
        }));

        Assert.NotNull(result);
        Assert.Contains(expectedSubject, result!.Subject);
        Assert.Contains("Scope sign-off", result.Subject);
    }

    [Fact]
    public void Map_WorkflowStageChange_ShowsReadableStageName()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("StageChange", new
        {
            clientId = Guid.NewGuid(),
            fromStage = "Onboarding",
            toStage = "DocumentCollection"
        }));

        Assert.NotNull(result);
        Assert.Contains("Document Collection", result!.Message);
    }

    [Theory]
    [InlineData("Started", true)]
    [InlineData("Closed", false)]
    [InlineData("Cancelled", false)]
    public void Map_WorkflowStatusChange_WelcomesOnlyWhenStarted(string toStatus, bool expectWelcome)
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("StatusChange", new
        {
            clientId = Guid.NewGuid(),
            fromStatus = "Draft",
            toStatus
        }));

        if (expectWelcome)
        {
            Assert.NotNull(result);
            Assert.Contains("Welcome", result!.Subject);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void Map_DocumentsVerificationRejected_IncludesFileNameAndReason()
    {
        var clientId = Guid.NewGuid();
        var result = _mapper.MapToClientSafeMessage(Wrapped("document.verification_rejected", new
        {
            clientId,
            fileName = "passport.pdf",
            rejectionReason = "The scan is cut off."
        }));

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Contains("passport.pdf", result.Message);
        Assert.Contains("The scan is cut off.", result.Message);
    }
}
