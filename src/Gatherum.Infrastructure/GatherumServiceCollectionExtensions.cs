using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Services;
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

        services.AddScoped<NodeService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<FileService>();
        services.AddScoped<SearchService>();
        services.AddScoped<UserService>();
        services.AddScoped<ApiKeyService>();
        return services;
    }
}
