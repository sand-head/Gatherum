using System.Data.Common;
using System.Globalization;
using Gatherum.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gatherum.Infrastructure.Data;

/// <summary>Reconciles the vector column with the model actually configured. The width of
/// an embedding is a property of the model, and pgvector wants it in the column type
/// before it will index anything — but a migration is written once and a model is chosen
/// later, so the migration leaves the column dimensionless and this settles it at
/// startup. Changing <c>Gatherum__Embedding__Dimensions</c> is therefore an env var and a
/// restart: the old vectors are dropped (vectors of two widths, or two models, are not
/// comparable, and blending them would silently mis-rank), every node is marked
/// unembedded, and the worker earns them back.</summary>
public static class EmbeddingSchema
{
    private const string IndexName = "IX_NodeEmbeddings_Embedding";

    public static async Task EnsureAsync(GatherumDbContext db, int dimensions, ILogger logger,
        CancellationToken ct = default)
    {
        if (dimensions <= 0)
            throw new InvalidOperationException(
                $"Gatherum__Embedding__Dimensions must be positive, not {dimensions}.");

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var current = await CurrentDimensionsAsync(db, ct);
            var indexed = await ScalarAsync(db,
                "SELECT to_regclass('\"" + IndexName + "\"') IS NOT NULL", ct) is true;
            if (current == dimensions && indexed)
                return;

            if (current != dimensions)
            {
                if (current > 0)
                    logger.LogWarning(
                        "Embedding width changed from {Old} to {New}; discarding every stored " +
                        "vector and re-embedding from scratch", current, dimensions);
                // A type modifier cannot be a parameter, so the width is written into the
                // statement — safe because it is an int this method has already refused
                // to accept unless it is positive.
                await ExecuteAsync(db,
                    "DROP INDEX IF EXISTS \"" + IndexName + "\";" +
                    "TRUNCATE TABLE \"NodeEmbeddings\";" +
                    "ALTER TABLE \"NodeEmbeddings\" ALTER COLUMN \"Embedding\" TYPE vector(" +
                    dimensions.ToString(CultureInfo.InvariantCulture) + ");" +
                    "UPDATE \"Nodes\" SET \"EmbeddedFingerprint\" = '';", ct);
            }

            // Cosine, because the question asked of these vectors is "about the same
            // thing?" and length carries no part of that answer.
            await ExecuteAsync(db,
                "CREATE INDEX IF NOT EXISTS \"" + IndexName + "\" ON \"NodeEmbeddings\" " +
                "USING hnsw (\"Embedding\" vector_cosine_ops);", ct);
            logger.LogInformation("Vector index ready at {Dimensions} dimensions", dimensions);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>pgvector keeps a column's width in the type modifier, so this is where the
    /// database says which model it was last filled by. -1 means dimensionless: the state
    /// the migration leaves behind, before any model is configured.</summary>
    private static async Task<int> CurrentDimensionsAsync(GatherumDbContext db, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(db, """
            SELECT atttypmod FROM pg_attribute
            WHERE attrelid = '"NodeEmbeddings"'::regclass AND attname = 'Embedding'
            """, ct), CultureInfo.InvariantCulture);

    /// <summary>Schema statements go through a plain command rather than
    /// <c>ExecuteSqlRaw</c>: none of this can be parameterized, and EF's analyzer is
    /// right to say so about every call that could be.</summary>
    private static async Task ExecuteAsync(GatherumDbContext db, string sql, CancellationToken ct)
    {
        await using var command = Command(db, sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(GatherumDbContext db, string sql, CancellationToken ct)
    {
        await using var command = Command(db, sql);
        return await command.ExecuteScalarAsync(ct);
    }

    private static DbCommand Command(GatherumDbContext db, string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
