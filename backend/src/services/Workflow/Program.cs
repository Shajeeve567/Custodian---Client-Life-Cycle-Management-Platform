using Confluent.Kafka;
using Custodian.Workflow;
using Custodian.Workflow.Data;
using Custodian.Workflow.Services;
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

// Register Workflow domain services (repositories, actions, conditions, gate, next action)
builder.Services.AddWorkflowDomainServices(builder.Configuration);

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
