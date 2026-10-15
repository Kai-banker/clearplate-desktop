namespace ClearPlate.Server;

public sealed record ServerOptions(string DataDirectory, byte[] EncryptionKey, string AdminPassword, string? PublicOrigin = null)
{
    public static ServerOptions FromEnvironment()
    {
        var key = Convert.FromBase64String(File.ReadAllText(Environment.GetEnvironmentVariable("CLEARPLATE_KEY_FILE") ?? "/run/secrets/session_key").Trim());
        var password = File.ReadAllText(Environment.GetEnvironmentVariable("CLEARPLATE_PASSWORD_FILE") ?? "/run/secrets/admin_password").Trim();
        var origin = Environment.GetEnvironmentVariable("CLEARPLATE_PUBLIC_ORIGIN");
        if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0))
            throw new InvalidOperationException("Invalid public origin");
        if (key.Length != 32 || password.Length is < 20 or > 256) throw new InvalidOperationException("Invalid secret configuration");
        return new(Environment.GetEnvironmentVariable("CLEARPLATE_DATA_DIR") ?? "/data", key, password, origin?.TrimEnd('/'));
    }
}
