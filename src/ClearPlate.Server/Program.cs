using ClearPlate.Server;

if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        var response = await http.GetAsync("http://127.0.0.1:8080/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}
try
{
    await using var app = ServerApp.Build(args, ServerOptions.FromEnvironment());
    await app.RunAsync(); return 0;
}
catch (Exception error)
{
    var code = error switch
    {
        System.Security.Cryptography.CryptographicException => "STATE_DECRYPT_FAILED",
        UnauthorizedAccessException => "DATA_ACCESS_DENIED",
        FileNotFoundException => "REQUIRED_FILE_MISSING",
        FormatException => "SECRET_FORMAT_INVALID",
        IOException => "DATA_LOCK_OR_IO_FAILED",
        _ => "SERVER_CONFIG_FAILED"
    };
    Console.Error.WriteLine($"ClearPlate server stopped ({code}). Check secrets, permissions, key and single-instance ownership. Existing state was not reset.");
    return 1;
}
