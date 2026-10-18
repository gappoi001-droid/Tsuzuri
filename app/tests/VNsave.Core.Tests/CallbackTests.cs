using System.Net;
using System.Net.Sockets;

namespace VNsave.Core.Tests;

public sealed class CallbackTests
{
    [Fact]
    public void WrongStateIsRejected()
    {
        var decision = CallbackProcessor.Process("GET", "/callback?state=wrong&code=private-code", "expected");
        Assert.Equal(400, decision.StatusCode);
        Assert.False(decision.Complete);
        Assert.Null(decision.Code);
    }

    [Fact]
    public void AccessDeniedWithCorrectStateEndsAuthorization()
    {
        var decision = CallbackProcessor.Process("GET", "/callback?state=expected&error=access_denied", "expected");
        Assert.True(decision.Complete);
        Assert.True(decision.Denied);
        Assert.Null(decision.Code);
    }

    [Fact]
    public void DuplicateStateIsRejected() => Assert.Equal(400,
        CallbackProcessor.Process("GET", "/callback?state=expected&state=wrong&code=secret", "expected").StatusCode);

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/other?state=expected&code=secret")]
    public void UnrelatedPathsReturn404(string path) => Assert.Equal(404, CallbackProcessor.Process("GET", path, "expected").StatusCode);

    [Fact]
    public async Task ReceiverBindsBeforeCallbackAndKeepsListeningAfterWrongState()
    {
        using var receiver = OAuthLoopback.Start("127.0.0.1", [0]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<string> receive = receiver.WaitForCodeAsync("expected", cancellation.Token);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var bad = await http.GetAsync(receiver.RedirectUri + "?state=wrong&code=not-the-code", cancellation.Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var unrelated = await http.GetAsync(receiver.RedirectUri.Replace("/callback", "/favicon.ico", StringComparison.Ordinal), cancellation.Token);
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        using var good = await http.GetAsync(receiver.RedirectUri + "?state=expected&code=private-code", cancellation.Token);
        Assert.Equal("private-code", await receive);
        Assert.DoesNotContain("private-code", await good.Content.ReadAsStringAsync(cancellation.Token));
    }

    [Fact]
    public void BusyPortFallsBackToNextCandidate()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.ExclusiveAddressUse = true;
        occupied.Start();
        try
        {
            int busy = ((IPEndPoint)occupied.LocalEndpoint).Port;
            using var receiver = OAuthLoopback.Start("127.0.0.1", [busy, 0]);
            Assert.NotEqual(busy, receiver.Port);
        }
        finally { occupied.Stop(); }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task LocalhostAcceptsBothLoopbackFamilies(string host)
    {
        using var receiver = OAuthLoopback.Start("localhost", [0]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<string> receive = receiver.WaitForCodeAsync("expected", cancellation.Token);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var result = await http.GetAsync($"http://{host}:{receiver.Port}/callback?state=expected&code=code-value", cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("code-value", await receive);
    }

    [Fact]
    public async Task CancellationStopsReceiver()
    {
        using var receiver = OAuthLoopback.Start("127.0.0.1", [0]);
        using var cancellation = new CancellationTokenSource();
        Task<string> receive = receiver.WaitForCodeAsync("expected", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
    }
}
