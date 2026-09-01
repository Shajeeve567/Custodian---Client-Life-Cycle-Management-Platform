using Microsoft.EntityFrameworkCore;
using Identity.Data;
using Scalar.AspNetCore;
using Custodian.Shared.Tenancy;
using Custodian.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AzureMySqlConnection");

// ORIGINAL/PREVIOUS DBNAME REGISTRATION CODE:
// builder.Services.AddOpenApi();
// builder.Services.AddDbContext<IdentityDbContext>(options =>
//             options.UseInMemoryDatabase("InMem"));
// builder.Services.AddDbContext<IdentityDbContext>(options =>
//             options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Azure MySQL Database Connection (Configured for Azure Database for MySQL Flexible Server 8.0)
var serverVersion = new MySqlServerVersion(new Version(8, 0, 35));
builder.Services.AddDbContext<IdentityDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));


// PREVIOUS IN-MEMORY FALLBACK CODE (COMMENTED OUT AS PER USER INSTRUCTION):
// bool useMySql = false;
// try
// {
//     if (!string.IsNullOrWhiteSpace(connectionString) && !connectionString.Contains("YOUR_SECRET_STRING"))
//     {
//         var serverVersion = ServerVersion.AutoDetect(connectionString);
//         builder.Services.AddDbContext<IdentityDbContext>(options =>
//             options.UseMySql(connectionString, serverVersion));
//         useMySql = true;
//         Console.WriteLine("[Identity] Connected to Azure MySQL Database.");
//     }
// }
// catch (Exception ex)
// {
//     Console.WriteLine($"[Identity] Azure MySQL connection failed: {ex.Message}. Falling back to In-Memory Database.");
// }
// if (!useMySql)
// {
//     builder.Services.AddDbContext<IdentityDbContext>(options =>
//         options.UseInMemoryDatabase("IdentityDb"));
//     Console.WriteLine("[Identity] Running with In-Memory Database.");
// }


builder.Services.AddScoped<IUserAccountRepository, UserAccountRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IClientProfileRepository, ClientProfileRepository>();

// Configure Controllers & JSON Serialization Options:
// 1. JsonStringEnumConverter: Converts enum values to/from string representation (e.g. "Staff", "Owner", "Client").
// 2. ReferenceHandler.IgnoreCycles: Prevents circular reference serialization errors (UserAccount -> TenantMembership -> UserAccount).
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

builder.Services.AddTenantContext(); 
builder.Services.AddJwtAuthentication(builder.Configuration);

// CORS Policy Configuration:
// Configures a permissive CORS policy for local development to allow frontend requests from any origin.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Database Schema Initialization:
// Automatically runs migrations for relational databases (MySQL) or ensures schema creation for In-Memory DB.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    db.Database.Migrate();
}



// Enable CORS Middleware
app.UseCors("AllowAll");

// Configure the HTTP request pipeline for OpenAPI / Scalar documentation
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
    app.MapOpenApi();
}

// Disabled HTTPS Redirection for local development to prevent port redirection conflicts
// app.UseHttpsRedirection();

// Authentication and Authorization Middleware pipeline
app.UseAuthentication();
app.UseAuthorization();

app.UseTenantContext();
app.MapControllers();

// Root Health Check Endpoint
app.MapGet("/", () => Results.Ok(new { status = "Healthy", service = "Identity Service" }));

app.Run();


