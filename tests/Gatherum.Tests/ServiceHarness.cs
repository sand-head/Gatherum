using Gatherum.Core;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Extraction;
using Gatherum.Infrastructure.Storage;
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

    private readonly string storageRoot =
        Path.Combine(Path.GetTempPath(), $"gatherum-test-{Guid.NewGuid():N}");

    public ServiceHarness(string connectionString)
    {
        Db = PostgresFixture.CreateContext(connectionString);
        var authorizer = new DefaultNodeAuthorizer();
        var storage = new FileSystemStorage(Options.Create(
            new GatherumOptions { Storage = new StorageOptions { Root = storageRoot } }));
        Nodes = new NodeService(Db, authorizer, Clock);
        Categories = new CategoryService(Db, Nodes, authorizer);
        Files = new FileService(Db, Nodes, storage,
            [new PlainTextExtractor(), new PdfTextExtractor(), new DocxTextExtractor(),
                new ImageMetadataExtractor()],
            Clock, NullLogger<FileService>.Instance);
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
