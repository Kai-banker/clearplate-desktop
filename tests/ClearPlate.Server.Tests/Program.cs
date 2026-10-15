using ClearPlate.Core;
using ClearPlate.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); count++; }
var scratch = Path.Combine(Path.GetTempPath(), "clearplate-server-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var key = RandomNumberGenerator.GetBytes(32);
const string password = "synthetic-admin-password-123456789";
var now = DateTimeOffset.Parse("2026-10-15T10:00:00+08:00");
ServerOptions Options(string name) => new(Path.Combine(scratch, name), key, password);
WebApplication? running = null;
try
{
    if (args.Contains("--serve-preview"))
    {
        var previewOptions = Options("preview");
        using (var store = new EncryptedStore(previewOptions))
        {
            _ = store.Load(); var state = new AppState { Location = "0,0", GroupId = "123" };
            var slotKey = Schedule.Key("12:00", DateTimeOffset.UtcNow);
            state.Attempted.Add(slotKey); state.Records[slotKey] = new("2026-10-15 12:00:00", "2026-10-15 12:00:00", "准备照片", "未成功", "模拟照片读取失败");
            store.Save(new("fake-A", "A", "模拟账号"), state, null);
        }
        running = ServerApp.Build(["--contentRoot", Path.GetFullPath("src/ClearPlate.Server")], previewOptions,
            new HttpClient(new FakePlatform(() => DateTimeOffset.UtcNow)));
        running.Urls.Add("http://127.0.0.1:18787");
        try { await running.RunAsync(); } finally { running = null; }
        return;
    }
    var options = Options("vault");
    using (var store = new EncryptedStore(options))
    {
        Check(!store.Load().State.ScheduleEnabled, "fresh vault starts with schedule disabled");
        var state = new AppState { Location = "12.345678,98.765432" };
        store.Save(new("fake-secret-token", "A", "测试账号"), state, null);
        var encoded = File.ReadAllBytes(Path.Combine(options.DataDirectory, "state.enc"));
        Check(!Encoding.UTF8.GetString(encoded).Contains("fake-secret-token") && !Encoding.UTF8.GetString(encoded).Contains(state.Location), "session and precise location are encrypted on disk");
        store.Save(new("fake-secret-token", "A", "测试账号"), state, null);
        Check(!encoded.SequenceEqual(File.ReadAllBytes(Path.Combine(options.DataDirectory, "state.enc"))), "fresh GCM nonce for every save");
        Check(store.Load().Session?.Token == "fake-secret-token", "encrypted session roundtrip");
        try { using var other = new EncryptedStore(options); throw new Exception("second store acquired lease"); }
        catch (IOException) { count++; }
        if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(Path.Combine(options.DataDirectory, "state.enc")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "private file permissions");
        var beforeClose = File.ReadAllBytes(Path.Combine(options.DataDirectory, "state.enc"));
        store.Dispose();
        try { store.Save(null, state, null); throw new Exception("late save used disposed encryption key"); } catch (ObjectDisposedException) { count++; }
        Check(beforeClose.SequenceEqual(File.ReadAllBytes(Path.Combine(options.DataDirectory, "state.enc"))), "late shutdown writes cannot corrupt persisted state");
    }
    using (var store = new EncryptedStore(options with { EncryptionKey = RandomNumberGenerator.GetBytes(32) }))
    {
        try { _ = store.Load(); throw new Exception("wrong key accepted"); } catch (CryptographicException) { count++; }
    }
    using (var store = new EncryptedStore(options))
    {
        var path = Path.Combine(options.DataDirectory, "state.enc"); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        try { _ = store.Load(); throw new Exception("tampered state accepted"); } catch (CryptographicException) { count++; }
        File.Delete(path);
        try { _ = store.Load(); throw new Exception("missing initialized state silently reset"); } catch (IOException) { count++; }
    }
    var handler = new FakePlatform(() => now);
    var serverOptions = Options("http");
    var serverRoot = Path.GetFullPath("src/ClearPlate.Server");
    async Task<(WebApplication App, Uri Address)> Start()
    {
        var app = ServerApp.Build(["--contentRoot", serverRoot], serverOptions, new HttpClient(handler, disposeHandler: false), () => now);
        app.Urls.Add("http://127.0.0.1:0"); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new Uri(address));
    }
    var host = await Start(); running = host.App;
    var cookies = new CookieContainer();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = host.Address };
    async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.Clone();
    }
    async Task<HttpResponseMessage> Post(string route, object? value, string? csrf = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(value ?? new { }) };
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    Check((await client.GetAsync("/healthz")).StatusCode == HttpStatusCode.OK, "minimal anonymous health endpoint");
    var html = await client.GetAsync("/");
    Check(html.IsSuccessStatusCode && (await html.Content.ReadAsStringAsync()).Contains("登录管理页"), "real static management UI is served");
    Check(html.Headers.Contains("Content-Security-Policy") && html.Headers.GetValues("X-Frame-Options").Single() == "DENY", "security headers on static UI");
    foreach (var route in new[] { "/api/state", "/api/photo" }) Check((await client.GetAsync(route)).StatusCode == HttpStatusCode.Unauthorized, "state and photos require authentication");
    Check((await Post("/api/login", new { password = "wrong-password" })).StatusCode == HttpStatusCode.Unauthorized, "wrong admin password rejected");
    Check((await Post("/api/login", new { password })).IsSuccessStatusCode, "administrator login");
    var authCookie = cookies.GetCookies(host.Address)[WebSessions.Cookie]!;
    Check(authCookie.HttpOnly, "authentication cookie is HttpOnly");
    var reply = await Read(await client.GetAsync("/api/state")); var csrf = reply.GetProperty("csrf").GetString()!;
    Check(csrf != authCookie.Value && !reply.GetProperty("state").GetProperty("scheduleEnabled").GetBoolean(), "CSRF token separated from cookie; default plan off");
    Check((await Post("/api/check", new { })).StatusCode == HttpStatusCode.Forbidden, "missing CSRF rejected");
    Check((await Post("/api/check", new { }, csrf, "https://attacker.invalid")).StatusCode == HttpStatusCode.Forbidden, "cross-origin authenticated mutation rejected");
    Check((await Post("/api/settings", new { groupId = "", location = "0,0", scheduleEnabled = true }, csrf)).StatusCode == HttpStatusCode.BadRequest, "plan cannot enable before account binding");
    Check((await Post("/api/session/preview", new { token = "code|:|temporary" }, csrf)).StatusCode == HttpStatusCode.BadRequest && handler.Calls == 0, "temporary WeChat code never sent as token");
    reply = await Read(await Post("/api/session/preview", new { token = "fake-A" }, csrf));
    var candidate = reply.GetProperty("id").GetString()!;
    Check(reply.GetProperty("nickname").GetString() == "模拟账号" && !reply.GetRawText().Contains("fake-A"), "preview returns nickname without token");
    Check((await Post("/api/session/confirm", new { id = candidate, accepted = false }, csrf)).StatusCode == HttpStatusCode.BadRequest, "first account requires explicit confirmation");
    Check((await Post("/api/session/confirm", new { id = candidate, accepted = true }, csrf)).IsSuccessStatusCode, "confirmed first account saved");
    reply = await Read(await client.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("hasSession").GetBoolean() && !reply.GetRawText().Contains("fake-A"), "dashboard does not disclose credentials");
    Check((await Post("/api/session/preview", new { token = "fake-B" }, csrf)).StatusCode == HttpStatusCode.BadRequest, "wrong-account candidate rejected");
    var beforeCheck = handler.Calls;
    reply = await Read(await Post("/api/check", new { }, csrf));
    Check(!reply.GetProperty("needsUpdate").GetBoolean() && handler.Calls == beforeCheck+1 && handler.Uploads == 0, "saved login checked without upload or capture");
    Check((await Post("/api/settings", new { groupId = "", location = "NaN,0", scheduleEnabled = true }, csrf)).StatusCode == HttpStatusCode.BadRequest, "non-finite location rejected");
    Check((await Post("/api/settings", new { groupId = "123", location = "0,0", scheduleEnabled = true }, csrf)).IsSuccessStatusCode, "valid configuration saved");
    var defaultPhoto = await client.GetByteArrayAsync("/api/photo");
    async Task<HttpResponseMessage> Photo(byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/photo") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Photo-Name", Uri.EscapeDataString("../../my-photo.png")); return await client.SendAsync(request);
    }
    Check((await Photo(new byte[2048])).StatusCode == HttpStatusCode.BadRequest, "invalid photo data rejected");
    Check((await Photo(defaultPhoto)).IsSuccessStatusCode, "valid PNG stored encrypted");
    reply = await Read(await client.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("photoName").GetString() == "my-photo.png", "photo filename cannot choose storage path");
    var previousPhotoVersion = reply.GetProperty("state").GetProperty("photoVersion").GetString();
    await Photo(defaultPhoto); reply = await Read(await client.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("photoVersion").GetString() != previousPhotoVersion, "photo revision changes even when uploads share a timestamp");
    Check(Directory.GetFiles(serverOptions.DataDirectory, "photo-*.enc").Length == 1, "only current encrypted photo retained after durable state save");
    Check((await client.GetByteArrayAsync("/api/photo")).SequenceEqual(defaultPhoto), "authenticated photo roundtrip");
    Check((await Post("/api/submit", new { slot = "09:00" }, csrf)).StatusCode == HttpStatusCode.BadRequest, "unknown slot rejected");
    Check((await Post("/api/submit", new { slot = "12:00" }, csrf)).IsSuccessStatusCode && handler.Punches == 1, "explicit submit invokes fake platform exactly once");
    var calls = handler.Calls;
    await Post("/api/submit", new { slot = "12:00" }, csrf);
    Check(handler.Punches == 1 && handler.Calls == calls, "confirmed slot blocks duplicate requests");
    handler.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously); handler.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var checking = Post("/api/check", new { }, csrf); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check((await Post("/api/settings", new { groupId = "123", location = "0,0", scheduleEnabled = false }, csrf)).StatusCode == HttpStatusCode.Conflict, "overlapping mutations return conflict instead of racing");
    reply = await Read(await client.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("busy").GetBoolean(), "busy snapshot remains readable during upstream request");
    handler.Pause.SetResult(true); await checking; handler.Pause = null;
    handler.Mode = "expired"; reply = await Read(await Post("/api/check", new { }, csrf));
    Check(reply.GetProperty("needsUpdate").GetBoolean() && reply.GetProperty("state").GetProperty("needsLogin").GetBoolean(), "expiry pauses automated submission");
    calls = handler.Calls; await running.Services.GetRequiredService<ServerController>().TickAsync();
    Check(handler.Calls == calls, "expired login does not trigger background requests");
    handler.Mode = "ok"; reply = await Read(await Post("/api/session/preview", new { token = "fake-A-new" }, csrf));
    await Post("/api/session/confirm", new { id = reply.GetProperty("id").GetString(), accepted = true }, csrf);
    now = now.AddHours(3); handler.Mode = "upload-error";
    await Post("/api/submit", new { slot = "18:00" }, csrf); calls = handler.Calls;
    await Post("/api/submit", new { slot = "18:00" }, csrf);
    Check(handler.Calls == calls && handler.Punches == 1, "uncertain upload remains guarded");
    handler.Mode = "ok"; handler.LastCheck = null; now = DateTimeOffset.Parse("2026-10-16T08:00:00+08:00");
    var controller = running.Services.GetRequiredService<ServerController>();
    await controller.TickAsync(); await controller.TickAsync();
    Check(handler.Punches == 2, "scheduled tick submits each slot once");
    now = now.AddMinutes(5); calls = handler.Calls; await controller.TickAsync();
    Check(handler.Calls == calls, "missed window does not backfill");
    for (var i = 0; i < 3; i++) await Post("/api/login", new { password = "wrong" });
    Check((await Post("/api/login", new { password = "wrong" })).StatusCode == (HttpStatusCode)429, "login attempts rate limited");
    var moved = serverOptions.DataDirectory + "-saved"; Directory.Move(serverOptions.DataDirectory, moved);
    Directory.CreateDirectory(Path.Combine(serverOptions.DataDirectory, "state.enc"));
    Check((await Post("/api/settings", new { groupId = "123", location = "0,0", scheduleEnabled = false }, csrf)).StatusCode == HttpStatusCode.ServiceUnavailable, "storage fault fails closed");
    reply = await Read(await client.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("persistenceFault").GetBoolean(), "storage fault visible in dashboard");
    calls = handler.Calls; await controller.TickAsync(); await Post("/api/submit", new { slot = "12:00" }, csrf);
    Check(handler.Calls == calls, "storage fault prevents additional submissions");
    await running.StopAsync(); await running.DisposeAsync(); running = null;
    Directory.Delete(serverOptions.DataDirectory, true); Directory.Move(moved, serverOptions.DataDirectory);
    host = await Start(); running = host.App;
    using var restarted = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = host.Address };
    Check((await restarted.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "restart invalidates memory-only browser sessions");
    Check((await restarted.PostAsJsonAsync("/api/login", new { password })).IsSuccessStatusCode, "can log in after restart");
    reply = await Read(await restarted.GetAsync("/api/state"));
    Check(reply.GetProperty("state").GetProperty("hasSession").GetBoolean() && reply.GetProperty("state").GetProperty("scheduleEnabled").GetBoolean(), "encrypted account and schedule survive restart");
    Check((await restarted.GetByteArrayAsync("/api/photo")).SequenceEqual(defaultPhoto), "encrypted photo survives restart");
    using (var stored = new EncryptedStore(Options("inspect"))) { _ = stored.Load(); }
    foreach (var file in Directory.EnumerateFiles(serverOptions.DataDirectory, "*.enc"))
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("fake-A"), "no plaintext credentials in persisted blobs");
    Console.WriteLine($"PASS: {count} server storage/authentication/CSRF/scheduler/HTTP checks. Fake platform only; no real upload, proxy or trust changes.");
}
finally
{
    if (running is not null) { await running.StopAsync(); await running.DisposeAsync(); }
    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

sealed class FakePlatform(Func<DateTimeOffset> clock) : HttpMessageHandler
{
    public int Calls, Uploads, Punches, Successes = 10;
    public string Mode = "ok";
    public DateTimeOffset? LastCheck;
    public TaskCompletionSource<bool>? Pause, Entered;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var route = Uri.UnescapeDataString(request.RequestUri!.Query).Split('=', 2).Last(); object body;
        if (request.RequestUri.Host == "clearplateimg.guangpanxingdong.com") { Uploads++; body = new { }; }
        else if (route == "/user/user-info")
        {
            if (Pause is not null) { Entered?.TrySetResult(true); await Pause.Task.WaitAsync(cancellationToken); }
            var token = request.Headers.GetValues("X-Token").Single();
            body = Mode == "expired" ? new { code = 10003 } : (object)new { code = 10007, data = new {
                user_id = token == "fake-B" ? "B" : "A", nickname = "模拟账号", clearplate_num_success = Successes, today_clearplate_num = 3,
                last_clearplate_clock_time = LastCheck?.ToUnixTimeSeconds() ?? 0 } };
        }
        else if (route == "/upload/my-upload")
        {
            if (Mode == "upload-error") throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic upload failure");
            body = new { code = 10007, data = new { host = "https://clearplateimg.guangpanxingdong.com", dir = "clockImg/" + Schedule.Beijing(clock()).ToString("yyyy/MM/dd") + "/",
                policy = "fake", accessid = "fake", signature = "fake", expire = clock().ToUnixTimeSeconds()+600 } };
        }
        else if (route == "/user-clear-plate/clear-plate") { Punches++; Successes++; LastCheck = clock(); body = new { code = 10007 }; }
        else throw new Exception("Unexpected fake request");
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
