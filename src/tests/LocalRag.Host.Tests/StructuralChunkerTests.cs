using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class StructuralChunkerTests
{
    [Theory]
    [MemberData(nameof(SupportedFamilyFixtures))]
    public void SupportedFamilyProducesStructuralProvenance(
        string path,
        string content,
        string chunkerId,
        string expectedKind,
        string? expectedSymbol,
        string? expectedQualifiedSymbol,
        int expectedStartLine)
    {
        var chunker = CreateChunker(DefaultOptions());

        var chunks = chunker.Chunk(Source(), File(path), content);

        var chunk = Assert.Single(chunks, candidate =>
            candidate.ChunkerId == chunkerId && candidate.ChunkKind == expectedKind && candidate.SymbolName == expectedSymbol);
        Assert.Equal(expectedStartLine, chunk.StartLine);
        Assert.InRange(chunk.EndLine, chunk.StartLine, ChunkingText.Lines(content).Length);
        Assert.False(string.IsNullOrWhiteSpace(chunk.StructuralLocator));
        Assert.Equal(expectedQualifiedSymbol, chunk.QualifiedSymbolName);
        Assert.Equal("1", chunk.ChunkerVersion);
        Assert.Equal(chunker.Profile.Fingerprint, chunk.ChunkProfileFingerprint);
    }

    [Fact]
    public void MalformedSupportedInputFallsBackWithoutStructuralClaims()
    {
        var chunker = CreateChunker(DefaultOptions());

        var chunks = chunker.Chunk(Source(), File("Broken.cs"), "public class Broken {\n    void Run() {\n");

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("generic", chunk.ChunkerId);
            Assert.Equal("1", chunk.ChunkerVersion);
            Assert.Equal("text", chunk.ChunkKind);
            Assert.Null(chunk.SymbolName);
            Assert.Null(chunk.QualifiedSymbolName);
            Assert.StartsWith("lines:", chunk.StructuralLocator, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OversizedUnitUsesDeterministicBoundedContinuations()
    {
        var vocabularyPath = Path.Combine(Path.GetTempPath(), $"localrag-structural-vocab-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllLines(vocabularyPath,
            ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "public", "class", "large", "string", "value", "=", "\"", ";", "{", "}", "!"]);
        try
        {
            var options = DefaultOptions(maximumTokens: 24, passagePrefix: "passage: ");
            var tokenizer = new BertWordPieceTokenizer(vocabularyPath);
            var chunker = CreateChunker(options, tokenCounter: tokenizer);
            var content = "public class Large\n{\n    public string Value = \"" + new string('!', 100) + "\";\n}";

            var first = chunker.Chunk(Source(), File("Large.cs"), content);
            var second = chunker.Chunk(Source(), File("Large.cs"), content);

            Assert.True(first.Count > 1);
            Assert.Equal(first.Select(chunk => chunk.ChunkId), second.Select(chunk => chunk.ChunkId));
            Assert.All(first, chunk =>
            {
                Assert.InRange(chunk.TokenCount, 1, 24);
                _ = tokenizer.Encode(options.Value.Embedding.PassagePrefix + chunk.Content, 24);
            });
            Assert.All(first, chunk => Assert.Contains(":segment:", chunk.StructuralLocator, StringComparison.Ordinal));
            Assert.Contains(first, chunk => chunk.ChunkKind == "class-continuation");
            Assert.Contains("public class Large", first[0].Content, StringComparison.Ordinal);
            Assert.Equal(first.Select(chunk => chunk.Ordinal), Enumerable.Range(0, first.Count));
        }
        finally
        {
            System.IO.File.Delete(vocabularyPath);
        }
    }

    [Fact]
    public void CanonicalProfileIgnoresEnabledAdapterOrderAndBindsChunkIdentity()
    {
        var firstOptions = DefaultOptions(enabledAdapters: ["xml", "csharp", "json"]);
        var reorderedOptions = DefaultOptions(enabledAdapters: ["json", "xml", "csharp", "json"]);
        var changedOptions = DefaultOptions(enabledAdapters: ["xml", "csharp", "json"], tokenizerId: "different-tokenizer");
        var first = CreateChunker(firstOptions);
        var reordered = CreateChunker(reorderedOptions);
        var changed = CreateChunker(changedOptions);

        Assert.Equal(first.Profile.Fingerprint, reordered.Profile.Fingerprint);
        Assert.NotEqual(first.Profile.Fingerprint, changed.Profile.Fingerprint);

        const string content = "public class Stable { }";
        var firstId = Assert.Single(first.Chunk(Source(), File("Stable.cs"), content)).ChunkId;
        var reorderedId = Assert.Single(reordered.Chunk(Source(), File("Stable.cs"), content)).ChunkId;
        var changedId = Assert.Single(changed.Chunk(Source(), File("Stable.cs"), content)).ChunkId;
        Assert.Equal(firstId, reorderedId);
        Assert.NotEqual(firstId, changedId);
    }

    [Fact]
    public void CanonicalProfileUsesGenericIdentityWhenAdaptersAreDisabledAndIncludesEmbeddingPrefixes()
    {
        var generic = CreateChunker(DefaultOptions(enabledAdapters: []));
        var first = CreateChunker(DefaultOptions(queryPrefix: "query: ", passagePrefix: "passage: "));
        var changedQuery = CreateChunker(DefaultOptions(queryPrefix: "search: ", passagePrefix: "passage: "));
        var changedPassage = CreateChunker(DefaultOptions(queryPrefix: "query: ", passagePrefix: "document: "));

        var changedGenericPrefix = CreateChunker(DefaultOptions(enabledAdapters: [], passagePrefix: "passage: "));
        Assert.Equal("generic/1", generic.Profile.ChunkerIdentity);
        Assert.NotEqual("generic/1", generic.Profile.Fingerprint);
        Assert.NotEqual(generic.Profile.Fingerprint, changedGenericPrefix.Profile.Fingerprint);
        Assert.NotEqual(first.Profile.Fingerprint, changedQuery.Profile.Fingerprint);
        Assert.NotEqual(first.Profile.Fingerprint, changedPassage.Profile.Fingerprint);
    }

    [Fact]
    public void UnsupportedExtensionUsesMandatoryGenericFallback()
    {
        var chunker = CreateChunker(DefaultOptions());

        var chunk = Assert.Single(chunker.Chunk(Source(), File("notes.txt"), "alpha\nbeta"));

        Assert.Equal("generic", chunk.ChunkerId);
        Assert.Equal("lines:1-2", chunk.StructuralLocator);
        Assert.Equal(chunker.Profile.Fingerprint, chunk.ChunkProfileFingerprint);
    }

    [Fact]
    public void NestedDeclarationsReceiveHierarchicalQualifiedNames()
    {
        var chunker = CreateChunker(DefaultOptions());
        const string csharp = "namespace Demo\n{\n    public class Worker\n    {\n        public void Run() { }\n    }\n}";
        const string python = "class Worker:\n    def run(self):\n        return True";

        Assert.Contains(chunker.Chunk(Source(), File("Nested.cs"), csharp),
            chunk => chunk.SymbolName == "Run" && chunk.QualifiedSymbolName == "Demo.Worker.Run");
        Assert.Contains(chunker.Chunk(Source(), File("nested.py"), python),
            chunk => chunk.SymbolName == "run" && chunk.QualifiedSymbolName == "Worker.run");
    }

    [Fact]
    public void FileScopedNamespaceQualifiesNestedDeclarations()
    {
        var chunks = CreateChunker(DefaultOptions()).Chunk(Source(), File("FileScoped.cs"),
            "namespace Demo;\npublic class Worker\n{\n    public void Run() { }\n}");

        Assert.Contains(chunks, chunk => chunk.SymbolName == "Worker" && chunk.QualifiedSymbolName == "Demo.Worker");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "Run" && chunk.QualifiedSymbolName == "Demo.Worker.Run");
    }

    [Fact]
    public void PythonIgnoresDeclarationsInsideTripleQuotedStrings()
    {
        const string content = "\"\"\"\ndef fake():\n    return False\n\"\"\"\ndef real():\n    return True";
        var chunks = CreateChunker(DefaultOptions()).Chunk(Source(), File("docstrings.py"), content);

        Assert.DoesNotContain(chunks, chunk => chunk.SymbolName == "fake");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "real" && chunk.QualifiedSymbolName == "real");
    }

    [Fact]
    public void PythonMultilineStringContentDoesNotTruncateEnclosingDeclarationSpan()
    {
        const string content = "class Worker:\n    def run(self):\n        text = \"\"\"\ndef fake():\n    return False\n\"\"\"\n        return True\n\ndef outside():\n    return True";
        var chunks = CreateChunker(DefaultOptions()).Chunk(Source(), File("multiline.py"), content);

        var run = Assert.Single(chunks, chunk => chunk.SymbolName == "run");
        Assert.Equal("Worker.run", run.QualifiedSymbolName);
        Assert.Equal(2, run.StartLine);
        Assert.Equal(8, run.EndLine);
        Assert.DoesNotContain(chunks, chunk => chunk.SymbolName == "fake");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "outside" && chunk.QualifiedSymbolName == "outside");
    }

    [Fact]
    public void SupportedAdapterPreservesSearchableTextOutsideDeclarations()
    {
        var chunker = CreateChunker(DefaultOptions());
        const string content = "using Example.Tools;\n\npublic class Worker\n{\n    public void Run() { }\n}\n\n[assembly: ExampleMarker]";

        var chunks = chunker.Chunk(Source(), File("Coverage.cs"), content);

        Assert.Contains(chunks, chunk => chunk.ChunkKind == "text" && chunk.Content.Contains("using Example.Tools", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.ChunkKind == "text" && chunk.Content.Contains("ExampleMarker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("component.tsx", "export function component() { return 1; }", "typescript-javascript")]
    [InlineData("component.js", "export function component() { return 1; }", "typescript-javascript")]
    [InlineData("component.jsx", "export function component() { return 1; }", "typescript-javascript")]
    [InlineData("project.csproj", "<Project><PropertyGroup><Name>demo</Name></PropertyGroup></Project>", "xml")]
    [InlineData("common.props", "<Project><PropertyGroup><Name>demo</Name></PropertyGroup></Project>", "xml")]
    [InlineData("build.targets", "<Project><Target Name=\"Build\" /></Project>", "xml")]
    public void SupportedExtensionVariantsSelectTheirApprovedAdapter(string path, string content, string chunkerId)
    {
        var chunks = CreateChunker(DefaultOptions()).Chunk(Source(), File(path), content);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal(chunkerId, chunk.ChunkerId));
    }

    [Theory]
    [InlineData("broken.cs", "public class Broken {\n    void Run( {\n}")]
    [InlineData("broken.ts", "export function broken( {\n}")]
    [InlineData("broken.py", "def broken(\n    return 1")]
    [InlineData("broken.py", "def broken(:\n    pass")]
    [InlineData("broken.py", "\"\"\"unterminated\ndef fake():")]
    [InlineData("broken.cs", "public class Broken { string Value = \"unterminated;\n}")]
    [InlineData("broken.ts", "export const value = \"unterminated;\n}")]
    [InlineData("broken.js", "/* unterminated block comment")]
    [InlineData("broken.json", "{ \"name\": }")]
    [InlineData("broken.yaml", "top level without colon")]
    [InlineData("broken.yaml", "service:\n  purpose: \"unterminated")]
    [InlineData("broken.yaml", "items: [one, two")]
    [InlineData("broken.toml", "[broken\nvalue = 1")]
    [InlineData("broken.toml", "[service]\npurpose = \"unterminated")]
    [InlineData("broken.toml", "items = [1, 2")]
    [InlineData("broken.xml", "<Root><Broken></Root>")]
    public void MalformedApprovedLanguageFallsBackAtomically(string path, string content)
    {
        var chunks = CreateChunker(DefaultOptions()).Chunk(Source(), File(path), content);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal("generic", chunk.ChunkerId));
    }

    [Theory]
    [InlineData(FaultMode.InvalidBounds)]
    [InlineData(FaultMode.DuplicateLocators)]
    [InlineData(FaultMode.NullUnits)]
    [InlineData(FaultMode.Exception)]
    public void InvalidAdapterOutputOrFailureFallsBackWithoutPartialClaims(FaultMode mode)
    {
        var options = DefaultOptions(enabledAdapters: ["fault"]);
        var chunks = CreateChunker(options, [new FaultingAdapter(mode)])
            .Chunk(Source(), File("sample.fault"), "alpha\nbeta");

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal("generic", chunk.ChunkerId));
    }

    [Fact]
    public void EmptyCommentOnlyAndNormalizedMixedLineEndingsNeverProduceBlankChunks()
    {
        var chunker = CreateChunker(DefaultOptions());
        Assert.Empty(chunker.Chunk(Source(), File("empty.cs"), string.Empty));
        var comments = chunker.Chunk(Source(), File("comments.cs"), "// first\n// second");
        Assert.All(comments, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Content)));
        var mixed = chunker.Chunk(Source(), File("mixed.cs"), "public class Mixed\n{\n    public void Run() { }\n}");
        Assert.Contains(mixed, chunk => chunk.SymbolName == "Mixed" && chunk.StartLine == 1 && chunk.EndLine == 4);
    }

    [Fact]
    public void ChunkingMetricsUseOnlyBoundedSafeTagsAndRecordFallbackDurationAndCounts()
    {
        var measurements = new ConcurrentQueue<(string Instrument, IReadOnlyList<KeyValuePair<string, object?>> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CompositeChunker.MeterName) currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        _ = CreateChunker(DefaultOptions()).Chunk(
            Source(), File("sensitive-relative-name.cs"), "public class Broken {\n    void Run( {\n}");
        listener.Dispose();
        var snapshot = measurements.ToArray();

        Assert.Contains(snapshot, measurement => measurement.Instrument == "localrag.chunking.files");
        Assert.Contains(snapshot, measurement => measurement.Instrument == "localrag.chunking.chunks");
        Assert.Contains(snapshot, measurement => measurement.Instrument == "localrag.chunking.fallbacks");
        Assert.Contains(snapshot, measurement => measurement.Instrument == "localrag.chunking.duration");
        Assert.All(snapshot.SelectMany(measurement => measurement.Tags), tag =>
        {
            Assert.True(tag.Key is "chunker.id" or "chunker.version" or "chunking.outcome");
            Assert.DoesNotContain("sensitive-relative-name", tag.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("public class Broken", tag.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    public static TheoryData<string, string, string, string, string?, string?, int> SupportedFamilyFixtures => new()
    {
        { "Sample.cs", "public class Sample\n{\n    public void Run() { }\n}", "csharp", "class", "Sample", "Sample", 1 },
        { "sample.ts", "export function run() {\n  return 1;\n}", "typescript-javascript", "function", "run", "run", 1 },
        { "sample.py", "def run():\n    return 1", "python", "def", "run", "run", 1 },
        { "README.md", "# Overview\nDetails", "markdown", "section", "Overview", "Overview", 1 },
        { "sample.json", "{\n  \"name\": \"demo\",\n  \"enabled\": true\n}", "json", "property", "name", "name", 2 },
        { "sample.yaml", "service:\n  name: demo\nlogging:\n  level: info", "yaml", "key", "service", "service", 1 },
        { "sample.toml", "[service]\nname = \"demo\"\n[logging]\nlevel = \"info\"", "toml", "table", "service", "service", 1 },
        { "sample.xml", "<Project>\n  <PropertyGroup>\n    <Name>demo</Name>\n  </PropertyGroup>\n</Project>", "xml", "element", "PropertyGroup", "Project.PropertyGroup", 2 }
    };

    private static TestComposite CreateChunker(
        IOptions<LocalRagOptions> options,
        IStructuralChunker[]? adapters = null,
        IChunkTokenCounter? tokenCounter = null)
    {
        adapters ??=
        [
            new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(),
            new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(),
            new TomlStructuralChunker(), new XmlStructuralChunker()
        ];
        var profile = new ChunkProfileProvider(adapters, options);
        tokenCounter ??= new CharacterUpperBoundTokenCounter();
        var generic = new GenericChunker(options, profile, tokenCounter);
        return new(new CompositeChunker(
            adapters,
            generic,
            profile,
            options,
            tokenCounter,
            NullLogger<CompositeChunker>.Instance), profile);
    }

    private static IOptions<LocalRagOptions> DefaultOptions(
        int maximumTokens = 480,
        string[]? enabledAdapters = null,
        string tokenizerId = "bert-wordpiece-lowercase-v1",
        string passagePrefix = "",
        string queryPrefix = "") => Options.Create(new LocalRagOptions
        {
            Chunking = new ChunkingOptions
            {
                TargetTokens = Math.Min(384, maximumTokens),
                MaximumTokens = maximumTokens,
                OverlapTokens = 64,
                EnabledAdapters = enabledAdapters ?? ["csharp", "json", "markdown", "python", "toml", "typescript-javascript", "xml", "yaml"]
            },
            Embedding = new EmbeddingOptions
            {
                ProfileId = "test-profile",
                MaximumTokens = 512,
                TokenizerId = tokenizerId,
                PassagePrefix = passagePrefix,
                QueryPrefix = queryPrefix
            }
        });

    private static SourceRecord Source() => new("source", "C:\\fixture", "fixture", SourceStatus.Ready,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, "test-profile");

    private static IndexedFile File(string path) => new("file", "source", path, "hash", 1, DateTimeOffset.UnixEpoch);

    private sealed record TestComposite(CompositeChunker Chunker, ChunkProfileProvider Profile)
    {
        public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string content) =>
            Chunker.Chunk(source, file, content);
    }

    public enum FaultMode { InvalidBounds, DuplicateLocators, NullUnits, Exception }

    private sealed class FaultingAdapter(FaultMode mode) : IStructuralChunker
    {
        public string ChunkerId => "fault";
        public string ChunkerVersion => "1";
        public bool Supports(string relativePath) => true;
        public bool TryChunk(string relativePath, string normalizedContent, out IReadOnlyList<StructuralUnit> units)
        {
            if (mode == FaultMode.Exception) throw new InvalidDataException("synthetic adapter failure");
            if (mode == FaultMode.NullUnits) { units = null!; return true; }
            if (mode == FaultMode.InvalidBounds)
            {
                units = [new StructuralUnit("member", "bad", "bad", "bad", 0, 1)];
                return true;
            }
            units =
            [
                new StructuralUnit("member", "first", "first", "same", 1, 1),
                new StructuralUnit("member", "second", "second", "same", 2, 2)
            ];
            return true;
        }
    }
}
