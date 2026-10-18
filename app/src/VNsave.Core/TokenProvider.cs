using System.Text.Json;

namespace VNsave.Core;

public interface IAccessTokens
{
    Task<string> GetAsync(bool forceRefresh, CancellationToken ct);
    TokenDiagnostics Diagnostics { get; }
}

public sealed record TokenDiagnostics(string? TokenSource, bool CredentialReadSucceeded, int? RefreshHttpStatus,
    double? ExpiresInSeconds, int SuccessfulRefreshCount);

public sealed class TokenProvider(HttpClient http, AppSettings settings, ICredentialStore credentials, SecretRedactor redactor,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IAccessTokens
{
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private string? accessToken;
    private DateTimeOffset expiresAt;
    public string[] GrantedScopes { get; private set; } = [];
    public string? TokenSource { get; private set; }
    public bool CredentialReadSucceeded { get; private set; }
    public int? RefreshHttpStatus { get; private set; }
    public double? ExpiresInSeconds { get; private set; }
    public int SuccessfulRefreshCount { get; private set; }
    public TokenDiagnostics Diagnostics => new(TokenSource, CredentialReadSucceeded, RefreshHttpStatus,
        ExpiresInSeconds, SuccessfulRefreshCount);

    public async Task<string[]> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        redactor.Register(code);
        redactor.Register(verifier);
        var payload = await RequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = settings.AppKey,
            ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier
        }, ct);
        if (!payload.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString()))
            throw new UserError("refresh token が返されませんでした。offline アクセスの設定を確認してください。");
        string refreshToken = refresh.GetString()!;
        redactor.Register(refreshToken);
        credentials.Write(refreshToken);
        AcceptAccessToken(payload);
        TokenSource = "authorization_code";
        return GrantedScopes;
    }

    public async Task<string> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && accessToken is not null && expiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return accessToken;
        CredentialReadSucceeded = false;
        RefreshHttpStatus = null;
        string refresh = credentials.Read() ?? throw new ConnectionUnavailableException();
        CredentialReadSucceeded = true;
        redactor.Register(refresh);
        var payload = await RequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = settings.AppKey
        }, ct);
        AcceptAccessToken(payload);
        TokenSource = "refresh_token";
        SuccessfulRefreshCount++;
        return accessToken!;
    }

    private async Task<JsonElement> RequestAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/oauth2/token")
            { Content = new FormUrlEncodedContent(values) };
            using var response = await http.SendAsync(request, ct);
            if (values["grant_type"] == "refresh_token") RefreshHttpStatus = (int)response.StatusCode;
            if ((int)response.StatusCode == 429 && attempt < 3)
            {
                var retry = response.Headers.RetryAfter;
                TimeSpan interval = retry?.Delta ??
                    (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
                if (interval < TimeSpan.FromMilliseconds(250)) interval = TimeSpan.FromMilliseconds(250);
                await wait(interval, ct);
                continue;
            }
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Only an allowlisted error tag can leave this response parser.
                throw TokenEndpointException.Parse((int)response.StatusCode, body, values["grant_type"]);
            }
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }

    private void AcceptAccessToken(JsonElement payload)
    {
        accessToken = payload.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken)) throw new UserError("access token が返されませんでした。");
        redactor.Register(accessToken);
        ExpiresInSeconds = payload.GetProperty("expires_in").GetDouble();
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(ExpiresInSeconds.Value);
        if (payload.TryGetProperty("scope", out var scope)) GrantedScopes = (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
