using System.Diagnostics.Metrics;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Sla;
using Custodian.Workflow.Services.Stall;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class NextActionServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static NextActionService CreateService(
        WorkflowDbContext db,
        IConditionService conditionService,
        IDocumentComplianceClient documentClient,
        IGateEvaluator gateEvaluator,
        IStallService? stallService = null,
        TimeProvider? timeProvider = null) =>
        new(
            db,
            conditionService,
            documentClient,
            new DefaultSlaCalculator(),
            gateEvaluator,
            stallService ?? new DefaultStallService(),
            timeProvider ?? new FakeTimeProvider(Now),
            NullLogger<NextActionService>.Instance);

    // The engine passes its prefetched documents/conditions to the gate (one Documents call per evaluation).
    private static void SetupGate(Mock<IGateEvaluator> gate, GateEvaluationResult result) =>
        gate.Setup(g => g.EvaluateAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<EngagementStage>(),
                It.IsAny<IReadOnlyList<DocumentSummaryDto>>(),
                It.IsAny<IReadOnlyList<EngagementCondition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static Engagement CreateEngagement(Guid engagementId, string tenantId) => new()
    {
        EngagementId = engagementId,
        TenantId = tenantId,
        ClientId = "client-001",
        StaffId = "staff-001",
        Status = EngagementStatus.Started,
        Stage = EngagementStage.Onboarding,
        CreatedAt = Now.UtcDateTime.AddDays(-5)
    };

    [Fact]
    public async Task AC7_RequirementSubmit_ChangesPrimaryFromSubmissionToReview()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "ArticlesOfIncorporation",
            Status = RequirementStatus.Requested,
            StageNumber = 1,
            CreatedAt = DateTime.UtcNow
        };

        await db.Engagements.AddAsync(engagement);
        await db.Requirements.AddAsync(requirement);
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var mockStall = new Mock<IStallService>();
        mockStall.Setup(s => s.GetStallStatusAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object, mockStall.Object);

        // 1. Initial read: primary is requirement submission (Client)
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.RequirementSubmission, initialResult.PrimaryAction?.Kind);
        Assert.Equal(ResponsibleParty.Client, initialResult.PrimaryAction?.ResponsibleParty);
        Assert.Equal(3, initialResult.PrimaryAction?.PriorityRank);

        // 2. Mutate dependency: client submits the requirement
        requirement.Status = RequirementStatus.Submitted;
        requirement.Value = "https://documents.example.com/articles.pdf";
        requirement.SubmittedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 3. Next read reflects change without caching (compute-on-read): primary is now requirement review (Staff)
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(NextActionKind.RequirementReview, updatedResult.PrimaryAction?.Kind);
        Assert.Equal(ResponsibleParty.Staff, updatedResult.PrimaryAction?.ResponsibleParty);
        Assert.Equal(10, updatedResult.PrimaryAction?.PriorityRank);
    }

    [Fact]
    public async Task AC7_VerifyDocument_ChangesPrimaryToAdvanceStage()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };
        var docId = Guid.NewGuid();
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Certificate of Incorporation",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Uploaded,
            // Evidence actions are client-assigned; once Uploaded they wait on staff verification.
            AssignedToRole = "Client",
            StageNumber = 1,
            LinkedDocumentId = docId
        };

        await db.Engagements.AddAsync(engagement);
        await db.ClientActions.AddAsync(action);
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        // Document initially unverified
        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DocumentSummaryDto
                {
                    DocumentId = docId,
                    Type = "CertificateOfIncorporation",
                    ComplianceStatus = "Compliant",
                    VerificationStatus = "Unverified"
                }
            });

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object);

        // 1. Initial read: primary is document verification (Staff)
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.DocumentVerification, initialResult.PrimaryAction?.Kind);
        Assert.Equal(ResponsibleParty.Staff, initialResult.PrimaryAction?.ResponsibleParty);
        Assert.Equal(9, initialResult.PrimaryAction?.PriorityRank);

        // 2. Document is verified and action marked completed
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DocumentSummaryDto
                {
                    DocumentId = docId,
                    Type = "CertificateOfIncorporation",
                    ComplianceStatus = "Compliant",
                    VerificationStatus = "Verified"
                }
            });
        action.Status = ClientActionStatus.Completed;
        await db.SaveChangesAsync();

        // 3. Next read reflects change: all items satisfied -> AdvanceStage
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(OverallState.ReadyToAdvance, updatedResult.OverallState);
        Assert.Equal(NextActionKind.AdvanceStage, updatedResult.PrimaryAction?.Kind);
        Assert.Equal(12, updatedResult.PrimaryAction?.PriorityRank);
    }

    [Fact]
    public async Task AC7_DeactivateCondition_BlockerDisappears()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };

        await db.Engagements.AddAsync(engagement);
        await db.SaveChangesAsync();

        var activeCondition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Legal Counsel Approval"
        };

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { activeCondition });

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object);

        // 1. Initial read: condition approval is primary
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.ConditionApproval, initialResult.PrimaryAction?.Kind);
        Assert.Equal(5, initialResult.PrimaryAction?.PriorityRank);

        // 2. Condition is deactivated (no active conditions returned)
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        // 3. Next read reflects change: condition blocker disappeared, ready to advance
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(OverallState.ReadyToAdvance, updatedResult.OverallState);
        Assert.Equal(NextActionKind.AdvanceStage, updatedResult.PrimaryAction?.Kind);
    }

    [Fact]
    public async Task Evaluation_CallsDocumentsServiceExactlyOnce_WithRealGateEvaluator()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await db.Engagements.AddAsync(CreateEngagement(engagementId, tenantId));
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var gateEvaluator = new GateEvaluator(
            mockDocClient.Object,
            mockConditionService.Object,
            NullLogger<GateEvaluator>.Instance,
            db);

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, gateEvaluator);

        var result = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);

        Assert.NotNull(result);
        mockDocClient.Verify(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()), Times.Once);
        mockConditionService.Verify(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConditionServiceFailure_ReturnsBlockedExternal_AndNeverReadyToAdvance()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await db.Engagements.AddAsync(CreateEngagement(engagementId, tenantId));
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object);

        var result = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);

        Assert.NotNull(result);
        Assert.Equal(OverallState.BlockedExternal, result.OverallState);
        Assert.NotEqual(NextActionKind.AdvanceStage, result.PrimaryAction?.Kind);
        Assert.Contains(result.Blockers, b => b.Kind == NextActionKind.Unavailable && b.Reason == "Condition status temporarily unavailable");
    }

    [Fact]
    public async Task InjectedTimeProvider_DrivesOverduePromotion()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await db.Engagements.AddAsync(CreateEngagement(engagementId, tenantId));
        await db.ClientActions.AddAsync(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload passport",
            Type = ClientActionType.KycDocument,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = Now.UtcDateTime.AddDays(1),
            CreatedAt = Now.UtcDateTime.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());
        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());
        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var clock = new FakeTimeProvider(Now);
        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object, timeProvider: clock);

        var beforeDeadline = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.Equal(4, beforeDeadline!.PrimaryAction?.PriorityRank);
        Assert.Equal(Now, beforeDeadline.EvaluatedAtUtc);

        clock.Advance(TimeSpan.FromDays(2));

        var afterDeadline = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.Equal(1, afterDeadline!.PrimaryAction?.PriorityRank);
        Assert.True(afterDeadline.PrimaryAction?.IsOverdue);
    }

    [Fact]
    public async Task Evaluation_RecordsDurationMetric_TaggedWithViewAndOverallState()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        await db.Engagements.AddAsync(CreateEngagement(engagementId, tenantId));
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());
        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());
        var mockGateEvaluator = new Mock<IGateEvaluator>();
        SetupGate(mockGateEvaluator, GateEvaluationResult.Satisfied());

        var measurements = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == NextActionService.MeterName &&
                instrument.Name == NextActionService.EvaluationDurationInstrument)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });
        listener.Start();

        var service = CreateService(db, mockConditionService.Object, mockDocClient.Object, mockGateEvaluator.Object);
        var result = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);

        Assert.Equal(OverallState.ReadyToAdvance, result!.OverallState);
        lock (measurements)
        {
            Assert.Contains(measurements, m =>
                m.Value >= 0 &&
                Equals(m.Tags["view"], "Staff") &&
                Equals(m.Tags["overall_state"], OverallState.ReadyToAdvance));
            // No client data in tags: only view and overall_state.
            Assert.All(measurements, m => Assert.Equal(new[] { "overall_state", "view" }, m.Tags.Keys.OrderBy(k => k)));
        }
    }
}
