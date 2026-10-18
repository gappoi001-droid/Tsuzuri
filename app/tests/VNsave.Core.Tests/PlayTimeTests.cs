namespace VNsave.Core.Tests;

public sealed class PlayTimeTests
{
    [Fact]
    public async Task SessionsFromBothPcsAreSummedFromTheListing()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new());
        var game = Fixtures.Game(directory);
        var pc1 = new PlayTimeLog(client, new LocalRepository(directory.File("pc1")));
        var pc2 = new PlayTimeLog(client, new LocalRepository(directory.File("pc2")));
        var started = new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
        await pc1.RecordAsync(game.Definition.GameId, started, TimeSpan.FromMinutes(90));
        await pc2.RecordAsync(game.Definition.GameId, started.AddDays(1), TimeSpan.FromMinutes(30));
        await pc1.RecordAsync(game.Definition.GameId, started, TimeSpan.FromMilliseconds(300)); // Under a second is not a session.
        Assert.Single(pc1.Pending([game.Definition.GameId]));
        await pc1.FlushAsync(game.Definition.GameId); await pc2.FlushAsync(game.Definition.GameId);
        Assert.Empty(pc1.Pending([game.Definition.GameId]));
        Assert.Equal(2, handler.Files.Keys.Count(k => k.Contains("/playtime/", StringComparison.OrdinalIgnoreCase)));
        var catalog = new CloudCatalog(client);
        await catalog.RefreshAsync();
        var total = PlayTimeLog.Totals(catalog.PlayTimeFiles)[game.Definition.GameId];
        Assert.Equal(TimeSpan.FromHours(2), total.Total);
        Assert.Equal(started.AddDays(1), total.LastPlayedUtc);
        Assert.Equal("2時間0分", PlayTimeLog.Format(total.Total));
    }
    [Fact]
    public async Task PendingSessionsCountOnceAndSurviveFailedUploads()
    {
        using var directory = new TempDirectory();
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("offline")); using var http = new HttpClient(handler);
        var log = new PlayTimeLog(new DropboxClient(http, new FakeTokens(), new(), (_, _) => Task.CompletedTask), new LocalRepository(directory.File("pc")));
        var game = Fixtures.Game(directory);
        await log.RecordAsync(game.Definition.GameId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => log.FlushAsync(game.Definition.GameId));
        var pending = Assert.Single(log.Pending([game.Definition.GameId]));
        // The same session pending locally and already listed in the cloud is counted once.
        Assert.Equal(TimeSpan.FromMinutes(5), PlayTimeLog.Totals([pending, pending])[game.Definition.GameId].Total);
    }
    [Theory]
    [InlineData("20261017t010000z-1a2b3c4d-3600.json", true)]
    [InlineData("20261017t010000z-1a2b3c4d-3600.json.tmp", false)]
    [InlineData("20261017t010000z-1a2b3c4d--5.json", false)]
    [InlineData("notes.json", false)]
    public void OnlySessionFileNamesAreParsed(string name, bool valid) => Assert.Equal(valid, PlayTimeLog.TryParse(name, out _, out _));
    [Theory]
    [InlineData(30, "1分未満")]
    [InlineData(59 * 60, "59分")]
    [InlineData(12 * 3600 + 34 * 60, "12時間34分")]
    public void DurationsAreShownInHoursAndMinutes(int seconds, string text) => Assert.Equal(text, PlayTimeLog.Format(TimeSpan.FromSeconds(seconds)));
}
