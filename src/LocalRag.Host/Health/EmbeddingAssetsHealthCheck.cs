using LocalRag.Application;
using LocalRag.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LocalRag.Health;

public sealed class EmbeddingAssetsHealthCheck(IOptions<LocalRagOptions> options, IEmbeddingService embeddings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var directory = Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory);
        var model = Path.Combine(directory, "model.onnx");
        var vocabulary = Path.Combine(directory, "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocabulary))
        {
            return HealthCheckResult.Degraded("Missing model.onnx or vocab.txt. Run scripts/Install-LocalRagEmbeddingModel.ps1 to provision verified local ONNX assets.");
        }

        try
        {
            await embeddings.ValidateAsync(cancellationToken);
            return HealthCheckResult.Healthy("Local ONNX model assets passed inference and profile validation.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Degraded("The local ONNX model assets are incompatible or failed the readiness inference probe.", exception);
        }
    }
}
