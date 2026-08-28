using Microsoft.EntityFrameworkCore;
using Identity.Data;
using Scalar.AspNetCore;
using Custodian.Shared.Tenancy;
using Custodian.Shared.Auth;
using Custodian.Identity.Services.Notifications;
using Custodian.Identity.Services.Notifications.Strategies;
using Custodian.Identity.Services.Kafka;

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
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();

// Register Notification Delivery Strategies (Strategy Pattern)
builder.Services.AddScoped<INotificationDeliveryStrategy, InAppPortalNotificationStrategy>();
builder.Services.AddScoped<INotificationDeliveryStrategy, ResendEmailNotificationStrategy>();

// Register Deduplicator & Dispatcher
builder.Services.AddSingleton<IEventDeduplicator, InMemoryEventDeduplicator>();
builder.Services.AddScoped<INotificationDispatcher, ClientNotificationDispatcher>();

// Configure Resend Official SDK Client
builder.Services.AddOptions();
builder.Services.AddHttpClient<Resend.ResendClient>();
builder.Services.Configure<Resend.ResendClientOptions>(options =>
{
    options.ApiToken = builder.Configuration["Resend:ApiKey"] ?? "";
});
builder.Services.AddTransient<Resend.IResend, Resend.ResendClient>();
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection(ResendOptions.SectionName));

// Configure Kafka Background Consumer
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddHostedService<KafkaNotificationConsumer>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
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
