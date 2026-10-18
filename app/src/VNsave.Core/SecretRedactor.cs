using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VNsave.Core;

public sealed partial class SecretRedactor
{
    private readonly HashSet<string> secrets = new(StringComparer.Ordinal);

    public void Register(string? value)
    {
        if (!string.IsNullOrEmpty(value)) secrets.Add(value);
    }

    public string Redact(string text)
    {
        foreach (var secret in secrets.OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        text = BearerPattern().Replace(text, "Bearer [REDACTED]");
        text = SensitiveFieldPattern().Replace(text, "$1[REDACTED]");
        return DropboxTokenPattern().Replace(text, "[REDACTED]");
    }

    public string FormatError(Exception error) => Redact(error switch
    {
        TokenEndpointException token => token.Message,
        DropboxApiException api => api.Message,
        UserError user => user.Message,
        OperationCanceledException => "処理を中止しました（または待機がタイムアウトしました）。",
        System.Text.Json.JsonException => "JSON の形式が不正です。設定または API 応答を確認してください。",
        HttpRequestException => "HTTP 接続に失敗しました。ネットワークと接続先を確認してください。",
        IOException => "ファイルまたは接続の読み書きに失敗しました。空き容量・アクセス権を確認してください。",
        UnauthorizedAccessException => "ファイルまたは資格情報へのアクセスが拒否されました。",
        _ => $"処理に失敗しました（{error.GetType().Name}）。"
    });

    // Redact decoded JSON values before serializing to disk so escaping and value types stay valid.
    public (JsonNode? Value, bool Changed) RedactJson(JsonNode? value)
    {
        if (value is JsonObject source)
        {
            var result = new JsonObject();
            bool changed = false;
            foreach (var property in source)
            {
                if (SensitiveKeyPattern().IsMatch(property.Key) && property.Value is not null)
                {
                    bool alreadyRedacted = property.Value is JsonValue item && item.TryGetValue<string>(out var text) && text == "[REDACTED]";
                    result[property.Key] = "[REDACTED]";
                    changed |= !alreadyRedacted;
                }
                else
                {
                    var child = RedactJson(property.Value);
                    result[property.Key] = child.Value;
                    changed |= child.Changed;
                }
            }
            return (result, changed);
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            bool changed = false;
            foreach (var element in array)
            {
                var child = RedactJson(element);
                result.Add(child.Value);
                changed |= child.Changed;
            }
            return (result, changed);
        }
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var original))
        {
            string safe = Redact(original);
            return (JsonValue.Create(safe), safe != original);
        }
        return (value?.DeepClone(), false);
    }

    [GeneratedRegex(@"Bearer\s+[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(""?(?:access_token|refresh_token|code_verifier|code|authorization)""?\s*[:=]\s*""?)[^""\s&},]+", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveFieldPattern();

    [GeneratedRegex(@"\A(?:access_token|refresh_token|code_verifier|code|authorization)\z", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeyPattern();

    [GeneratedRegex(@"\bsl\.[A-Za-z0-9._~\-]+")]
    private static partial Regex DropboxTokenPattern();
}
