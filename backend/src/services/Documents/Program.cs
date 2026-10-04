using Custodian.Documents.Data;
using Confluent.Kafka;
using Custodian.Documents.Services;
using Custodian.Documents.Services.Kafka;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Auth;
using Custodian.Shared.Http;
using Custodian.Shared.Reporting;
using Custodian.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & services
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration, builder.Environment);
builder.Services.AddTenantContext();
builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment);
// Tenant APIs require a workspace token (tenant_id claim); see TenantAuthorizationExtensions.
builder.Services.AddTenantScopedAuthorization();
builder.Services.AddCustodianReporting();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<IDocumentValidator, DocumentValidator>();

builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IValidationVerificationReportService, ValidationVerificationReportService>();

// Engagement access lives in Workflow (client ownership; staff only for engagements they are responsible
// for). Documents asks it, as the caller, before a Client or Staff member touches an engagement's documents.
// Fails closed when Workflow is unreachable.
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<Custodian.Documents.Services.EngagementAccess.IEngagementAccessClient,
    Custodian.Documents.Services.EngagementAccess.WorkflowEngagementAccessClient>(client =>
{
    var workflowBaseUrl = builder.Configuration["Services:WorkflowUrl"] ?? "http://localhost:5225";
    client.BaseAddress = new Uri(workflowBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddHttpClient<IReportEngagementScopeResolver, WorkflowReportEngagementScopeResolver>(client =>
{
    var workflowBaseUrl = builder.Configuration["Services:WorkflowUrl"] ?? "http://localhost:5225";
    client.BaseAddress = new Uri(workflowBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Audit transport: "Kafka" publishes document events to the shared custodian.events topic, where Audit
// records them, Workflow applies verification outcomes to the linked task (DocumentEventsConsumer) and
// Identity notifies the client. "Http" posts to the Audit API only (no Workflow sync, no notifications).
var auditTransport = builder.Configuration["Audit:Transport"] ?? "Http";
if (string.Equals(auditTransport, "Kafka", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.Configure<KafkaProducerOptions>(builder.Configuration.GetSection(KafkaProducerOptions.SectionName));
    builder.Services.AddSingleton<IProducer<string, string>>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<KafkaProducerOptions>>().Value;
        var config = new ProducerConfig
        {
            BootstrapServers = opts.BootstrapServers,
            ClientId = opts.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        if (!string.IsNullOrWhiteSpace(opts.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(opts.SecurityProtocol, true, out var secProtocol))
        {
            config.SecurityProtocol = secProtocol;
            if (Enum.TryParse<SaslMechanism>(opts.SaslMechanism ?? "Plain", true, out var saslMech))
            {
                config.SaslMechanism = saslMech;
            }
            config.SaslUsername = !string.IsNullOrWhiteSpace(opts.SaslUsername) ? opts.SaslUsername : "$ConnectionString";
            config.SaslPassword = opts.SaslPassword;
        }

        return new ProducerBuilder<string, string>(config).Build();
    });
    builder.Services.AddSingleton<IAuditPublisher, KafkaAuditPublisher>();
}
else
{
    builder.Services.AddHttpClient<IAuditPublisher, AuditPublisher>(client =>
    {
        var auditBaseUrl = builder.Configuration["Services:AuditUrl"] ?? builder.Configuration["AuditService:BaseUrl"] ?? "http://localhost:5051";
        client.BaseAddress = new Uri(auditBaseUrl);
        // Service-to-service key for POST /api/audit-events (see Custodian.Shared.Messaging.AuditIngestion).
        var ingestionKey = builder.Configuration[Custodian.Shared.Messaging.AuditIngestion.ConfigKey];
        if (Custodian.Shared.Messaging.AuditIngestion.IsUsableKey(ingestionKey))
        {
            client.DefaultRequestHeaders.Add(Custodian.Shared.Messaging.AuditIngestion.HeaderName, ingestionKey);
        }
    });
}


// Compliance Rule Store, Engine & Rules
builder.Services.Configure<Custodian.Documents.Compliance.Store.ComplianceRuleOptions>(
    builder.Configuration.GetSection(Custodian.Documents.Compliance.Store.ComplianceRuleOptions.SectionName));
builder.Services.AddSingleton<Custodian.Documents.Compliance.Store.IComplianceRuleStore, Custodian.Documents.Compliance.Store.ComplianceRuleStore>();
builder.Services.AddSingleton<Custodian.Documents.Compliance.Rules.IComplianceRule, Custodian.Documents.Compliance.Rules.DocumentFreshnessRule>();
builder.Services.AddSingleton<Custodian.Documents.Compliance.Rules.IComplianceRule, Custodian.Documents.Compliance.Rules.DocumentExpiryRule>();
builder.Services.AddSingleton<Custodian.Documents.Compliance.IComplianceRuleEngine, Custodian.Documents.Compliance.ComplianceRuleEngine>();

// Configure EF Core with MySQL
var connectionString = builder.Configuration.GetConnectionString("AzureMySqlConnection");
if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_SECRET_STRING"))
{
    connectionString = builder.Configuration.GetConnectionString("Default");
}

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<DocumentDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
}

builder.Services.AddOpenApi();

var app = builder.Build();

Custodian.Shared.Messaging.AuditTransport.LogSelection(app.Logger, builder.Configuration, auditTransport);

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetService<DocumentDbContext>();
    if (dbContext?.Database.IsRelational() == true)
    {
        dbContext.Database.Migrate();
    }
}

app.LogCustodianCors();
app.UseCors();
// app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantContext();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Documents Service" }));
// Every controller endpoint requires a workspace token (tenant_id), combined with its own roles.
app.MapControllers().RequireTenantMembership();

app.Run();

public partial class Program { }
