using System.Net;
using System.Text;
using System.Text.Json;

namespace VNsave.Core.Tests;

public sealed class TokenFailureTests
{
    [Theory]
    [InlineData("authorization_code", "invalid_grant", "認可コードが無効または期限切れ", "「接続」をやり直してください")]
    [InlineData("refresh_token", "invalid_grant", "保存された認可が無効", "「接続を解除」の後")]
    [InlineData("authorization_code", "invalid_client", "App Console", "公開クライアント・PKCE")]
    [InlineData("refresh_token", "invalid_client", "App Console", "公開クライアント・PKCE")]
    [InlineData("authorization_code", "unauthorized_client", "App Console", "公開クライアント・PKCE")]
    [InlineData("refresh_token", "unauthorized_client", "App Console", "公開クライアント・PKCE")]
    [InlineData("refresh_token", "temporarily_unavailable", "トークン取得・更新に失敗", "temporarily_unavailable")]
    public async Task FailureTagAndRequestControlTheSafeMessage(string requestType, string tag, string expected, string instruction)
    {
        const string hidden = "sl.unknown-private-description-token";
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json(new
        {
            error = tag, error_description = hidden, other = "private-other-field"
        }, HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler);
        var credentials = new MemoryCredentials { Value = "saved-private-refresh" };
        var redactor = new SecretRedactor();
        var provider = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, credentials, redactor);
        var error = await Assert.ThrowsAsync<TokenEndpointException>(async () =>
        {
            if (requestType == "authorization_code")
                await provider.ExchangeCodeAsync("private-code", "private-verifier", "http://127.0.0.1:53134/callback", default);
            else await provider.GetAsync(true, default);
        });
        Assert.Equal(400, error.HttpStatus);
        Assert.Equal(tag, error.ErrorTag);
        Assert.Equal(requestType, error.TokenRequest);
        Assert.Contains(expected, error.Message);
        Assert.Contains(instruction, error.Message);
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains(tag, error.Message);
        Assert.Equal("saved-private-refresh", credentials.Value);
        Assert.Equal(0, provider.SuccessfulRefreshCount);
        foreach (string output in new[] { error.Message, error.ToString(), redactor.FormatError(error), JsonSerializer.Serialize(error.Observed()) })
        {
            Assert.DoesNotContain(hidden, output);
            Assert.DoesNotContain("private-other-field", output);
            Assert.DoesNotContain("saved-private-refresh", output);
            Assert.DoesNotContain("private-code", output);
            Assert.DoesNotContain("private-verifier", output);
        }
    }

    public static TheoryData<string> UnclassifiedBodies => new()
    {
        "not JSON: private-description",
        "[]",
        "null",
        "{}",
        "{\"error\":null}",
        "{\"error\":42}",
        "{\"error\":{\"tag\":\"invalid_grant\"}}",
        "{\"error\":\"INVALID_GRANT\"}",
        "{\"error\":\"invalid-grant\"}",
        "{\"error\":\"invalid_grant/other\"}",
        "{\"error\":\"\"}",
        "{\"error\":\"invalid_grant\\n\"}",
        JsonSerializer.Serialize(new { error = new string('a', 65) })
    };

    [Theory]
    [MemberData(nameof(UnclassifiedBodies))]
    public async Task InvalidErrorValuesAreUnclassified(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(handler);
        var provider = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, new MemoryCredentials { Value = "saved-refresh" }, new SecretRedactor());
        var error = await Assert.ThrowsAsync<TokenEndpointException>(() => provider.GetAsync(true, default));
        Assert.Equal("unclassified", error.ErrorTag);
        Assert.Contains("HTTP 400, unclassified", error.Message);
        Assert.DoesNotContain("private-description", error.Message);
    }

    [Fact]
    public void ErrorTagLimitIncludesExactly64AllowedCharacters()
    {
        string tag = new('a', 64);
        Assert.Equal(tag, TokenEndpointException.Parse(400, JsonSerializer.Serialize(new { error = tag }), "refresh_token").ErrorTag);
    }

    [Fact]
    public async Task DiagnosticsTrackAcquisitionSourceAndOnlySuccessfulRefreshes()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json(new
        {
            access_token = "private-access", refresh_token = "private-refresh", expires_in = 14400,
            scope = "account_info.read"
        })));
        using var http = new HttpClient(handler);
        var credentials = new MemoryCredentials();
        var provider = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, credentials, new SecretRedactor());
        await provider.ExchangeCodeAsync("private-code", "private-verifier", "http://127.0.0.1:53134/callback", default);
        Assert.Equal("authorization_code", provider.TokenSource);
        Assert.Equal(0, provider.SuccessfulRefreshCount);
        Assert.Null(provider.RefreshHttpStatus);
        await provider.GetAsync(false, default);
        Assert.Equal(0, provider.SuccessfulRefreshCount);
        await provider.GetAsync(true, default);
        Assert.Equal("refresh_token", provider.TokenSource);
        Assert.Equal(1, provider.SuccessfulRefreshCount);
        Assert.True(provider.CredentialReadSucceeded);
        Assert.Equal(200, provider.RefreshHttpStatus);
        Assert.Equal(14400, provider.ExpiresInSeconds);
        await provider.GetAsync(false, default);
        Assert.Equal(1, provider.SuccessfulRefreshCount);
        await provider.GetAsync(true, default);
        Assert.Equal(2, provider.SuccessfulRefreshCount);
        string diagnostics = JsonSerializer.Serialize(provider.Diagnostics);
        Assert.DoesNotContain("private-access", diagnostics);
        Assert.DoesNotContain("private-refresh", diagnostics);
    }
}
