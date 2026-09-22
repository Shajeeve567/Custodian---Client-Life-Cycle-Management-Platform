using Confluent.Kafka;
using Custodian.Workflow.Data;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Kafka;
using Custodian.Shared.Http;
using Custodian.Shared.Auth;
using Custodian.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & CORS
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration);
builder.Services.AddTenantContext();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();

// CSTD-33: SLA thresholds are config driven
// No runtime editing
builder.Services.Configure<Custodian.Workflow.Configuration.SlaOptions>(
    builder.Configuration.GetSection(Custodian.Workflow.Configuration.SlaOptions.SectionName)
);

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

// Register Repository & Audit Services
builder.Services.AddScoped<IEngagementRepository, EngagementRepository>();
builder.Services.AddScoped<IClientActionService, ClientActionService>();
builder.Services.AddScoped<IClientPortalService, ClientPortalService>();
builder.Services.AddScoped<IRequirementService, RequirementService>();

// CSTD-33
builder.Services.AddScoped<IStallDetectionService, StallDetectionService>();

// Audit transport is feature-flagged: "Http" (default) keeps the existing
// synchronous HTTP call to the Audit service; "Kafka" switches to publishing
// onto the shared "custodian.events" topic instead. Toggle via Audit:Transport
// config (or the Audit__Transport env var) once the Kafka path is verified.
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
    });
}

// CSTD-18: Gate Evaluation — mandatory gates (required documents today) that must be
// satisfied before an engagement's stage transition proceeds.
builder.Services.AddHttpClient<IDocumentComplianceClient, DocumentComplianceClient>(client =>
{
    var documentsBaseUrl = builder.Configuration["Services:DocumentsUrl"] ?? "http://localhost:5171";
    client.BaseAddress = new Uri(documentsBaseUrl);
});
builder.Services.AddScoped<IGateEvaluator, GateEvaluator>();

builder.Services.AddOpenApi();

var app = builder.Build();

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
    dbContext?.Database.Migrate();
}

app.UseCors();
// app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantContext();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Workflow Service" }));
app.MapControllers();

app.Run();
