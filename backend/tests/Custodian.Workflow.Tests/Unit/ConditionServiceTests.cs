using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ConditionServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    private static async Task<Engagement> SeedEngagementAsync(
        WorkflowDbContext db,
        Guid engagementId,
        string tenantId = "tenant-001",
        string clientId = "client-001",
        EngagementStatus status = EngagementStatus.Started,
        EngagementStage stage = EngagementStage.Onboarding)
    {
        var eng = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-001",
            Status = status,
            Stage = stage,
            CreatedAt = DateTime.UtcNow
        };
        db.Engagements.Add(eng);
        await db.SaveChangesAsync();
        return eng;
    }

    [Fact]
    public async Task AttachConditionAsync_ValidApproval_PersistsAndCreatesLinkedAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId, stage: EngagementStage.Onboarding);

        var mockClientActionService = new Mock<IClientActionService>();
        var mockAudit = new Mock<IAuditPublisher>();
        var service = new ConditionService(db, mockClientActionService.Object, mockAudit.Object, NullLogger<ConditionService>.Instance);

        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Scope approval",
            Description = "Client signs off on proposal",
            RequiredBeforeStage = EngagementStage.Execution,
            DueDateUtc = DateTime.UtcNow.AddDays(7),
            InternalNote = "Confidential margin details"
        };

        // Act
        var result = await service.AttachConditionAsync(engagementId, tenantId, dto, "staff-user-1");

        // Assert: Condition persisted
        Assert.NotNull(result);
        Assert.Equal(ConditionType.Approval, result.Type);
        Assert.Equal("Scope approval", result.Title);
        Assert.True(result.IsActive);
        Assert.Equal(ConditionStatus.Pending, result.Status);
        Assert.Equal("Pending", result.ApprovalStatus);
        Assert.Equal("client-001", result.TargetClientId);
        Assert.Equal("Execution", result.RequiredBeforeStage);
        Assert.Equal("Confidential margin details", result.InternalNote);

        var stored = await db.EngagementConditions.FirstOrDefaultAsync(c => c.ConditionId == result.ConditionId);
        Assert.NotNull(stored);
        Assert.Equal(tenantId, stored!.TenantId);

        // Assert: Linked ClientAction created with StageNumber = 3 (Verification)
        mockClientActionService.Verify(c => c.CreateLinkedActionAsync(
            engagementId,
            tenantId,
            ClientActionSourceType.Condition,
            result.ConditionId,
            "Scope approval",
            "Client signs off on proposal",
            ClientActionType.Approval,
            3,
            dto.DueDateUtc,
            "Client",
            false,
            null), Times.Once);

        // Assert: Audit event published once with clientId and without internal note
        mockAudit.Verify(a => a.PublishEventAsync(
            engagementId,
            tenantId,
            "staff-user-1",
            "ConditionAttached",
            It.Is<object>(p => p.ToString()!.Contains("clientId") && !p.ToString()!.Contains("Confidential margin details"))), Times.Once);
    }

    [Fact]
    public async Task AttachConditionAsync_DuplicateActiveCondition_ThrowsInvalidOperationException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "First approval",
            RequiredBeforeStage = EngagementStage.Execution
        };

        await service.AttachConditionAsync(engagementId, tenantId, dto, "staff-1");

        // Act & Assert: attaching second active Approval condition throws 409 conflict
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Second approval",
                RequiredBeforeStage = EngagementStage.Execution
            }, "staff-1"));

        Assert.Equal("An active Approval condition already exists for this engagement.", ex.Message);
    }

    [Fact]
    public async Task AttachConditionAsync_ApprovalAndPaymentTogether_BothSucceed()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        // Act: attach Approval
        var approval = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Approval sign-off",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act: attach Payment
        var payment = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Upfront Retainer",
            Amount = 50000m,
            Currency = "LKR",
            PaymentType = ConditionPaymentType.Upfront,
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Assert: both active
        Assert.True(approval.IsActive);
        Assert.True(payment.IsActive);
        var count = await db.EngagementConditions.CountAsync(c => c.EngagementId == engagementId && c.IsActive);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task AttachConditionAsync_DeactivateThenReattach_Succeeds()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var first = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Initial approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        await service.DeactivateConditionAsync(engagementId, first.ConditionId, tenantId, new DeactivateConditionDto { Reason = "Replaced with revised scope" }, "staff-1");

        // Act: re-attaching Approval after deactivation succeeds
        var second = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Revised scope approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Assert
        Assert.True(second.IsActive);
        var activeCount = await db.EngagementConditions.CountAsync(c => c.EngagementId == engagementId && c.IsActive && c.Type == ConditionType.Approval);
        Assert.Equal(1, activeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-150)]
    public async Task AttachConditionAsync_PaymentWithoutPositiveAmount_ThrowsArgumentException(int? amountVal)
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var dto = new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Deposit",
            Amount = amountVal.HasValue ? (decimal)amountVal.Value : null,
            Currency = "USD",
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.AttachConditionAsync(engagementId, tenantId, dto, "staff-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("US")]
    [InlineData("USDT")]
    public async Task AttachConditionAsync_PaymentWithoutValid3LetterCurrency_ThrowsArgumentException(string? currency)
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var dto = new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Deposit",
            Amount = 1000m,
            Currency = currency,
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.AttachConditionAsync(engagementId, tenantId, dto, "staff-1"));
    }

    [Fact]
    public async Task AttachConditionAsync_ClosedEngagement_ThrowsInvalidOperationException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId, status: EngagementStatus.Closed);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Sign off",
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachConditionAsync(engagementId, tenantId, dto, "staff-1"));
        Assert.Contains("Cannot attach conditions to an engagement with status 'Closed'", ex.Message);
    }

    [Fact]
    public async Task AttachConditionAsync_RequiredBeforeStageNotGreaterThanCurrentStage_ThrowsArgumentException()
    {
        // Arrange: engagement is currently in Verification (stage 2)
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId, stage: EngagementStage.Verification);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        // RequiredBeforeStage is Verification (not > current stage)
        var dto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Past stage sign-off",
            RequiredBeforeStage = EngagementStage.Verification
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.AttachConditionAsync(engagementId, tenantId, dto, "staff-1"));
    }

    [Fact]
    public async Task UpdateConditionAsync_PendingCondition_UpdatesConfigurableFieldsAndPublishesEvent()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var mockAudit = new Mock<IAuditPublisher>();
        var service = new ConditionService(db, new Mock<IClientActionService>().Object, mockAudit.Object, NullLogger<ConditionService>.Instance);

        var created = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Milestone payment",
            Amount = 1000m,
            Currency = "USD",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act: update while Pending
        var updated = await service.UpdateConditionAsync(engagementId, created.ConditionId, tenantId, new UpdateConditionDto
        {
            Title = "Updated Milestone Payment",
            Amount = 1500m,
            Currency = "EUR",
            PaymentType = ConditionPaymentType.Milestone
        }, "staff-editor");

        // Assert
        Assert.Equal("Updated Milestone Payment", updated.Title);
        Assert.Equal(1500m, updated.Amount);
        Assert.Equal("EUR", updated.Currency);
        Assert.Equal("staff-editor", updated.UpdatedBy);

        mockAudit.Verify(a => a.PublishEventAsync(
            engagementId,
            tenantId,
            "staff-editor",
            "ConditionUpdated",
            It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task UpdateConditionAsync_SatisfiedOrInactive_ThrowsInvalidOperationException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var created = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Deactivate condition
        await service.DeactivateConditionAsync(engagementId, created.ConditionId, tenantId, new DeactivateConditionDto { Reason = "Cancelled" }, "staff-1");

        // Act & Assert: cannot update inactive condition
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateConditionAsync(engagementId, created.ConditionId, tenantId, new UpdateConditionDto { Title = "New Title" }, "staff-1"));

        Assert.Contains("can only be updated while active and pending", ex.Message);
    }

    [Fact]
    public async Task DeactivateConditionAsync_ActiveCondition_CancelsActionsAndPublishesEvent()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var mockClientAction = new Mock<IClientActionService>();
        var mockAudit = new Mock<IAuditPublisher>();
        var service = new ConditionService(db, mockClientAction.Object, mockAudit.Object, NullLogger<ConditionService>.Instance);

        var condition = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Approval to cancel",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act
        var result = await service.DeactivateConditionAsync(engagementId, condition.ConditionId, tenantId, new DeactivateConditionDto
        {
            Reason = "No longer required by customer"
        }, "staff-lead");

        // Assert
        Assert.False(result.IsActive);
        Assert.Equal("staff-lead", result.DeactivatedBy);
        Assert.Equal("No longer required by customer", result.DeactivationReason);

        mockClientAction.Verify(c => c.CancelActionsForSourceAsync(
            engagementId,
            tenantId,
            ClientActionSourceType.Condition,
            condition.ConditionId,
            "staff-lead",
            "No longer required by customer"), Times.Once);

        mockAudit.Verify(a => a.PublishEventAsync(
            engagementId,
            tenantId,
            "staff-lead",
            "ConditionDeactivated",
            It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task DeactivateConditionAsync_AlreadyInactive_IsIdempotent()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var mockClientAction = new Mock<IClientActionService>();
        var mockAudit = new Mock<IAuditPublisher>();
        var service = new ConditionService(db, mockClientAction.Object, mockAudit.Object, NullLogger<ConditionService>.Instance);

        var condition = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        await service.DeactivateConditionAsync(engagementId, condition.ConditionId, tenantId, new DeactivateConditionDto { Reason = "Reason 1" }, "staff-1");
        mockAudit.Invocations.Clear();
        mockClientAction.Invocations.Clear();

        // Act: second call
        var result = await service.DeactivateConditionAsync(engagementId, condition.ConditionId, tenantId, new DeactivateConditionDto { Reason = "Reason 2" }, "staff-1");

        // Assert: idempotent — returns 200/result without calling cancel actions or publishing event again
        Assert.False(result.IsActive);
        mockClientAction.Verify(c => c.CancelActionsForSourceAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockAudit.Verify(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "ConditionDeactivated", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task GetConditionsClientAsync_EnforcesOwnershipAndHidesInternalNote()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var mockClientAction = new Mock<IClientActionService>();
        mockClientAction.Setup(c => c.ClientOwnsEngagementAsync(engagementId, tenantId, "client-owner"))
            .ReturnsAsync(true);
        mockClientAction.Setup(c => c.ClientOwnsEngagementAsync(engagementId, tenantId, "client-imposter"))
            .ReturnsAsync(false);

        var service = new ConditionService(db, mockClientAction.Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Client Approval",
            InternalNote = "Top secret internal staff note",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act 1: Owner client
        var ownerResults = (await service.GetConditionsClientAsync(engagementId, tenantId, "client-owner")).ToList();

        // Assert
        Assert.Single(ownerResults);
        Assert.Equal("Client Approval", ownerResults[0].Title);

        // Act 2: Imposter client
        var imposterResults = (await service.GetConditionsClientAsync(engagementId, tenantId, "client-imposter")).ToList();
        Assert.Empty(imposterResults);
    }

    [Fact]
    public async Task GetActiveConditionsAsync_ReturnsOnlyActiveOrderedByStage()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await SeedEngagementAsync(db, engagementId, tenantId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        // Stage 4 (Closure)
        await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Final Payment",
            Amount = 1000m,
            Currency = "USD",
            RequiredBeforeStage = EngagementStage.Closure
        }, "staff-1");

        // Stage 3 (Execution)
        var approval = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Scope Approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Deactivated condition
        await service.DeactivateConditionAsync(engagementId, approval.ConditionId, tenantId, new DeactivateConditionDto { Reason = "Discard" }, "staff-1");

        // Act
        var active = await service.GetActiveConditionsAsync(engagementId, tenantId);

        // Assert: only 1 active condition returned, ordered by RequiredBeforeStage
        Assert.Single(active);
        Assert.Equal("Final Payment", active[0].Title);
        Assert.Equal(EngagementStage.Closure, active[0].RequiredBeforeStage);
    }

    [Theory]
    [InlineData(EngagementStage.DocumentCollection, 1)]
    [InlineData(EngagementStage.Verification, 2)]
    [InlineData(EngagementStage.Execution, 3)]
    [InlineData(EngagementStage.Closure, 4)]
    public void MapRequiredBeforeStageToStageNumber_VerifiesPrecedingStageMapping(EngagementStage stage, int expectedStageNumber)
    {
        var mapped = ConditionService.MapRequiredBeforeStageToStageNumber(stage);
        Assert.Equal(expectedStageNumber, mapped);
    }

    // =========================================================================
    // CSTD-142: Approval State Model & Target Client Tests
    // =========================================================================

    [Fact]
    public async Task CSTD142_StaffApprovalResponse_ContainsTargetClientIdAndPendingApprovalStatus()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-cstd142";
        var expectedClientId = "client-target-999";
        await SeedEngagementAsync(db, engagementId, tenantId, clientId: expectedClientId);

        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var attachDto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Contract Signoff",
            RequiredBeforeStage = EngagementStage.Execution,
            DueDateUtc = DateTime.UtcNow.AddDays(5)
        };

        // Act 1: AttachConditionAsync
        var attachResult = await service.AttachConditionAsync(engagementId, tenantId, attachDto, "staff-1");

        // Assert 1: AttachConditionAsync returns TargetClientId and ApprovalStatus
        Assert.Equal(expectedClientId, attachResult.TargetClientId);
        Assert.Equal(ConditionStatus.Pending, attachResult.Status);
        Assert.Equal("Pending", attachResult.ApprovalStatus);

        // Act 2: GetConditionByIdStaffAsync
        var byIdResult = await service.GetConditionByIdStaffAsync(engagementId, attachResult.ConditionId, tenantId);
        Assert.NotNull(byIdResult);
        Assert.Equal(expectedClientId, byIdResult!.TargetClientId);
        Assert.Equal(ConditionStatus.Pending, byIdResult.Status);
        Assert.Equal("Pending", byIdResult.ApprovalStatus);

        // Act 3: GetConditionsStaffAsync
        var listResult = (await service.GetConditionsStaffAsync(engagementId, tenantId)).ToList();
        Assert.Single(listResult);
        Assert.Equal(expectedClientId, listResult[0].TargetClientId);
        Assert.Equal(ConditionStatus.Pending, listResult[0].Status);
        Assert.Equal("Pending", listResult[0].ApprovalStatus);

        // Act 4: UpdateConditionAsync
        var updateResult = await service.UpdateConditionAsync(engagementId, attachResult.ConditionId, tenantId,
            new UpdateConditionDto { Title = "Updated Signoff" }, "staff-1");
        Assert.Equal(expectedClientId, updateResult.TargetClientId);
        Assert.Equal(ConditionStatus.Pending, updateResult.Status);
        Assert.Equal("Pending", updateResult.ApprovalStatus);

        // Act 5: DeactivateConditionAsync
        var deactResult = await service.DeactivateConditionAsync(engagementId, attachResult.ConditionId, tenantId,
            new DeactivateConditionDto { Reason = "No longer needed" }, "staff-1");
        Assert.Equal(expectedClientId, deactResult.TargetClientId);
        Assert.Equal("Pending", deactResult.ApprovalStatus);
    }

    [Fact]
    public async Task CSTD142_ApprovalStateModel_Satisfied_MapsRawSatisfiedAndApprovedStatus()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-cstd142";
        var clientId = "client-cstd142";
        await SeedEngagementAsync(db, engagementId, tenantId, clientId: clientId);

        var mockClientAction = new Mock<IClientActionService>();
        mockClientAction.Setup(c => c.ClientOwnsEngagementAsync(engagementId, tenantId, clientId))
            .ReturnsAsync(true);

        var service = new ConditionService(db, mockClientAction.Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var condition = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "KYC Approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act: Transition to Satisfied
        await service.SetConditionStatusAsync(condition.ConditionId, tenantId, ConditionStatus.Satisfied, "staff-1");

        // Staff read
        var staffResult = await service.GetConditionByIdStaffAsync(engagementId, condition.ConditionId, tenantId);
        Assert.NotNull(staffResult);
        Assert.Equal(ConditionStatus.Satisfied, staffResult!.Status); // raw status remains Satisfied
        Assert.Equal("Approved", staffResult.ApprovalStatus);         // business approval status is Approved
        Assert.Equal(clientId, staffResult.TargetClientId);

        // Client read
        var clientResult = await service.GetConditionByIdClientAsync(engagementId, condition.ConditionId, tenantId, clientId);
        Assert.NotNull(clientResult);
        Assert.Equal(ConditionStatus.Satisfied, clientResult!.Status); // raw status remains Satisfied
        Assert.Equal("Approved", clientResult.ApprovalStatus);         // client-safe approval status is Approved

        var clientList = (await service.GetConditionsClientAsync(engagementId, tenantId, clientId)).ToList();
        Assert.Single(clientList);
        Assert.Equal(ConditionStatus.Satisfied, clientList[0].Status);
        Assert.Equal("Approved", clientList[0].ApprovalStatus);
    }

    [Fact]
    public async Task CSTD142_ApprovalStateModel_Rejected_MapsRawRejectedAndRejectedStatus()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-cstd142";
        var clientId = "client-cstd142";
        await SeedEngagementAsync(db, engagementId, tenantId, clientId: clientId);

        var mockClientAction = new Mock<IClientActionService>();
        mockClientAction.Setup(c => c.ClientOwnsEngagementAsync(engagementId, tenantId, clientId))
            .ReturnsAsync(true);

        var service = new ConditionService(db, mockClientAction.Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var condition = await service.AttachConditionAsync(engagementId, tenantId, new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Document Approval",
            RequiredBeforeStage = EngagementStage.Execution
        }, "staff-1");

        // Act: Transition to Rejected
        await service.SetConditionStatusAsync(condition.ConditionId, tenantId, ConditionStatus.Rejected, "client-1", "Missing required seal");

        // Staff read
        var staffResult = await service.GetConditionByIdStaffAsync(engagementId, condition.ConditionId, tenantId);
        Assert.NotNull(staffResult);
        Assert.Equal(ConditionStatus.Rejected, staffResult!.Status);
        Assert.Equal("Rejected", staffResult.ApprovalStatus);
        Assert.Equal(clientId, staffResult.TargetClientId);

        // Client read
        var clientResult = await service.GetConditionByIdClientAsync(engagementId, condition.ConditionId, tenantId, clientId);
        Assert.NotNull(clientResult);
        Assert.Equal(ConditionStatus.Rejected, clientResult!.Status);
        Assert.Equal("Rejected", clientResult.ApprovalStatus);
    }

    [Fact]
    public async Task CSTD142_PaymentCondition_HasNullApprovalStatus_ForStaffAndClient()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-cstd142";
        var clientId = "client-pay";
        await SeedEngagementAsync(db, engagementId, tenantId, clientId: clientId);

        var mockClientAction = new Mock<IClientActionService>();
        mockClientAction.Setup(c => c.ClientOwnsEngagementAsync(engagementId, tenantId, clientId))
            .ReturnsAsync(true);

        var service = new ConditionService(db, mockClientAction.Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        var attachDto = new AttachConditionDto
        {
            Type = ConditionType.Payment,
            Title = "Deposit Payment",
            Amount = 500m,
            Currency = "USD",
            PaymentType = ConditionPaymentType.Upfront,
            RequiredBeforeStage = EngagementStage.Execution
        };

        // Act: Attach Payment
        var attachResult = await service.AttachConditionAsync(engagementId, tenantId, attachDto, "staff-1");

        // Assert: ApprovalStatus is null, TargetClientId is populated
        Assert.Equal(ConditionType.Payment, attachResult.Type);
        Assert.Null(attachResult.ApprovalStatus);
        Assert.Equal(clientId, attachResult.TargetClientId);

        // Staff read
        var staffResult = await service.GetConditionByIdStaffAsync(engagementId, attachResult.ConditionId, tenantId);
        Assert.NotNull(staffResult);
        Assert.Null(staffResult!.ApprovalStatus);
        Assert.Equal(clientId, staffResult.TargetClientId);

        // Client read
        var clientResult = await service.GetConditionByIdClientAsync(engagementId, attachResult.ConditionId, tenantId, clientId);
        Assert.NotNull(clientResult);
        Assert.Null(clientResult!.ApprovalStatus);
    }

    [Theory]
    [InlineData("Approval", "Pending", "Pending")]
    [InlineData("Approval", "Satisfied", "Approved")]
    [InlineData("Approval", "Rejected", "Rejected")]
    [InlineData("approval", "pending", "Pending")]
    [InlineData("approval", "satisfied", "Approved")]
    [InlineData("approval", "rejected", "Rejected")]
    [InlineData("Payment", "Pending", null)]
    [InlineData("Payment", "Satisfied", null)]
    [InlineData("Payment", "Rejected", null)]
    [InlineData("Custom", "Pending", null)]
    public void CSTD142_DeriveApprovalStatus_MapsExpectedValues(string type, string status, string? expectedApprovalStatus)
    {
        var result = ConditionService.DeriveApprovalStatus(type, status);
        Assert.Equal(expectedApprovalStatus, result);
    }

    [Fact]
    public async Task CSTD142_GetConditionsStaffAsync_EmptyList_ReturnsEmptyImmediately()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-cstd142";
        var service = new ConditionService(db, new Mock<IClientActionService>().Object, new Mock<IAuditPublisher>().Object, NullLogger<ConditionService>.Instance);

        // Act
        var result = await service.GetConditionsStaffAsync(engagementId, tenantId);

        // Assert
        Assert.Empty(result);
    }
}
