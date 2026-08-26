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
    public AccessService Access { get; }
    public UserRoots Roots { get; }
    public INodeMetadataStore Metadata { get; }
    public NodeMetadataWriter Sidecar { get; }
    public IFileStorage Storage => storage;

    /// <summary>The live options the stack was built with, so a test can flip an
    /// operator's switch and see the effect immediately.</summary>
    public IOptions<GatherumOptions> Settings { get; private set; } = null!;
    public CategoryService Categories { get; }
    public FileService Files { get; }
    public BookmarkService Bookmarks { get; }
    public SearchService Search { get; }
    public ManualClock Clock { get; } = new();
    public MediaAnalysisQueue AnalysisQueue { get; } = new();
    public EmbeddingService Embeddings { get; }

    /// <summary>Stands in for an embedding model. Registers no subjects by default, so
    /// every text embeds to the same point and the tests that predate semantic search
    /// see a vector half that ranks nothing above anything.</summary>
    public FakeEmbedder Embedder { get; } = new();

    /// <summary>Stands in for a model without one: tests say what it should have read
    /// and heard, and assert on everything around it — the queueing, the reuse, the
    /// search text — which is the part that has to be right.</summary>
    public FakeMediaAnalyzer Analyzer { get; } = new();

    /// <summary>Stands in for the web, so a bookmark test never fetches anything.</summary>
    public FakePageArchiver Archiver { get; } = new();

    private readonly string storageRoot;

    /// <summary>The directory the whole knowledge base lives in — the system of record,
    /// and the thing a recovery test is allowed to keep when it throws the index away.</summary>
    public string StorageRoot => storageRoot;

    /// <summary>Whether this harness owns its storage. A fork shares the original's
    /// directory and must not delete it out from under the harness that made it.</summary>
    private readonly bool ownsStorage;

    private readonly FileSystemStorage storage;

    public ServiceHarness(string connectionString)
        : this(connectionString, Path.Combine(Path.GetTempPath(), $"gatherum-test-{Guid.NewGuid():N}"),
            ownsStorage: true)
    {
    }

    /// <summary>A second stack over the same directories and an empty database — what a
    /// restored deployment looks like the moment before it reindexes.</summary>
    public ServiceHarness Fork(string connectionString) =>
        new(connectionString, storageRoot, ownsStorage: false);

    private ServiceHarness(string connectionString, string storageRoot, bool ownsStorage)
    {
        this.storageRoot = storageRoot;
        this.ownsStorage = ownsStorage;
        Db = PostgresFixture.CreateContext(connectionString);
        var settings = Options.Create(new GatherumOptions
        {
            Storage = new StorageOptions { Root = storageRoot },
            Embedding = new EmbeddingOptions
            {
                Endpoint = "http://embedder.invalid",
                Model = "fake-embed",
                Dimensions = PostgresFixture.EmbeddingDimensions,
                QueryTimeoutMs = 5_000,
                MaxChunkChars = 400,
                // The shipped default is measured against the packaged model. FakeEmbedder
                // has its own spread — subjects it was told about land together, hashed
                // words scatter — so it states its own cutoff rather than borrowing one
                // tuned for a model it is standing in for. LocalEmbedderTests is where the
                // real default is held to account.
                MaxDistance = 0.55,
            },
        });
        Settings = settings;
        var authorizer = new DefaultNodeAuthorizer(settings);
        storage = new FileSystemStorage(settings);
        Embeddings = new EmbeddingService(Db, [Embedder], new QueryEmbeddingCache(), settings,
            NullLogger<EmbeddingService>.Instance);
        Roots = new UserRoots(Db);
        Metadata = new JsonNodeMetadataStore(storage);
        Sidecar = new NodeMetadataWriter(Db, Metadata, Roots);
        Access = new AccessService(Db, Clock, Sidecar);
        Nodes = new NodeService(Db, authorizer, Clock, Embeddings, Access, Sidecar);
        Files = new FileService(Db, Nodes, storage, Roots, Sidecar,
            [new HtmlTextExtractor(), new PlainTextExtractor(), new PdfTextExtractor(),
                new DocxTextExtractor(), new ImageMetadataExtractor()],
            [Analyzer], AnalysisQueue, Clock, NullLogger<FileService>.Instance);
        Categories = new CategoryService(Db, Nodes, Files, authorizer, Sidecar, Clock);
        Bookmarks = new BookmarkService(Db, Nodes, Files, Archiver, Sidecar);
        Search = new SearchService(Db, authorizer, Embeddings);
    }

    /// <summary>Files a node under a category, then nests that category under another —
    /// which is all a subcategory is now, and what most of these tests used to spell as
    /// "Homelab/Podman".</summary>
    public async Task<Node> FileUnderAsync(Guid userId, Guid nodeId, string name,
        string? nestedUnder = null)
    {
        await Categories.AddAsync(userId, nodeId, name);
        var category = await Categories.ResolveAsync(name)
            ?? throw new InvalidOperationException($"No category '{name}' after filing.");
        if (nestedUnder is { Length: > 0 })
            await Categories.AddAsync(userId, category.Id, nestedUnder);
        return category;
    }

    public async Task<Guid> AddUserAsync(string name)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Subject = name,
            Email = $"{name}@example.org",
            DisplayName = name,
            Username = name,
            // The same mapping production uses, so a username that needs sanitizing is
            // exercised here rather than only in the wild.
            RootName = UserRoots.Propose(name, name, Guid.NewGuid(),
                taken => Db.Users.Any(u => u.RootName == taken)),
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>One pass of what EmbeddingWorker does per sweep: find the nodes whose
    /// text has moved on from their vectors, and re-embed them. The worker keeps no logic
    /// of its own beyond the interval, so this exercises the same door it calls.</summary>
    public async Task EmbedStaleAsync()
    {
        foreach (var node in await Embeddings.StaleNodesAsync(1000))
            await Embeddings.EmbedNodeAsync(node.Id);
        Db.ChangeTracker.Clear();
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
                version.SizeBytes, ct => Files.OpenVersionAsync(id, ct));
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

    /// <summary>Where a node's bytes and its sidecar entry actually live, for the tests
    /// that check what was written down rather than what was indexed.</summary>
    public async Task<NodePath> PathOfAsync(Guid nodeId)
    {
        var node = await Db.Nodes.FindAsync(nodeId)
            ?? throw new InvalidOperationException($"No node {nodeId}.");
        return new NodePath(await Roots.ForAsync(node.OwnerId), node.RelativePath);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        if (ownsStorage && Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
    }
}
