using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ClearPlate.Server;

public sealed record WebSession(string Csrf, DateTimeOffset Expires);
public sealed class WebSessions(ServerOptions options)
{
    public const string Cookie = "clearplate_session";
    private readonly ConcurrentDictionary<string, WebSession> sessions = new();
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(Hash(left), Hash(right));
    private static string Key(string cookie) => Convert.ToHexString(Hash(cookie));
    public bool ValidPassword(string password) => password.Length <= 256 && Equal(password, options.AdminPassword);
    public string Create()
    {
        foreach (var item in sessions.Where(item => item.Value.Expires <= DateTimeOffset.UtcNow).ToArray()) sessions.TryRemove(item.Key, out _);
        if (sessions.Count >= 32)
        {
            var oldest = sessions.OrderBy(item => item.Value.Expires).FirstOrDefault();
            if (oldest.Key is not null) sessions.TryRemove(oldest.Key, out _);
        }
        var cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[Key(cookie)] = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), DateTimeOffset.UtcNow.AddHours(8));
        return cookie;
    }
    public WebSession? Find(string? cookie)
    {
        if (cookie is null || cookie.Length != 64 || !sessions.TryGetValue(Key(cookie), out var value)) return null;
        if (value.Expires > DateTimeOffset.UtcNow) return value;
        sessions.TryRemove(Key(cookie), out _); return null;
    }
    public void Remove(string? cookie) { if (cookie is not null) sessions.TryRemove(Key(cookie), out _); }
    public bool ValidOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true; // non-browser clients still need credentials and CSRF
        var expected = options.PublicOrigin ?? $"{request.Scheme}://{request.Host}";
        return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);
    }
    public CookieOptions CookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(8),
        Secure = options.PublicOrigin?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true || request.IsHttps
    };
}
