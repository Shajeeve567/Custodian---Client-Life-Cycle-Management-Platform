using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class RequirementServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    [Fact]
    public async Task RequestRequirementAsync_ValidRequest_PersistsRequirementAndMirroredClientAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var auditPublisher = new Mock<IAuditPublisher>();
        var service = new RequirementService(db, auditPublisher.Object);

        var dto = new RequestRequirementDto
        {
            Type = "CompanyRegistrationNumber",
            StageNumber = 2,
            AssignedToRole = "Client",
            Title = "Provide company registration number",
            RequestedByActor = "staff-1"
        };

        // Act
        var result = await service.RequestRequirementAsync(engagementId, tenantId, dto);

        // Assert: Requirement persisted
        Assert.NotNull(result);
        Assert.Equal("CompanyRegistrationNumber", result.Type);
        Assert.Equal(RequirementStatus.Requested, result.Status);
        Assert.Equal(2, result.StageNumber);
        Assert.Null(result.Value);

        var dbRequirement = await db.Requirements.FirstOrDefaultAsync(r => r.RequirementId == result.RequirementId);
        Assert.NotNull(dbRequirement);
        Assert.Equal(tenantId, dbRequirement!.TenantId);

        // Assert: mirrored ClientAction created and linked, so it surfaces in Next Action
        var mirroredAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedRequirementId == result.RequirementId);
        Assert.NotNull(mirroredAction);
        Assert.Equal(engagementId, mirroredAction!.EngagementId);
        Assert.Equal(tenantId, mirroredAction.TenantId);
        Assert.Equal("Provide company registration number", mirroredAction.Title);
        Assert.Equal(ClientActionStatus.Pending, mirroredAction.Status);
        Assert.Equal("Client", mirroredAction.AssignedToRole);
        Assert.False(mirroredAction.IsInternalOnly);
        Assert.Equal(2, mirroredAction.StageNumber);
    }

    [Fact]
    public async Task RequestRequirementAsync_NoTitleProvided_DerivesTitleFromType()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        var dto = new RequestRequirementDto { Type = "SourceOfFunds" };

        // Act
        var result = await service.RequestRequirementAsync(engagementId, tenantId, dto);

        // Assert
        var mirroredAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedRequirementId == result.RequirementId);
        Assert.NotNull(mirroredAction);
        Assert.Equal("Provide: SourceOfFunds", mirroredAction!.Title);
    }

    [Fact]
    public async Task RequestRequirementAsync_PublishesRequirementRequestedEvent_WithoutValue()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var auditPublisher = new Mock<IAuditPublisher>();
        var service = new RequirementService(db, auditPublisher.Object);

        var dto = new RequestRequirementDto { Type = "SourceOfFunds", RequestedByActor = "staff-42" };

        // Act
        await service.RequestRequirementAsync(engagementId, tenantId, dto);

        // Assert
        auditPublisher.Verify(
            a => a.PublishEventAsync(engagementId, tenantId, "staff-42", "RequirementRequested", It.IsAny<object>()),
            Times.Once());
    }

    [Fact]
    public async Task SubmitRequirementAsync_ExistingRequirement_UpdatesValueAndStatus_AndCompletesLinkedAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        var requested = await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds" });

        var submitDto = new SubmitRequirementDto { Value = "Salary income", SubmittedByActor = "client-user-1" };

        // Act
        var result = await service.SubmitRequirementAsync(engagementId, requested.RequirementId, tenantId, submitDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(RequirementStatus.Submitted, result!.Status);
        Assert.Equal("Salary income", result.Value);
        Assert.NotNull(result.SubmittedAt);

        var mirroredAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedRequirementId == requested.RequirementId);
        Assert.NotNull(mirroredAction);
        Assert.Equal(ClientActionStatus.Completed, mirroredAction!.Status);
        Assert.Equal("client-user-1", mirroredAction.CompletedByActor);
        Assert.NotNull(mirroredAction.CompletedAt);
    }

    [Fact]
    public async Task SubmitRequirementAsync_PublishesRequirementSubmittedEvent_WithoutValueInPayload()
    {
        // Arrange: AC5 calls for a "client-safe" event — the submitted Value must never appear
        // in the published payload, since it flows through a shared topic.
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var auditPublisher = new Mock<IAuditPublisher>();
        var service = new RequirementService(db, auditPublisher.Object);

        var requested = await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds" });

        object? capturedPayload = null;
        auditPublisher
            .Setup(a => a.PublishEventAsync(engagementId, tenantId, "client-user-1", "RequirementSubmitted", It.IsAny<object>()))
            .Callback<Guid, string, string, string, object>((_, _, _, _, payload) => capturedPayload = payload)
            .Returns(Task.CompletedTask);

        // Act
        await service.SubmitRequirementAsync(engagementId, requested.RequirementId, tenantId,
            new SubmitRequirementDto { Value = "top-secret-income-source", SubmittedByActor = "client-user-1" });

        // Assert
        auditPublisher.Verify(
            a => a.PublishEventAsync(engagementId, tenantId, "client-user-1", "RequirementSubmitted", It.IsAny<object>()),
            Times.Once());
        Assert.NotNull(capturedPayload);
        var json = System.Text.Json.JsonSerializer.Serialize(capturedPayload);
        Assert.DoesNotContain("top-secret-income-source", json);
        Assert.DoesNotContain("\"value\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequirementAsync_NonexistentRequirement_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        // Act
        var result = await service.SubmitRequirementAsync(Guid.NewGuid(), Guid.NewGuid(), "tenant-001", new SubmitRequirementDto { Value = "x" });

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SubmitRequirementAsync_CrossTenant_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);
        var requested = await service.RequestRequirementAsync(engagementId, "tenant-legitimate", new RequestRequirementDto { Type = "SourceOfFunds" });

        // Act: attempt submit using a different tenant
        var result = await service.SubmitRequirementAsync(engagementId, requested.RequirementId, "tenant-attacker", new SubmitRequirementDto { Value = "x" });

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ReviewRequirementAsync_Approved_SetsStatusApproved_LeavesLinkedActionCompleted()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        var requested = await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds" });
        await service.SubmitRequirementAsync(engagementId, requested.RequirementId, tenantId, new SubmitRequirementDto { Value = "Salary" });

        var reviewDto = new ReviewRequirementDto { Status = RequirementReviewStatus.Approved, ReviewerActor = "staff-reviewer-1" };

        // Act
        var result = await service.ReviewRequirementAsync(engagementId, requested.RequirementId, tenantId, reviewDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(RequirementStatus.Approved, result!.Status);
        Assert.Equal("staff-reviewer-1", result.ReviewedBy);
        Assert.NotNull(result.ReviewedAt);

        var mirroredAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedRequirementId == requested.RequirementId);
        Assert.Equal(ClientActionStatus.Completed, mirroredAction!.Status);
    }

    [Fact]
    public async Task ReviewRequirementAsync_Rejected_SetsStatusRejected_ReopensLinkedActionInNextAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        var requested = await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds" });
        await service.SubmitRequirementAsync(engagementId, requested.RequirementId, tenantId, new SubmitRequirementDto { Value = "Unclear answer" });

        var reviewDto = new ReviewRequirementDto
        {
            Status = RequirementReviewStatus.Rejected,
            ReviewerActor = "staff-reviewer-1",
            RejectionReason = "Please provide a more specific source of funds."
        };

        // Act
        var result = await service.ReviewRequirementAsync(engagementId, requested.RequirementId, tenantId, reviewDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(RequirementStatus.Rejected, result!.Status);
        Assert.Equal("Please provide a more specific source of funds.", result.RejectionReason);

        // Resurfaces in Next Action, same as a rejected evidence action
        var mirroredAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedRequirementId == requested.RequirementId);
        Assert.Equal(ClientActionStatus.Rejected, mirroredAction!.Status);
        Assert.Null(mirroredAction.CompletedAt);
    }

    [Fact]
    public async Task ReviewRequirementAsync_InvalidStatus_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);
        var requested = await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds" });

        var reviewDto = new ReviewRequirementDto { Status = "MaybeLater", ReviewerActor = "staff-1" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReviewRequirementAsync(engagementId, requested.RequirementId, tenantId, reviewDto));
    }

    [Fact]
    public async Task GetRequirementsByEngagementAsync_ClientView_StripsInternalFields()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds", RequestedByActor = "staff-1" });

        // Act
        var result = (await service.GetRequirementsByEngagementAsync(engagementId, tenantId, isClientView: true)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Null(result[0].AssignedToRole);
        Assert.Null(result[0].RequestedBy);
    }

    [Fact]
    public async Task GetRequirementsByEngagementAsync_StaffView_ReturnsFullFields()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);

        await service.RequestRequirementAsync(engagementId, tenantId, new RequestRequirementDto { Type = "SourceOfFunds", RequestedByActor = "staff-1" });

        // Act
        var result = (await service.GetRequirementsByEngagementAsync(engagementId, tenantId, isClientView: false)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Client", result[0].AssignedToRole);
        Assert.Equal("staff-1", result[0].RequestedBy);
    }

    [Fact]
    public async Task GetRequirementsByEngagementAsync_CrossTenant_ReturnsEmpty()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var service = new RequirementService(db, new Mock<IAuditPublisher>().Object);
        await service.RequestRequirementAsync(engagementId, "tenant-legitimate", new RequestRequirementDto { Type = "SourceOfFunds" });

        // Act
        var result = await service.GetRequirementsByEngagementAsync(engagementId, "tenant-attacker", isClientView: false);

        // Assert
        Assert.Empty(result);
    }
}
