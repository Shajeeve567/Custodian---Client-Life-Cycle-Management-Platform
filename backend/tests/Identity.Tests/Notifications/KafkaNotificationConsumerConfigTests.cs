using Confluent.Kafka;
using Custodian.Identity.Services.Kafka;
using Xunit;

namespace Identity.Tests.Notifications;

public class KafkaNotificationConsumerConfigTests
{
    [Fact]
    public void BuildConsumerConfig_WhenSecurityProtocolIsEmpty_UsesPlaintextDefaults()
    {
        var options = new KafkaOptions
        {
            BootstrapServers = "localhost:9092",
            GroupId = "custodian-identity-local",
            AutoOffsetReset = "Earliest",
            SecurityProtocol = null,
            SaslMechanism = null,
            SaslUsername = null,
            SaslPassword = null
        };

        var config = KafkaNotificationConsumer.BuildConsumerConfig(options);

        Assert.Equal("localhost:9092", config.BootstrapServers);
        Assert.Equal("custodian-identity-local", config.GroupId);
        Assert.Equal(AutoOffsetReset.Earliest, config.AutoOffsetReset);
        Assert.False(config.EnableAutoCommit);
        Assert.Null(config.SecurityProtocol);
        Assert.Null(config.SaslMechanism);
        Assert.Null(config.SaslUsername);
        Assert.Null(config.SaslPassword);
    }

    [Fact]
    public void BuildConsumerConfig_WhenSecurityProtocolIsSaslSsl_ConfiguresAzureEventHubsCredentials()
    {
        var options = new KafkaOptions
        {
            BootstrapServers = "myns.servicebus.windows.net:9093",
            GroupId = "custodian-identity-azure",
            AutoOffsetReset = "Latest",
            SecurityProtocol = "SaslSsl",
            SaslMechanism = "Plain",
            SaslUsername = null, // Should default to $ConnectionString
            SaslPassword = "Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=fakekey"
        };

        var config = KafkaNotificationConsumer.BuildConsumerConfig(options);

        Assert.Equal("myns.servicebus.windows.net:9093", config.BootstrapServers);
        Assert.Equal("custodian-identity-azure", config.GroupId);
        Assert.Equal(AutoOffsetReset.Latest, config.AutoOffsetReset);
        Assert.False(config.EnableAutoCommit);
        Assert.Equal(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, config.SaslMechanism);
        Assert.Equal("$ConnectionString", config.SaslUsername);
        Assert.Equal("Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=fakekey", config.SaslPassword);
    }

    [Fact]
    public void BuildConsumerConfig_WhenCustomSaslUsernameProvided_PreservesExplicitUsername()
    {
        var options = new KafkaOptions
        {
            BootstrapServers = "custom-kafka:9093",
            GroupId = "custodian-identity-custom",
            SecurityProtocol = "SaslSsl",
            SaslMechanism = "Plain",
            SaslUsername = "explicit-user",
            SaslPassword = "custom-password"
        };

        var config = KafkaNotificationConsumer.BuildConsumerConfig(options);

        Assert.Equal(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, config.SaslMechanism);
        Assert.Equal("explicit-user", config.SaslUsername);
        Assert.Equal("custom-password", config.SaslPassword);
    }
}
