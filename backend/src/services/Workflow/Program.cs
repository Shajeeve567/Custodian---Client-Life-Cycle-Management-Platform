using Custodian.Workflow.Data;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Shared.Http;
using Custodian.Shared.Auth;
using Custodian.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & CORS
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration);
builder.Services.AddTenantContext();
builder.Services.AddJwtAuthentication(builder.Configuration);

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

builder.Services.AddHttpClient<IAuditPublisher, AuditPublisher>(client =>
{
    var auditBaseUrl = builder.Configuration["Services:AuditUrl"] ?? builder.Configuration["AuditService:BaseUrl"] ?? "http://localhost:5051";
    client.BaseAddress = new Uri(auditBaseUrl);
});

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
    dbContext?.Database.EnsureCreated();
}

app.UseCors();
// app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantContext();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Workflow Service" }));
app.MapControllers();

app.Run();
