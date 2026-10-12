using Custodian.Shared.Reporting.Errors;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>
/// CSTD-78: who may read an engagement summary, and that a dependency failure is an error, never a
/// partial report. E1 belongs to client-1 in tenant-a, staff-1 responsible; tenant-b has its own E2
/// for the same client id (so the tenant check, not just the client check, is exercised).
/// </summary>
public class EngagementSummaryReportServiceTests : IDisposable
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid E1 = Guid.NewGuid(), E2 = Guid.NewGuid();

    private readonly WorkflowDbContext _db;
    private readonly Mock<IClientPortalService> _portal = new();
    private readonly Mock<IClientActionService> _actions = new();
    private readonly Mock<IDocumentComplianceClient> _documents = new();
    private readonly EngagementSummaryReportService _service;

    public EngagementSummaryReportServiceTests()
    {
        _db = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase($"engagement-summary-{Guid.NewGuid()}").Options);

        _db.Engagements.AddRange(
            new Engagement { EngagementId = E1, TenantId = TenantA, ClientId = "client-1", StaffId = "staff-1", Status = EngagementStatus.Started },
            new Engagement { EngagementId = E2, TenantId = TenantB, ClientId = "client-1", StaffId = "staff-9", Status = EngagementStatus.Started });
        _db.EngagementConditions.AddRange(
            new EngagementCondition
            {
                EngagementId = E1, TenantId = TenantA, Type = ConditionType.Payment, Title = "Deposit", Status = ConditionStatus.Satisfied,
                Amount = 1000m, Currency = "LKR", SatisfiedAt = new DateTime(2026, 10, 5), InternalNote = "SECRET-NOTE", CreatedBy = "staff-1"
            },
            new EngagementCondition
            {
                EngagementId = E1, TenantId = TenantA, Type = ConditionType.Approval, Title = "Withdrawn approval", IsActive = false, CreatedBy = "staff-1"
            });
        _db.SaveChanges();

        _portal.Setup(p => p.GetDashboardForEngagementAsync(E1, TenantA, It.IsAny<string?>()))
            .ReturnsAsync(new ClientPortalDashboardDto { EngagementId = E1, Status = "Started", CurrentStageNumber = 2, CurrentStageName = "Document Collection" });
        _actions.Setup(a => a.GetActionsByEngagementAsync(E1, TenantA, true, null))
            .ReturnsAsync(Array.Empty<ClientActionResponseDto>());
        _documents.Setup(d => d.GetDocumentsAsync(E1, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        _service = new EngagementSummaryReportService(
            _db, _portal.Object, _actions.Object, _documents.Object, NullLogger<EngagementSummaryReportService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static EngagementSummaryRequest ClientRequest(Guid id, string tenant, string clientId) =>
        new(id, tenant, EngagementSummaryAudience.Client, clientId, null, "client@example.com", Now);

    private static EngagementSummaryRequest StaffRequest(Guid id, string tenant, string? restrictedStaffId) =>
        new(id, tenant, EngagementSummaryAudience.Staff, null, restrictedStaffId, "staff@example.com", Now);

    [Fact]
    public async Task Client_OwnEngagement_GetsClientSafeData()
    {
        var data = await _service.ComputeAsync(ClientRequest(E1, TenantA, "client-1"));

        Assert.Equal(E1, data.EngagementId);
        Assert.Null(data.Staff);
        var payment = Assert.Single(data.Payments);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), payment.CompletedAtUtc);
        Assert.Empty(data.Approvals); // the deactivated approval is not in the client view
        _portal.Verify(p => p.GetDashboardForEngagementAsync(E1, TenantA, "client-1"), Times.Once);
    }

    public static TheoryData<string, Guid, string, string> DeniedClientRequests => new()
    {
        { "another client", E1, TenantA, "client-2" },
        { "other tenant's engagement", E2, TenantA, "client-1" },
        { "own engagement id, wrong tenant", E1, TenantB, "client-1" },
        { "unknown engagement", Guid.NewGuid(), TenantA, "client-1" },
        { "blank client id", E1, TenantA, " " }
    };

    [Theory]
    [MemberData(nameof(DeniedClientRequests))]
    public async Task Client_CannotReadAnEngagementTheyDoNotOwn_AndGetsTheSameNotFound(string _, Guid id, string tenant, string clientId)
    {
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => _service.ComputeAsync(ClientRequest(id, tenant, clientId)));

        Assert.Equal(ReportErrorKind.SubjectNotFound, ex.Kind);
        Assert.Equal(EngagementSummaryReportService.NotFoundMessage, ex.Message);
        _documents.Verify(d => d.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _portal.Verify(p => p.GetDashboardForEngagementAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(null, true)]       // Owner: any engagement in the tenant
    [InlineData("staff-1", true)]  // responsible Staff
    [InlineData("STAFF-1", true)]
    [InlineData("staff-2", false)] // Staff not assigned
    public async Task Staff_AccessFollowsAssignment(string? restrictedStaffId, bool allowed)
    {
        var request = StaffRequest(E1, TenantA, restrictedStaffId);

        if (allowed)
        {
            var data = await _service.ComputeAsync(request);
            Assert.Equal(new StaffSummaryDetails("client-1", "staff-1"), data.Staff);
            _portal.Verify(p => p.GetDashboardForEngagementAsync(E1, TenantA, null), Times.Once);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => _service.ComputeAsync(request));
            Assert.Equal(ReportErrorKind.SubjectNotFound, ex.Kind);
        }
    }

    [Fact]
    public async Task DocumentsDown_IsDataSourceUnavailable_NotAPartialReport()
    {
        _documents.Setup(d => d.GetDocumentsAsync(E1, TenantA, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentComplianceUnavailableException("Documents service returned 503."));

        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => _service.ComputeAsync(ClientRequest(E1, TenantA, "client-1")));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
        Assert.DoesNotContain("503", ex.Message); // the user-facing message carries no internals
    }

    [Fact]
    public async Task NextActionWithAnUnavailableSource_IsDataSourceUnavailable()
    {
        _portal.Setup(p => p.GetDashboardForEngagementAsync(E1, TenantA, "client-1"))
            .ReturnsAsync(new ClientPortalDashboardDto
            {
                EngagementId = E1,
                NextAction = new NextActionResult
                {
                    Blockers = [new NextActionItem { Kind = NextActionKind.Unavailable, Title = "Condition status temporarily unavailable" }]
                }
            });

        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => _service.ComputeAsync(ClientRequest(E1, TenantA, "client-1")));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task BlankTenant_IsForbidden()
    {
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => _service.ComputeAsync(ClientRequest(E1, " ", "client-1")));
        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }
}
