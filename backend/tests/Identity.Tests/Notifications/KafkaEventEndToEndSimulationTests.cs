using System.Text.Json;
using Custodian.Identity.Domain;
using Custodian.Identity.Services.Kafka;
using Custodian.Identity.Services.Notifications;
using Custodian.Identity.Services.Notifications.Mappers;
using Custodian.Identity.Services.Notifications.Strategies;
using Custodian.Shared.Messaging;
using Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Resend;
using Xunit;
using Xunit.Abstractions;

namespace Identity.Tests.Notifications;

public class KafkaEventEndToEndSimulationTests
{
    private readonly ITestOutputHelper _output;

    public KafkaEventEndToEndSimulationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Simulate_KafkaEventReceived_TriggersMapper_SavesToDb_AndSendsResendEmail()
    {
        // 1. Setup DI Container & In-Memory DB
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new TestLoggerProvider(_output)));

        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<IdentityDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        services.AddScoped(sp => new Custodian.Shared.Tenancy.TenantContext { TenantId = tenantId.ToString() });
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IClientProfileRepository, ClientProfileRepository>();

        // Register Strategies & Dispatcher
        services.AddScoped<INotificationDeliveryStrategy, InAppPortalNotificationStrategy>();
        services.AddScoped<INotificationDeliveryStrategy, ResendEmailNotificationStrategy>();
        services.AddSingleton<IEventDeduplicator, InMemoryEventDeduplicator>();
        services.AddSingleton<IEventToMessageMapper, EventToClientSafeMessageMapper>();
        services.AddScoped<INotificationDispatcher, ClientNotificationDispatcher>();

        // Mock Resend SDK client to capture sent email
        EmailMessage? sentEmail = null;
        var resendMock = new Mock<IResend>();
        resendMock.Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((msg, _) => sentEmail = msg)
            .ReturnsAsync((EmailMessage _, CancellationToken _) =>
            {
                var response = (ResendResponse<Guid>)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ResendResponse<Guid>));
                foreach (var field in typeof(ResendResponse).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(bool)) field.SetValue(response, true);
                    if (field.FieldType == typeof(System.Net.HttpStatusCode)) field.SetValue(response, System.Net.HttpStatusCode.OK);
                }
                return response;
            });

        services.AddSingleton(resendMock.Object);
        services.AddSingleton(Options.Create(new ResendOptions
        {
            ApiKey = "re_test_live_key",
            FromEmail = "Custodian <onboarding@resend.dev>"
        }));
        services.AddSingleton(Options.Create(new KafkaOptions { Enabled = true }));
        services.AddTransient<KafkaNotificationConsumer>();

        var provider = services.BuildServiceProvider();

        // 2. Prepare Sample Client in Database
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            db.Clients.Add(new ClientProfile
            {
                Id = clientId,
                TenantId = tenantId,
                Name = "Acme Corp Client",
                Email = "client.contact@acme.com",
                Status = UserStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // 3. Construct a real Kafka message envelope (as published by Documents Microservice)
        var kafkaEnvelope = new KafkaEnvelope(
            EventId: "evt-" + Guid.NewGuid().ToString("N")[..8],
            EventType: "document.verified",
            TenantId: tenantId.ToString(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new
            {
                clientId = clientId,
                clientEmail = "client.contact@acme.com",
                documentName = "Proof_Of_Identity.pdf",
                documentType = "IdentityVerification",
                status = "Verified"
            })
        );

        var rawKafkaMessageJson = JsonSerializer.Serialize(kafkaEnvelope);
        _output.WriteLine($"[1. KAFKA MESSAGE ARRIVED ON TOPIC 'custodian.events']:\n{rawKafkaMessageJson}\n");

        // 4. Simulate Kafka consumer picking up the message from the topic
        var consumer = provider.GetRequiredService<KafkaNotificationConsumer>();
        await consumer.ProcessMessageAsync(rawKafkaMessageJson);

        // 5. Verify In-App Portal Database Persistence (ST01)
        using (var scope = provider.CreateScope())
        {
            var notificationRepo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
            var clientNotifications = (await notificationRepo.GetByClientAsync(clientId, tenantId)).ToList();

            _output.WriteLine($"[2. IN-APP PORTAL DB CHECK]: Found {clientNotifications.Count} notification(s) stored in MySQL for client {clientId}:");
            foreach (var n in clientNotifications)
            {
                _output.WriteLine($"    - ID: {n.NotificationId} | IsRead: {n.IsRead} | Event: {n.SourceEventType}");
                _output.WriteLine($"      Message: \"{n.Message}\"");
            }

            Assert.Single(clientNotifications);
            Assert.False(clientNotifications[0].IsRead);
            Assert.Contains("Proof_Of_Identity.pdf", clientNotifications[0].Message);
        }

        // 6. Verify Email Sent via Resend Strategy (ST02 & ST03)
        Assert.NotNull(sentEmail);
        _output.WriteLine($"\n[3. RESEND EMAIL DISPATCH CHECK]:");
        _output.WriteLine($"    - From:    {sentEmail.From}");
        _output.WriteLine($"    - To:      {string.Join(", ", sentEmail.To)}");
        _output.WriteLine($"    - Subject: {sentEmail.Subject}");
        _output.WriteLine($"    - Body:    {sentEmail.HtmlBody}");

        Assert.NotNull(sentEmail.From);
        Assert.Equal("Custodian <onboarding@resend.dev>", sentEmail.From!.ToString());
        Assert.Contains("client.contact@acme.com", sentEmail.To.Select(t => t.Email));
        Assert.Contains("Document Verified: Proof_Of_Identity.pdf", sentEmail.Subject);
        Assert.Contains("Proof_Of_Identity.pdf", sentEmail.HtmlBody);
        _output.WriteLine("\n--> FULL EVENT-DRIVEN KAFKA + IN-APP + EMAIL PIPELINE VERIFIED SUCCESSFULLY! <--");
    }

    private class TestLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;
        public TestLoggerProvider(ITestOutputHelper output) => _output = output;
        public ILogger CreateLogger(string categoryName) => new TestLogger(_output, categoryName);
        public void Dispose() { }

        private class TestLogger : ILogger
        {
            private readonly ITestOutputHelper _output;
            private readonly string _category;
            public TestLogger(ITestOutputHelper output, string category) { _output = output; _category = category; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _output.WriteLine($"[{logLevel}] [{_category}] {formatter(state, exception)}");
            }
        }
    }
}
