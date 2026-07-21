using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Embeddings;

public sealed class BgeOnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<BertWordPieceTokenizer> _tokenizer;

    public BgeOnnxEmbeddingService(IOptions<LocalRagOptions> options)
        : this(options, null)
    {
    }

    internal BgeOnnxEmbeddingService(IOptions<LocalRagOptions> options, BertWordPieceTokenizer? tokenizer)
    {
        _options = options.Value.Embedding;
        var modelDirectory = Environment.ExpandEnvironmentVariables(_options.ModelDirectory);
        _session = new Lazy<InferenceSession>(() => new InferenceSession(Path.Combine(modelDirectory, "model.onnx")));
        _tokenizer = new Lazy<BertWordPieceTokenizer>(() =>
            tokenizer ?? new BertWordPieceTokenizer(Path.Combine(modelDirectory, "vocab.txt")));
    }

    public string ProfileId => _options.ProfileId;

    public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) =>
        EmbedAsync(_options.QueryPrefix + input, cancellationToken);

    public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken) =>
        EmbedAsync(_options.PassagePrefix + input, cancellationToken);

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var session = _session.Value;
        var requiredInputs = new[] { "input_ids", "attention_mask", "token_type_ids" };
        var missingInputs = requiredInputs.Where(input => !session.InputMetadata.ContainsKey(input)).ToArray();
        if (missingInputs.Length > 0)
        {
            throw new InvalidOperationException($"The ONNX model is missing required inputs: {string.Join(", ", missingInputs)}.");
        }

        var vector = await EmbedPassageAsync("local rag embedding readiness probe", cancellationToken);
        if (vector.Count != _options.Dimensions || vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException($"The ONNX embedding probe did not produce {_options.Dimensions} finite values.");
        }

        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (Math.Abs(magnitude - 1d) > 0.001d)
        {
            throw new InvalidOperationException("The ONNX embedding probe did not produce a normalized vector.");
        }
    }

    private Task<IReadOnlyList<float>> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokenIds = _tokenizer.Value.Encode(input, _options.MaximumTokens);
        var inputIds = new DenseTensor<long>(tokenIds, [1, _options.MaximumTokens]);
        var attentionMask = new DenseTensor<long>([1, _options.MaximumTokens]);
        var tokenTypeIds = new DenseTensor<long>([1, _options.MaximumTokens]);
        for (var index = 0; index < tokenIds.Length; index++) attentionMask[0, index] = tokenIds[index] == 0 ? 0 : 1;

        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
        };

        using var results = _session.Value.Run(values);
        var hidden = results[0].AsTensor<float>();
        if (hidden.Rank != 3 || hidden.Dimensions[0] != 1 || hidden.Dimensions[1] < _options.MaximumTokens || hidden.Dimensions[2] != _options.Dimensions)
        {
            throw new InvalidOperationException($"The ONNX model output shape is incompatible with the configured {_options.Dimensions}-dimension embedding profile.");
        }
        var vector = new float[_options.Dimensions];
        var nonPadding = Math.Max(1, attentionMask.ToArray().Count(value => value == 1));
        for (var token = 0; token < _options.MaximumTokens; token++)
        {
            if (attentionMask[0, token] == 0) continue;
            for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] += hidden[0, token, dimension];
        }

        var magnitude = 0d;
        for (var dimension = 0; dimension < vector.Length; dimension++)
        {
            vector[dimension] /= nonPadding;
            magnitude += vector[dimension] * vector[dimension];
        }
        magnitude = Math.Sqrt(magnitude);
        if (magnitude > 0)
        {
            for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(vector[dimension] / magnitude);
        }

        return Task.FromResult<IReadOnlyList<float>>(vector);
    }

    public void Dispose()
    {
        if (_session.IsValueCreated) _session.Value.Dispose();
    }
}
