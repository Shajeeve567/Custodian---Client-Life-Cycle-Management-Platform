using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Custodian.Audit.Data;

/// <summary>
/// CSTD-40 (40-N1): applies EF migrations at startup, first baselining databases that were created
/// by the old <c>EnsureCreated()</c> call.
///
/// Such a database has an <c>events</c> table but no migration history, so a plain <c>Migrate()</c>
/// would try to create <c>events</c> again and fail startup. When that is detected, the migrations
/// the existing schema already contains are recorded as applied: InitialCreate always, and
/// AddPreviousHash when the <c>previous_hash</c> column already exists. Later migrations then run
/// normally. Databases created by migrations (such as the QA database) are left untouched.
/// </summary>
public static class AuditDatabaseInitializer
{
    public static void Migrate(AuditDbContext dbContext, ILogger logger)
    {
        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        BaselineEnsureCreatedDatabase(dbContext, logger);
        dbContext.Database.Migrate();
    }

    private static void BaselineEnsureCreatedDatabase(AuditDbContext dbContext, ILogger logger)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            connection.Open();
        }

        try
        {
            if (!TableExists(connection, "events"))
            {
                return; // new database: Migrate() creates everything
            }

            var historyExists = TableExists(connection, "__EFMigrationsHistory");
            if (historyExists && Scalar(connection, "SELECT COUNT(*) FROM `__EFMigrationsHistory`") > 0)
            {
                return; // already managed by migrations
            }

            var migrations = dbContext.Database.GetMigrations().ToList();
            var baseline = migrations.Where(id => id.EndsWith("_InitialCreate", StringComparison.Ordinal)).ToList();
            if (ColumnExists(connection, "events", "previous_hash"))
            {
                baseline.AddRange(migrations.Where(id => id.EndsWith("_AddPreviousHash", StringComparison.Ordinal)));
            }

            Execute(connection,
                "CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (" +
                "`MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL, " +
                "`ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL, " +
                "CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)) CHARACTER SET=utf8mb4");

            foreach (var migrationId in baseline)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES (@id, @version)";
                AddParameter(insert, "@id", migrationId);
                AddParameter(insert, "@version", ProductInfo.GetVersion());
                insert.ExecuteNonQuery();
            }

            logger.LogWarning(
                "Audit database was created without migrations (EnsureCreated). Recorded baseline migrations {Migrations} before migrating.",
                string.Join(", ", baseline));
        }
        finally
        {
            if (openedHere)
            {
                connection.Close();
            }
        }
    }

    // information_schema comparisons are case-insensitive so this works with lower_case_table_names=1 (Azure).
    private static bool TableExists(DbConnection connection, string table) =>
        Scalar(connection,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER(@p0)",
            table) > 0;

    private static bool ColumnExists(DbConnection connection, string table, string column) =>
        Scalar(connection,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER(@p0) AND LOWER(column_name) = LOWER(@p1)",
            table, column) > 0;

    private static long Scalar(DbConnection connection, string sql, params string[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            AddParameter(command, $"@p{i}", parameters[i]);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
