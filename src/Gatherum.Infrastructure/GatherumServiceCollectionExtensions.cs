using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Analysis;
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
            options.UseNpgsql(gatherum.Database.ConnectionString,
                npgsql => npgsql.MigrationsAssembly("Gatherum.Infrastructure"));
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeAuthorizer, DefaultNodeAuthorizer>();
        services.AddSingleton<IFileStorage, FileSystemStorage>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, ImageMetadataExtractor>();

        AddAnalysis(services, configuration);

        services.AddScoped<NodeService>();
        services.AddScoped<FileService>();
        services.AddScoped<SearchService>();
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
}
