using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ClearPlate.Core;

public sealed record HttpHead(string FirstLine, Dictionary<string, string> Headers);

public static class CaptureProtocol
{
    public const string ApiHost = "clearplate-api.guangpanxingdong.com";
    public const int MaximumBody = 1024 * 1024;
    public static async Task<HttpHead?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 32768)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                return buffer.Length == 0 ? null : throw new IOException("请求头被截断。");
            buffer.WriteByte(one[0]);
            var b = buffer.GetBuffer(); var n = (int)buffer.Length;
            if (n >= 4 && b[n - 4] == 13 && b[n - 3] == 10 && b[n - 2] == 13 && b[n - 1] == 10)
            {
                var lines = Encoding.Latin1.GetString(b, 0, n - 4).Split("\r\n", StringSplitOptions.None);
                if (lines[0].Length == 0) throw new IOException("请求行为空。");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0 || char.IsWhiteSpace(line[0])) throw new IOException("请求头格式异常。");
                    var name = line[..colon]; var value = line[(colon + 1)..].Trim();
                    if (name.Any(c => c <= 32 || c >= 127 || c == ':')) throw new IOException("请求头名称异常。");
                    if (!headers.TryAdd(name, value)) throw new IOException("重复请求头，已拒绝以避免解析歧义。");
                }
                return new(lines[0], headers);
            }
        }
        throw new IOException("请求头超过安全大小限制。");
    }
    public static async Task<byte[]> ReadBodyAsync(Stream stream, HttpHead head, CancellationToken cancellationToken)
    {
        if (head.Headers.ContainsKey("Transfer-Encoding") && head.Headers.ContainsKey("Content-Length"))
            throw new IOException("请求长度定义冲突。");
        if (head.Headers.TryGetValue("Transfer-Encoding", out var transfer))
        {
            if (!transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw new IOException("不支持的请求编码。");
            using var output = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(stream, cancellationToken);
                if (!int.TryParse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, null, out var size)
                    || size < 0 || size > MaximumBody - output.Length) throw new IOException("请求体超过限制。");
                if (size == 0)
                {
                    var trailers = 0;
                    while ((await ReadLineAsync(stream, cancellationToken)).Length > 0)
                        if (++trailers > 32) throw new IOException("尾部请求头超过限制。");
                    return output.ToArray();
                }
                var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk, cancellationToken); output.Write(chunk);
                if (await ReadLineAsync(stream, cancellationToken) != "") throw new IOException("分块编码格式异常。");
            }
        }
        if (!head.Headers.TryGetValue("Content-Length", out var length)) return [];
        if (!int.TryParse(length, out var count) || count < 0 || count > MaximumBody) throw new IOException("请求体超过限制。");
        var body = new byte[count]; await stream.ReadExactlyAsync(body, cancellationToken); return body;
    }
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 2048)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0) throw new IOException("分块请求被截断。");
            bytes.Add(one[0]);
            if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10) return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new IOException("分块行超过限制。");
    }
    public static string? Candidate(string host, int httpStatus, string route, IReadOnlyDictionary<string, string> requestHeaders,
        ReadOnlySpan<byte> response, string? contentEncoding)
    {
        if (!host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase) || httpStatus != 200 || response.Length > MaximumBody) return null;
        try
        {
            byte[] decoded;
            if (string.IsNullOrEmpty(contentEncoding)) decoded = response.ToArray();
            else
            {
                using var input = new MemoryStream(response.ToArray());
                using Stream decompressor = contentEncoding.ToLowerInvariant() switch {
                    "gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    _ => throw new InvalidDataException("不支持的响应编码") };
                using var output = new MemoryStream(); var block = new byte[4096]; int count;
                while ((count = decompressor.Read(block)) > 0)
                {
                    if (output.Length + count > MaximumBody) return null;
                    output.Write(block, 0, count);
                }
                decoded = output.ToArray();
            }
            using var document = JsonDocument.Parse(decoded);
            var result = document.RootElement;
            if (PlateApi.Number(result, "code") != 10007) return null;
            if ((route.TrimStart('/') is "site/login" or "user/user-info") && result.TryGetProperty("data", out var data))
            {
                var issued = PlateApi.Text(data, "token");
                if (PlateApi.ValidToken(issued)) return issued;
            }
            var header = requestHeaders.FirstOrDefault(h => h.Key.Equals("X-Token", StringComparison.OrdinalIgnoreCase)).Value;
            return header is not null && PlateApi.ValidToken(header) ? header : null;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { return null; }
    }
}
