namespace LocalRag.Domain;

public static class ChunkRecordValidation
{
    public static void Validate(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, int hardTokenLimit)
    {
        if (hardTokenLimit < 3) throw new InvalidDataException("The hard token limit must be at least three.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var locators = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            Validate(chunk, hardTokenLimit);
            if (chunk.SourceId != file.SourceId || chunk.FileId != file.FileId)
                throw new InvalidDataException("Chunk source/file identity does not match its indexed file.");
            if (!string.Equals(
                    chunk.RelativePath.Replace('\\', '/'),
                    file.RelativePath.Replace('\\', '/'),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Chunk relative path does not match its indexed file.");
            if (chunk.Ordinal != index) throw new InvalidDataException("Chunk ordinals must be contiguous and deterministic.");
            if (!ids.Add(chunk.ChunkId)) throw new InvalidDataException("Chunk IDs must be unique within a file.");
            if (!locators.Add(chunk.StructuralLocator)) throw new InvalidDataException("Structural locators must be unique within a file.");
        }
    }

    public static void Validate(ChunkRecord chunk, int hardTokenLimit)
    {
        if (string.IsNullOrWhiteSpace(chunk.ChunkId) || string.IsNullOrWhiteSpace(chunk.SourceId) ||
            string.IsNullOrWhiteSpace(chunk.FileId) || string.IsNullOrWhiteSpace(chunk.RelativePath) ||
            string.IsNullOrWhiteSpace(chunk.Content) || string.IsNullOrWhiteSpace(chunk.ContentHash) ||
            string.IsNullOrWhiteSpace(chunk.ChunkKind) || string.IsNullOrWhiteSpace(chunk.StructuralLocator) ||
            string.IsNullOrWhiteSpace(chunk.ChunkerId) || string.IsNullOrWhiteSpace(chunk.ChunkerVersion) ||
            string.IsNullOrWhiteSpace(chunk.ChunkProfileFingerprint))
            throw new InvalidDataException("Chunk identity, content, and structural provenance are required.");
        if (chunk.StartLine < 1 || chunk.EndLine < chunk.StartLine)
            throw new InvalidDataException("Chunk line bounds are invalid.");
        if (chunk.TokenCount < 1 || chunk.TokenCount > hardTokenLimit)
            throw new InvalidDataException("Chunk token count exceeds the configured hard limit.");
    }
}
