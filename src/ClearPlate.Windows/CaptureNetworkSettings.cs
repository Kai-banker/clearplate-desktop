using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ClearPlate.Windows;

internal sealed record ProxySnapshot(int Flags, string Server, string Bypass, string AutoConfigUrl);
internal sealed record CaptureRecovery(string Id, int Port, string Thumbprint, ProxySnapshot Original);

internal static class CaptureNetworkSettings
{
    private const int PerConnectionOption = 75;
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearPlateDesktop", "capture-recovery.bin");
    [StructLayout(LayoutKind.Explicit)] private struct OptionValue { [FieldOffset(0)] public int Number; [FieldOffset(0)] public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential)] private struct Option { public int Name; public OptionValue Value; }
    [StructLayout(LayoutKind.Sequential)] private struct OptionList { public int Size; public IntPtr Connection; public int Count; public int Error; public IntPtr Options; }
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetQueryOptionW(IntPtr handle, int option, ref OptionList buffer, ref int length);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetSetOptionW(IntPtr handle, int option, ref OptionList buffer, int length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NotifyOption(IntPtr handle, int option, IntPtr buffer, int length);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GlobalFree(IntPtr value);
    private static ProxySnapshot ReadProxy()
    {
        var size = Marshal.SizeOf<Option>(); var memory = Marshal.AllocHGlobal(size * 4);
        try
        {
            for (var i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Name = i + 1 }, memory + i * size, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 4, Options = memory }; var length = list.Size;
            if (!InternetQueryOptionW(IntPtr.Zero, PerConnectionOption, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "不能读取原网络代理设置。");
            var flags = Marshal.PtrToStructure<Option>(memory).Value.Number;
            var strings = new string[3];
            for (var i = 1; i < 4; i++)
            {
                var pointer = Marshal.PtrToStructure<Option>(memory + i * size).Value.Text;
                strings[i - 1] = Marshal.PtrToStringUni(pointer) ?? "";
                if (pointer != IntPtr.Zero) _ = GlobalFree(pointer);
            }
            return new(flags, strings[0], strings[1], strings[2]);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal static void CheckReadOnly()
    {
        _ = ReadProxy(); // Windows smoke check verifies interop without modifying any setting.
    }
    private static void WriteProxy(ProxySnapshot snapshot)
    {
        var size = Marshal.SizeOf<Option>(); var memory = Marshal.AllocHGlobal(size * 4);
        var pointers = new[] { Marshal.StringToHGlobalUni(snapshot.Server), Marshal.StringToHGlobalUni(snapshot.Bypass), Marshal.StringToHGlobalUni(snapshot.AutoConfigUrl) };
        try
        {
            Marshal.StructureToPtr(new Option { Name = 1, Value = new() { Number = snapshot.Flags } }, memory, false);
            for (var i = 1; i < 4; i++) Marshal.StructureToPtr(new Option { Name = i + 1, Value = new() { Text = pointers[i - 1] } }, memory + i * size, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 4, Options = memory };
            if (!InternetSetOptionW(IntPtr.Zero, PerConnectionOption, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "不能设置网络代理。");
            _ = NotifyOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            _ = NotifyOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }
        finally { foreach (var pointer in pointers) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(memory); }
    }
    internal static CaptureRecovery Activate(int port, X509Certificate2 root)
    {
        if (File.Exists(FilePath)) { Restore(); if (File.Exists(FilePath)) throw new IOException("上次代理尚未恢复，不能开始新的采集。"); }
        var recovery = new CaptureRecovery(Guid.NewGuid().ToString("N"), port, root.Thumbprint, ReadProxy());
        // Snapshot is encrypted and written BEFORE changing certificate trust or networking.
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(recovery);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var temporary = FilePath + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(encrypted); stream.Flush(true); }
                File.Move(temporary, FilePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite);
            using var publicRoot = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
            store.Add(publicRoot);
            WriteProxy(new(3, $"127.0.0.1:{port}", "localhost;127.0.0.1;<local>", ""));
            return recovery;
        }
        catch { Restore(recovery.Id); throw; }
    }
    internal static bool Restore(string? expectedId = null)
    {
        if (!File.Exists(FilePath)) return false;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        CaptureRecovery recovery;
        try { recovery = JsonSerializer.Deserialize<CaptureRecovery>(bytes) ?? throw new IOException("代理恢复记录无效。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (expectedId is not null && expectedId != recovery.Id) return false;
        Exception? failure = null;
        try
        {
            var current = ReadProxy();
            // Do not overwrite proxy preferences the user changed during capture.
            if (current.Server == $"127.0.0.1:{recovery.Port}" && current.Flags == 3) WriteProxy(recovery.Original);
        }
        catch (Exception error) { failure = error; }
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, recovery.Thumbprint, false))
            {
                if (!certificate.Subject.StartsWith("CN=ClearPlate temporary login capture ", StringComparison.Ordinal)) throw new IOException("恢复证书标识不符合本工具，未移除其他证书。");
                store.Remove(certificate); certificate.Dispose();
            }
        }
        catch (Exception error) { failure ??= error; }
        if (failure is not null) throw new IOException("临时代理或证书未能完全恢复，请再次启动工具恢复。", failure);
        File.Delete(FilePath);
        return true;
    }
}
