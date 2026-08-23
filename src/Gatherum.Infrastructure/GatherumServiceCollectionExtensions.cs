using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Analysis;
using Gatherum.Infrastructure.Embedding;
using Gatherum.Infrastructure.Extraction;
using Gatherum.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, ImageMetadataExtractor>();

        AddAnalysis(services, configuration);
        AddEmbedding(services, configuration);

        services.AddScoped<AccessService>();
        services.AddScoped<UserRoots>();
        services.AddScoped<NodeService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<FileService>();
        services.AddScoped<SearchService>();
        services.AddScoped<EmbeddingService>();
        services.AddScoped<UserService>();
        services.AddScoped<ApiKeyService>();
        return services;
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
