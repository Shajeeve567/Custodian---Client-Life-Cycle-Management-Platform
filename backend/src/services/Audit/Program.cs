using Custodian.Audit.Data;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers
builder.Services.AddControllers();

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

var app = builder.Build();

// Configure HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Make Program class public for WebApplicationFactory in integration testing
public partial class Program { }
