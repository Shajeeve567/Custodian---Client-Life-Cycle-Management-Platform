using Custodian.Audit.Data;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Shared.Auth;
using Custodian.Audit.Services.Kafka;
using Custodian.Shared.Http;
using Custodian.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Custodian.Audit.Services.HashChain;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers & CORS
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration, builder.Environment);
builder.Services.AddTenantContext();
builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment);
// Tenant APIs require a workspace token (tenant_id claim); see TenantAuthorizationExtensions.
builder.Services.AddTenantScopedAuthorization();
// HTTP audit ingestion is service-only: callers must present AuditIngestion:ApiKey.
builder.Services.Configure<AuditIngestionOptions>(builder.Configuration.GetSection(AuditIngestionOptions.SectionName));

// Add OpenAPI / Swagger
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// Add EF Core DbContext
var azureConn = builder.Configuration.GetConnectionString("AzureMySqlConnection");
var defaultConn = builder.Configuration.GetConnectionString("Default") 
                  ?? builder.Configuration.GetConnectionString("DefaultConnection")
                  ?? "Server=localhost;Database=custodian_audit;Uid=root;Pwd=password;";

var connectionString = (!string.IsNullOrWhiteSpace(azureConn) && !azureConn.Contains("YOUR_SECRET_STRING"))
    ? azureConn
    : defaultConn;

var serverVersion = new MySqlServerVersion(new Version(8, 0, 30));

builder.Services.AddDbContext<AuditDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

// Register Application Services & Repositories
builder.Services.AddScoped<IAuditEventRepository, AuditEventRepository>();
builder.Services.AddScoped<IAuditEventService, AuditEventService>();

// SHA-256 hash chain: stateless, deterministic, no shared mutable state
// Singleton so the genesis constant and canonicalization are one instance
builder.Services.AddSingleton<IHashChainService, HashChainService>();

// Configure Kafka Background Consumer (mirrors Identity's registration pattern)
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddHostedService<KafkaAuditEventConsumer>();

var app = builder.Build();

// Configure HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetService<AuditDbContext>();
    if (dbContext != null)
    {
        // Baselines databases created by the old EnsureCreated() call, then applies migrations.
        AuditDatabaseInitializer.Migrate(dbContext, app.Logger);
    }
}

app.LogCustodianCors();
app.UseCors();
// app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantContext();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Audit Service" }));
// Every controller endpoint requires a workspace token (tenant_id), combined with its own roles.
app.MapControllers().RequireTenantMembership();

app.Run();

// Make Program class public for WebApplicationFactory in integration testing
public partial class Program { }
