using System.Text.Json;

namespace VNsave.Core.Tests;

public sealed class RetentionTests
{
    private static string Id(int i) => $"20261001t{i:000000}z-{i:x8}";
    private static string BackupName(int i, string suffix = "") => $"20261002t{i:000000}z-{i:x8}{suffix}.zip";

    private static async Task SeedSnapshotAsync(DropboxClient client, string gameId, string id, DateTimeOffset time, string device, bool complete = true)
    {
        string root = LocalRepository.RemoteGame(gameId) + "/snapshots/" + id;
        await client.UploadBytesAsync(root + "/save.zip", [1], default);
        if (!complete) return;
        await client.UploadBytesAsync(root + "/manifest.json", [2], default);
        var marker = new SnapshotComplete(id, gameId, time, null, new string('b', 64), device);
        await client.UploadBytesAsync(root + "/complete.json", JsonSerializer.SerializeToUtf8Bytes(marker, JsonFormat.Options), default);
    }

    private static string[] SnapshotFolders(FakeDropboxHandler handler, string gameId)
    {
        string prefix = LocalRepository.RemoteGame(gameId) + "/snapshots/";
        return handler.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('/')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task OnlyTheSurplusIsDeletedAfterAnUpload()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var redactor = new SecretRedactor(); var client = new DropboxClient(http, new FakeTokens(), redactor);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        string gameId = game.Definition.GameId, remote = LocalRepository.RemoteGame(gameId);
        var catalog = new CloudCatalog(client); await catalog.RegisterAsync(game.Definition);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        // The other PC's only generation is the oldest; twelve newer ones come from this PC.
        await SeedSnapshotAsync(client, gameId, Id(0), start, "other-pc");
        for (int i = 1; i <= 12; i++) await SeedSnapshotAsync(client, gameId, Id(i), start.AddMinutes(i), repository.Library.DeviceId);
        string uploading = "20200101t000000z-deadbeef";
        await SeedSnapshotAsync(client, gameId, uploading, start, "other-pc", complete: false);
        for (int i = 1; i <= 12; i++) await client.UploadBytesAsync(remote + "/backups/" + BackupName(i, i == 12 ? "-pending" : ""), [3], default);
        await client.UploadBytesAsync(remote + "/backups/notes.txt", [4], default);
        string playtime = remote + "/playtime/20261001t000000z-00000000-60.json";
        await client.UploadBytesAsync(playtime, [5], default);
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "slot.sav"), "new save");
        var service = new SyncService(client, repository, catalog, new SnapshotStore(client, repository, "account"), new FakeGameProcess(), new(repository.Root, redactor));

        await service.UploadAsync(game);

        string[] expected = [Id(0), .. Enumerable.Range(4, 9).Select(Id), game.LastSnapshotId!, uploading];
        Assert.Equal(expected.Order(StringComparer.Ordinal), SnapshotFolders(handler, gameId));
        Assert.True(handler.Files.ContainsKey(remote + "/snapshots/" + uploading + "/save.zip"));
        Assert.DoesNotContain(handler.Files.Keys, k => k.Contains(BackupName(1), StringComparison.Ordinal) || k.Contains(BackupName(2), StringComparison.Ordinal));
        Assert.Equal(11, handler.Files.Keys.Count(k => k.StartsWith(remote + "/backups/", StringComparison.OrdinalIgnoreCase)));
        Assert.True(handler.Files.ContainsKey(remote + "/backups/notes.txt"));
        Assert.True(handler.Files.ContainsKey(remote + "/game.json"));
        Assert.True(handler.Files.ContainsKey(playtime));
        Assert.Equal(5, handler.Endpoints.Count(e => e == "files/delete_v2"));
        Assert.Equal(game.LastSnapshotId, catalog.Latest[gameId].SnapshotId);
    }

    [Fact]
    public void TheLatestThisPcsBaseAndEachPcsNewestGenerationAreKept()
    {
        string gameId = new('a', 32);
        var start = DateTimeOffset.UtcNow;
        var generations = Enumerable.Range(0, 14).Select(i => new CompletedSnapshot("/" + i,
            Fixtures.Complete(id: Id(i), gameId: gameId, time: start.AddMinutes(i)) with { DeviceId = i == 1 ? "other-pc" : "this-pc" })).ToList();
        generations.Add(new("/another-game", Fixtures.Complete(id: Id(99), gameId: new string('c', 32), time: start.AddDays(-1))));
        Assert.Equal(new[] { Id(3), Id(2) }, Retention.SnapshotsToDelete(gameId, generations, Id(0)));
        Assert.Empty(Retention.SnapshotsToDelete(gameId, generations.Take(Retention.KeepSnapshots), null));
    }

    [Fact]
    public async Task AFailedPruneLeavesTheSyncSuccessful()
    {
        using var directory = new TempDirectory(); using var handler = new FakeDropboxHandler { FailDeletes = true }; using var http = new HttpClient(handler);
        var redactor = new SecretRedactor(); var client = new DropboxClient(http, new FakeTokens(), redactor);
        var repository = new LocalRepository(directory.File("app")); var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        string gameId = game.Definition.GameId;
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (int i = 1; i <= 12; i++) await SeedSnapshotAsync(client, gameId, Id(i), start.AddMinutes(i), "other-pc");
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "slot.sav"), "new save");
        var snapshots = new SnapshotStore(client, repository, "account");
        var service = new SyncService(client, repository, new CloudCatalog(client), snapshots, new FakeGameProcess(), new(repository.Root, redactor));

        await service.AfterExitAsync(game);

        Assert.False(snapshots.HasPending(gameId));
        Assert.Contains(game.LastSnapshotId!, SnapshotFolders(handler, gameId));
        Assert.Equal(13, SnapshotFolders(handler, gameId).Length);
        Assert.Equal(1, handler.Endpoints.Count(e => e == "files/delete_v2"));
        Assert.Contains("古いデータの整理を次回に回します", await File.ReadAllTextAsync(Path.Combine(repository.Root, "logs", "vnsave.log")));
    }
}
