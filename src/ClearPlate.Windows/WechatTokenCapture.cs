using ClearPlate.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Web;

#if MAC_CAPTURE
namespace ClearPlate.MacCapture;
#else
namespace ClearPlate.Windows;
#endif

internal sealed class WechatTokenCapture : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly SemaphoreSlim limit = new(32);
    private readonly CaptureCertificates certificates = new();
    private readonly HttpClient upstream = new(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
#if !MAC_CAPTURE
    private CaptureRecovery? recovery;
#endif
    private int captured;
    private bool disposed;
    internal event Action<string>? TokenFound;
    internal event Action<string>? Diagnostic;
#if MAC_CAPTURE
    internal byte[] PublicRoot => certificates.Root.RawData;
    internal int Start()
    {
        listener.Start();
        _ = ListenAsync();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
#else
    internal void Start()
    {
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            recovery = CaptureNetworkSettings.Activate(port, certificates.Root);
            var executable = Environment.ProcessPath ?? throw new IOException("无法定位恢复守护程序。");
            var watchdog = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            watchdog.ArgumentList.Add("--capture-watchdog"); watchdog.ArgumentList.Add(Environment.ProcessId.ToString()); watchdog.ArgumentList.Add(recovery.Id);
            _ = Process.Start(watchdog) ?? throw new IOException("不能启动异常退出恢复守护程序。");
            _ = ListenAsync();
        }
        catch { Dispose(); throw; }
    }
#endif
    private async Task ListenAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                if (!limit.Wait(0)) { client.Dispose(); continue; }
                clients.TryAdd(client, 0); _ = HandleAsync(client);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) != 0xfc;
        return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168);
    }
    private async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken token)
    {
        var addresses = (await Dns.GetHostAddressesAsync(host, token)).Where(PublicAddress).ToArray();
        if (addresses.Length == 0) throw new IOException("代理不连接本机或内网地址。");
        var client = new TcpClient();
        try { await client.ConnectAsync(addresses, port, token); return client; }
        catch { client.Dispose(); throw; }
    }
    private async Task HandleAsync(TcpClient client)
    {
        var isApi = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var stream = client.GetStream();
            var head = await CaptureProtocol.ReadHeadAsync(stream, deadline.Token); if (head is null) return;
            var first = head.FirstLine.Split(' ');
            if (first.Length != 3) throw new IOException("无效代理请求。");
            if (first[0] != "CONNECT")
            {
                if (!Uri.TryCreate(first[1], UriKind.Absolute, out var plain) || plain.Scheme != "http" || plain.Port != 80 || plain.UserInfo.Length != 0) throw new IOException("仅支持正常 HTTP/HTTPS 代理请求。");
                using var remote = await ConnectAsync(plain.Host, 80, deadline.Token); using var remoteStream = remote.GetStream();
                var text = $"{first[0]} {plain.PathAndQuery} HTTP/1.1\r\n" + string.Join("", head.Headers.Where(h => !h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)).Select(h => $"{h.Key}: {h.Value}\r\n")) + "\r\n";
                await remoteStream.WriteAsync(Encoding.Latin1.GetBytes(text), deadline.Token);
                await RelayAsync(stream, remoteStream, deadline.Token); return;
            }
            if (!Uri.TryCreate("https://" + first[1], UriKind.Absolute, out var destination) || destination.Port != 443
                || destination.UserInfo.Length != 0 || destination.AbsolutePath != "/" || destination.Query.Length != 0 || destination.Fragment.Length != 0)
                throw new IOException("CONNECT 目标异常。");
            isApi = destination.Host.Equals(CaptureProtocol.ApiHost, StringComparison.OrdinalIgnoreCase);
            if (!isApi)
            {
                using var remote = await ConnectAsync(destination.Host, 443, deadline.Token); using var remoteStream = remote.GetStream();
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), deadline.Token);
                await RelayAsync(stream, remoteStream, deadline.Token); return;
            }
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), deadline.Token);
            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            var context = SslStreamCertificateContext.Create(certificates.Server, new System.Security.Cryptography.X509Certificates.X509Certificate2Collection { certificates.Root }, offline: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context,
                ApplicationProtocols = [SslApplicationProtocol.Http11], EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            var requestHead = await CaptureProtocol.ReadHeadAsync(tls, deadline.Token); if (requestHead is null) return;
            var line = requestHead.FirstLine.Split(' ');
            if (line.Length != 3 || line[0] is not ("GET" or "POST" or "OPTIONS") || !line[1].StartsWith('/')) throw new IOException("光盘请求格式不受支持。");
            if (!Uri.TryCreate("https://" + CaptureProtocol.ApiHost + line[1], UriKind.Absolute, out var url)
                || url.Host != CaptureProtocol.ApiHost || url.Port != 443 || url.UserInfo.Length != 0) throw new IOException("光盘目标地址异常。");
            if (requestHead.Headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await tls.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), deadline.Token);
            var body = await CaptureProtocol.ReadBodyAsync(tls, requestHead, deadline.Token);
            using var request = new HttpRequestMessage(new HttpMethod(line[0]), url);
            if (body.Length > 0 || line[0] == "POST") request.Content = new ByteArrayContent(body);
            foreach (var header in requestHead.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Expect", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var responseStream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await responseStream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (bytes.Length + read > CaptureProtocol.MaximumBody) throw new IOException("光盘响应超过采集大小限制。");
                bytes.Write(buffer, 0, read);
            }
            var responseBytes = bytes.ToArray();
            var headers = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} Response\r\nConnection: close\r\nContent-Length: {responseBytes.Length}\r\n");
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (header.Key is not ("Content-Length" or "Transfer-Encoding" or "Connection")) headers.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            headers.Append("\r\n");
            await tls.WriteAsync(Encoding.Latin1.GetBytes(headers.ToString()), deadline.Token);
            await tls.WriteAsync(responseBytes, deadline.Token); await tls.FlushAsync(deadline.Token);
            var route = HttpUtility.ParseQueryString(url.Query)["r"] ?? "";
            var candidate = CaptureProtocol.Candidate(CaptureProtocol.ApiHost, (int)response.StatusCode, route, requestHead.Headers,
                responseBytes, response.Content.Headers.ContentEncoding.FirstOrDefault());
            if (candidate is not null && Interlocked.CompareExchange(ref captured, 1, 0) == 0) TokenFound?.Invoke(candidate);
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            if (isApi && !cancellation.IsCancellationRequested)
                Diagnostic?.Invoke("光盘连接未能完成。请确认微信使用系统代理并接受临时证书；若仍失败，请停止采集。");
        }
        finally { clients.TryRemove(client, out _); client.Dispose(); limit.Release(); }
    }
    private static async Task RelayAsync(Stream left, Stream right, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outgoing = left.CopyToAsync(right, linked.Token); var incoming = right.CopyToAsync(left, linked.Token);
        await Task.WhenAny(outgoing, incoming); linked.Cancel();
        try { await Task.WhenAll(outgoing, incoming); } catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; cancellation.Cancel(); listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
#if !MAC_CAPTURE
        try { if (recovery is not null) _ = CaptureNetworkSettings.Restore(recovery.Id); }
        finally { upstream.Dispose(); certificates.Dispose(); }
#else
        upstream.Dispose(); certificates.Dispose();
#endif
    }
}
