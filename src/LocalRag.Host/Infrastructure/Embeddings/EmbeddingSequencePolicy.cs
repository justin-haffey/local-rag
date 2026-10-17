using LocalRag.Configuration;

namespace LocalRag.Infrastructure.Embeddings;

internal sealed record EmbeddingRuntimeIdentity(string ManagedSha256, string NativeSha256, string AssemblyVersion, bool WindowsX64);
internal sealed record EmbeddingInputShape(Type ElementType, int[] Dimensions);

/// <summary>Limits padding optimization to the experimentally validated embedding contract.</summary>
internal static class EmbeddingSequencePolicy
{
    internal const string ModelSha256 = "828E1496D7FABB79CFA4DCD84FA38625C0D3D21DA474A00F08DB0F559940CF35";
    internal const string VocabularySha256 = "07ECED375CEC144D27C900241F3E339478DEC958F92FDDBC551F295C992038A3";
    internal const string OrtManagedSha256 = "EA768B03B1C7560AE524E65807652763163D92DCC598151BE38C3107658AE6C9";
    internal const string OrtNativeSha256 = "79DF49BCBEFB604C019785925DAEF859C0348280616364E28103D73D4152F6D7";

    internal static bool AllowsDynamic(EmbeddingOptions options, string? modelSha256, string vocabularySha256, EmbeddingRuntimeIdentity? runtime, IReadOnlyDictionary<string, EmbeddingInputShape> inputs)
    {
        var dynamic = true;
        foreach (var name in new[] { "input_ids", "attention_mask", "token_type_ids" })
        {
            if (!inputs.TryGetValue(name, out var shape)) throw new InvalidOperationException($"The ONNX model is missing required input '{name}'. Repair the configured embedding model.");
            if (shape.ElementType != typeof(long) || shape.Dimensions.Length != 2 || shape.Dimensions[0] is not (-1 or 1))
                throw new InvalidOperationException($"The ONNX input '{name}' must be int64 rank 2 with batch size 1 or dynamic. Repair the configured embedding model.");
            var sequence = shape.Dimensions[1];
            if (sequence != -1 && sequence != options.MaximumTokens)
                throw new InvalidOperationException($"The ONNX input '{name}' sequence length must be dynamic or equal the configured maximum of {options.MaximumTokens}. Repair the model/configuration.");
            dynamic &= sequence == -1;
        }
        return dynamic && runtime is { WindowsX64: true, AssemblyVersion: "1.27.1.0" } &&
            runtime.ManagedSha256 == OrtManagedSha256 && runtime.NativeSha256 == OrtNativeSha256 &&
            modelSha256 == ModelSha256 && vocabularySha256 == VocabularySha256 &&
            options.MaximumTokens == 512 && options.Dimensions == 384 && options.ProfileId == "bge-small-en-v1.5-384" &&
            options.TokenizerId == "bert-wordpiece-lowercase-v1" && options.QueryPrefix == string.Empty && options.PassagePrefix == string.Empty;
    }

    internal static int SelectLength(long[] tokenIds, bool dynamic) => dynamic ? Math.Max(3, Array.FindLastIndex(tokenIds, id => id != 0) + 1) : tokenIds.Length;
}
