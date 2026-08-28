using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Custodian.Documents.Data;

public class DocumentDbContextFactory : IDesignTimeDbContextFactory<DocumentDbContext>
{
    public DocumentDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("AzureMySqlConnection");
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_SECRET_STRING"))
        {
            connectionString = configuration.GetConnectionString("Default") ?? "Server=localhost;Database=custodian_documents;Uid=root;Pwd=password;";
        }

        var optionsBuilder = new DbContextOptionsBuilder<DocumentDbContext>();
        var serverVersion = new MySqlServerVersion(new System.Version(8, 0, 30));
        optionsBuilder.UseMySql(connectionString, serverVersion);

        return new DocumentDbContext(optionsBuilder.Options);
    }
}
