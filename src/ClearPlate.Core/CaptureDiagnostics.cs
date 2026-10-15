using System.Net.Sockets;
using System.Security.Authentication;

namespace ClearPlate.Core;

// Only fixed codes leave the transport. Never expose exception text, URLs, headers or bodies.
public static class CaptureDiagnostics
{
    public static string Failure(string stage, Exception error)
    {
        if (stage == "client-tls") return "CLIENT_TLS";
        if (stage == "request") return "CLIENT_REQUEST";
        if (stage == "response") return "CLIENT_RESPONSE";
        if (error is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return "UPSTREAM_DNS";
        if (error is OperationCanceledException) return "UPSTREAM_TIMEOUT";
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException) return "UPSTREAM_TLS";
            if (inner is SocketException socket && socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain) return "UPSTREAM_DNS";
        }
        if (error is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return "UPSTREAM_TLS";
        return "UPSTREAM_NETWORK";
    }
    public static string Describe(string code) => code switch
    {
        "CLIENT_TLS" => "微信与本地代理的 TLS 握手未完成（CLIENT_TLS）。请停止连接后重试；此提示不代表 token 已失效。",
        "CLIENT_REQUEST" => "微信连接已建立，但请求没有完整传输（CLIENT_REQUEST）。",
        "CLIENT_RESPONSE" => "平台已响应，微信端在接收过程中断开了连接（CLIENT_RESPONSE）。",
        "UPSTREAM_TLS" => "本地代理连接光盘平台时 HTTPS 校验失败（UPSTREAM_TLS）；微信端握手已通过。",
        "UPSTREAM_DNS" => "本地代理无法解析平台域名（UPSTREAM_DNS）。请检查网络。",
        "UPSTREAM_TIMEOUT" => "本地代理等待平台响应超时（UPSTREAM_TIMEOUT）。",
        "UPSTREAM_NETWORK" => "本地代理连接平台失败（UPSTREAM_NETWORK）；不能据此判断微信拒绝证书。",
        "NO_CANDIDATE" => "光盘接口已连通，尚未获取可用登录状态。请在微信正常登录并打开首页或“我的”。",
        _ when code.StartsWith("HTTP_", StringComparison.Ordinal) && int.TryParse(code.AsSpan(5), out var status) && status is >= 100 and <= 599
            => $"平台返回 HTTP {status}（{code}），微信端握手已通过，尚未同步登录状态。",
        _ => "连接诊断暂不可用，请停止连接后重试。"
    };
}
