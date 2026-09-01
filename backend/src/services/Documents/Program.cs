using Custodian.Documents.Data;
using Microsoft.EntityFrameworkCore;

using Custodian.Documents.Services;

var builder = WebApplication.CreateBuilder(args);

// Add controllers & services
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
builder.Services.AddSingleton<IDocumentValidator, DocumentValidator>();
builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();

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
app.MapControllers();

app.Run();
