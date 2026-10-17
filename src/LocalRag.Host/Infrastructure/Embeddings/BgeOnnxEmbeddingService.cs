using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Options;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocalRag.Infrastructure.Embeddings;

public sealed class BgeOnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<BertWordPieceTokenizer> _tokenizer;
    private readonly Lazy<bool> _dynamicPadding;
    private static readonly Lazy<EmbeddingRuntimeIdentity?> RuntimeIdentity = new(ReadRuntimeIdentity);
    private string? _loadedModelSha256;

    public BgeOnnxEmbeddingService(IOptions<LocalRagOptions> options)
        : this(options, null)
    {
    }

    internal BgeOnnxEmbeddingService(IOptions<LocalRagOptions> options, BertWordPieceTokenizer? tokenizer)
    {
        _options = options.Value.Embedding;
        var modelDirectory = Environment.ExpandEnvironmentVariables(_options.ModelDirectory);
        _session = new Lazy<InferenceSession>(() =>
        {
            var path = Path.Combine(modelDirectory, "model.onnx");
            // On the guarded Windows platform this also prevents replacement between hash and load.
            using var modelLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(modelLock));
            var session = new InferenceSession(path);
            _loadedModelSha256 = hash;
            return session;
        });
        _tokenizer = new Lazy<BertWordPieceTokenizer>(() =>
            tokenizer ?? new BertWordPieceTokenizer(Path.Combine(modelDirectory, "vocab.txt")));
        _dynamicPadding = new Lazy<bool>(() =>
        {
            var session = _session.Value;
            var inputs = session.InputMetadata.ToDictionary(pair => pair.Key, pair => new EmbeddingInputShape(pair.Value.ElementType, pair.Value.Dimensions));
            return EmbeddingSequencePolicy.AllowsDynamic(_options, _loadedModelSha256, _tokenizer.Value.VocabularySha256, RuntimeIdentity.Value, inputs);
        });
    }

    public string ProfileId => _options.ProfileId;
    internal bool UsesDynamicPadding => _dynamicPadding.Value;

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
        var sequenceLength = EmbeddingSequencePolicy.SelectLength(tokenIds, _dynamicPadding.Value);
        cancellationToken.ThrowIfCancellationRequested();
        var inputIds = new DenseTensor<long>(tokenIds.AsMemory(0, sequenceLength), [1, sequenceLength]);
        var attentionMask = new DenseTensor<long>([1, sequenceLength]);
        var tokenTypeIds = new DenseTensor<long>([1, sequenceLength]);
        for (var index = 0; index < sequenceLength; index++) attentionMask[0, index] = tokenIds[index] == 0 ? 0 : 1;

        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
        };

        using var results = _session.Value.Run(values);
        var hidden = results[0].AsTensor<float>();
        if (hidden.Rank != 3 || hidden.Dimensions[0] != 1 || hidden.Dimensions[1] < sequenceLength || hidden.Dimensions[2] != _options.Dimensions)
        {
            throw new InvalidOperationException($"The ONNX model output shape is incompatible with the configured {_options.Dimensions}-dimension embedding profile.");
        }
        var vector = new float[_options.Dimensions];
        var nonPadding = Math.Max(1, attentionMask.ToArray().Count(value => value == 1));
        for (var token = 0; token < sequenceLength; token++)
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

    private static EmbeddingRuntimeIdentity? ReadRuntimeIdentity()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        try
        {
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>().Where(module => module.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (modules.Length != 1) return null;
            var managedPath = typeof(InferenceSession).Assembly.Location;
            var nativePath = modules[0].FileName;
            if (string.IsNullOrEmpty(managedPath) || string.IsNullOrEmpty(nativePath)) return null;
            using var managed = new FileStream(managedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var native = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(Convert.ToHexString(SHA256.HashData(managed)), Convert.ToHexString(SHA256.HashData(native)), typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Unverifiable runtime identity retains the existing fixed-width inference contract.
            return null;
        }
    }

    public void Dispose()
    {
        if (_session.IsValueCreated) _session.Value.Dispose();
    }
}
