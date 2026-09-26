using Custodian.Workflow.Data;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Builds the real Workflow registrations (AddWorkflowDomainServices, as Program.cs does) with
/// ValidateOnBuild + ValidateScopes, so a DI cycle or missing registration fails here instead of
/// at service startup. Regression test for the IGateEvaluator → IConditionService →
/// IClientActionService → IGateEvaluator cycle.
/// </summary>
public class WorkflowServiceRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:DocumentsUrl"] = "http://documents.test",
                ["Kafka:ConsumerEnabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(configuration);
        // Infrastructure Program.cs registers separately (MySQL, audit transport).
        services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new Mock<IAuditPublisher>().Object);

        services.AddWorkflowDomainServices(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void AddWorkflowDomainServices_BuildsWithoutCircularOrMissingDependencies()
    {
        var exception = Record.Exception(() => BuildProvider().Dispose());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(typeof(IEngagementRepository))]
    [InlineData(typeof(IClientActionService))]
    [InlineData(typeof(IClientPortalService))]
    [InlineData(typeof(IRequirementService))]
    [InlineData(typeof(IConditionReader))]
    [InlineData(typeof(IConditionService))]
    [InlineData(typeof(IGateEvaluator))]
    [InlineData(typeof(INextActionService))]
    [InlineData(typeof(IDocumentComplianceClient))]
    // CSTD-33/34 (merged from dev) and the CSTD-19 adapters over them
    [InlineData(typeof(IStallDetectionService))]
    [InlineData(typeof(IStallActionsProvider))]
    [InlineData(typeof(IStallEventDeduplicator))]
    [InlineData(typeof(IStallQueueService))]
    [InlineData(typeof(Custodian.Workflow.Services.Sla.ISlaCalculator))]
    [InlineData(typeof(Custodian.Workflow.Services.Stall.IStallService))]
    public void AddWorkflowDomainServices_ResolvesEveryControllerDependency(Type serviceType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(serviceType));
    }

    [Fact]
    public void GateEvaluator_DependsOnReadOnlyConditionReader_NotConditionService()
    {
        var parameterTypes = typeof(GateEvaluator).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.Contains(typeof(IConditionReader), parameterTypes);
        Assert.DoesNotContain(typeof(IConditionService), parameterTypes);
    }
}
