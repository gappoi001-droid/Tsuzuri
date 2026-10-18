using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNsave.Core;

public sealed partial class TokenEndpointException : Exception
{
    public int HttpStatus { get; }
    public string ErrorTag { get; }
    public string TokenRequest { get; }

    private TokenEndpointException(int httpStatus, string errorTag, string tokenRequest)
        : base(MessageFor(httpStatus, errorTag, tokenRequest))
    {
        HttpStatus = httpStatus;
        ErrorTag = errorTag;
        TokenRequest = tokenRequest;
    }

    public static TokenEndpointException Parse(int httpStatus, string body, string tokenRequest)
    {
        string tag = "unclassified";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() is { } candidate && ErrorTagPattern().IsMatch(candidate))
                tag = candidate;
        }
        catch (JsonException) { }
        return new(httpStatus, tag, tokenRequest);
    }

    public Dictionary<string, object?> Observed() => new()
    {
        ["token_request"] = TokenRequest, ["http_status"] = HttpStatus, ["token_error"] = ErrorTag
    };

    private static string MessageFor(int status, string tag, string request)
    {
        string explanation = (request, tag) switch
        {
            ("authorization_code", "invalid_grant") => "認可コードが無効または期限切れです。「接続」をやり直してください。",
            ("refresh_token", "invalid_grant") => "保存された認可が無効です。「接続を解除」の後、もう一度「接続」を実行してください。",
            (_, "invalid_client" or "unauthorized_client") => "App key、または App Console の OAuth 設定（公開クライアント・PKCE）を確認してください。",
            _ => "Dropbox のトークン取得・更新に失敗しました。App key・許可状態を確認してください。"
        };
        return $"{explanation}（HTTP {status}, {tag}）";
    }

    [GeneratedRegex(@"\A[a-z_]{1,64}\z")]
    private static partial Regex ErrorTagPattern();
}
