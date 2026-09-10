using Custodian.Documents.Data;
using Custodian.Shared.Http;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

using Custodian.Documents.Services;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & services
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration);
builder.Services.AddSingleton<IDocumentValidator, DocumentValidator>();
builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();

// Compliance Rule Engine & Rules
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

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetService<DocumentDbContext>();
    dbContext?.Database.Migrate();
}

app.UseCors();
// app.UseHttpsRedirection();
app.UseAuthorization();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Documents Service" }));
app.MapControllers();

app.Run();
