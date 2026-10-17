using System.Security.Cryptography;
using System.Text.Json;

namespace LocalRag.Infrastructure.LanceDb;

internal sealed record LanceDbNativeManifest(
    int SchemaVersion,
    int AbiVersion,
    string Rid,
    string LibraryFile,
    string Sha256,
    string[] RequiredExports,
    JsonElement[] Licenses)
{
    internal const int SupportedAbi = 1;
    private static readonly string[] ExpectedExports =
    [
        "lrag_abi_negotiate",
        "lrag_db_open",
        "lrag_db_close",
        "lrag_cancel_new",
        "lrag_cancel_request",
        "lrag_cancel_release",
        "lrag_db_ensure_ready",
        "lrag_db_upsert_ipc",
        "lrag_db_delete_chunks",
        "lrag_db_delete_source",
        "lrag_db_search",
        "lrag_db_verify_ownership",
        "lrag_db_reset_owned",
        "lrag_db_optimize",
        "lrag_buffer_data",
        "lrag_buffer_len",
        "lrag_buffer_release",
        "lrag_error_code",
        "lrag_error_message",
        "lrag_error_message_len",
        "lrag_error_release"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public static string ValidateAndResolve(string manifestPath)
    {
        if (!System.IO.File.Exists(manifestPath))
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native manifest is missing. Repair the LocalRAG installation.");
        }

        LanceDbNativeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LanceDbNativeManifest>(
                System.IO.File.ReadAllBytes(manifestPath),
                SerializerOptions)
                ?? throw new JsonException("The manifest was empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native manifest is invalid. Repair the LocalRAG installation.");
        }

        if (manifest.SchemaVersion != 1 || manifest.AbiVersion != SupportedAbi)
        {
            throw new LanceDbConfigurationException(
                $"The embedded LanceDB ABI is incompatible (expected {SupportedAbi}, found {manifest.AbiVersion}). Repair the LocalRAG installation.");
        }

        var expectedRid = OperatingSystem.IsWindows() && Environment.Is64BitProcess ? "win-x64" : string.Empty;
        if (!string.Equals(manifest.Rid, expectedRid, StringComparison.Ordinal))
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native asset does not match this process. Repair the LocalRAG installation.");
        }

        if (!string.Equals(manifest.LibraryFile, "localrag_lancedb.dll", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Sha256) ||
            manifest.Sha256.Length != 64)
        {
            throw new LanceDbConfigurationException("The embedded LanceDB native manifest contains unsafe values.");
        }
        if (manifest.RequiredExports is null ||
            !manifest.RequiredExports.Order(StringComparer.Ordinal).SequenceEqual(ExpectedExports.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            manifest.Licenses is null ||
            manifest.Licenses.Length == 0)
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native manifest does not declare the required ABI exports and licenses.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.LibraryFile));
        if (!System.IO.File.Exists(fullPath))
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native library is missing. Repair the LocalRAG installation.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(fullPath)));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                ParseExpectedHash(manifest.Sha256)))
        {
            throw new LanceDbConfigurationException(
                "The embedded LanceDB native library failed integrity validation. Repair the LocalRAG installation.");
        }

        return fullPath;
    }

    private static byte[] ParseExpectedHash(string value)
    {
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new LanceDbConfigurationException("The embedded LanceDB native manifest contains an invalid hash.");
        }
    }
}
