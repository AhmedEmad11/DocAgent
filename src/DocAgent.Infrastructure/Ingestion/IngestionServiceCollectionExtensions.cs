using DocAgent.Infrastructure.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>Resolved location of the embedding model files.</summary>
    public sealed record ModelDirectory(string Path);

    public static IServiceCollection AddDocumentIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));
        services.Configure<ChunkingOptions>(configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));
        services.Configure<DocumentLimitsOptions>(configuration.GetSection(DocumentLimitsOptions.SectionName));

        // Heavy objects are created on first use, so the API starts (and non-document tests run) without the model;
        // a missing model then fails ingestion with a message that names scripts/download-models.sh.
        services.AddSingleton(sp =>
        {
            var embedding = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new ModelDirectory(ModelPathResolver.Resolve(embedding.ModelPath, env.ContentRootPath));
        });
        services.AddSingleton(sp => EmbeddingTokenizer.Load(sp.GetRequiredService<ModelDirectory>().Path));
        services.AddSingleton<ITokenCounter>(sp => sp.GetRequiredService<EmbeddingTokenizer>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var embedding = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            return new OnnxEmbeddingGenerator(
                sp.GetRequiredService<ModelDirectory>().Path, sp.GetRequiredService<EmbeddingTokenizer>(),
                embedding.MaxSequenceLength, embedding.BatchSize);
        });
        services.AddSingleton(sp => new TextChunker(sp.GetRequiredService<ITokenCounter>(), sp.GetRequiredService<IOptions<ChunkingOptions>>().Value));

        services.AddSingleton<IngestionSignal>();
        services.AddScoped<IngestionJobQueue>();
        services.AddScoped<IngestionProcessor>();

        if (configuration.GetValue("Documents:Ingestion:WorkerEnabled", true))
        {
            services.AddHostedService<IngestionWorker>();
        }

        return services;
    }
}
