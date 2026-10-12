using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ReadinessHandoffReportServiceTests
{
    private static WorkflowDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (ReadinessHandoffReportService Svc, Mock<IReadinessService> Readiness, Mock<IDocumentComplianceClient> Documents)
        Build(WorkflowDbContext db, Engagement? engagement = null, IReadOnlyList<DocumentSummaryDto>? documents = null)
    {
        var engRepo = new Mock<IEngagementRepository>();
        engRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<string>()))
               .ReturnsAsync(engagement);

        var readiness = new Mock<IReadinessService>();
        var documentsClient = new Mock<IDocumentComplianceClient>();
        if (documents is not null)
            documentsClient.Setup(c => c.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(documents);

        var svc = new ReadinessHandoffReportService(
            db, engRepo.Object, readiness.Object, documentsClient.Object, NullLogger<ReadinessHandoffReportService>.Instance);
        return (svc, readiness, documentsClient);
    }

    private static Engagement StartedEngagement() => new()
    {
        EngagementId = Guid.NewGuid(),
        TenantId = "t1",
        ClientId = "c1",
        StaffId = "s1",
        Status = EngagementStatus.Started,
        Stage = EngagementStage.Closure,
        CreatedAt = DateTime.UtcNow.AddDays(-30)
    };

    [Fact]
    public async Task EngagementNotFound_Throws404()
    {
        var (svc, _, _) = Build(NewDb(), engagement: null);
        await Assert.ThrowsAsync<Custodian.Shared.Reporting.Errors.ReportGenerationException>(() =>
            svc.ComputeAsync("t1", Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ReadyEngagement_ProducesReadyData()
    {
        var eng = StartedEngagement();
        var (svc, readiness, _) = Build(NewDb(), eng, documents: Array.Empty<DocumentSummaryDto>());
        readiness.Setup(r => r.EvaluateAsync(eng.EngagementId, "t1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ReadinessResponse
                 {
                     EngagementId = eng.EngagementId,
                     IsReady = true,
                     State = "Ready",
                     Blockers = new List<ReadinessBlocker>(),
                     EvaluatedAtUtc = DateTime.UtcNow
                 });

        var data = await svc.ComputeAsync("t1", eng.EngagementId, DateTimeOffset.UtcNow);

        Assert.True(data.IsReady);
        Assert.False(data.ReadinessUnavailable);
        Assert.False(data.DocumentsUnavailable);
    }

    [Fact]
    public async Task NotReady_BlockersArePassedThrough()
    {
        var eng = StartedEngagement();
        var (svc, readiness, _) = Build(NewDb(), eng, documents: Array.Empty<DocumentSummaryDto>());
        readiness.Setup(r => r.EvaluateAsync(eng.EngagementId, "t1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ReadinessResponse
                 {
                     EngagementId = eng.EngagementId,
                     IsReady = false,
                     State = "NotReady",
                     Blockers = new List<ReadinessBlocker>
                     {
                         new() { Kind = "Task", Title = "Open task", Reason = "'Sign-off' is still Pending." }
                     },
                     EvaluatedAtUtc = DateTime.UtcNow
                 });

        var data = await svc.ComputeAsync("t1", eng.EngagementId, DateTimeOffset.UtcNow);

        Assert.False(data.IsReady);
        Assert.Single(data.Readiness!.Blockers);
    }

    [Fact]
    public async Task ReadinessUnavailable_NeverReportedAsReady()
    {
        var eng = StartedEngagement();
        var (svc, readiness, _) = Build(NewDb(), eng, documents: Array.Empty<DocumentSummaryDto>());
        readiness.Setup(r => r.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("readiness service down"));

        var data = await svc.ComputeAsync("t1", eng.EngagementId, DateTimeOffset.UtcNow);

        Assert.False(data.IsReady);
        Assert.True(data.ReadinessUnavailable);
        Assert.Null(data.Readiness);
    }

    [Fact]
    public async Task DocumentsUnavailable_ForcesNotReadyEvenIfReadinessReportsReady()
    {
        // Fail-closed: the report cannot claim Ready without evidence, even if the CSTD-20
        // verdict (from an earlier moment) said Ready.
        var eng = StartedEngagement();
        var (svc, readiness, documentsClient) = Build(NewDb(), eng);
        readiness.Setup(r => r.EvaluateAsync(eng.EngagementId, "t1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ReadinessResponse
                 {
                     EngagementId = eng.EngagementId, IsReady = true, State = "Ready",
                     Blockers = new List<ReadinessBlocker>(), EvaluatedAtUtc = DateTime.UtcNow
                 });
        documentsClient.Setup(c => c.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new DocumentComplianceUnavailableException("documents down"));

        var data = await svc.ComputeAsync("t1", eng.EngagementId, DateTimeOffset.UtcNow);

        Assert.False(data.IsReady);
        Assert.True(data.DocumentsUnavailable);
    }

    [Fact]
    public async Task HandoffContext_LoadedWhenPresent()
    {
        var eng = StartedEngagement();
        eng.HandoffSummary = "Handover complete.";
        eng.HandedOffBy = "staff-1";
        eng.HandedOffAtUtc = DateTime.UtcNow.AddHours(-1);

        var (svc, readiness, _) = Build(NewDb(), eng, documents: Array.Empty<DocumentSummaryDto>());
        readiness.Setup(r => r.EvaluateAsync(eng.EngagementId, "t1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ReadinessResponse
                 {
                     EngagementId = eng.EngagementId, IsReady = true, State = "Ready",
                     Blockers = new List<ReadinessBlocker>(), EvaluatedAtUtc = DateTime.UtcNow
                 });

        var data = await svc.ComputeAsync("t1", eng.EngagementId, DateTimeOffset.UtcNow);

        Assert.NotNull(data.Handoff);
        Assert.Equal("Handover complete.", data.Handoff!.Summary);
        Assert.Equal("staff-1", data.Handoff.HandedOffBy);
    }
}