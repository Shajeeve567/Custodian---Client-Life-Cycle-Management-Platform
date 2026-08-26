using Microsoft.EntityFrameworkCore;
using Identity.Data;
using Scalar.AspNetCore;
using Custodian.Shared.Tenancy;
using Custodian.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AzureMySqlConnection");

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
// builder.Services.AddDbContext<IdentityDbContext>(options =>
//             options.UseInMemoryDatabase("InMem"));
builder.Services.AddDbContext<IdentityDbContext>(options =>
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.AddScoped<IUserAccountRepository, UserAccountRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IClientProfileRepository, ClientProfileRepository>();
builder.Services.AddControllers();
builder.Services.AddTenantContext(); 
builder.Services.AddJwtAuthentication(builder.Configuration);


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseTenantContext();
app.MapControllers();

app.Run();
