using ClearPlate.Core;
using System.Buffers.Binary;

namespace ClearPlate.Server;

public sealed record SlotView(string Slot, string State, string Detail);
public sealed record Dashboard(string Version, string Day, string Nickname, bool HasSession, bool NeedsLogin, bool Busy,
    bool PersistenceFault, bool ScheduleEnabled, string GroupId, string Location, string Status, string PhotoName,
    string PhotoUpdatedAt, string PhotoVersion, IReadOnlyList<SlotView> Slots);
public sealed record SessionPreview(string Id, string Nickname);
public sealed class ServerController : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly EncryptedStore store;
    private readonly PlateApi api;
    private readonly PlateEngine engine;
    private readonly Func<DateTimeOffset> now;
    private PhotoReference? photo;
    private sealed record PhotoData(byte[] Bytes, string Mime, string Extension);
    private PhotoData image;
    private (string Id, string Token, string Account, DateTimeOffset Expires)? candidate;
    private volatile Dashboard view;
    private bool persistenceFault;
    public ServerController(ServerOptions options, HttpClient http, Func<DateTimeOffset>? clock = null)
    {
        now = clock ?? (() => DateTimeOffset.UtcNow); store = new(options);
        try
        {
            var saved = store.Load(); photo = saved.Photo;
            image = photo is null ? DefaultPhoto() : new(store.LoadPhoto(photo), photo.Mime, photo.Mime == "image/png" ? "png" : "jpg");
            ValidatePhoto(image.Bytes);
            api = new(http, now);
            engine = new(api, saved.State, saved.Session, Save, now);
            view = Snapshot(); engine.Changed += UpdateView;
        }
        catch { store.Dispose(); throw; }
    }
    private static PhotoData DefaultPhoto()
    {
        using var stream = typeof(ServerController).Assembly.GetManifestResourceStream("ClearPlate.default.png") ?? throw new IOException("Default photo missing");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return new(buffer.ToArray(), "image/png", "png");
    }
    private void Save(Session? session, AppState state)
    {
        try { store.Save(session, state, photo); }
        catch { persistenceFault = true; throw; }
    }
    private string Safe(string text)
    {
        if (text.StartsWith("首次使用", StringComparison.Ordinal)) return "已读取保存的本人会话，可检查登录状态或管理今天的打卡计划。";
        if (engine.Session is { Token.Length: > 0 }) text = text.Replace(engine.Session.Token, "[已隐藏]", StringComparison.Ordinal);
        if (candidate is { } pending) text = text.Replace(pending.Token, "[已隐藏]", StringComparison.Ordinal);
        return text.Replace("点击“连接微信”", "在会话设置中更新登录").Replace("请先点击“连接微信”获取有效的本人登录。", "请先在会话设置中更新本人登录。");
    }
    private Dashboard Snapshot() => new("1.3.0", Schedule.Day(now()), engine.Session?.Nickname ?? "尚未连接账号", engine.Session is not null,
        engine.State.NeedsLogin, engine.Busy, persistenceFault, engine.State.ScheduleEnabled, engine.State.GroupId, engine.State.Location,
        persistenceFault ? "本地保存失败，自动提交已暂停；修复存储后重新启动服务。"
            : engine.Session is null ? "先在会话设置中核验本人登录，再填写位置并保存计划。" : Safe(engine.Status), photo?.Name ?? "内置默认照片",
        photo?.UpdatedAt ?? "", photo?.Id ?? "default", Schedule.Slots.Select(slot =>
        {
            var key = Schedule.Key(slot, now()); var detail = engine.State.Records.GetValueOrDefault(key);
            var result = engine.State.Confirmed.Contains(key) ? "成功" : engine.State.Attempted.Contains(key)
                ? detail?.Outcome ?? "结果未确认" : detail?.RetryPolicy == "manual" ? "未提交，可手动重试" : detail?.Outcome ?? "待执行";
            return new SlotView(slot, result, Safe(engine.SlotText(slot)));
        }).ToArray());
    private void UpdateView() => view = Snapshot();
    public Dashboard View => view;
    private async Task<T> Run<T>(Func<Task<T>> work)
    {
        if (!await gate.WaitAsync(0)) throw new ServerProblem(409, "正在处理上一项操作，请稍候。");
        try
        {
            if (persistenceFault) throw new ServerProblem(503, "存储发生故障，请修复后重启服务。");
            return await work();
        }
        finally { UpdateView(); gate.Release(); }
    }
    public Task<ConnectionResult> CheckAsync() => Run(async () => await engine.ConnectAsync());
    public Task<SessionPreview> PreviewAsync(string token) => Run(async () =>
    {
        token = token.Trim();
        if (!PlateApi.ValidToken(token)) throw new ServerProblem(400, "请输入有效的本人 token，临时登录 code 不能使用。");
        UserProfile user;
        try { user = await api.UserAsync(token); }
        catch { throw new ServerProblem(400, "登录核验失败，请从正常登录的微信设备更新会话。"); }
        if (engine.Session is { } current && current.AccountId != user.AccountId) throw new ServerProblem(400, "账号与原绑定不一致，原会话已保留。");
        var id = Guid.NewGuid().ToString("N"); candidate = (id, token, user.AccountId, now().AddMinutes(2));
        return new SessionPreview(id, Safe(user.Nickname));
    });
    public Task ConfirmAsync(string id, bool accepted) => Run(async () =>
    {
        if (!accepted || candidate is not { } pending || pending.Id != id || pending.Expires <= now())
            throw new ServerProblem(400, "确认已失效，请重新核验登录。");
        try
        {
            if (!await engine.ImportAsync(pending.Token, profile => profile.AccountId == pending.Account))
                throw new ServerProblem(400, "未能保存该登录，原会话已保留。");
        }
        finally { candidate = null; }
        return true;
    });
    public Task SettingsAsync(string group, string location, bool enabled) => Run(() =>
    {
        group = group.Trim(); location = location.Trim();
        if (group.Length > 32 || group.Length > 0 && !int.TryParse(group, out _)) throw new ServerProblem(400, "组织 ID 应为整数或留空。");
        var parts = location.Split(',');
        if (location.Length > 128 || location.Length > 0 && (parts.Length != 2
            || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var lon)
            || !double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180))
            throw new ServerProblem(400, "位置应为真实纬度,经度。");
        if (enabled && (engine.Session is null || engine.State.NeedsLogin || location.Length == 0))
            throw new ServerProblem(400, "请先核验登录并设置真实位置，再开启计划。");
        engine.State.GroupId = group; engine.State.Location = location; engine.State.ScheduleEnabled = enabled; engine.Persist();
        return Task.FromResult(true);
    });
    public Task SubmitAsync(string slot) => Run(async () =>
    {
        if (!Schedule.Slots.Contains(slot)) throw new ServerProblem(400, "请选择有效时段。");
        await engine.SubmitAsync(slot, () => (image.Bytes, image.Mime, image.Extension)); return true;
    });
    public async Task TickAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (candidate is { } pending && pending.Expires <= now()) candidate = null;
            if (!persistenceFault) await engine.TickAsync(() => (image.Bytes, image.Mime, image.Extension));
        }
        catch { persistenceFault = true; }
        finally { UpdateView(); gate.Release(); }
    }
    public static string ValidatePhoto(byte[] bytes)
    {
        if (bytes.Length is < 1024 or > 20 * 1024 * 1024) throw new ServerProblem(400, "照片大小需为 1 KB 至 20 MB。");
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)); var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is > 0 and <= 20000 && height is > 0 and <= 20000 && (ulong)width * height <= 60000000) return "image/png";
        }
        else if (bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217) return "image/jpeg";
        throw new ServerProblem(400, "请选择 PNG 或 JPG 照片，最终内容仍由平台正常校验。");
    }
    public Task PhotoAsync(byte[] bytes, string name) => Run(() =>
    {
        var mime = ValidatePhoto(bytes); name = new string(Path.GetFileName(name).Where(c => !char.IsControl(c)).Take(80).ToArray());
        var previous = photo; var next = store.SavePhoto(bytes, mime, name.Length == 0 ? "上传的照片" : name, now());
        photo = next;
        try { engine.Persist(); }
        catch { photo = previous; store.RemovePhoto(next); throw; }
        image = new(bytes, mime, mime == "image/png" ? "png" : "jpg");
        if (previous is not null) store.RemovePhoto(previous);
        return Task.FromResult(true);
    });
    public (byte[] Bytes, string Mime) Photo { get { var value = image; return (value.Bytes, value.Mime); } }
    public void Dispose() { store.Dispose(); gate.Dispose(); }
}
public sealed class ServerProblem(int status, string message) : Exception(message) { public int Status { get; } = status; }
