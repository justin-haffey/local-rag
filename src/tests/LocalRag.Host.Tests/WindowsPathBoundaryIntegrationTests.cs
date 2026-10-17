using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace LocalRag.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsPathBoundaryIntegrationTests
{
    [WindowsPathFact]
    public Task AuthenticatedCanonicalDuplicatesDriveListingAndAbsentDriveAreControlled() => RunAsync(async fixture =>
    {
        var source = fixture.Directory("Reference folder 日本語");
        var first = await fixture.RegisterAsync(source, "Original name");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var sourceId = firstBody.GetProperty("source").GetProperty("sourceId").GetString();
        Assert.False(firstBody.GetProperty("alreadyRegistered").GetBoolean());
        foreach (var variant in new[] { source.ToUpperInvariant(), source.ToLowerInvariant() + Path.DirectorySeparatorChar, source + Path.DirectorySeparatorChar })
        {
            Assert.True(System.IO.Directory.Exists(variant));
            var duplicate = await fixture.RegisterAsync(variant, "Must not overwrite metadata");
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("alreadyRegistered").GetBoolean());
            Assert.Equal(sourceId, body.GetProperty("source").GetProperty("sourceId").GetString());
            Assert.Equal("Original name", body.GetProperty("source").GetProperty("displayName").GetString());
        }
        Assert.Single(await fixture.SourcesAsync());
        var drive = Path.GetPathRoot(source)!;
        var listing = await fixture.ListAsync(drive);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Equal(drive, (await listing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("current").GetString());

        var occupied = DriveInfo.GetDrives().Select(item => item.Name[0]).ToHashSet();
        var absent = Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => (char)value).Reverse().First(letter => !occupied.Contains(letter));
        var missing = $"{absent}:\\LocalRagMissing-{Guid.NewGuid():N}";
        Assert.False(System.IO.Directory.Exists(missing));
        await Fixture.AssertControlledFailureAsync(await fixture.ListAsync(missing));
        var missingRegistration = await fixture.RegisterAsync(missing);
        Assert.Equal("Selected directory is unavailable.", await Fixture.AssertControlledFailureAsync(missingRegistration));
        var legacyMissing = await fixture.RegisterLegacyAsync(missing);
        Assert.Equal("Selected directory is unavailable.", await Fixture.AssertControlledFailureAsync(legacyMissing));
        Assert.Single(await fixture.SourcesAsync());
        fixture.Record("absent-local-drive-not-network-disconnect", new { missing });
    });

    [WindowsPathFact]
    public Task AuthenticatedJunctionRootAndReparseAncestorAreRejectedWithoutTouchingTarget() => RunAsync(async fixture =>
    {
        var target = fixture.Directory("junction target 日本語");
        var child = fixture.Directory(Path.Combine("junction target 日本語", "child"));
        var sentinel = Path.Combine(child, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "Preserve junction target bytes.");
        var link = fixture.Directory("junction root");
        fixture.Cleanup.Junction = link; fixture.Cleanup.JunctionTarget = target; fixture.Cleanup.JunctionSentinel = sentinel;
        CreateJunction(fixture.Owned(link), fixture.Owned(target));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        foreach (var candidate in new[] { link, Path.Combine(link, "child") })
        {
            Assert.True(System.IO.Directory.Exists(candidate));
            await Fixture.AssertControlledFailureAsync(await fixture.ListAsync(candidate));
            await Fixture.AssertControlledFailureAsync(await fixture.RegisterAsync(candidate));
        }
        Assert.Empty(await fixture.SourcesAsync());
        Assert.Equal("Preserve junction target bytes.", await File.ReadAllTextAsync(sentinel));
        fixture.Record("junction-and-ancestor-refused", new { link, target });
    });

    [WindowsPathFact]
    public Task ActualOwnedListDirectoryDenialReturnsControlledHttpErrorsAndRestoresAcl() => RunAsync(async fixture =>
    {
        var denied = new DirectoryInfo(fixture.Directory("owned denied folder"));
        var sentinel = Path.Combine(denied.FullName, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "Preserve denied folder bytes.");
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var original = denied.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
        var originalDacl = denied.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var sddlPath = fixture.Owned(Path.Combine(fixture.Root, "acl-original.sddl")); await File.WriteAllTextAsync(sddlPath, original);
        fixture.Cleanup.DeniedDirectory = denied.FullName; fixture.Cleanup.OriginalSddl = sddlPath; fixture.Cleanup.DeniedSentinel = sentinel;
        var restore = new DirectorySecurity(); restore.SetSecurityDescriptorSddlForm(originalDacl, AccessControlSections.Access);
        var changed = denied.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        changed.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ListDirectory, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            fixture.Owned(denied.FullName); denied.SetAccessControl(changed);
            Assert.Throws<UnauthorizedAccessException>(() => { using var iterator = System.IO.Directory.EnumerateFileSystemEntries(denied.FullName).GetEnumerator(); iterator.MoveNext(); });
            await Fixture.AssertControlledFailureAsync(await fixture.ListAsync(denied.FullName));
            await Fixture.AssertControlledFailureAsync(await fixture.RegisterAsync(denied.FullName));
            Assert.Empty(await fixture.SourcesAsync());
            fixture.Record("actual-list-directory-denial", new { denied = denied.FullName, right = "ListDirectory", inherited = false });
        }
        finally
        {
            fixture.Owned(denied.FullName); denied.SetAccessControl(restore);
            var restored = denied.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
            fixture.Cleanup.AclRestoredExactly = restored == original;
            Assert.Equal(original, restored);
            Assert.Contains(sentinel, System.IO.Directory.EnumerateFiles(denied.FullName));
            Assert.Equal("Preserve denied folder bytes.", await File.ReadAllTextAsync(sentinel));
            fixture.Record("acl-restored-in-finally", new { exact = fixture.Cleanup.AclRestoredExactly });
        }
    });

    private static async Task RunAsync(Func<Fixture, Task> action)
    {
        await using var fixture = await Fixture.CreateAsync();
        Exception? failure = null;
        try { await action(fixture); }
        catch (Exception error) { failure = error; throw; }
        finally { await fixture.FinishAsync(failure); }
    }

    private static void CreateJunction(string link, string target)
    {
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var printable = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(buffer), Encoding.Unicode))
        {
            writer.Write(0xA0000003u); writer.Write(checked((ushort)(buffer.Length - 8))); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(checked((ushort)substitute.Length)); writer.Write(checked((ushort)(substitute.Length + 2))); writer.Write(checked((ushort)printable.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(printable); writer.Write((ushort)0);
        }
        using var handle = CreateFile(link, 0x40000000, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open owned junction fixture.");
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create owned junction fixture.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, int inputBytes, IntPtr output, int outputBytes, out int returnedBytes, IntPtr overlapped);

    private sealed class CleanupRecord
    {
        public string? Junction { get; set; }
        public string? JunctionTarget { get; set; }
        public string? JunctionSentinel { get; set; }
        public string? DeniedDirectory { get; set; }
        public string? OriginalSddl { get; set; }
        public string? DeniedSentinel { get; set; }
        public bool? AclRestoredExactly { get; set; }
        public string Retention { get; } = "Luna removes only the owned junction; independently verifies restored ACL and sentinel bytes. Retain fixture files/data/evidence.";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(RepositoryRoot(), "artifacts", "runs", "p8-path-boundaries-20261017", Guid.NewGuid().ToString("N"));
        internal CleanupRecord Cleanup { get; } = new();
        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private SqliteDatabase _database = null!;
        private readonly List<object> _events = [];
        private bool _stopped;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.ValidateRoot();
            Assert.False(System.IO.Directory.Exists(fixture.Root)); System.IO.Directory.CreateDirectory(fixture.Root);
            fixture._app = HostApplication.Create([
                "--contentRoot", Path.Combine(RepositoryRoot(), "src", "LocalRag.Host"), "--urls", "http://127.0.0.1:0", "--environment", "Production",
                "--LocalRag:DataDirectory", Path.Combine(fixture.Root, "data"), "--LocalRag:Authentication:Token", "path-fixture-token",
                "--LocalRag:VectorStore:Provider", "Weaviate", "--LocalRag:VectorStore:Weaviate:Endpoint", "http://127.0.0.1:1",
                "--LocalRag:Embedding:ModelDirectory", Path.Combine(fixture.Root, "missing-model")
            ]);
            await fixture._app.StartAsync();
            fixture._database = fixture._app.Services.GetRequiredService<SqliteDatabase>();
            fixture._client = new HttpClient { BaseAddress = new(fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(20) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture._client.GetAsync("/api/v1/folders")).StatusCode);
            fixture._client.DefaultRequestHeaders.Authorization = new("Bearer", "path-fixture-token");
            return fixture;
        }
        internal string Owned(string path)
        {
            ValidateRoot();
            var resolved = Path.GetFullPath(path); var root = Path.GetFullPath(Root);
            if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture mutation escaped its new run root.");
            EnsureNoReparseAncestors(resolved);
            return resolved;
        }
        private void ValidateRoot()
        {
            var intended = Path.GetFullPath(Path.Combine(RepositoryRoot(), "artifacts", "runs", "p8-path-boundaries-20261017"));
            if (!Path.GetFullPath(Root).StartsWith(intended + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture root escaped the intended artifacts run directory.");
            EnsureNoReparseAncestors(Root);
        }
        private static void EnsureNoReparseAncestors(string path)
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture mutation cannot traverse a pre-existing reparse ancestor.");
        }
        internal string Directory(string relative)
        {
            var path = Owned(Path.Combine(Root, relative));
            Assert.False(System.IO.Directory.Exists(path)); System.IO.Directory.CreateDirectory(path); return path;
        }
        internal async Task<HttpResponseMessage> ListAsync(string path) => await RecordResponseAsync("folders", path, await _client.GetAsync("/api/v1/folders?path=" + Uri.EscapeDataString(path)));
        internal async Task<HttpResponseMessage> RegisterAsync(string path, string name = "Boundary fixture") => await RecordResponseAsync("register", path, await _client.PostAsJsonAsync("/api/v1/catalog/sources", new { rootPath = path, name, description = "Preserve boundary metadata", scope = "global" }));
        internal async Task<HttpResponseMessage> RegisterLegacyAsync(string path) => await RecordResponseAsync("legacy-register", path, await _client.PostAsJsonAsync("/api/v1/sources", new { rootPath = path, displayName = "Missing legacy source" }));
        internal async Task<JsonElement[]> SourcesAsync() => (await _client.GetFromJsonAsync<JsonElement>("/api/v1/catalog")).GetProperty("sources").EnumerateArray().ToArray();
        private async Task<HttpResponseMessage> RecordResponseAsync(string action, string path, HttpResponseMessage response)
        {
            Record("http-response", new { action, path, status = (int)response.StatusCode, body = await response.Content.ReadAsStringAsync() }); return response;
        }
        internal static async Task<string> AssertControlledFailureAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
            Assert.False(string.IsNullOrWhiteSpace(error)); Assert.DoesNotContain("Exception", error, StringComparison.OrdinalIgnoreCase);
            return error!;
        }
        internal void Record(string stage, object detail) => _events.Add(new { utc = DateTimeOffset.UtcNow, stage, detail });
        internal async Task FinishAsync(Exception? failure)
        {
            Exception? stopFailure = null;
            try { await StopAsync(); }
            catch (Exception error) { stopFailure = error; }
            Exception? evidenceFailure = null;
            try
            {
                using var closureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using (var connection = await _database.OpenAsync(closureTimeout.Token)) SqliteConnection.ClearPool(connection);
                var files = System.IO.Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Select(path => new { path = Path.GetRelativePath(Root, path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
                await File.WriteAllTextAsync(Path.Combine(Root, "manifest.json"), JsonSerializer.Serialize(new { files, cleanup = Cleanup }, Json));
            }
            catch (Exception error) { evidenceFailure = error; }
            await File.WriteAllTextAsync(Path.Combine(Root, "outcome.json"), JsonSerializer.Serialize(new { passed = failure is null && stopFailure is null && evidenceFailure is null, actionPassed = failure is null, failure = failure?.ToString(), stopFailure = stopFailure?.ToString(), evidenceFailure = evidenceFailure?.ToString(), installedProduct = false, boundary = "Authenticated production HTTP, intentionally unavailable model/provider; no ingestion assertion", cleanup = Cleanup, events = _events }, Json));
            if (stopFailure is not null) ExceptionDispatchInfo.Capture(stopFailure).Throw();
            if (evidenceFailure is not null) ExceptionDispatchInfo.Capture(evidenceFailure).Throw();
        }
        private async Task StopAsync()
        {
            if (_stopped) return; _stopped = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _app.StopAsync(timeout.Token); }
            finally { _client.Dispose(); await _app.DisposeAsync(); }
        }
        public async ValueTask DisposeAsync() => await StopAsync();
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LocalRag.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }
}

internal sealed class WindowsPathFactAttribute : FactAttribute
{
    public WindowsPathFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Actual Windows filesystem/ACL boundary test."; }
}
