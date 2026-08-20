using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Extraction;
using Gatherum.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatherum.Tests;

/// <summary>The real service stack over a test database and a temp-dir blob store —
/// the same wiring AddGatherum does, minus DI.</summary>
public sealed class ServiceHarness : IAsyncDisposable
{
    public GatherumDbContext Db { get; }
    public NodeService Nodes { get; }
    public CategoryService Categories { get; }
    public FileService Files { get; }
    public SearchService Search { get; }
    public ManualClock Clock { get; } = new();
    public MediaAnalysisQueue AnalysisQueue { get; } = new();

    /// <summary>Stands in for a model without one: tests say what it should have read
    /// and heard, and assert on everything around it — the queueing, the reuse, the
    /// search text — which is the part that has to be right.</summary>
    public FakeMediaAnalyzer Analyzer { get; } = new();

    private readonly string storageRoot =
        Path.Combine(Path.GetTempPath(), $"gatherum-test-{Guid.NewGuid():N}");

    private readonly FileSystemStorage storage;

    public ServiceHarness(string connectionString)
    {
        Db = PostgresFixture.CreateContext(connectionString);
        var authorizer = new DefaultNodeAuthorizer();
        storage = new FileSystemStorage(Options.Create(
            new GatherumOptions { Storage = new StorageOptions { Root = storageRoot } }));
        Nodes = new NodeService(Db, authorizer, Clock);
        Categories = new CategoryService(Db, Nodes, authorizer);
        Files = new FileService(Db, Nodes, storage,
            [new PlainTextExtractor(), new PdfTextExtractor(), new DocxTextExtractor(),
                new ImageMetadataExtractor()],
            [Analyzer], AnalysisQueue, Clock, NullLogger<FileService>.Instance);
        Search = new SearchService(Db, authorizer);
    }

    public async Task<Guid> AddUserAsync(string name)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Subject = name,
            Email = $"{name}@example.org",
            DisplayName = name,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>One pass of what MediaAnalysisWorker does per queued file, minus the
    /// hosting: find the pending work, hand the blob to an analyzer, record the answer.
    /// The worker keeps no logic of its own beyond that, so this exercises the same
    /// FileService doors it calls.</summary>
    public async Task AnalyzePendingAsync()
    {
        foreach (var id in await Files.PendingAnalysisIdsAsync())
        {
            var version = await Db.FileVersions
                .Where(v => v.Id == id)
                .Select(v => new { v.Hash, v.MediaType, v.FileName, v.SizeBytes })
                .FirstAsync();
            var source = new MediaSource(version.Hash, version.MediaType, version.FileName,
                version.SizeBytes, ct => storage.OpenReadAsync(version.Hash, ct));
            try
            {
                await Files.ApplyAnalysisAsync(id, await Analyzer.AnalyzeAsync(source));
            }
            catch (Exception ex)
            {
                await Files.FailAnalysisAsync(id, ex.Message);
            }
        }
    }

    public async Task<Node> ReloadAsync(Guid userId, Guid nodeId)
    {
        Db.ChangeTracker.Clear();
        return await Nodes.GetWithBodyAsync(userId, nodeId);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
    }
}
