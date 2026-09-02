using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Analysis;
using Gatherum.Infrastructure.Bookmarks;
using Gatherum.Infrastructure.Embedding;
using Gatherum.Infrastructure.Extraction;
using Gatherum.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure;

public static class GatherumServiceCollectionExtensions
{
    public static IServiceCollection AddGatherum(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatherumOptions>(configuration.GetSection(GatherumOptions.Section));
        services.AddDbContext<GatherumDbContext>((provider, options) =>
        {
            var gatherum = provider.GetRequiredService<IOptions<GatherumOptions>>().Value;
            options.UseNpgsql(gatherum.Database.ConnectionString, GatherumNpgsql.Configure);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeAuthorizer, DefaultNodeAuthorizer>();
        services.AddSingleton<IFileStorage, FileSystemStorage>();
        // Html before PlainText: the first extractor that claims a file wins, and an
        // HTML file is text — it should be searchable by its words, not its tags.
        services.AddSingleton<ITextExtractor, HtmlTextExtractor>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, EpubTextExtractor>();
        services.AddSingleton<ITextExtractor, RomTextExtractor>();
        services.AddSingleton<ITextExtractor, ImageMetadataExtractor>();

        AddAnalysis(services, configuration);
        AddEmbedding(services, configuration);
        AddBookmarks(services, configuration);

        services.AddSingleton<INodeMetadataStore, JsonNodeMetadataStore>();
        services.AddScoped<AccessService>();
        services.AddSingleton<FirmwareService>();
        services.AddScoped<UserRoots>();
        services.AddScoped<NodeMetadataWriter>();
        services.AddScoped<Reindexer>();
        services.AddScoped<NodeService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<FileService>();
        services.AddScoped<BookmarkService>();
        services.AddScoped<SharedListService>();
        services.AddScoped<SearchService>();
        services.AddScoped<EmbeddingService>();
        services.AddScoped<UserService>();
        services.AddScoped<ApiKeyService>();
        return services;
    }

    /// <summary>Bookmarks need nothing configured. The capture renders in a headless
    /// Chromium when one can be found — the container ships one, a dev machine may have
    /// a Playwright install, and <c>Gatherum__Bookmarks__BrowserPath</c> names one
    /// explicitly — and degrades to a plain HTTP fetch when none can, so a bare
    /// <c>dotnet run</c> still bookmarks. The plain client is registered either way:
    /// it is the browser's fallback for documents and its second chance at assets, and
    /// it dresses as a browser because plenty of servers refuse a bare one.</summary>
    private static void AddBookmarks(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<HttpPageArchiver>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; Gatherum/1.0)");
            client.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,*/*;q=0.8");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // The blocklist's own client: a list fetch is not a page fetch, and a page's
        // 30-second patience would be an absurd thing to spend on one.
        services.AddHttpClient(AdBlocklistProvider.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; Gatherum/1.0)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        var bookmarks = configuration
            .GetSection($"{GatherumOptions.Section}:{nameof(GatherumOptions.Bookmarks)}")
            .Get<BookmarkOptions>() ?? new BookmarkOptions();
        services.AddSingleton(provider =>
            !bookmarks.BlockAds ? new AdBlocklistProvider(AdBlocklist.None)
            : bookmarks.AdHostsUrl.Length == 0 ? new AdBlocklistProvider(AdBlocklist.Packaged())
            : new AdBlocklistProvider(bookmarks.AdHostsUrl,
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<AdBlocklistProvider>>()));
        if (BrowserPageArchiver.ResolveBrowser(bookmarks.BrowserPath) is { } browser)
            services.AddScoped<IPageArchiver>(provider => new BrowserPageArchiver(browser,
                provider.GetRequiredService<HttpPageArchiver>(),
                provider.GetRequiredService<AdBlocklistProvider>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<BrowserPageArchiver>>()));
        else
            services.AddScoped<IPageArchiver>(provider =>
                provider.GetRequiredService<HttpPageArchiver>());
    }

    /// <summary>Multimedia analysis is opt-in and self-hosted: with no endpoint
    /// configured, no analyzer is registered, nothing claims an image or a recording,
    /// and every upload behaves exactly as it did before this existed. The queue is
    /// registered either way so <see cref="FileService"/> has one to talk to.</summary>
    private static void AddAnalysis(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MediaAnalysisQueue>();

        var analysis = configuration
            .GetSection($"{GatherumOptions.Section}:{nameof(GatherumOptions.Analysis)}")
            .Get<AnalysisOptions>() ?? new AnalysisOptions();
        if (!analysis.IsConfigured)
            return;

        services.AddHttpClient<IMediaAnalyzer, OpenAiMediaAnalyzer>(client =>
        {
            client.BaseAddress = new Uri(analysis.Endpoint.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(analysis.TimeoutSeconds);
            if (analysis.ApiKey.Length > 0)
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", analysis.ApiKey);
        });
        services.AddHostedService<MediaAnalysisWorker>();
    }

    /// <summary>Unlike analysis, semantic search asks nothing of you: a model ships with
    /// the app and runs in this process, so a fresh Gatherum searches by meaning without
    /// being configured to. An endpoint of your own wins when there is one — it is
    /// presumably a better model than twenty-three megabytes can be. Turned off with no
    /// endpoint set, or built without the packaged model present, nothing is registered,
    /// no vector is ever computed, and <see cref="SearchService"/> answers from the
    /// tsvector index alone. The cache and the service are registered either way so
    /// nothing downstream has to ask whether the feature exists.</summary>
    private static void AddEmbedding(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<QueryEmbeddingCache>();

        var embedding = Embedding(configuration);
        if (embedding.IsConfigured)
            services.AddHttpClient<IEmbedder, OpenAiEmbedder>(client =>
            {
                client.BaseAddress = new Uri(embedding.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(embedding.TimeoutSeconds);
                if (embedding.ApiKey.Length > 0)
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", embedding.ApiKey);
            });
        else if (UsesPackagedModel(embedding))
            services.AddSingleton<IEmbedder, LocalEmbedder>();
        else
            return;

        services.AddHostedService<EmbeddingWorker>();
    }

    /// <summary>Whether anything will embed — which is not the same question as whether
    /// an endpoint is configured, and is what the vector schema has to be built for.
    /// Startup asks it without resolving an embedder, because resolving one loads a
    /// model.</summary>
    public static bool EmbeddingEnabled(IConfiguration configuration)
    {
        var embedding = Embedding(configuration);
        return embedding.IsConfigured || UsesPackagedModel(embedding);
    }

    private static bool UsesPackagedModel(EmbeddingOptions embedding) =>
        embedding.Local && LocalEmbedder.IsAvailable(embedding.ModelPath);

    private static EmbeddingOptions Embedding(IConfiguration configuration) =>
        configuration
            .GetSection($"{GatherumOptions.Section}:{nameof(GatherumOptions.Embedding)}")
            .Get<EmbeddingOptions>() ?? new EmbeddingOptions();
}
