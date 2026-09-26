using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Kafka;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Sla;
using Custodian.Workflow.Services.Stall;

namespace Custodian.Workflow;

/// <summary>
/// Workflow domain service registrations, shared by Program.cs and the DI validation test so the
/// real dependency graph (including cycle detection) is checked in CI, not first at startup.
/// Infrastructure that needs live endpoints (DbContext, IAuditPublisher transport) stays in Program.cs.
/// </summary>
public static class WorkflowServiceRegistration
{
    public static IServiceCollection AddWorkflowDomainServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IEngagementRepository, EngagementRepository>();
        services.AddScoped<IClientActionService, ClientActionService>();
        services.AddScoped<IClientPortalService, ClientPortalService>();
        services.AddScoped<IRequirementService, RequirementService>();
        services.AddScoped<IConditionReader, ConditionReader>();
        services.AddScoped<IConditionService, ConditionService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISlaCalculator, DefaultSlaCalculator>();
        services.AddScoped<IStallService, DefaultStallService>();
        services.AddScoped<INextActionService, NextActionService>();

        // CSTD-18: Gate Evaluation — mandatory gates that must be satisfied before an engagement's
        // stage transition proceeds. 19-N7: bounded timeout/retry on the Documents call.
        services.AddHttpClient<IDocumentComplianceClient, DocumentComplianceClient>(client =>
        {
            var documentsBaseUrl = configuration["Services:DocumentsUrl"] ?? "http://localhost:5171";
            client.BaseAddress = new Uri(documentsBaseUrl);
        }).AddDocumentComplianceResilience();
        services.AddScoped<IGateEvaluator, GateEvaluator>();

        // CSTD-19 (19-N5): service-side document sync. Disabled unless Kafka:ConsumerEnabled=true
        // (Documents does not publish to Kafka yet; the frontend dual call is the live path).
        services.Configure<KafkaConsumerOptions>(configuration.GetSection(KafkaConsumerOptions.SectionName));
        services.AddHostedService<DocumentEventsConsumer>();

        return services;
    }
}
