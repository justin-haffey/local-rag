using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalRag.Infrastructure.LanceDb;

internal enum LanceDbStatus : uint
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    NotReady = 3,
    Locked = 4,
    NotOwned = 5,
    Cancelled = 6,
    NativeFailure = 7,
    Panic = 8
}

internal readonly record struct LanceDbSearchWireRequest(
    string Query,
    string? Language,
    string? PathPrefix,
    byte[] SourceIdsIpc,
    IReadOnlyList<float> Vector,
    int Limit,
    float Alpha,
    uint Mode);

internal interface ILanceDbNativeApi : IDisposable
{
    LanceDbSafeHandle Open(string path, string table, string ownershipId);
    void EnsureReady(LanceDbSafeHandle database, CancellationToken cancellationToken);
    void Upsert(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken);
    void DeleteChunks(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken);
    void DeleteSource(LanceDbSafeHandle database, string sourceId, CancellationToken cancellationToken);
    byte[] Search(LanceDbSafeHandle database, LanceDbSearchWireRequest request, CancellationToken cancellationToken);
    bool VerifyOwnership(LanceDbSafeHandle database, string ownershipId, CancellationToken cancellationToken);
    void ResetOwned(LanceDbSafeHandle database, string ownershipId, CancellationToken cancellationToken);
    void Optimize(LanceDbSafeHandle database, CancellationToken cancellationToken);
}

internal sealed class LanceDbSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Func<IntPtr, bool>? _release;

    public LanceDbSafeHandle() : base(true) { }

    internal LanceDbSafeHandle(IntPtr value, Func<IntPtr, bool> release) : base(true)
    {
        _release = release;
        SetHandle(value);
    }

    protected override bool ReleaseHandle() => _release?.Invoke(handle) ?? true;
}

internal sealed class LanceDbNativeApi : ILanceDbNativeApi
{
    private const uint AbiV1 = 1;
    private const int MaxIpcBytes = 16 * 1024 * 1024;
    private readonly IntPtr _library;
    private readonly AbiNegotiate _negotiate;
    private readonly DbOpen _open;
    private readonly DbClose _close;
    private readonly CancelNew _cancelNew;
    private readonly CancelRequest _cancelRequest;
    private readonly CancelRelease _cancelRelease;
    private readonly DbUnary _ensureReady;
    private readonly DbBytes _upsert;
    private readonly DbBytes _deleteChunks;
    private readonly DbBytes _deleteSource;
    private readonly DbSearch _search;
    private readonly DbVerifyOwnership _verifyOwnership;
    private readonly DbOwned _resetOwned;
    private readonly DbUnary _optimize;
    private readonly BufferData _bufferData;
    private readonly BufferLength _bufferLength;
    private readonly BufferRelease _bufferRelease;
    private readonly ErrorMessage _errorMessage;
    private readonly ErrorLength _errorMessageLength;
    private readonly ErrorRelease _errorRelease;
    private volatile bool _disposed;
    private static readonly object LibraryGate = new();
    private static readonly Dictionary<string, (IntPtr Handle, string Hash)> ProcessLibraries = new(StringComparer.OrdinalIgnoreCase);

    public LanceDbNativeApi(string manifestPath)
    {
        var libraryPath = LanceDbNativeManifest.ValidateAndResolve(manifestPath);
        try
        {
            _library = LoadProcessLibrary(libraryPath);
            _negotiate = Load<AbiNegotiate>("lrag_abi_negotiate");
            _open = Load<DbOpen>("lrag_db_open");
            _close = Load<DbClose>("lrag_db_close");
            _cancelNew = Load<CancelNew>("lrag_cancel_new");
            _cancelRequest = Load<CancelRequest>("lrag_cancel_request");
            _cancelRelease = Load<CancelRelease>("lrag_cancel_release");
            _ensureReady = Load<DbUnary>("lrag_db_ensure_ready");
            _upsert = Load<DbBytes>("lrag_db_upsert_ipc");
            _deleteChunks = Load<DbBytes>("lrag_db_delete_chunks");
            _deleteSource = Load<DbBytes>("lrag_db_delete_source");
            _search = Load<DbSearch>("lrag_db_search");
            _verifyOwnership = Load<DbVerifyOwnership>("lrag_db_verify_ownership");
            _resetOwned = Load<DbOwned>("lrag_db_reset_owned");
            _optimize = Load<DbUnary>("lrag_db_optimize");
            _bufferData = Load<BufferData>("lrag_buffer_data");
            _bufferLength = Load<BufferLength>("lrag_buffer_len");
            _bufferRelease = Load<BufferRelease>("lrag_buffer_release");
            _errorMessage = Load<ErrorMessage>("lrag_error_message");
            _errorMessageLength = Load<ErrorLength>("lrag_error_message_len");
            _errorRelease = Load<ErrorRelease>("lrag_error_release");
            var status = _negotiate(AbiV1, out var selected, out var error);
            Check(status, error, CancellationToken.None);
            if (selected != AbiV1)
            {
                throw new LanceDbConfigurationException("The embedded LanceDB library selected an incompatible ABI.");
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new LanceDbConfigurationException("The embedded LanceDB native package could not be loaded. Repair the Local RAG installation.");
        }
    }

    public unsafe LanceDbSafeHandle Open(string path, string table, string ownershipId)
    {
        ThrowIfDisposed();
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var tableBytes = Encoding.UTF8.GetBytes(table);
        var ownerBytes = Encoding.UTF8.GetBytes(ownershipId);
        fixed (byte* pathPtr = pathBytes)
        fixed (byte* tablePtr = tableBytes)
        fixed (byte* ownerPtr = ownerBytes)
        {
            var request = new OpenRequest
            {
                StructSize = (uint)Marshal.SizeOf<OpenRequest>(),
                Path = pathPtr,
                PathLength = (nuint)pathBytes.Length,
                Table = tablePtr,
                TableLength = (nuint)tableBytes.Length,
                OwnershipId = ownerPtr,
                OwnershipIdLength = (nuint)ownerBytes.Length
            };
            var status = _open(in request, out var database, out var error);
            Check(status, error, CancellationToken.None);
            return new LanceDbSafeHandle(database, Close);
        }
    }

    public void EnsureReady(LanceDbSafeHandle database, CancellationToken cancellationToken) =>
        InvokeCancelable(
            database,
            (handle, cancel) =>
            {
                var status = _ensureReady(handle, cancel, out var error);
                return new NativeCallResult(status, error);
            },
            cancellationToken);

    public void Upsert(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken) =>
        InvokeBytes(_upsert, database, ipc, cancellationToken);

    public void DeleteChunks(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken) =>
        InvokeBytes(_deleteChunks, database, ipc, cancellationToken);

    public void DeleteSource(LanceDbSafeHandle database, string sourceId, CancellationToken cancellationToken) =>
        InvokeBytes(_deleteSource, database, Encoding.UTF8.GetBytes(sourceId), cancellationToken);

    public unsafe byte[] Search(
        LanceDbSafeHandle database,
        LanceDbSearchWireRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var query = Encoding.UTF8.GetBytes(request.Query);
        var language = request.Language is null ? [] : Encoding.UTF8.GetBytes(request.Language);
        var pathPrefix = request.PathPrefix is null ? [] : Encoding.UTF8.GetBytes(request.PathPrefix);
        var sources = request.SourceIdsIpc;
        var vector = request.Vector.ToArray();

        fixed (byte* queryPtr = query)
        fixed (byte* languagePtr = language)
        fixed (byte* pathPtr = pathPrefix)
        fixed (byte* sourcesPtr = sources)
        fixed (float* vectorPtr = vector)
        {
            var wire = new SearchRequest
            {
                StructSize = (uint)Marshal.SizeOf<SearchRequest>(),
                Query = queryPtr,
                QueryLength = (nuint)query.Length,
                Language = languagePtr,
                LanguageLength = (nuint)language.Length,
                PathPrefix = pathPtr,
                PathPrefixLength = (nuint)pathPrefix.Length,
                SourceIdsIpc = sourcesPtr,
                SourceIdsIpcLength = (nuint)sources.Length,
                Vector = vectorPtr,
                VectorLength = (nuint)vector.Length,
                Limit = checked((uint)request.Limit),
                Alpha = request.Alpha,
                Mode = request.Mode
            };
            IntPtr nativeBuffer = IntPtr.Zero;
            try
            {
                InvokeCancelable(
                    database,
                    (handle, cancel) =>
                    {
                        var status = _search(handle, in wire, cancel, out nativeBuffer, out var error);
                        return new NativeCallResult(status, error);
                    },
                    cancellationToken);
                if (nativeBuffer == IntPtr.Zero)
                {
                    throw new LanceDbNativeException(
                        (int)LanceDbStatus.NativeFailure,
                        "The embedded LanceDB search returned no result buffer.");
                }
                var length = checked((int)_bufferLength(nativeBuffer));
                if (length is < 0 or > MaxIpcBytes)
                {
                    throw new LanceDbNativeException(
                        (int)LanceDbStatus.NativeFailure,
                        "The embedded LanceDB search result exceeded its IPC bound.");
                }
                var pointer = _bufferData(nativeBuffer);
                if (length > 0 && pointer == IntPtr.Zero)
                {
                    throw new LanceDbNativeException(
                        (int)LanceDbStatus.NativeFailure,
                        "The embedded LanceDB search result was invalid.");
                }
                var bytes = new byte[length];
                if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
                cancellationToken.ThrowIfCancellationRequested();
                return bytes;
            }
            finally
            {
                if (nativeBuffer != IntPtr.Zero) _bufferRelease(nativeBuffer);
            }
        }
    }

    public unsafe bool VerifyOwnership(
        LanceDbSafeHandle database,
        string ownershipId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(ownershipId);
        fixed (byte* pointer = bytes)
        {
            var pointerValue = (IntPtr)pointer;
            byte owned = 0;
            WithDatabase(database, handle =>
            {
                var status = _verifyOwnership(handle, (byte*)pointerValue, (nuint)bytes.Length, out owned, out var error);
                Check(status, error, cancellationToken);
            });
            cancellationToken.ThrowIfCancellationRequested();
            return owned != 0;
        }
    }

    public unsafe void ResetOwned(
        LanceDbSafeHandle database,
        string ownershipId,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(ownershipId);
        fixed (byte* pointer = bytes)
        {
            var pointerValue = (IntPtr)pointer;
            InvokeCancelable(
                database,
                (handle, cancel) =>
                {
                    var status = _resetOwned(handle, (byte*)pointerValue, (nuint)bytes.Length, cancel, out var error);
                    return new NativeCallResult(status, error);
                },
                cancellationToken);
        }
    }

    public void Optimize(LanceDbSafeHandle database, CancellationToken cancellationToken) =>
        InvokeCancelable(
            database,
            (handle, cancel) =>
            {
                var status = _optimize(handle, cancel, out var error);
                return new NativeCallResult(status, error);
            },
            cancellationToken);

    public void Dispose() => _disposed = true;

    private bool Close(IntPtr database)
    {
        try
        {
            Check(_close(database, out var error), error, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private unsafe void InvokeBytes(
        DbBytes operation,
        LanceDbSafeHandle database,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        fixed (byte* pointer = bytes)
        {
            var pointerValue = (IntPtr)pointer;
            InvokeCancelable(
                database,
                (handle, cancel) =>
                {
                    var status = operation(handle, (byte*)pointerValue, (nuint)bytes.Length, cancel, out var error);
                    return new NativeCallResult(status, error);
                },
                cancellationToken);
        }
    }

    private void InvokeCancelable(
        LanceDbSafeHandle database,
        Func<IntPtr, IntPtr, NativeCallResult> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Check(_cancelNew(out var cancel, out var cancelError), cancelError, cancellationToken);
        try
        {
            using var registration = cancellationToken.Register(
                static state =>
                {
                    var callback = (CancelCallbackState)state!;
                    var status = callback.Api._cancelRequest(callback.Cancel, out var error);
                    if (error != IntPtr.Zero) callback.Api._errorRelease(error);
                    _ = status;
                },
                new CancelCallbackState(this, cancel));
            WithDatabase(database, handle =>
            {
                var result = operation(handle, cancel);
                Check(result.Status, result.Error, cancellationToken);
            });
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            var status = _cancelRelease(cancel, out var error);
            if (error != IntPtr.Zero) _errorRelease(error);
            _ = status;
        }
    }

    private void WithDatabase(LanceDbSafeHandle database, Action<IntPtr> action)
    {
        ThrowIfDisposed();
        var added = false;
        try
        {
            database.DangerousAddRef(ref added);
            ObjectDisposedException.ThrowIf(database.IsInvalid || database.IsClosed, database);
            action(database.DangerousGetHandle());
        }
        finally
        {
            if (added) database.DangerousRelease();
        }
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private void Check(uint value, IntPtr error, CancellationToken cancellationToken)
    {
        if ((LanceDbStatus)value == LanceDbStatus.Ok)
        {
            if (error != IntPtr.Zero) _errorRelease(error);
            return;
        }

        var message = "The embedded LanceDB operation failed.";
        if (error != IntPtr.Zero)
        {
            try
            {
                var pointer = _errorMessage(error);
                var length = checked((int)_errorMessageLength(error));
                if (pointer != IntPtr.Zero && length > 0)
                {
                    var bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                    message = Encoding.UTF8.GetString(bytes);
                }
            }
            finally
            {
                _errorRelease(error);
            }
        }
        if ((LanceDbStatus)value == LanceDbStatus.Cancelled)
        {
            throw new OperationCanceledException(message, cancellationToken);
        }
        throw new LanceDbNativeException((int)value, message);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // Lance/DataFusion can create process-wide worker pools. Closing every
    // database does not stop those workers; unloading their code caused an
    // observed access violation after successful operations. Cache one module
    // reference per validated package path until OS process teardown. Database
    // handles, runtimes, buffers and exclusive writer locks still close normally.
    private static IntPtr LoadProcessLibrary(string path)
    {
        path = Path.GetFullPath(path);
        lock (LibraryGate)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!ProcessLibraries.TryGetValue(path, out var library))
            {
                library = (NativeLibrary.Load(path), hash);
                ProcessLibraries.Add(path, library);
            }
            else if (!string.Equals(library.Hash, hash, StringComparison.Ordinal))
            {
                throw new LanceDbConfigurationException("The embedded LanceDB package changed after loading. Restart Local RAG to use the repaired package.");
            }
            return library.Handle;
        }
    }
    private readonly record struct CancelCallbackState(LanceDbNativeApi Api, IntPtr Cancel);
    private readonly record struct NativeCallResult(uint Status, IntPtr Error);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OpenRequest
    {
        public uint StructSize;
        public byte* Path;
        public nuint PathLength;
        public byte* Table;
        public nuint TableLength;
        public byte* OwnershipId;
        public nuint OwnershipIdLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SearchRequest
    {
        public uint StructSize;
        public byte* Query;
        public nuint QueryLength;
        public byte* Language;
        public nuint LanguageLength;
        public byte* PathPrefix;
        public nuint PathPrefixLength;
        public byte* SourceIdsIpc;
        public nuint SourceIdsIpcLength;
        public float* Vector;
        public nuint VectorLength;
        public uint Limit;
        public float Alpha;
        public uint Mode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiNegotiate(uint requested, out uint selected, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DbOpen(in OpenRequest request, out IntPtr database, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DbClose(IntPtr database, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CancelNew(out IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CancelRequest(IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CancelRelease(IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DbUnary(IntPtr database, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate uint DbBytes(IntPtr database, byte* bytes, nuint length, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate uint DbSearch(IntPtr database, in SearchRequest request, IntPtr cancel, out IntPtr result, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate uint DbVerifyOwnership(IntPtr database, byte* owner, nuint length, out byte owned, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate uint DbOwned(IntPtr database, byte* owner, nuint length, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr BufferData(IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint BufferLength(IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BufferRelease(IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorMessage(IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ErrorLength(IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ErrorRelease(IntPtr error);
}
