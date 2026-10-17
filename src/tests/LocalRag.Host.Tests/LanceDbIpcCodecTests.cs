using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using System.Globalization;
using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LanceDbIpcCodecTests
{
    [Fact]
    public async Task EncodeDocumentsProducesBoundedArrowStreamWithFixedVector()
    {
        var codec = new LanceDbIpcCodec();
        var bytes = codec.EncodeDocuments([Document("chunk-1", "source-1")]);

        Assert.InRange(bytes.Length, 1, LanceDbIpcCodec.MaxBytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new ArrowStreamReader(stream);
        using var batch = await reader.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(1, batch.Length);
        Assert.Equal("chunk-1", Assert.IsType<StringArray>(batch.Column("chunkId")).GetString(0));
        var vectors = Assert.IsType<FixedSizeListArray>(batch.Column("vector"));
        Assert.Equal(
            LanceDbIpcCodec.VectorDimensions,
            Assert.IsType<FixedSizeListType>(batch.Schema.GetFieldByName("vector").DataType).ListSize);
        Assert.Equal(LanceDbIpcCodec.VectorDimensions, vectors.Values.Length);
        Assert.Null(await reader.ReadNextRecordBatchAsync());
    }

    [Fact]
    public void EncodeChunkIdsSupportsEmptyAndRejectsInvalidIdentifiers()
    {
        var codec = new LanceDbIpcCodec();

        Assert.NotEmpty(codec.EncodeChunkIds([]));
        Assert.Throws<ArgumentException>(() => codec.EncodeChunkIds([""]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => codec.EncodeChunkIds(Enumerable.Range(0, LanceDbIpcCodec.MaxRows + 1)
                .Select(index => index.ToString(CultureInfo.InvariantCulture))
                .ToArray()));
    }

    [Fact]
    public void DecodeResultsMapsNativeArrowSchema()
    {
        var indexed = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var batch = ResultBatch(indexed);
        var codec = new LanceDbIpcCodec();

        var result = Assert.Single(codec.DecodeResults(Write(batch)));

        Assert.Equal("chunk-1", result.ChunkId);
        Assert.Equal("source-1", result.SourceId);
        Assert.Equal("Example.Run", result.QualifiedSymbolName);
        Assert.Equal(0.75, result.Score);
        Assert.Equal(indexed, result.LastIndexedUtc);
    }

    [Fact]
    public void DecodeResultsRejectsMalformedPayload()
    {
        var codec = new LanceDbIpcCodec();

        Assert.Throws<InvalidDataException>(() => codec.DecodeResults([1, 2, 3]));
        Assert.Throws<InvalidDataException>(
            () => codec.DecodeResults(new byte[LanceDbIpcCodec.MaxBytes + 1]));
    }

    private static VectorDocument Document(string chunkId, string sourceId)
    {
        var chunk = new ChunkRecord(
            chunkId,
            sourceId,
            "file-1",
            "src/example.cs",
            "csharp",
            "Run",
            1,
            4,
            0,
            "void Run() { }",
            "hash-1",
            8,
            "embedding-v1",
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
            "symbol",
            "Example.Run",
            "symbol:Example.Run",
            "csharp",
            "1",
            "profile-1");
        return new VectorDocument(
            chunk,
            Enumerable.Range(0, LanceDbIpcCodec.VectorDimensions)
                .Select(index => index == 0 ? 1f : 0f)
                .ToArray());
    }

    private static RecordBatch ResultBatch(DateTimeOffset indexed)
    {
        var fields = new[]
        {
            Utf8("chunkId"), Utf8("sourceId"), Utf8("relativePath"), Utf8("language"),
            Utf8("symbolName", true), Int32("startLine"), Int32("endLine"),
            new Field("score", DoubleType.Default, false), Utf8("content"),
            Utf8("contentHash"), Utf8("lastIndexedUtc"), Utf8("chunkKind"),
            Utf8("qualifiedSymbolName", true), Utf8("structuralLocator"),
            Utf8("chunkerId"), Utf8("chunkerVersion"), Utf8("chunkProfileFingerprint")
        };
        IArrowArray[] arrays =
        [
            Strings("chunk-1"), Strings("source-1"), Strings("src/example.cs"),
            Strings("csharp"), Strings("Run"), Ints(1), Ints(4),
            new DoubleArray.Builder().Append(0.75).Build(), Strings("void Run() { }"),
            Strings("hash-1"), Strings(indexed.ToString("O")), Strings("symbol"),
            Strings("Example.Run"), Strings("symbol:Example.Run"), Strings("csharp"),
            Strings("1"), Strings("profile-1")
        ];
        return new RecordBatch(new Schema(fields, null), arrays, 1);
    }

    private static byte[] Write(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        using var writer = new ArrowStreamWriter(stream, batch.Schema, leaveOpen: true);
        writer.WriteRecordBatchAsync(batch).GetAwaiter().GetResult();
        writer.WriteEndAsync().GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static StringArray Strings(string value) => new StringArray.Builder().Append(value).Build();
    private static Int32Array Ints(int value) => new Int32Array.Builder().Append(value).Build();
    private static Field Utf8(string name, bool nullable = false) => new(name, StringType.Default, nullable);
    private static Field Int32(string name) => new(name, Int32Type.Default, false);
}
