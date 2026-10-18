using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VNsave.Core;

public static class Pkce
{
    public static string GenerateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Uri AuthorizationUri(AppSettings settings, string redirectUri, string state, string challenge)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = settings.AppKey,
            ["response_type"] = "code",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["token_access_type"] = "offline",
            ["redirect_uri"] = redirectUri,
            ["state"] = state
        };
        if (settings.Scopes.Length > 0) values["scope"] = string.Join(' ', settings.Scopes);
        return new Uri("https://www.dropbox.com/oauth2/authorize?" + string.Join('&',
            values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));
    }
}

public sealed record CallbackDecision(int StatusCode, bool Complete = false, string? Code = null, bool Denied = false);

public static class CallbackProcessor
{
    public static CallbackDecision Process(string method, string target, string expectedState)
    {
        if (method != "GET") return new(405);
        if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal) ||
            !Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out var uri)) return new(400);
        if (uri.AbsolutePath != "/callback") return new(404);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=', 2);
                string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
                string value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
                if (!query.TryAdd(key, value)) return new(400);
            }
        }
        catch (UriFormatException) { return new(400); }
        if (!query.TryGetValue("state", out var receivedState) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(receivedState)))
            return new(400);
        if (query.ContainsKey("error")) return new(200, Complete: true, Denied: true);
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) return new(400);
        return new(200, Complete: true, Code: code);
    }
}

public sealed class OAuthLoopback : IDisposable
{
    private readonly List<TcpListener> listeners;
    public string RedirectUri { get; }
    public int Port { get; }

    private OAuthLoopback(List<TcpListener> listeners, string host, int port)
    {
        this.listeners = listeners;
        Port = port;
        RedirectUri = $"http://{host}:{port}/callback";
    }

    public static OAuthLoopback Start(string host, IEnumerable<int> ports)
    {
        if (host is not ("127.0.0.1" or "localhost")) throw new UserError("ループバック以外では待ち受けできません。");
        foreach (int port in ports)
        {
            var bound = new List<TcpListener>();
            try
            {
                var ipv4 = new TcpListener(IPAddress.Loopback, port);
                bound.Add(ipv4);
                ipv4.ExclusiveAddressUse = true;
                ipv4.Start();
                int actualPort = ((IPEndPoint)ipv4.LocalEndpoint).Port;
                if (host == "localhost")
                {
                    var ipv6 = new TcpListener(IPAddress.IPv6Loopback, actualPort);
                    bound.Add(ipv6);
                    ipv6.Server.DualMode = false;
                    ipv6.ExclusiveAddressUse = true;
                    ipv6.Start();
                }
                return new OAuthLoopback(bound, host, actualPort);
            }
            catch (SocketException)
            {
                foreach (var listener in bound) listener.Stop();
            }
        }
        throw new UserError("登録済みのポート候補で待ち受けできません。ポートの使用状況を確認してください。");
    }

    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(5));
        var tasks = listeners.Select(l => AcceptAsync(l, expectedState, linked.Token)).ToArray();
        try
        {
            var completed = await Task.WhenAny(tasks);
            return await completed;
        }
        finally
        {
            await linked.CancelAsync();
            foreach (var listener in listeners) listener.Stop();
            try { await Task.WhenAll(tasks); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or UserError or ObjectDisposedException) { }
        }
    }

    private static async Task<string> AcceptAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                int used = 0;
                while (used < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(used), requestTimeout.Token);
                    if (read == 0) break;
                    used += read;
                    if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                string request = Encoding.ASCII.GetString(buffer, 0, used);
                string[] line = request.Split("\r\n", 2)[0].Split(' ');
                var decision = line.Length == 3 && request.Contains("\r\n\r\n", StringComparison.Ordinal)
                    ? CallbackProcessor.Process(line[0], line[1], state) : new CallbackDecision(400);
                string message = decision.Complete
                    ? "受け付けました。このタブを閉じ、コマンド画面で結果を確認してください。"
                    : "このリクエストは受け付けられません。";
                byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><title>VNsave</title><p>" + message + "</p></html>");
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {decision.StatusCode} {Reason(decision.StatusCode)}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\n\r\n");
                await stream.WriteAsync(header, requestTimeout.Token);
                await stream.WriteAsync(body, requestTimeout.Token);
                if (decision.Denied) throw new UserError("Dropbox のアクセス許可が取り消されました。");
                if (decision.Complete) return decision.Code!;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) when (!ct.IsCancellationRequested) { }
        }
    }

    private static string Reason(int status) => status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Bad Request" };
    public void Dispose() { foreach (var listener in listeners) listener.Stop(); }
}
