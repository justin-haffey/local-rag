using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using Microsoft.Data.Sqlite;

namespace LocalRag.Infrastructure.Sqlite;

/// <summary>Owns the single persisted vector-store identity for the SQLite index.</summary>
public sealed class SqliteVectorStoreIdentityStore(SqliteDatabase database) : IVectorStoreIdentityStore
{
    private const int IdentitySchemaVersion = 1;
    private const string CosineMetric = "cosine";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS VectorStoreIdentity (
              SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
              SchemaVersion INTEGER NOT NULL,
              Provider TEXT NOT NULL,
              StorageIdentity TEXT NOT NULL,
              SchemaIdentity TEXT NOT NULL,
              EmbeddingProfileId TEXT NOT NULL,
              EmbeddingDimensions INTEGER NOT NULL,
              DistanceMetric TEXT NOT NULL,
              Fingerprint TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VectorStoreIdentity?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await ReadAsync(connection, cancellationToken);
    }

    public async Task<VectorStoreIdentityEnforcementResult> EnforceAsync(
        VectorStoreIdentity configuredIdentity,
        CancellationToken cancellationToken)
    {
        Validate(configuredIdentity);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var stored = await ReadAsync(connection, transaction, cancellationToken);
        var hasIndexedChunks = await HasIndexedChunksAsync(connection, transaction, cancellationToken);

        if (stored is null)
        {
            if (hasIndexedChunks && !string.Equals(configuredIdentity.Provider, "weaviate", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Indexed legacy state has no vector-store identity and can only be adopted by the Weaviate provider. Reset and rebuild before selecting LanceDb.");
            }

            await WriteAsync(connection, transaction, configuredIdentity, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return VectorStoreIdentityEnforcementResult.Adopted;
        }

        if (string.Equals(stored.Fingerprint, configuredIdentity.Fingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return VectorStoreIdentityEnforcementResult.Matched;
        }

        if (hasIndexedChunks)
        {
            throw new InvalidOperationException(
                "The configured vector-store identity does not match indexed state. Reset and rebuild before changing vector-store provider, storage, schema, or embedding profile.");
        }

        await WriteAsync(connection, transaction, configuredIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return VectorStoreIdentityEnforcementResult.ReplacedEmptyState;
    }

    public static VectorStoreIdentity CreateConfiguredIdentity(LocalRagOptions options)
    {
        var (provider, storageIdentity, schemaIdentity) = options.VectorStore.Provider switch
        {
            VectorStoreProvider.Weaviate => (
                "weaviate",
                NormalizeWeaviateEndpoint(options.VectorStore.Weaviate.Endpoint),
                NormalizeIdentifier(options.VectorStore.Weaviate.Collection)),
            VectorStoreProvider.LanceDb => (
                "lancedb",
                NormalizeLanceDbPath(options.VectorStore.LanceDb.Path),
                NormalizeIdentifier(options.VectorStore.LanceDb.Table)),
            _ => throw new InvalidOperationException("The configured vector-store provider is not supported.")
        };

        var profile = NormalizeIdentifier(options.Embedding.ProfileId);
        var fingerprintInput = string.Join("\n", [
            $"version={IdentitySchemaVersion}",
            $"provider={provider}",
            $"storage={storageIdentity}",
            $"schema={schemaIdentity}",
            $"embeddingProfile={profile}",
            $"embeddingDimensions={options.Embedding.Dimensions}",
            $"metric={CosineMetric}"
        ]);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
        return new(
            IdentitySchemaVersion,
            provider,
            storageIdentity,
            schemaIdentity,
            profile,
            options.Embedding.Dimensions,
            CosineMetric,
            fingerprint);
    }

    private static async Task<VectorStoreIdentity?> ReadAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction: null, cancellationToken);

    private static async Task<VectorStoreIdentity?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SchemaVersion, Provider, StorageIdentity, SchemaIdentity, EmbeddingProfileId,
                   EmbeddingDimensions, DistanceMetric, Fingerprint
            FROM VectorStoreIdentity
            WHERE SingletonId = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7))
            : null;
    }

    private static async Task<bool> HasIndexedChunksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Chunks LIMIT 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorStoreIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO VectorStoreIdentity(
              SingletonId, SchemaVersion, Provider, StorageIdentity, SchemaIdentity, EmbeddingProfileId,
              EmbeddingDimensions, DistanceMetric, Fingerprint)
            VALUES(1, $version, $provider, $storage, $schema, $profile, $dimensions, $metric, $fingerprint)
            ON CONFLICT(SingletonId) DO UPDATE SET
              SchemaVersion = excluded.SchemaVersion,
              Provider = excluded.Provider,
              StorageIdentity = excluded.StorageIdentity,
              SchemaIdentity = excluded.SchemaIdentity,
              EmbeddingProfileId = excluded.EmbeddingProfileId,
              EmbeddingDimensions = excluded.EmbeddingDimensions,
              DistanceMetric = excluded.DistanceMetric,
              Fingerprint = excluded.Fingerprint;
            """;
        command.Parameters.AddWithValue("$version", identity.SchemaVersion);
        command.Parameters.AddWithValue("$provider", identity.Provider);
        command.Parameters.AddWithValue("$storage", identity.StorageIdentity);
        command.Parameters.AddWithValue("$schema", identity.SchemaIdentity);
        command.Parameters.AddWithValue("$profile", identity.EmbeddingProfileId);
        command.Parameters.AddWithValue("$dimensions", identity.EmbeddingDimensions);
        command.Parameters.AddWithValue("$metric", identity.DistanceMetric);
        command.Parameters.AddWithValue("$fingerprint", identity.Fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(VectorStoreIdentity identity)
    {
        if (identity.SchemaVersion != IdentitySchemaVersion ||
            string.IsNullOrWhiteSpace(identity.Provider) ||
            string.IsNullOrWhiteSpace(identity.StorageIdentity) ||
            string.IsNullOrWhiteSpace(identity.SchemaIdentity) ||
            string.IsNullOrWhiteSpace(identity.EmbeddingProfileId) ||
            identity.EmbeddingDimensions < 1 ||
            !string.Equals(identity.DistanceMetric, CosineMetric, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Fingerprint))
        {
            throw new InvalidOperationException("The configured vector-store identity is invalid.");
        }
    }

    private static string NormalizeWeaviateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("The configured Weaviate endpoint is invalid.");
        }

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/'), Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            .TrimEnd('/');
    }

    private static string NormalizeLanceDbPath(string path)
    {
        var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
    }

    private static string NormalizeIdentifier(string value) => value.Trim();
}
