using Custodian.Workflow.Data;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers
builder.Services.AddControllers();

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

builder.Services.AddHttpClient<IAuditPublisher, AuditPublisher>(client =>
{
    var auditBaseUrl = builder.Configuration["Services:AuditUrl"] ?? "http://localhost:5005";
    client.BaseAddress = new Uri(auditBaseUrl);
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
