using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.VectorStore;

public sealed record SelectedVectorStoreDescriptor(string Provider, string ResetTarget, string ResetDescription);

public interface ISelectedVectorStoreDescriptor
{
    SelectedVectorStoreDescriptor Value { get; }
}

public sealed class SelectedVectorStoreDescriptorProvider(IOptions<LocalRagOptions> options) : ISelectedVectorStoreDescriptor
{
    public SelectedVectorStoreDescriptor Value => options.Value.VectorStore.Provider switch
    {
        VectorStoreProvider.Weaviate => new("Weaviate", $"weaviate:{options.Value.VectorStore.Weaviate.Collection}", "the owned Local RAG Weaviate collection"),
        VectorStoreProvider.LanceDb => new("LanceDb", $"lancedb:{options.Value.VectorStore.LanceDb.Table}", "the owned Local RAG LanceDB table"),
        _ => throw new InvalidOperationException("The configured vector-store provider is not supported.")
    };
}

public static class SelectedVectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddSelectedVectorStore(this IServiceCollection services)
    {
        services.AddHttpClient<WeaviateVectorStore>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<LocalRagOptions>>().Value.VectorStore.Weaviate;
            client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(options.ApiKey)) client.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
        });
        services.AddSingleton<LanceDbVectorStore>();
        services.AddSingleton<ISelectedVectorStoreDescriptor, SelectedVectorStoreDescriptorProvider>();
        services.AddSingleton<IVectorStore>(CreateVectorStore);
        services.AddSingleton<IManagementVectorStore>(provider => (IManagementVectorStore)provider.GetRequiredService<IVectorStore>());
        return services;
    }

    private static IVectorStore CreateVectorStore(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IOptions<LocalRagOptions>>().Value.VectorStore.Provider;
        return provider switch
        {
            VectorStoreProvider.Weaviate => services.GetRequiredService<WeaviateVectorStore>(),
            VectorStoreProvider.LanceDb => services.GetRequiredService<LanceDbVectorStore>(),
            _ => throw new InvalidOperationException("The configured vector-store provider is not supported.")
        };
    }
}
