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

    #region CSTD-144 Approval Event Tests

    [Fact]
    public void Map_ApprovalAttached_ReturnsApprovalRequiredNotification()
    {
        var clientId = Guid.NewGuid();
        var envelope = CreateEnvelope("ApprovalAttached", new
        {
            clientId,
            type = "Approval",
            title = "Final Design Sign-off"
        });

        var result = _mapper.MapToClientSafeMessage(envelope);

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Equal("Approval Required: Final Design Sign-off", result.Subject);
        Assert.Contains("Final Design Sign-off", result.Message);
        Assert.Contains("awaiting your review in the portal", result.Message);
    }

    [Fact]
    public void Map_WorkflowWrapped_ApprovalAttached_ExtractsTitleAndClientId()
    {
        var clientId = Guid.NewGuid();
        var result = _mapper.MapToClientSafeMessage(Wrapped("ApprovalAttached", new
        {
            conditionId = Guid.NewGuid(),
            clientId = clientId.ToString(),
            type = "Approval",
            title = "Board Resolution",
            requiredBeforeStage = "Execution",
            status = "Pending"
        }));

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Equal("Approval Required: Board Resolution", result.Subject);
        Assert.Contains("Board Resolution", result.Message);
    }

    [Fact]
    public void Map_ApprovalCompleted_ReturnsNull()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("ApprovalCompleted", new
        {
            conditionId = Guid.NewGuid(),
            clientId = Guid.NewGuid(),
            type = "Approval",
            title = "Board Resolution",
            status = "Satisfied",
            decision = "Approved"
        }));

        Assert.Null(result);
    }

    [Fact]
    public void Map_ApprovalRejected_ReturnsNull()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("ApprovalRejected", new
        {
            conditionId = Guid.NewGuid(),
            clientId = Guid.NewGuid(),
            type = "Approval",
            title = "Board Resolution",
            status = "Rejected",
            decision = "Rejected",
            rejectionReason = "Internal financial constraints"
        }));

        Assert.Null(result);
    }

    [Fact]
    public void Map_LegacyPaymentConditionAttached_StillReturnsPaymentNotification()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("ConditionAttached", new
        {
            conditionId = Guid.NewGuid(),
            clientId = Guid.NewGuid(),
            type = "Payment",
            title = "Initial Deposit"
        }));

        Assert.NotNull(result);
        Assert.Contains("Payment Condition Update", result!.Subject);
        Assert.Contains("Initial Deposit", result.Message);
    }

    #endregion

    #region Event name aliases

    // Every spelling listed in a case label must route to that case, not fall through to "not sent".
    [Theory]
    [InlineData("engagement.created", "Welcome! Your onboarding engagement has started")]
    [InlineData("  ENGAGEMENT.STARTED  ", "Welcome! Your onboarding engagement has started")]
    [InlineData("document.requested", "Document Requested: New Requirement")]
    [InlineData("requirementrequested", "Document Requested: New Requirement")]
    [InlineData("approval.attached", "Approval Required: Engagement Step")]
    [InlineData("approval.requested", "Approval Required: Engagement Step")]
    [InlineData("conditionattached", "Approval Required: Engagement Step")]
    [InlineData("engagement.ready", "Engagement Update: Progressing to Next Stage")]
    public void Map_EventNameAlias_RoutesToClientFacingMessage(string eventType, string expectedSubject)
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope(eventType, new { clientId = Guid.NewGuid() }));

        Assert.NotNull(result);
        Assert.Equal(expectedSubject, result!.Subject);
    }

    #endregion

    #region Field fallbacks and defaults

    [Theory]
    [InlineData("title")]
    [InlineData("fileName")]
    public void Map_DocumentVerified_FallsBackToAlternativeNameFields(string field)
    {
        var payload = new Dictionary<string, object> { ["clientId"] = Guid.NewGuid(), [field] = "Lease.pdf" };

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", payload));

        Assert.Equal("Document Verified: Lease.pdf", result!.Subject);
    }

    [Fact]
    public void Map_DocumentVerified_WithoutName_UsesDefault()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Document Verified: Your document", result!.Subject);
        Assert.Equal("Great news! Your submitted document 'Your document' has been successfully reviewed and verified.", result.Message);
    }

    [Fact]
    public void Map_DocumentVerified_NonStringNameField_IsIgnored()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", new { clientId = Guid.NewGuid(), documentName = 42 }));

        Assert.Equal("Document Verified: Your document", result!.Subject);
    }

    [Fact]
    public void Map_DocumentRejected_WithoutNameOrReason_UsesDefaults()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.rejected", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Action Required: Document Update Needed", result!.Subject);
        Assert.Equal("Your uploaded document 'document' requires revision: Please re-upload a clear copy.", result.Message);
    }

    // Each keyword on its own must trigger sanitizing - not only all three together.
    [Theory]
    [InlineData("NullReferenceException at line 4")]
    [InlineData("Sql timeout")]
    [InlineData("Stack overflow")]
    public void Map_DocumentRejected_AnySingleTechnicalKeyword_IsSanitized(string reason)
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.rejected", new { clientId = Guid.NewGuid(), reason }));

        Assert.EndsWith("The document does not meet compliance standards. Please re-upload a clean, valid copy.", result!.Message);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("requirementType")]
    [InlineData("documentType")]
    public void Map_RequirementRequested_FallsBackToAlternativeNameFields(string field)
    {
        var payload = new Dictionary<string, object> { ["clientId"] = Guid.NewGuid(), [field] = "Tax Form" };

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("requirement.requested", payload));

        Assert.Equal("Document Requested: Tax Form", result!.Subject);
    }

    [Fact]
    public void Map_RequirementRequested_WithoutName_UsesDefaults()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("requirement.requested", new { clientId = Guid.NewGuid(), assignedToRole = "client" }));

        Assert.Equal("Document Requested: New Requirement", result!.Subject);
        Assert.Equal("A required document ('document') has been requested for your engagement. Please log into your portal to upload it.", result.Message);
    }

    [Fact]
    public void Map_ApprovalAttached_WithoutTitle_UsesDefaults()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("ApprovalAttached", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Approval Required: Engagement Step", result!.Subject);
        Assert.Equal("A new approval item ('approval request') is awaiting your review in the portal to proceed to the next stage.", result.Message);
    }

    [Fact]
    public void Map_ConditionAttached_WithoutTitle_UsesConditionType()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("condition.attached", new { clientId = Guid.NewGuid(), conditionType = "Signoff" }));

        Assert.Equal("Approval Required: Signoff", result!.Subject);
        Assert.Equal("A new approval item ('Signoff') is awaiting your review on the portal to proceed to the next stage.", result.Message);
    }

    [Fact]
    public void Map_ConditionAttached_PaymentConditionType_ReturnsPaymentMessage()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("condition.attached", new { clientId = Guid.NewGuid(), conditionType = "payment", title = "Deposit" }));

        Assert.Equal("Payment Condition Update: Deposit", result!.Subject);
    }

    [Fact]
    public void Map_PaymentConditionAttached_WithoutTitle_UsesDefaults()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("payment.condition.attached", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Payment Condition Update: Milestone", result!.Subject);
        Assert.Equal("A payment condition milestone ('milestone') is now active for your engagement.", result.Message);
    }

    [Fact]
    public void Map_ActionOverdue_FallsBackToTitle()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("action.overdue", new { clientId = Guid.NewGuid(), title = "Sign NDA" }));

        Assert.Contains("('Sign NDA')", result!.Message);
    }

    [Fact]
    public void Map_ActionOverdue_WithoutTitle_UsesDefault()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("action.overdue", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Friendly reminder: An action on your engagement ('pending step') is awaiting your input to keep your onboarding moving smoothly.", result!.Message);
    }

    [Theory]
    [InlineData("stageName")]
    [InlineData("stage")]
    public void Map_StageChange_FallsBackToAlternativeStageFields(string field)
    {
        var payload = new Dictionary<string, object> { ["clientId"] = Guid.NewGuid(), [field] = "Review" };

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("StageChange", payload));

        Assert.Equal("Great news! Your engagement has been updated and has progressed to the 'Review' stage.", result!.Message);
    }

    [Fact]
    public void Map_StageChange_WithoutStage_UsesDefault()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("StageChange", new { clientId = Guid.NewGuid() }));

        Assert.Equal("Great news! Your engagement has been updated and has progressed to the 'next' stage.", result!.Message);
    }

    [Fact]
    public void Map_InterventionRecovered_UsesFixedClientSafeMessage()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("intervention.recovered", new { clientId = Guid.NewGuid(), outcome = "recovered" }));

        Assert.Equal("Engagement Update: Back on track", result!.Subject);
        Assert.Equal("Good news! Your onboarding team has resolved a blocker and your engagement is moving forward again. Please check your portal for any next steps", result.Message);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("Email")]
    public void Map_ClientEmail_FallsBackToAlternativeEmailFields(string field)
    {
        var payload = new Dictionary<string, object> { ["clientId"] = Guid.NewGuid(), [field] = "c@example.com" };

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("engagement.started", payload));

        Assert.Equal("c@example.com", result!.ClientEmail);
    }

    #endregion

    #region PaymentStatusChanged

    [Fact]
    public void Map_PaymentSatisfied_WithAmountAndCurrency_IncludesAmount()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("PaymentStatusChanged", new
        {
            clientId = Guid.NewGuid(),
            status = "satisfied",
            amount = "500",
            currency = "USD",
            title = "Deposit"
        }));

        Assert.Equal("Payment confirmed", result!.Subject);
        Assert.Equal("Payment of 500 USD for 'Deposit' has been confirmed. Your engagement can continue.", result.Message);
    }

    [Fact]
    public void Map_PaymentSatisfied_WithAmountOnly_TrimsMissingCurrency()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("PaymentStatusChanged", new { clientId = Guid.NewGuid(), status = "Satisfied", amount = "500" }));

        Assert.Equal("Payment of 500 for 'payment condition' has been confirmed. Your engagement can continue.", result!.Message);
    }

    [Fact]
    public void Map_PaymentSatisfied_WithoutAmount_OmitsAmountText()
    {
        var result = _mapper.MapToClientSafeMessage(Wrapped("PaymentStatusChanged", new { clientId = Guid.NewGuid(), status = "Satisfied", title = "Deposit" }));

        Assert.Equal("Payment for 'Deposit' has been confirmed. Your engagement can continue.", result!.Message);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]
    public void Map_PaymentNotSatisfied_IsNotSent(string status)
    {
        Assert.Null(_mapper.MapToClientSafeMessage(Wrapped("PaymentStatusChanged", new { clientId = Guid.NewGuid(), status, amount = "500" })));
    }

    #endregion

    #region Payload shapes and client id extraction

    [Fact]
    public void Map_PascalCaseClientIdKey_IsRead()
    {
        var clientId = Guid.NewGuid();

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("engagement.started", new { ClientId = clientId }));

        Assert.Equal(clientId, result!.ClientId);
    }

    [Fact]
    public void Map_PascalCaseClientIdKey_AsString_IsParsed()
    {
        var clientId = Guid.NewGuid();

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("engagement.started", new { ClientId = clientId.ToString("N") }));

        Assert.Equal(clientId, result!.ClientId);
    }

    // Non-GUID client ids are hashed to a stable GUID so the same client always maps to the same id.
    [Theory]
    [InlineData("clientId")]
    [InlineData("ClientId")]
    public void Map_NonGuidClientId_MapsToStableHashedGuid(string field)
    {
        static KafkaEnvelope Envelope(string field, string value) =>
            CreateEnvelope("engagement.started", new Dictionary<string, object> { [field] = value });

        var first = _mapper.MapToClientSafeMessage(Envelope(field, "client-42"))!.ClientId;
        var again = _mapper.MapToClientSafeMessage(Envelope(field, "client-42"))!.ClientId;
        var other = _mapper.MapToClientSafeMessage(Envelope(field, "client-43"))!.ClientId;

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("client-42"));
        Assert.Equal(new Guid(hash[..16]), first);
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("ClientId")]
    public void Map_BlankClientId_ResolvesToEmpty(string field)
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("engagement.started", new Dictionary<string, object> { [field] = "  " }));

        Assert.Equal(Guid.Empty, result!.ClientId);
    }

    [Fact]
    public void Map_MissingClientId_ResolvesToEmpty()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("engagement.started", new { title = "x" }));

        Assert.Equal(Guid.Empty, result!.ClientId);
    }

    [Fact]
    public void Map_LowercaseDataWrapper_IsUnwrapped()
    {
        var clientId = Guid.NewGuid();

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", new { data = new { clientId, documentName = "ID.pdf" } }));

        Assert.Equal(clientId, result!.ClientId);
        Assert.Equal("Document Verified: ID.pdf", result.Subject);
    }

    // A "Data"/"data" property that is not an object is not a wrapper: fields are read from the top level.
    [Theory]
    [InlineData("Data")]
    [InlineData("data")]
    public void Map_NonObjectDataProperty_ReadsTopLevelFields(string wrapperName)
    {
        var clientId = Guid.NewGuid();
        var payload = new Dictionary<string, object> { [wrapperName] = "not-an-object", ["clientId"] = clientId, ["documentName"] = "ID.pdf" };

        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", payload));

        Assert.Equal(clientId, result!.ClientId);
        Assert.Equal("Document Verified: ID.pdf", result.Subject);
    }

    [Fact]
    public void Map_NonObjectPayload_UsesDefaultsWithoutThrowing()
    {
        var result = _mapper.MapToClientSafeMessage(CreateEnvelope("document.verified", new[] { 1, 2, 3 }));

        Assert.NotNull(result);
        Assert.Equal(Guid.Empty, result!.ClientId);
        Assert.Null(result.ClientEmail);
        Assert.Equal("Document Verified: Your document", result.Subject);
    }

    #endregion
}
