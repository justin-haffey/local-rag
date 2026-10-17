using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LocalRag.Configuration;

/// <summary>Preserves schema-v1 <c>LocalRag:Weaviate</c> configuration during the schema-v2 transition.</summary>
public sealed class LocalRagOptionsCompatibilityPostConfigure(IConfiguration configuration) : IPostConfigureOptions<LocalRagOptions>
{
    public void PostConfigure(string? name, LocalRagOptions options)
    {
        var vectorStore = configuration.GetSection($"{LocalRagOptions.SectionName}:VectorStore");
        if (!vectorStore.GetSection(nameof(VectorStoreOptions.Weaviate)).Exists())
        {
            options.VectorStore = new VectorStoreOptions
            {
                Provider = options.VectorStore.Provider,
                Weaviate = options.Weaviate,
                LanceDb = options.VectorStore.LanceDb
            };
        }
    }
}
