using Gatherum.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gatherum.Tests;

/// <summary>One Postgres for the whole test run — a Testcontainers instance by default,
/// or whatever GATHERUM_TEST_DB points at. Each test class carves out its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string adminConnectionString = "";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GATHERUM_TEST_DB") is { Length: > 0 } external)
        {
            adminConnectionString = external;
            return;
        }
        container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        adminConnectionString = container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"gatherum_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name };
        await using var db = CreateContext(builder.ConnectionString);
        await db.Database.MigrateAsync();
        return builder.ConnectionString;
    }

    public static GatherumDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<GatherumDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("Gatherum.Infrastructure"))
            .Options);

    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
