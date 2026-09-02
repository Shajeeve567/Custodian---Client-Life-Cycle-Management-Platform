using Custodian.Identity.Domain;
using Custodian.Identity.Services.Notifications;
using Custodian.Identity.Services.Notifications.Strategies;
using Identity.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Resend;
using Xunit;

namespace Identity.Tests.Notifications;

public class ResendEmailStrategyTests
{
    private readonly Mock<IResend> _resendMock;
    private readonly Mock<IClientProfileRepository> _clientRepoMock;
    private readonly Mock<ILogger<ResendEmailNotificationStrategy>> _loggerMock;

    public ResendEmailStrategyTests()
    {
        _resendMock = new Mock<IResend>();
        _clientRepoMock = new Mock<IClientProfileRepository>();
        _loggerMock = new Mock<ILogger<ResendEmailNotificationStrategy>>();
    }

    [Fact]
    public void Channel_ShouldBeEmail()
    {
        var options = Options.Create(new ResendOptions());
        var strategy = new ResendEmailNotificationStrategy(_resendMock.Object, options, _clientRepoMock.Object, _loggerMock.Object);
        Assert.Equal(NotificationChannel.Email, strategy.Channel);
    }

    [Fact]
    public async Task CanHandleAsync_ShouldReturnTrue_WhenClientEmailProvidedInContext()
    {
        var options = Options.Create(new ResendOptions());
        var strategy = new ResendEmailNotificationStrategy(_resendMock.Object, options, _clientRepoMock.Object, _loggerMock.Object);

        var context = new NotificationContext
        {
            ClientEmail = "client@example.com"
        };

        var result = await strategy.CanHandleAsync(context);
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_ShouldLookupClientEmail_WhenContextEmailEmpty()
    {
        var clientId = Guid.NewGuid();
        _clientRepoMock.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientProfile { Id = clientId, Email = "lookedup@example.com" });

        var options = Options.Create(new ResendOptions());
        var strategy = new ResendEmailNotificationStrategy(_resendMock.Object, options, _clientRepoMock.Object, _loggerMock.Object);

        var context = new NotificationContext
        {
            ClientId = clientId,
            ClientEmail = null
        };

        var result = await strategy.CanHandleAsync(context);
        Assert.True(result);
    }

    [Fact]
    public async Task DeliverAsync_WithoutApiKey_ShouldSimulateDevDeliveryWithoutError()
    {
        // When ApiKey is null/empty (e.g. dev mock), strategy should log and succeed
        var options = Options.Create(new ResendOptions { ApiKey = null });
        var strategy = new ResendEmailNotificationStrategy(_resendMock.Object, options, _clientRepoMock.Object, _loggerMock.Object);

        var context = new NotificationContext
        {
            ClientEmail = "test@example.com",
            Message = "Welcome to Custodian!"
        };

        var result = await strategy.DeliverAsync(context);
        Assert.True(result);
    }

    [Fact]
    public async Task DeliverAsync_WithApiKey_ShouldCallResendSdkAndSucceedOnSuccess()
    {
        // Arrange
        var options = Options.Create(new ResendOptions
        {
            ApiKey = "re_test_12345",
            FromEmail = "notifications@test.com"
        });

        var resendResponse = (ResendResponse<Guid>)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ResendResponse<Guid>));
        foreach (var field in typeof(ResendResponse).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (field.FieldType == typeof(System.Net.HttpStatusCode))
            {
                field.SetValue(resendResponse, System.Net.HttpStatusCode.OK);
            }
            else if (field.FieldType == typeof(bool))
            {
                field.SetValue(resendResponse, true);
            }
        }

        _resendMock.Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resendResponse);

        var strategy = new ResendEmailNotificationStrategy(_resendMock.Object, options, _clientRepoMock.Object, _loggerMock.Object);

        var context = new NotificationContext
        {
            ClientEmail = "client@example.com",
            Subject = "Test Subject",
            Message = "Test Message"
        };

        // Act
        var result = await strategy.DeliverAsync(context);

        // Assert
        Assert.True(result);
        _resendMock.Verify(r => r.EmailSendAsync(
            It.Is<EmailMessage>(m => m.Subject == "Test Subject" && m.From != null && m.From.Email == "notifications@test.com"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
