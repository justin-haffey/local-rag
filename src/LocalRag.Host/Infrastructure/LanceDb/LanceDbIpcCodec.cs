using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using LocalRag.Domain;

namespace LocalRag.Infrastructure.LanceDb;

internal interface ILanceDbIpcCodec
{
    byte[] EncodeDocuments(IReadOnlyList<VectorDocument> documents);
    byte[] EncodeChunkIds(IReadOnlyList<string> chunkIds);
    IReadOnlyList<SearchResult> DecodeResults(byte[] ipc);
}

internal sealed class LanceDbIpcCodec : ILanceDbIpcCodec
{
    internal const int VectorDimensions = 384;
    internal const int MaxRows = 100;
    internal const int MaxBytes = 16 * 1024 * 1024;

    private static readonly Field[] DocumentFields =
    [
        Utf8("chunkId"), Utf8("sourceId"), Utf8("fileId"), Utf8("relativePath"),
        Utf8("language"), Utf8("symbolName", nullable: true),
        Int32("startLine"), Int32("endLine"), Int32("ordinal"),
        Utf8("content"), Utf8("contentHash"), Int32("tokenCount"),
        Utf8("embeddingProfileId"), Utf8("lastIndexedUtc"),
        Utf8("chunkKind"), Utf8("qualifiedSymbolName", nullable: true),
        Utf8("structuralLocator"), Utf8("chunkerId"), Utf8("chunkerVersion"),
        Utf8("chunkProfileFingerprint"),
        new("vector", new FixedSizeListType(FloatType.Default, VectorDimensions), nullable: false)
    ];

    public byte[] EncodeDocuments(IReadOnlyList<VectorDocument> documents)
    {
        ValidateDocuments(documents);
        var chunks = documents.Select(document => document.Chunk).ToArray();
        var vectorValues = new FloatArray.Builder();
        foreach (var document in documents)
        {
            foreach (var value in document.Vector)
            {
                vectorValues.Append(value);
            }
        }

        var vectorType = (FixedSizeListType)DocumentFields[^1].DataType;
        var vectors = new FixedSizeListArray(
            vectorType,
            documents.Count,
            vectorValues.Build(),
            ArrowBuffer.Empty,
            nullCount: 0,
            offset: 0);
        var arrays = new IArrowArray[]
        {
            Strings(chunks.Select(chunk => chunk.ChunkId)),
            Strings(chunks.Select(chunk => chunk.SourceId)),
            Strings(chunks.Select(chunk => chunk.FileId)),
            Strings(chunks.Select(chunk => chunk.RelativePath)),
            Strings(chunks.Select(chunk => chunk.Language)),
            NullableStrings(chunks.Select(chunk => chunk.SymbolName)),
            Ints(chunks.Select(chunk => chunk.StartLine)),
            Ints(chunks.Select(chunk => chunk.EndLine)),
            Ints(chunks.Select(chunk => chunk.Ordinal)),
            Strings(chunks.Select(chunk => chunk.Content)),
            Strings(chunks.Select(chunk => chunk.ContentHash)),
            Ints(chunks.Select(chunk => chunk.TokenCount)),
            Strings(chunks.Select(chunk => chunk.EmbeddingProfileId)),
            Strings(chunks.Select(chunk => chunk.LastIndexedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))),
            Strings(chunks.Select(chunk => chunk.ChunkKind)),
            NullableStrings(chunks.Select(chunk => chunk.QualifiedSymbolName)),
            Strings(chunks.Select(chunk => chunk.StructuralLocator)),
            Strings(chunks.Select(chunk => chunk.ChunkerId)),
            Strings(chunks.Select(chunk => chunk.ChunkerVersion)),
            Strings(chunks.Select(chunk => chunk.ChunkProfileFingerprint)),
            vectors
        };
        return Write(new RecordBatch(new Schema(DocumentFields, null), arrays, documents.Count));
    }

    public byte[] EncodeChunkIds(IReadOnlyList<string> chunkIds)
    {
        if (chunkIds.Count > MaxRows) throw new ArgumentOutOfRangeException(nameof(chunkIds), "A delete batch cannot exceed 100 rows.");
        if (chunkIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Chunk IDs must be non-empty.", nameof(chunkIds));
        var schema = new Schema([Utf8("id")], null);
        return Write(new RecordBatch(schema, [Strings(chunkIds)], chunkIds.Count));
    }

    public IReadOnlyList<SearchResult> DecodeResults(byte[] ipc)
    {
        ArgumentNullException.ThrowIfNull(ipc);
        if (ipc.Length > MaxBytes) throw new InvalidDataException("The native search result exceeded the 16 MiB IPC bound.");
        if (ipc.Length == 0) return [];

        try
        {
            using var stream = new MemoryStream(ipc, writable: false);
            using var reader = new ArrowStreamReader(stream);
            var results = new List<SearchResult>();
            while (reader.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                RequireResultSchema(batch.Schema);
                var chunkIds = Strings(batch, "chunkId");
                var sourceIds = Strings(batch, "sourceId");
                var relativePaths = Strings(batch, "relativePath");
                var languages = Strings(batch, "language");
                var symbols = Strings(batch, "symbolName");
                var startLines = Ints(batch, "startLine");
                var endLines = Ints(batch, "endLine");
                var scores = Doubles(batch, "score");
                var contents = Strings(batch, "content");
                var contentHashes = Strings(batch, "contentHash");
                var lastIndexed = Strings(batch, "lastIndexedUtc");
                var chunkKinds = Strings(batch, "chunkKind");
                var qualifiedSymbols = Strings(batch, "qualifiedSymbolName");
                var locators = Strings(batch, "structuralLocator");
                var chunkerIds = Strings(batch, "chunkerId");
                var chunkerVersions = Strings(batch, "chunkerVersion");
                var profileFingerprints = Strings(batch, "chunkProfileFingerprint");

                for (var row = 0; row < batch.Length; row++)
                {
                    results.Add(new SearchResult(
                        Required(chunkIds, row, "chunkId"),
                        Required(sourceIds, row, "sourceId"),
                        Required(relativePaths, row, "relativePath"),
                        Required(languages, row, "language"),
                        symbols.GetString(row),
                        Required(startLines, row, "startLine"),
                        Required(endLines, row, "endLine"),
                        Required(scores, row, "score"),
                        Required(contents, row, "content"),
                        Required(contentHashes, row, "contentHash"),
                        DateTimeOffset.Parse(
                            Required(lastIndexed, row, "lastIndexedUtc"),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                        Required(chunkKinds, row, "chunkKind"),
                        qualifiedSymbols.GetString(row),
                        Required(locators, row, "structuralLocator"),
                        Required(chunkerIds, row, "chunkerId"),
                        Required(chunkerVersions, row, "chunkerVersion"),
                        Required(profileFingerprints, row, "chunkProfileFingerprint")));
                }
            }
            return results;
        }
        catch (Exception exception) when (exception is not LanceDbConfigurationException)
        {
            throw new InvalidDataException("The native search result was not valid LocalRAG Arrow IPC.", exception);
        }
    }

    internal static void ValidateDocuments(IReadOnlyList<VectorDocument> documents)
    {
        if (documents.Count is < 1 or > MaxRows)
        {
            throw new ArgumentOutOfRangeException(nameof(documents), "An upsert batch must contain between 1 and 100 rows.");
        }
        foreach (var document in documents)
        {
            if (document.Vector.Count != VectorDimensions)
            {
                throw new ArgumentException("Every LanceDB vector must contain exactly 384 values.", nameof(documents));
            }
            if (document.Vector.Any(value => !float.IsFinite(value)))
            {
                throw new ArgumentException("LanceDB vectors may contain only finite values.", nameof(documents));
            }
        }
    }

    private static byte[] Write(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, batch.Schema, leaveOpen: true))
        {
            writer.WriteRecordBatchAsync(batch).GetAwaiter().GetResult();
            writer.WriteEndAsync().GetAwaiter().GetResult();
        }
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException("The encoded LanceDB batch exceeded the 16 MiB IPC bound.");
        }
        return stream.ToArray();
    }

    private static StringArray Strings(IEnumerable<string> values)
    {
        var builder = new StringArray.Builder();
        foreach (var value in values) builder.Append(value);
        return builder.Build();
    }

    private static StringArray NullableStrings(IEnumerable<string?> values)
    {
        var builder = new StringArray.Builder();
        foreach (var value in values)
        {
            if (value is null) builder.AppendNull();
            else builder.Append(value);
        }
        return builder.Build();
    }

    private static Int32Array Ints(IEnumerable<int> values)
    {
        var builder = new Int32Array.Builder();
        foreach (var value in values) builder.Append(value);
        return builder.Build();
    }

    private static Field Utf8(string name, bool nullable = false) => new(name, StringType.Default, nullable);
    private static Field Int32(string name) => new(name, Int32Type.Default, nullable: false);

    private static void RequireResultSchema(Schema schema)
    {
        string[] fields =
        [
            "chunkId", "sourceId", "relativePath", "language", "symbolName",
            "startLine", "endLine", "score", "content", "contentHash",
            "lastIndexedUtc", "chunkKind", "qualifiedSymbolName", "structuralLocator",
            "chunkerId", "chunkerVersion", "chunkProfileFingerprint"
        ];
        foreach (var field in fields)
        {
            if (schema.GetFieldByName(field) is null)
            {
                throw new InvalidDataException($"The native result omitted required field '{field}'.");
            }
        }
    }

    private static StringArray Strings(RecordBatch batch, string name) =>
        batch.Column(batch.Schema.GetFieldIndex(name)) as StringArray
        ?? throw new InvalidDataException($"The native result field '{name}' had the wrong type.");

    private static Int32Array Ints(RecordBatch batch, string name) =>
        batch.Column(batch.Schema.GetFieldIndex(name)) as Int32Array
        ?? throw new InvalidDataException($"The native result field '{name}' had the wrong type.");

    private static DoubleArray Doubles(RecordBatch batch, string name) =>
        batch.Column(batch.Schema.GetFieldIndex(name)) as DoubleArray
        ?? throw new InvalidDataException($"The native result field '{name}' had the wrong type.");

    private static string Required(StringArray array, int index, string name) =>
        array.GetString(index) ?? throw new InvalidDataException($"The native result field '{name}' contained null.");

    private static int Required(Int32Array array, int index, string name) =>
        array.GetValue(index) ?? throw new InvalidDataException($"The native result field '{name}' contained null.");

    private static double Required(DoubleArray array, int index, string name) =>
        array.GetValue(index) ?? throw new InvalidDataException($"The native result field '{name}' contained null.");
}
