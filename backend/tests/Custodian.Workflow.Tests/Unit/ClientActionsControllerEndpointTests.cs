using System.Security.Claims;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Stall;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// ClientActionsController branches not covered by <see cref="ClientActionsControllerTests"/>: the checklist
/// preview, stall status, and how each task endpoint maps a missing task or a service exception.
/// </summary>
public class ClientActionsControllerEndpointTests
{
    private const string Tenant = "tenant-a";
    private static readonly Guid EngagementId = Guid.Parse("12345678-0000-0000-0000-000000000001");
    private static readonly Guid ActionId = Guid.Parse("12345678-0000-0000-0000-000000000002");
    private static readonly string MissingAction =
        $"Action '{ActionId}' was not found for engagement '{EngagementId}' and tenant '{Tenant}'.";

    private readonly Mock<IClientActionService> _actions = new();
    private readonly Mock<IStallActionsProvider> _stallActions = new();
    private readonly Mock<IStallRecorder> _stallRecorder = new();
    private readonly Mock<IEngagementRepository> _engagements = new();
    private readonly Mock<IDocumentComplianceClient> _documents = new();

    public ClientActionsControllerEndpointTests()
    {
        WithEngagement(EngagementStatus.Started);
        _actions.Setup(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-1")).ReturnsAsync(true);
    }

    private void WithEngagement(EngagementStatus? status) =>
        _engagements.Setup(r => r.GetByIdAsync(EngagementId, Tenant))
            .ReturnsAsync(status is null ? null : new Engagement { EngagementId = EngagementId, TenantId = Tenant, Status = status.Value });

    private ClientActionsController Controller(params Claim[] claims) =>
        new(_actions.Object, _stallActions.Object, _stallRecorder.Object, _documents.Object,
            _engagements.Object, NullLogger<ClientActionsController>.Instance)
        { ControllerContext = ContextFor(claims) };

    private ClientActionsController Staff() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1"));

    private ClientActionsController Client() =>
        Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), new Claim("client_id", "client-1"));

    // ==========================================
    // Standard checklist preview
    // ==========================================

    [Fact]
    public async Task Preview_ReturnsTheTasksThatWouldBeAdded()
    {
        var preview = new List<ClientActionResponseDto> { new() };
        _actions.Setup(s => s.PreviewStandardChecklistAsync(EngagementId, Tenant)).ReturnsAsync(preview);

        var result = await Staff().PreviewStandardChecklist(EngagementId, null);

        Assert.Same(preview, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Preview_UnknownEngagement_IsNotFoundWithMessage()
    {
        var result = await Staff().PreviewStandardChecklist(EngagementId, null);

        Assert.Equal($"Engagement '{EngagementId}' was not found for tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Preview_InvalidOperation_IsConflict()
    {
        _actions.Setup(s => s.PreviewStandardChecklistAsync(EngagementId, Tenant)).ThrowsAsync(new InvalidOperationException("Engagement is closed."));

        var result = await Staff().PreviewStandardChecklist(EngagementId, null);

        Assert.Equal("Engagement is closed.", Message(Assert.IsType<ConflictObjectResult>(result.Result)));
    }

    [Fact]
    public async Task ApplyChecklist_UnknownEngagement_HasMessage()
    {
        var result = await Staff().ApplyStandardChecklist(EngagementId, null);

        Assert.Equal($"Engagement '{EngagementId}' was not found for tenant '{Tenant}'.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task ApplyChecklist_InvalidOperation_IsConflict()
    {
        _actions.Setup(s => s.ApplyStandardChecklistAsync(EngagementId, Tenant, "staff-1")).ThrowsAsync(new InvalidOperationException("Not now."));

        var result = await Staff().ApplyStandardChecklist(EngagementId, null);

        Assert.Equal("Not now.", Message(Assert.IsType<ConflictObjectResult>(result.Result)));
    }

    // ==========================================
    // Stall status
    // ==========================================

    [Fact]
    public async Task StallStatus_SyncsTheEngagement_AndReturnsItsStatus()
    {
        var actions = new List<ClientAction> { new() { EngagementId = EngagementId, TenantId = Tenant } };
        _stallActions.Setup(p => p.GetRawActionsAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StallActionsResult("client-1", actions));
        var status = new StallStatusDto();
        IReadOnlyList<StallSyncInput>? synced = null;
        _stallRecorder.Setup(r => r.SyncAsync(Tenant, It.IsAny<IReadOnlyList<StallSyncInput>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyList<StallSyncInput> inputs, CancellationToken _) => synced = inputs)
            .ReturnsAsync(new Dictionary<Guid, StallSnapshot> { [EngagementId] = new(status, null, 0) });

        var result = await Staff().GetStallStatus(EngagementId, null);

        Assert.Same(status, Assert.IsType<OkObjectResult>(result.Result).Value);
        var input = Assert.Single(synced!);
        Assert.Equal(EngagementId, input.EngagementId);
        Assert.Equal("client-1", input.ClientId);
        Assert.Same(actions, input.Actions);
    }

    [Fact]
    public async Task StallStatus_UnknownEngagement_IsNotFoundWithMessage()
    {
        var result = await Staff().GetStallStatus(EngagementId, null);

        Assert.Equal($"Engagement '{EngagementId}' was not found.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
        _stallRecorder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StallStatus_CrossTenant_IsForbidden()
    {
        var result = await Staff().GetStallStatus(EngagementId, "tenant-b");

        Assert.IsType<ForbidResult>(result.Result);
        _stallActions.VerifyNoOtherCalls();
    }

    // ==========================================
    // Missing task and service errors, per endpoint
    // ==========================================

    [Fact]
    public async Task TaskEndpoints_MissingTask_AreNotFoundWithMessage()
    {
        var c = Staff();

        Assert.Equal(MissingAction, Message((await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result));
        Assert.Equal(MissingAction, Message((await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result));
        Assert.Equal(MissingAction, Message((await c.CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null)).Result));
        Assert.Equal(MissingAction, Message((await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result));
        Assert.Equal(MissingAction, Message((await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result));
        Assert.Equal(MissingAction, Message((await c.ApplyVerification(EngagementId, ActionId, new ApplyActionVerificationDto(), null)).Result));
    }

    public static TheoryData<Exception, Type> TaskErrors => new()
    {
        { new ArgumentException("Bad value."), typeof(BadRequestObjectResult) },
        { new InvalidOperationException("Wrong state."), typeof(ConflictObjectResult) }
    };

    [Theory]
    [MemberData(nameof(TaskErrors))]
    public async Task TaskEndpoints_MapServiceExceptions(Exception error, Type expected)
    {
        _actions.Setup(s => s.UpdateActionAsync(EngagementId, ActionId, Tenant, It.IsAny<UpdateClientActionDto>(), It.IsAny<string>())).ThrowsAsync(error);
        _actions.Setup(s => s.CancelActionAsync(EngagementId, ActionId, Tenant, It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(error);
        _actions.Setup(s => s.UploadEvidenceAsync(EngagementId, ActionId, Tenant, It.IsAny<UploadActionEvidenceDto>())).ThrowsAsync(error);
        _actions.Setup(s => s.ReviewActionAsync(EngagementId, ActionId, Tenant, It.IsAny<ReviewActionDto>())).ThrowsAsync(error);
        _actions.Setup(s => s.CreateActionAsync(EngagementId, Tenant, It.IsAny<CreateClientActionDto>())).ThrowsAsync(error);
        var c = Staff();

        var results = new IActionResult?[]
        {
            (await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result,
            (await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result,
            (await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result,
            (await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result,
            (await c.CreateAction(EngagementId, new CreateClientActionDto(), null)).Result
        };

        Assert.All(results, r =>
        {
            Assert.IsType(expected, r);
            Assert.Equal(error.Message, Message(r));
        });
    }

    [Fact]
    public async Task Create_KeyNotFound_IsNotFoundWithMessage()
    {
        _actions.Setup(s => s.CreateActionAsync(EngagementId, Tenant, It.IsAny<CreateClientActionDto>()))
            .ThrowsAsync(new KeyNotFoundException("Stage 9 does not exist."));

        var result = await Staff().CreateAction(EngagementId, new CreateClientActionDto(), null);

        Assert.Equal("Stage 9 does not exist.", Message(Assert.IsType<NotFoundObjectResult>(result.Result)));
    }

    [Fact]
    public async Task TaskEndpoints_ReturnTheServiceResult_WithTheJwtActor()
    {
        var updated = new ClientActionResponseDto();
        var cancelled = new ClientActionResponseDto();
        var reviewed = new ClientActionResponseDto();
        var review = new ReviewActionDto { ReviewerActor = "from-body" };
        _actions.Setup(s => s.UpdateActionAsync(EngagementId, ActionId, Tenant, It.IsAny<UpdateClientActionDto>(), "staff-1")).ReturnsAsync(updated);
        _actions.Setup(s => s.CancelActionAsync(EngagementId, ActionId, Tenant, "No longer needed", "staff-1")).ReturnsAsync(cancelled);
        _actions.Setup(s => s.ReviewActionAsync(EngagementId, ActionId, Tenant, review)).ReturnsAsync(reviewed);

        Assert.Same(updated, Assert.IsType<OkObjectResult>((await Staff().UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result).Value);
        Assert.Same(cancelled, Assert.IsType<OkObjectResult>((await Staff().CancelAction(EngagementId, ActionId, new CancelClientActionDto { Reason = "No longer needed" }, null)).Result).Value);
        Assert.Same(reviewed, Assert.IsType<OkObjectResult>((await Staff().ReviewAction(EngagementId, ActionId, review, null)).Result).Value);
        Assert.Equal("staff-1", review.ReviewerActor);
    }

    [Fact]
    public async Task TaskEndpoints_WithoutJwtActor_DefaultToStaff_OrKeepTheBodyValue()
    {
        var anonymous = () => Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"));
        var review = new ReviewActionDto { ReviewerActor = "from-body" };
        var upload = new UploadActionEvidenceDto { UploaderActor = "uploader" };

        await anonymous().UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null);
        await anonymous().CancelAction(EngagementId, ActionId, new CancelClientActionDto { Reason = "r" }, null);
        await anonymous().ReviewAction(EngagementId, ActionId, review, null);
        await anonymous().UploadEvidence(EngagementId, ActionId, upload, null);

        _actions.Verify(s => s.UpdateActionAsync(EngagementId, ActionId, Tenant, It.IsAny<UpdateClientActionDto>(), "Staff"), Times.Once);
        _actions.Verify(s => s.CancelActionAsync(EngagementId, ActionId, Tenant, "r", "Staff"), Times.Once);
        Assert.Equal("from-body", review.ReviewerActor);
        Assert.Equal("uploader", upload.UploaderActor);
    }

    [Fact]
    public async Task Create_And_ApplyVerification_TakeTheActorFromTheJwt_OrKeepTheBodyValue()
    {
        var created = new CreateClientActionDto { CreatedBy = "from-body" };
        var verified = new ApplyActionVerificationDto { VerifiedBy = "from-body" };
        _actions.Setup(s => s.CreateActionAsync(EngagementId, Tenant, It.IsAny<CreateClientActionDto>())).ReturnsAsync(new ClientActionResponseDto());

        await Staff().CreateAction(EngagementId, created, null);
        await Staff().ApplyVerification(EngagementId, ActionId, verified, null);
        Assert.Equal("staff-1", created.CreatedBy);
        Assert.Equal("staff-1", verified.VerifiedBy);

        var anonymous = () => Controller(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"));
        var created2 = new CreateClientActionDto { CreatedBy = "from-body" };
        var verified2 = new ApplyActionVerificationDto { VerifiedBy = "from-body" };
        await anonymous().CreateAction(EngagementId, created2, null);
        await anonymous().ApplyVerification(EngagementId, ActionId, verified2, null);
        Assert.Equal("from-body", created2.CreatedBy);
        Assert.Equal("from-body", verified2.VerifiedBy);
    }

    [Fact]
    public async Task ApplyVerification_InvalidOperation_IsConflict_AndNoTenantHasMessage()
    {
        _actions.Setup(s => s.ApplyVerificationOutcomeAsync(EngagementId, ActionId, Tenant, It.IsAny<ApplyActionVerificationDto>()))
            .ThrowsAsync(new InvalidOperationException("Already verified."));

        var conflict = await Staff().ApplyVerification(EngagementId, ActionId, new ApplyActionVerificationDto(), null);
        Assert.Equal("Already verified.", Message(Assert.IsType<ConflictObjectResult>(conflict.Result)));

        var noTenant = await Controller(new Claim(ClaimTypes.Role, "Staff")).ApplyVerification(EngagementId, ActionId, new ApplyActionVerificationDto(), null);
        Assert.Equal("Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter.", Message(noTenant.Result));
    }

    [Fact]
    public async Task TaskEndpoints_InvalidModel_AreBadRequest_AndCallNothing()
    {
        var c = Staff();
        c.ModelState.AddModelError("x", "invalid");

        Assert.IsType<BadRequestObjectResult>((await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.ApplyVerification(EngagementId, ActionId, new ApplyActionVerificationDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.CreateAction(EngagementId, new CreateClientActionDto(), null)).Result);
        _actions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "Engagement is closed and cannot accept this change.")]
    [InlineData(EngagementStatus.Cancelled, "Engagement is cancelled and cannot accept this change.")]
    public async Task TaskEndpoints_OnTerminalEngagement_AreConflict(EngagementStatus status, string message)
    {
        WithEngagement(status);
        var c = Staff();

        var results = new IActionResult?[]
        {
            (await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result,
            (await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result,
            (await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result,
            (await c.ApplyVerification(EngagementId, ActionId, new ApplyActionVerificationDto(), null)).Result,
            (await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result,
            (await c.CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null)).Result,
            (await c.ApplyStandardChecklist(EngagementId, null)).Result,
            (await c.CreateAction(EngagementId, new CreateClientActionDto(), null)).Result
        };

        Assert.All(results, r => Assert.Equal(message, Message(Assert.IsType<ConflictObjectResult>(r))));
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TaskEndpoints_OnMissingEngagement_AreNotFound()
    {
        WithEngagement(null);
        var c = Staff();

        Assert.IsType<NotFoundResult>((await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result);
        Assert.IsType<NotFoundResult>((await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result);
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TaskEndpoints_NoTenant_AreBadRequest()
    {
        var c = Controller(new Claim(ClaimTypes.Role, "Staff"));
        const string required = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter.";

        Assert.Equal(required, Message((await c.GetActionHistory(EngagementId, null, null, null)).Result));
        Assert.Equal(required, Message((await c.CreateAction(EngagementId, new CreateClientActionDto(), null)).Result));
        Assert.Equal(required, Message((await c.ApplyStandardChecklist(EngagementId, null)).Result));
        Assert.Equal(required, Message((await c.UpdateAction(EngagementId, ActionId, new UpdateClientActionDto(), null)).Result));
        Assert.Equal(required, Message((await c.CancelAction(EngagementId, ActionId, new CancelClientActionDto(), null)).Result));
        Assert.Equal(required, Message((await c.CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null)).Result));
        Assert.Equal(required, Message((await c.UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null)).Result));
        Assert.Equal(required, Message((await c.ReviewAction(EngagementId, ActionId, new ReviewActionDto(), null)).Result));
        _actions.VerifyNoOtherCalls();
    }

    // ==========================================
    // Evidence upload: document outcomes come from Documents
    // ==========================================

    private static readonly Guid DocumentId = Guid.Parse("12345678-0000-0000-0000-0000000000d0");

    private void CaptureUpload() =>
        _actions.Setup(s => s.UploadEvidenceAsync(EngagementId, ActionId, Tenant, It.IsAny<UploadActionEvidenceDto>()))
            .ReturnsAsync(new ClientActionResponseDto());

    private void DocumentsReturn(params DocumentSummaryDto[] documents) =>
        _documents.Setup(d => d.GetDocumentsAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(documents);

    [Theory]
    [InlineData("Verified")]
    [InlineData("rejected")]
    public async Task Upload_StaffDecisionRecordedInDocuments_IsCopied(string verification)
    {
        DocumentsReturn(new DocumentSummaryDto { DocumentId = DocumentId, ComplianceStatus = "Compliant", VerificationStatus = verification });
        var dto = new UploadActionEvidenceDto { DocumentId = DocumentId, VerificationStatus = "spoofed" };
        CaptureUpload();

        await Staff().UploadEvidence(EngagementId, ActionId, dto, null);

        Assert.Equal(verification, dto.VerificationStatus);
        Assert.Equal("Compliant", dto.ComplianceStatus);
    }

    [Fact]
    public async Task Upload_FreshUploadInDocuments_StaysAwaitingReview()
    {
        DocumentsReturn(new DocumentSummaryDto { DocumentId = DocumentId, ComplianceStatus = "Compliant", VerificationStatus = "Unverified" });
        var dto = new UploadActionEvidenceDto { DocumentId = DocumentId, VerificationStatus = "Verified", VerificationReason = "x", VerifiedBy = "me" };
        CaptureUpload();

        await Staff().UploadEvidence(EngagementId, ActionId, dto, null);

        Assert.Null(dto.VerificationStatus);
        Assert.Null(dto.VerificationReason);
        Assert.Null(dto.VerifiedBy);
    }

    [Fact]
    public async Task Upload_WithoutDocument_ClearsCallerSuppliedOutcomes_WithoutAskingDocuments()
    {
        var dto = new UploadActionEvidenceDto { ComplianceStatus = "Compliant", RejectionReason = "r", VerificationStatus = "Verified" };
        CaptureUpload();

        await Staff().UploadEvidence(EngagementId, ActionId, dto, null);

        Assert.Null(dto.ComplianceStatus);
        Assert.Null(dto.RejectionReason);
        Assert.Null(dto.VerificationStatus);
        _documents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Upload_DeletedOrUnknownDocument_IsBadRequestWithMessage()
    {
        DocumentsReturn(new DocumentSummaryDto { DocumentId = DocumentId, IsDeleted = true });

        var result = await Staff().UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto { DocumentId = DocumentId }, null);

        Assert.Equal($"Document '{DocumentId}' was not found for engagement '{EngagementId}'.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
        _actions.Verify(s => s.UploadEvidenceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UploadActionEvidenceDto>()), Times.Never);
    }

    [Fact]
    public async Task Upload_DocumentsUnavailable_Is503WithMessage()
    {
        _documents.Setup(d => d.GetDocumentsAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentComplianceUnavailableException("down"));

        var result = await Staff().UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto { DocumentId = DocumentId }, null);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("Document status is temporarily unavailable. Please try again shortly.", Message(unavailable));
    }

    // ==========================================
    // Client callers
    // ==========================================

    [Fact]
    public async Task Client_CompletingATaskWhoseStageHasNotStarted_IsConflictWithReason()
    {
        _actions.Setup(s => s.GetClientAvailabilityBlockReasonAsync(EngagementId, ActionId, Tenant)).ReturnsAsync("Stage 2 has not started yet.");

        var result = await Client().CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null);

        Assert.Equal("Stage 2 has not started yet.", Message(Assert.IsType<ConflictObjectResult>(result.Result)));
        _actions.Verify(s => s.CompleteActionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CompleteClientActionDto>()), Times.Never);
    }

    [Fact]
    public async Task Client_UploadingBeforeTheStageStarts_IsConflictWithReason()
    {
        _actions.Setup(s => s.GetClientAvailabilityBlockReasonAsync(EngagementId, ActionId, Tenant)).ReturnsAsync("Stage 3 has not started yet.");

        var result = await Client().UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null);

        Assert.Equal("Stage 3 has not started yet.", Message(Assert.IsType<ConflictObjectResult>(result.Result)));
        _actions.Verify(s => s.UploadEvidenceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UploadActionEvidenceDto>()), Times.Never);
    }

    [Fact]
    public async Task Client_UploadingWhenAvailable_Succeeds()
    {
        var uploaded = new ClientActionResponseDto();
        _actions.Setup(s => s.UploadEvidenceAsync(EngagementId, ActionId, Tenant, It.IsAny<UploadActionEvidenceDto>())).ReturnsAsync(uploaded);

        var result = await Client().UploadEvidence(EngagementId, ActionId, new UploadActionEvidenceDto(), null);

        Assert.Same(uploaded, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Staff_CompletingATask_SkipsTheClientChecks()
    {
        var completed = new ClientActionResponseDto();
        _actions.Setup(s => s.CompleteActionAsync(EngagementId, ActionId, Tenant, It.IsAny<CompleteClientActionDto>())).ReturnsAsync(completed);

        var result = await Staff().CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null);

        Assert.Same(completed, Assert.IsType<OkObjectResult>(result.Result).Value);
        _actions.Verify(s => s.GetClientAvailabilityBlockReasonAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _actions.Verify(s => s.ClientOwnsEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Complete_ArgumentException_IsBadRequestWithMessage()
    {
        _actions.Setup(s => s.CompleteActionAsync(EngagementId, ActionId, Tenant, It.IsAny<CompleteClientActionDto>()))
            .ThrowsAsync(new ArgumentException("Requirement-backed."));

        var result = await Staff().CompleteAction(EngagementId, ActionId, new CompleteClientActionDto(), null);

        Assert.Equal("Requirement-backed.", Message(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task GetActionHistory_PassesTheStatusFilter_AndStaffView()
    {
        _actions.Setup(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, false, "Pending")).ReturnsAsync([new ClientActionResponseDto()]);

        var result = await Staff().GetActionHistory(EngagementId, null, "Pending", isClientView: false);

        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientActionResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task GetActionHistory_StaffWithoutViewRequest_GetsTheClientView()
    {
        await Staff().GetActionHistory(EngagementId, null, null, isClientView: null);

        _actions.Verify(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, true, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_StaffRequestingClientView_GetsTheClientView()
    {
        await Staff().GetActionHistory(EngagementId, null, null, isClientView: true);

        _actions.Verify(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, true, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_XClientViewHeaderTrue_KeepsTheClientView()
    {
        var c = Staff();
        c.HttpContext.Request.Headers["X-Client-View"] = "true";

        await c.GetActionHistory(EngagementId, null, null, isClientView: null);

        _actions.Verify(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, true, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_XClientViewHeaderFalse_GivesStaffTheStaffView()
    {
        var c = Staff();
        c.HttpContext.Request.Headers["X-Client-View"] = "false";

        await c.GetActionHistory(EngagementId, null, null, isClientView: null);

        _actions.Verify(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, false, null), Times.Once);
    }

    [Fact]
    public async Task GetActionHistory_UnparseableXClientViewHeader_IsIgnored()
    {
        var c = Staff();
        c.HttpContext.Request.Headers["X-Client-View"] = "nope";

        await c.GetActionHistory(EngagementId, null, null, isClientView: null);

        _actions.Verify(s => s.GetActionsByEngagementAsync(EngagementId, Tenant, true, null), Times.Once);
    }
}
