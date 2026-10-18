using System.Text.Json;

namespace VNsave.Core.Tests;

internal sealed class FakeGameProcess : IGameProcess
{
    public bool Running { get; set; }
    public bool IsRunning(string executable) => Running;
    public Task RunAsync(LocalGame game, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SyncWorkflowTests
{
    [Fact]
    public async Task InterruptedPublicationRetriesTheSameFrozenBytesAndWritesCompleteLast()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler { FailUploadNumber = 2 }; using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        string source = Path.Combine(game.Locations[0].Path, "slot.sav"); await File.WriteAllTextAsync(source, "frozen");
        var client = new DropboxClient(http, new FakeTokens(), new()); var publisher = new SnapshotStore(client, repository, "account");
        var pending = (await publisher.CreateAsync(game))!;
        await Assert.ThrowsAsync<DropboxApiException>(() => publisher.PublishAsync(game, pending));
        Assert.True(publisher.HasPending(game.Definition.GameId));
        Assert.DoesNotContain(handler.Files.Keys, p => p.EndsWith("/complete.json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(source, "changed after failure");
        var retry = Assert.Single(await publisher.FindAsync(game.Definition.GameId));
        Assert.Equal(pending.Publication.Complete, retry.Publication.Complete);
        await publisher.PublishAsync(game, retry);
        Assert.False(publisher.HasPending(game.Definition.GameId));
        Assert.Equal(pending.Publication.Complete.ContentHash, game.LastContentHash);
        Assert.EndsWith("/complete.json", handler.Files.Keys.Last());
        Assert.All(handler.Files.Keys, p => Assert.StartsWith("/vnsave/games/", p));
        Assert.DoesNotContain(handler.Endpoints, e => e == "files/delete_v2");
    }
    [Fact]
    public async Task ChangedPendingBytesAndDifferentAccountsAreNeverPublished()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory);
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "slot.sav"), "save");
        var client = new DropboxClient(http, new FakeTokens(), new()); var publisher = new SnapshotStore(client, repository, "account-a");
        var pending = (await publisher.CreateAsync(game))!;
        await Assert.ThrowsAsync<UserError>(() => new SnapshotStore(client, repository, "account-b").FindAsync(game.Definition.GameId));
        await File.WriteAllTextAsync(Path.Combine(pending.Directory, "save.zip"), "damaged");
        await Assert.ThrowsAsync<UserError>(() => publisher.FindAsync(game.Definition.GameId));
        Assert.Equal(0, handler.Uploads); Assert.True(Directory.Exists(pending.Directory));
    }
    [Fact]
    public async Task TwoPcsExchangeSavesWithoutDependingOnTheirInstallationPaths()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var redactor = new SecretRedactor(); var client = new DropboxClient(http, new FakeTokens(), redactor);
        var first = Fixtures.Game(directory, "first"); var second = Fixtures.Game(directory, "second", first.Definition.GameId);
        var repo1 = new LocalRepository(directory.File("pc1")); repo1.Library.Games.Add(first);
        var repo2 = new LocalRepository(directory.File("pc2")); repo2.Library.Games.Add(second);
        var catalog1 = new CloudCatalog(client); var catalog2 = new CloudCatalog(client);
        var snapshots1 = new SnapshotStore(client, repo1, "same-account"); var snapshots2 = new SnapshotStore(client, repo2, "same-account");
        var service1 = new SyncService(client, repo1, catalog1, snapshots1, new FakeGameProcess(), new(repo1.Root, redactor));
        var service2 = new SyncService(client, repo2, catalog2, snapshots2, new FakeGameProcess(), new(repo2.Root, redactor));
        await catalog1.RegisterAsync(first.Definition);
        await File.WriteAllTextAsync(Path.Combine(first.Locations[0].Path, "slot.sav"), "first save");
        await service1.BeforeLaunchAsync(first);
        await service2.BeforeLaunchAsync(second);
        Assert.Equal("first save", await File.ReadAllTextAsync(Path.Combine(second.Locations[0].Path, "slot.sav")));
        Assert.Equal(first.LastSnapshotId, second.LastSnapshotId);
        await File.WriteAllTextAsync(Path.Combine(first.Locations[0].Path, "slot.sav"), "second save");
        await service1.BeforeLaunchAsync(first);
        await service2.BeforeLaunchAsync(second);
        Assert.Equal("second save", await File.ReadAllTextAsync(Path.Combine(second.Locations[0].Path, "slot.sav")));
        int count = handler.Count;
        await service2.UploadAsync(second);
        Assert.Equal(count, handler.Count); // An identical manual upload adds no generation.
        int SnapshotFiles() => handler.Files.Keys.Count(k => k.Contains("/snapshots/", StringComparison.OrdinalIgnoreCase));
        int snapshotFiles = SnapshotFiles();
        await File.WriteAllTextAsync(Path.Combine(second.Locations[0].Path, "local-only.sav"), "discard me");
        await snapshots2.CreateAsync(second);
        await service2.DownloadAsync(second);
        Assert.False(snapshots2.HasPending(second.Definition.GameId));
        Assert.False(File.Exists(Path.Combine(second.Locations[0].Path, "local-only.sav")));
        string backups = LocalRepository.RemoteGame(second.Definition.GameId) + "/backups/";
        Assert.Contains(handler.Files.Keys, k => k.StartsWith(backups, StringComparison.OrdinalIgnoreCase) && k.EndsWith("-pending.zip", StringComparison.Ordinal));
        Assert.Contains(handler.Files.Keys, k => k.StartsWith(backups, StringComparison.OrdinalIgnoreCase) && !k.EndsWith("-pending.zip", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(repo2.Root, "backups")));
        Assert.Equal(snapshotFiles, SnapshotFiles()); // Manual download never deletes or adds generations.
        Assert.Equal(second.LastSnapshotId, catalog2.Latest[second.Definition.GameId].SnapshotId); // Backups are never adopted.
    }
    [Fact]
    public async Task BeforeLaunchSendsPendingBeforeReadingTheCloud()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "slot.sav"), "saved");
        var client = new DropboxClient(http, new FakeTokens(), new()); var snapshots = new SnapshotStore(client, repository, "account");
        await snapshots.CreateAsync(game); var catalog = new CloudCatalog(client);
        await catalog.RegisterAsync(game.Definition); handler.Endpoints.Clear();
        await new SyncService(client, repository, catalog, snapshots, new FakeGameProcess(), new(repository.Root, new())).BeforeLaunchAsync(game);
        Assert.Equal("files/upload", handler.Endpoints[0]);
        Assert.False(snapshots.HasPending(game.Definition.GameId));
    }
    [Fact]
    public async Task OfflinePostGamePublicationIsFrozenAndRemainsPending()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory);
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "slot.sav"), "unsent");
        var client = new DropboxClient(http, new FakeTokens(), new()); var snapshots = new SnapshotStore(client, repository, "account");
        var service = new SyncService(client, repository, new(client), snapshots, new FakeGameProcess(), new(repository.Root, new()),
            _ => throw new HttpRequestException("offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.AfterExitAsync(game));
        Assert.True(snapshots.HasPending(game.Definition.GameId)); Assert.Equal(0, handler.Uploads);
    }
}

public sealed class ListingTests
{
    [Fact]
    public async Task PaginationUsesAppRootAndDoesNotRequestDeletedEntries()
    {
        using var handler = new StubHandler(async (request, ct) =>
        {
            using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("", args.RootElement.GetProperty("path").GetString());
            Assert.False(args.RootElement.TryGetProperty("include_deleted", out _));
            Assert.True(args.RootElement.GetProperty("recursive").GetBoolean());
            return StubHandler.Json(new { entries = Array.Empty<object>(), cursor = "cursor", has_more = false });
        });
        using var http = new HttpClient(handler);
        await new DropboxClient(http, new FakeTokens(), new()).ListFolderAsync("/", 2, default);
        Assert.Equal("VNsave/dropbox", WindowsCredentialStore.TargetName);
    }
    [Fact]
    public async Task MultiplePagesAndResetRebuildTheIndexAndMarkerCache()
    {
        using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new()); var index = new CloudIndex();
        var reader = new FolderListingReader(client, index, "/vnsave", 2);
        var first = Fixtures.Complete(); string root = LocalRepository.RemoteGame(first.GameId) + "/snapshots/" + first.SnapshotId;
        await client.UploadBytesAsync(root + "/complete.json", JsonSerializer.SerializeToUtf8Bytes(first, JsonFormat.Options), default);
        await client.UploadBytesAsync(root + "/save.zip", [0], default);
        var initial = await reader.ReadAllAsync(default); Assert.True(initial.PageCount > 1);
        await index.ReadCompletionsAsync(client, "/vnsave", default); Assert.Equal(1, index.CompletionFetchCount);
        var second = Fixtures.Complete(gameId: first.GameId, time: first.SavedAtUtc.AddMinutes(1));
        string path = LocalRepository.RemoteGame(second.GameId) + "/snapshots/" + second.SnapshotId + "/complete.json";
        await client.UploadBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(second, JsonFormat.Options), default);
        await reader.ContinueAsync(default); await index.ReadCompletionsAsync(client, "/vnsave", default);
        Assert.Equal(2, index.CompletionFetchCount);
        handler.Files.Remove(root + "/save.zip"); handler.ResetsRemaining = 1;
        var rebuilt = await reader.ContinueAsync(default); Assert.True(rebuilt.Rebuilt);
        Assert.False(index.Entries.ContainsKey(root + "/save.zip"));
        await index.ReadCompletionsAsync(client, "/vnsave", default); Assert.Equal(4, index.CompletionFetchCount);
        index.Apply(new CloudEntry("deleted", root.ToUpperInvariant()));
        Assert.DoesNotContain(index.Entries.Keys, p => CloudIndex.AtOrBelow(p, root));
    }
    [Fact]
    public async Task RepeatedCursorIsRejected()
    {
        using var handler = new FakeDropboxHandler { RepeatCursorOnContinue = true }; using var http = new HttpClient(handler);
        var client = new DropboxClient(http, new FakeTokens(), new());
        await client.UploadBytesAsync("/vnsave/a", [0], default); await client.UploadBytesAsync("/vnsave/b", [0], default);
        await Assert.ThrowsAsync<UserError>(() => new FolderListingReader(client, new(), "/vnsave", 1).ReadAllAsync(default));
    }
    [Fact]
    public void FilePathsIgnoreCaseAndLatestUsesTimeThenOrdinalId()
    {
        var index = new CloudIndex(); index.Apply(new CloudEntry("file", "/SAVE/FILE", 1, "hash", "rev"));
        index.Apply(new CloudEntry("file", "/save/file", 2, "next", "rev2"));
        Assert.Single(index.Entries.Values, e => e.Tag == "file"); Assert.Equal(2, index.Entries["/SAVE/FILE"].Size);
        var first = Fixtures.Complete(id: "id-a"); var second = first with { SnapshotId = "id-z" };
        var evacuated = first with { SnapshotId = "evacuated", SavedAtUtc = first.SavedAtUtc.AddDays(1) };
        CompletedSnapshot[] candidates = [new("/a", first), new("/b", second), new("/c", evacuated, "evacuated")];
        Assert.Equal("id-z", CloudIndex.Latest(candidates.Reverse())[first.GameId].SnapshotId);
    }
}
