using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Custodian.Shared.Data;

public sealed class SqlConnectionFactory(string connectionString)
{
    public MySqlConnection Create() => new(connectionString);
}

public static class SqlConnectionFactoryExtensions
{
    public static IServiceCollection AddSqlConnection(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new SqlConnectionFactory(
            configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.")));
}