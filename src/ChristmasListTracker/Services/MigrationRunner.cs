using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace ChristmasListTracker.Services;

public sealed class MigrationRunner(
    NpgsqlDataSource dataSource,
    string migrationsPath,
    ILogger<MigrationRunner> logger)
{
    private static readonly Regex MigrationFilePattern =
        new(@"^(?<version>\d+)_[a-z0-9_]+\.sql$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const long AdvisoryLockKey = 741_921_062;

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id SMALLINT PRIMARY KEY CHECK (id = 1),
                current_version BIGINT NOT NULL CHECK (current_version >= 0)
            );
            INSERT INTO schema_migrations (id, current_version)
            VALUES (1, 0)
            ON CONFLICT (id) DO NOTHING;
            """,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@lockKey);",
            new { lockKey = AdvisoryLockKey },
            cancellationToken: cancellationToken));

        try
        {
            var files = GetMigrationFiles();
            var currentVersion = await connection.QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT current_version FROM schema_migrations WHERE id = 1;",
                    cancellationToken: cancellationToken));

            foreach (var migration in files.Where(file => file.Version > currentVersion))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sql = await File.ReadAllTextAsync(migration.Path, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    transaction: transaction,
                    commandTimeout: 120,
                    cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE schema_migrations SET current_version = @version WHERE id = 1;",
                    new { version = migration.Version },
                    transaction,
                    cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);

                currentVersion = migration.Version;
                logger.LogInformation("Applied database migration {Version}: {Name}", migration.Version, migration.Name);
            }

            logger.LogInformation("Database schema is at version {Version}.", currentVersion);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@lockKey);",
                new { lockKey = AdvisoryLockKey },
                cancellationToken: CancellationToken.None));
        }
    }

    private IReadOnlyList<MigrationFile> GetMigrationFiles()
    {
        if (!Directory.Exists(migrationsPath))
        {
            throw new DirectoryNotFoundException($"Database migration directory was not found: {migrationsPath}");
        }

        var migrations = new List<MigrationFile>();
        foreach (var path in Directory.GetFiles(migrationsPath, "*.sql"))
        {
            var name = Path.GetFileName(path);
            var match = MigrationFilePattern.Match(name);
            if (!match.Success || !long.TryParse(match.Groups["version"].Value, out var version) || version <= 0)
            {
                throw new InvalidDataException($"Migration filename must use a positive numeric version, such as 0001_create_users.sql: {name}");
            }

            migrations.Add(new MigrationFile(version, name, path));
        }

        var duplicateVersion = migrations
            .GroupBy(migration => migration.Version)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateVersion is not null)
        {
            throw new InvalidDataException($"More than one migration uses version {duplicateVersion.Key}.");
        }

        return migrations.OrderBy(migration => migration.Version).ToArray();
    }

    private sealed record MigrationFile(long Version, string Name, string Path);
}
