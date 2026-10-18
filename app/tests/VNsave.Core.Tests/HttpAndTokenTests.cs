using System.Net;
using System.Text.Json;

namespace VNsave.Core.Tests;

public sealed class HttpAndTokenTests
{
    [Fact]
    public async Task ArgumentlessRpcHasNoBodyAnd401RefreshesOnce()
    {
        int calls = 0;
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Null(request.Content);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            calls++;
            return Task.FromResult(calls == 1 ? StubHandler.Error("expired_access_token/...", HttpStatusCode.Unauthorized)
                : StubHandler.Json(new { account_id = "test" }));
        });
        using var http = new HttpClient(handler);
        var tokens = new FakeTokens();
        var client = new DropboxClient(http, tokens, new SecretRedactor());
        await client.GetCurrentAccountAsync(default);
        Assert.Equal(2, calls);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task Repeated401DoesNotLoop()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Error("expired_access_token/...", HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var tokens = new FakeTokens();
        var client = new DropboxClient(http, tokens, new SecretRedactor());
        var error = await Assert.ThrowsAsync<DropboxApiException>(() => client.GetCurrentAccountAsync(default));
        Assert.Equal(401, error.HttpStatus);
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal(2, client.Observations.Count);
    }

    [Fact]
    public async Task RateLimitsHonorRetryAfterAndUsePositiveDelayForZero()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            if (calls > 2) return Task.FromResult(StubHandler.Json(new { account_id = "test" }));
            var response = StubHandler.Error("too_many_write_operations/...", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(calls == 1 ? 2 : 0));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new SecretRedactor(), (duration, _) => { waits.Add(duration); return Task.CompletedTask; });
        await client.GetCurrentAccountAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(2), waits[0]);
        Assert.True(waits[1] > TimeSpan.Zero);
    }

    [Fact]
    public async Task Persistent5xxHasBoundedRetries()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Error("internal_error", HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new SecretRedactor(), (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<DropboxApiException>(() => client.GetCurrentAccountAsync(default));
        Assert.Equal(4, client.Observations.Count);
    }

    [Fact]
    public async Task StoredRefreshTokenWorksInNewProviderAndForceRefreshBypassesCache()
    {
        int calls = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("grant_type=refresh_token", body);
            Assert.Contains("client_id=testkey", body);
            Assert.Contains("refresh_token=test-refresh-value", body);
            calls++;
            return StubHandler.Json(new { access_token = "test-access-" + calls, expires_in = 14400 });
        });
        using var http = new HttpClient(handler);
        var credentials = new MemoryCredentials { Value = "test-refresh-value" };
        var settings = new AppSettings { AppKey = "testkey" };
        var provider = new TokenProvider(http, settings, credentials, new SecretRedactor());
        Assert.Equal("test-access-1", await provider.GetAsync(false, default));
        Assert.Equal("test-access-1", await provider.GetAsync(false, default));
        Assert.Equal("test-access-2", await provider.GetAsync(true, default));
        provider = new TokenProvider(http, settings, credentials, new SecretRedactor());
        Assert.Equal("test-access-3", await provider.GetAsync(false, default));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TokenEndpointHonorsRateLimitBeforeRetrying()
    {
        int calls = 0;
        TimeSpan waited = TimeSpan.Zero;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            if (calls > 1) return Task.FromResult(StubHandler.Json(new { access_token = "access-after-retry", expires_in = 14400 }));
            var response = StubHandler.Error("rate_limit", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var provider = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, new MemoryCredentials { Value = "refresh-private" },
            new SecretRedactor(), (duration, _) => { waited = duration; return Task.CompletedTask; });
        Assert.Equal("access-after-retry", await provider.GetAsync(false, default));
        Assert.Equal(TimeSpan.FromSeconds(3), waited);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AppendReconcilesOnlyTheExactAlreadyReceivedChunk()
    {
        using var directory = new TempDirectory();
        string file = directory.File("chunk.bin");
        await File.WriteAllBytesAsync(file, new byte[8]);
        int appends = 0;
        using var handler = new StubHandler((request, _) =>
        {
            string endpoint = request.RequestUri!.AbsolutePath;
            if (endpoint.EndsWith("/start", StringComparison.Ordinal)) return Task.FromResult(StubHandler.Json(new { session_id = "session-private" }));
            if (endpoint.EndsWith("/append_v2", StringComparison.Ordinal))
            {
                appends++;
                if (appends == 1) return Task.FromResult(StubHandler.Error("internal_error", HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(StubHandler.Json(new { error_summary = "incorrect_offset/...", error = new { correct_offset = 8 } }, HttpStatusCode.Conflict));
            }
            return Task.FromResult(StubHandler.Json(new { id = "id:file", name = "chunk.bin", path_lower = "/chunk.bin", rev = "rev", size = 8, content_hash = "hash" }));
        });
        using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new SecretRedactor(), (_, _) => Task.CompletedTask);
        var uploaded = await client.UploadSessionAsync("/chunk.bin", file, default);
        Assert.Equal(8, uploaded.Size);
        Assert.Equal(2, appends);
    }

    [Fact]
    public async Task CodeExchangeSavesRefreshOnlyAndCredentialFailureIsFatal()
    {
        using var handler = new StubHandler(async (request, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("code_verifier=verifier-value", body);
            Assert.Contains("client_id=testkey", body);
            return StubHandler.Json(new { access_token = "access-private-value", refresh_token = "refresh-private-value", expires_in = 14400, scope = "files.content.read" });
        });
        using var http = new HttpClient(handler);
        var redactor = new SecretRedactor();
        var credentials = new MemoryCredentials();
        var provider = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, credentials, redactor);
        Assert.Equal(["files.content.read"], await provider.ExchangeCodeAsync("code-value", "verifier-value", "http://127.0.0.1:53134/callback", default));
        Assert.Equal("refresh-private-value", credentials.Value);
        Assert.Equal("[REDACTED] [REDACTED] [REDACTED]", redactor.Redact("access-private-value refresh-private-value verifier-value"));
        var failing = new TokenProvider(http, new AppSettings { AppKey = "testkey" }, new MemoryCredentials { FailWrite = true }, redactor);
        await Assert.ThrowsAsync<UserError>(() => failing.ExchangeCodeAsync("code-value", "verifier-value", "http://127.0.0.1:53134/callback", default));
    }

    [Fact]
    public void ErrorsNeverEchoRawResponseOrToken()
    {
        var redactor = new SecretRedactor();
        redactor.Register("opaque-private-refresh");
        const string raw = "{\"error_summary\":\"opaque-private-refresh\",\"error_description\":\"private@example.invalid\"}";
        string message = redactor.FormatError(DropboxApiException.Parse(400, raw, redactor));
        Assert.DoesNotContain("opaque-private-refresh", message);
        Assert.DoesNotContain("private@example.invalid", message);
        Assert.Contains("unclassified_error", message);
    }

    [Fact]
    public void RedactorHidesKeyedValuesAndBearerTokens()
    {
        var redactor = new SecretRedactor();
        string safe = redactor.Redact("{\"access_token\":\"opaque-access\",\"refresh_token\":\"opaque-refresh\"} code=private-code code_verifier=private-verifier Bearer bearer-value sl.some-token-value");
        foreach (string secret in new[] { "opaque-access", "opaque-refresh", "private-code", "private-verifier", "bearer-value", "sl.some-token-value" })
            Assert.DoesNotContain(secret, safe);
    }

    [Fact]
    public async Task SessionsUseBoundedChunksAndEmptyFinishWithFinalOffset()
    {
        using var directory = new TempDirectory();
        string file = directory.File("large.bin");
        await File.WriteAllBytesAsync(file, new byte[DropboxClient.SessionChunkSize + 3]);
        var chunks = new List<int>();
        long currentOffset = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            string endpoint = request.RequestUri!.AbsolutePath;
            using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            if (endpoint.EndsWith("/start", StringComparison.Ordinal))
            {
                Assert.Empty(bytes);
                return StubHandler.Json(new { session_id = "session-private-value" });
            }
            Assert.Equal(currentOffset, argument.RootElement.GetProperty("cursor").GetProperty("offset").GetInt64());
            if (endpoint.EndsWith("/append_v2", StringComparison.Ordinal))
            {
                chunks.Add(bytes.Length);
                currentOffset += bytes.Length;
                return StubHandler.Json((object?)null!);
            }
            Assert.Empty(bytes);
            Assert.Equal("add", argument.RootElement.GetProperty("commit").GetProperty("mode").GetString());
            return StubHandler.Json(new { id = "id:file", name = "large.bin", path_lower = "/large.bin", rev = "rev", size = currentOffset, content_hash = "hash" });
        });
        using var http = new HttpClient(handler);
        await new DropboxClient(http, new FakeTokens(), new SecretRedactor()).UploadSessionAsync("/large.bin", file, default);
        Assert.Equal([DropboxClient.SessionChunkSize, 3], chunks);
    }
}
