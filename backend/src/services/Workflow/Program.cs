using Custodian.Workflow.Data;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & CORS
builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

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

builder.Services.AddHttpClient<IAuditPublisher, AuditPublisher>(client =>
{
    var auditBaseUrl = builder.Configuration["Services:AuditUrl"] ?? builder.Configuration["AuditService:BaseUrl"] ?? "http://localhost:5051";
    client.BaseAddress = new Uri(auditBaseUrl);
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetService<WorkflowDbContext>();
    dbContext?.Database.EnsureCreated();
}

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
