using ClearPlate.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClearPlate.Windows;

public sealed class WindowsStore(string? folder = null)
{
    private readonly string directory = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearPlateDesktop");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed class Stored
    {
        public Stored() { }
        public int Schema { get; set; } = 1;
        public string ProtectedToken { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string Nickname { get; set; } = "";
        public AppState State { get; set; } = new();
    }
    public (Session? Session, AppState State) Load()
    {
        var file = Path.Combine(directory, "state.json");
        if (!File.Exists(file)) return (null, new());
        var value = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file)) ?? throw new IOException("本地状态文件为空。");
        if (value.Schema != 1 || value.State is null || value.State.Attempted is null || value.State.Confirmed is null || value.State.Records is null)
            throw new IOException("本地状态格式不受支持；请保留原文件并检查，避免重复打卡。");
        Session? session = null;
        if (value.ProtectedToken.Length > 0)
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedToken), null, DataProtectionScope.CurrentUser);
            try { session = new(Encoding.UTF8.GetString(bytes), value.AccountId, value.Nickname); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        value.State.Attempted.UnionWith(value.State.Confirmed);
        return (session, value.State);
    }
    public void Save(Session? session, AppState state)
    {
        var value = new Stored { State = state, AccountId = session?.AccountId ?? "", Nickname = session?.Nickname ?? "" };
        if (session is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(session.Token);
            try { value.ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
            File.Move(temporary, Path.Combine(directory, "state.json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
