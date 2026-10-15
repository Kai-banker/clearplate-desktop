using ClearPlate.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace ClearPlate.Server;

public sealed record PhotoReference(string Id, string Mime, string Name, string UpdatedAt);
public sealed record ServerDocument(int Version, Session? Session, AppState State, PhotoReference? Photo);
public sealed class EncryptedStore : IDisposable
{
    private readonly string directory;
    private readonly byte[] key;
    private readonly FileStream lease;
    private readonly object sync = new();
    private bool disposed;
    public EncryptedStore(ServerOptions options)
    {
        if (options.EncryptionKey.Length != 32) throw new InvalidOperationException("Invalid encryption key");
        directory = Path.GetFullPath(options.DataDirectory); key = options.EncryptionKey.ToArray();
        Directory.CreateDirectory(directory);
        Private(directory, true);
        lease = new(Path.Combine(directory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Private(lease.Name);
    }
    private static void Private(string path, bool folder = false)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, folder ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private byte[] Seal(byte[] bytes, string purpose)
    {
        var result = new byte[1 + 12 + 16 + bytes.Length]; result[0] = 1;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(result.AsSpan(1, 12), bytes, result.AsSpan(29), result.AsSpan(13, 16), Encoding.UTF8.GetBytes(purpose));
        return result;
    }
    private byte[] Open(byte[] bytes, string purpose)
    {
        if (bytes.Length < 29 || bytes[0] != 1) throw new IOException("Invalid encrypted data");
        var plain = new byte[bytes.Length - 29];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(bytes.AsSpan(1, 12), bytes.AsSpan(29), bytes.AsSpan(13, 16), plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }
    private void Write(string name, byte[] bytes)
    {
        var temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Private(temporary); file.Write(bytes); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(directory, name), overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                var descriptor = OpenDirectory(directory, 0);
                try { if (descriptor < 0 || SyncDirectory(descriptor) != 0) throw new IOException("Cannot synchronize state directory"); }
                finally { if (descriptor >= 0) CloseDirectory(descriptor); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDirectory(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDirectory(int descriptor);
    public ServerDocument Load()
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return LoadCore(); }
    }
    private ServerDocument LoadCore()
    {
        var path = Path.Combine(directory, "state.enc");
        var marker = Path.Combine(directory, "initialized");
        if (!File.Exists(path))
        {
            if (File.Exists(marker)) throw new IOException("Previously initialized state is missing");
            var fresh = new ServerDocument(1, null, new(), null); Save(fresh.Session, fresh.State, fresh.Photo);
            Write("initialized", [1]); return fresh;
        }
        var value = JsonSerializer.Deserialize<ServerDocument>(Open(File.ReadAllBytes(path), "clearplate/state/v1")) ?? throw new IOException("Missing state");
        if (value.Version != 1 || value.State is null || value.State.Attempted is null || value.State.Confirmed is null || value.State.Records is null)
            throw new IOException("Unsupported state");
        if (!File.Exists(marker)) Write("initialized", [1]);
        return value;
    }
    public void Save(Session? session, AppState state, PhotoReference? photo)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Write("state.enc", Seal(JsonSerializer.SerializeToUtf8Bytes(new ServerDocument(1, session, state, photo)), "clearplate/state/v1"));
        }
    }
    public PhotoReference SavePhoto(byte[] bytes, string mime, string name, DateTimeOffset now)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var id = Guid.NewGuid().ToString("N"); Write("photo-" + id + ".enc", Seal(bytes, "clearplate/photo/v1"));
            return new(id, mime, name, Schedule.Timestamp(now));
        }
    }
    private string PhotoPath(PhotoReference photo)
    {
        if (photo.Id.Length != 32 || photo.Id.Any(c => !Uri.IsHexDigit(c))) throw new IOException("Invalid photo identifier");
        return Path.Combine(directory, "photo-" + photo.Id + ".enc");
    }
    public byte[] LoadPhoto(PhotoReference photo)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return Open(File.ReadAllBytes(PhotoPath(photo)), "clearplate/photo/v1"); }
    }
    public void RemovePhoto(PhotoReference photo)
    {
        lock (sync)
        {
            if (disposed) return;
            try { File.Delete(PhotoPath(photo)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; lease.Dispose(); CryptographicOperations.ZeroMemory(key);
        }
    }
}
