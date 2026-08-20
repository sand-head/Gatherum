using Gatherum.Core.Data;
using Gatherum.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gatherum.Tests;

/// <summary>One Postgres for the whole test run — a Testcontainers instance by default,
/// or whatever GATHERUM_TEST_DB points at. Each test class carves out its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Vector width for the whole suite: eight dimensions FakeEmbedder reserves
    /// for declared subjects, and twenty-four it scatters hashed words across.</summary>
    public const int EmbeddingDimensions = 32;


    private PostgreSqlContainer? container;
    private string adminConnectionString = "";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GATHERUM_TEST_DB") is { Length: > 0 } external)
        {
            adminConnectionString = external;
            return;
        }
        container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
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
        // The app does this at startup once a model is configured; the tests configure a
        // fake one, so they have to size the column themselves.
        await EmbeddingSchema.EnsureAsync(db, EmbeddingDimensions, NullLogger.Instance);
        return builder.ConnectionString;
    }

    public static GatherumDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<GatherumDbContext>()
            .UseNpgsql(connectionString, GatherumNpgsql.Configure)
            .Options);

    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
