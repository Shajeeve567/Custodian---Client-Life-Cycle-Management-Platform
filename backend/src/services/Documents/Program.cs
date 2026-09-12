using Custodian.Documents.Data;
using Custodian.Documents.Services;
using Custodian.Shared.Auth;
using Custodian.Shared.Http;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & services
builder.Services.AddControllers();
builder.Services.AddCustodianCors(builder.Configuration);

// Configure Authentication & Authorization
if (builder.Configuration.GetSection("Jwt").Exists())
{
    builder.Services.AddJwtAuthentication(builder.Configuration);
}
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IDocumentValidator, DocumentValidator>();

builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();

builder.Services.AddHttpClient<IAuditPublisher, AuditPublisher>(client =>
{
    var auditBaseUrl = builder.Configuration["Services:AuditUrl"] ?? builder.Configuration["AuditService:BaseUrl"] ?? "http://localhost:5051";
    client.BaseAddress = new Uri(auditBaseUrl);
});


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
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Documents Service" }));
app.MapControllers();

app.Run();
