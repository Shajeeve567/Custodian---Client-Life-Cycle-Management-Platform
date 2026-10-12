using Confluent.Kafka;
using Custodian.Workflow;
using Custodian.Workflow.Data;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Access;
using Custodian.Workflow.Services.Kafka;
using Custodian.Shared.Http;
using Custodian.Shared.Auth;
using Custodian.Shared.Reporting;
using Custodian.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & CORS
// Staff only reach engagements they are responsible for (Owners: all; see EngagementAccess).
builder.Services.AddControllers(options => options.Filters.Add<StaffEngagementAccessFilter>())
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddCustodianCors(builder.Configuration, builder.Environment);
builder.Services.AddTenantContext();
builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment);
// Tenant APIs require a workspace token (tenant_id claim); see TenantAuthorizationExtensions.
builder.Services.AddTenantScopedAuthorization();
builder.Services.AddHttpContextAccessor();

// Configure EF Core with MySQL
var connectionString = builder.Configuration.GetConnectionString("AzureMySqlConnection");
if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_SECRET_STRING"))
{
    connectionString = builder.Configuration.GetConnectionString("Default");
}

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<WorkflowDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
}

// Register Workflow domain services (repositories, actions, conditions, gate, next action, SLA/stall)
builder.Services.AddWorkflowDomainServices(builder.Configuration);

// CSTD-36 shared report renderer + CSV exporter (reports are generated from Workflow's own live data).
builder.Services.AddCustodianReporting();

// Audit transport (Audit:Transport, env Audit__Transport). "Kafka" (appsettings.json) publishes onto the
// shared "custodian.events" topic, which Audit, Identity (notifications) and Workflow's own consumer
// read. "Http" posts to the Audit API only (needs AuditIngestion:ApiKey) and is the fallback when the
// setting is absent. With Kafka selected and no broker reachable, each event fails after the producer
// timeout and is logged as an error; the startup log line below says which transport is active.
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
    var dbContext = scope.ServiceProvider.GetService<WorkflowDbContext>();
    // Migrate (not EnsureCreated): applies any pending EF migration to an existing
    // database, including creating it fresh if it doesn't exist yet. EnsureCreated
    // only creates a brand-new database from the current model and silently does
    // nothing to a database that already exists, so later migrations (e.g. adding
    // the Stage column) would never actually reach it.
    // Migrations only apply to the relational (MySQL) provider; test hosts may use an in-memory store.
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
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Workflow Service" }));
// Every controller endpoint requires a workspace token (tenant_id), combined with its own roles.
app.MapControllers().RequireTenantMembership();

app.Run();

public partial class Program { }