using System.IO.Compression;
using System.Text.Json;

namespace VNsave.Core.Tests;

internal static class Fixtures
{
    public static LocalGame Game(TempDirectory directory, string suffix = "one", string? id = null)
    {
        string install = directory.File("install-" + suffix); Directory.CreateDirectory(install);
        string exe = Path.Combine(install, "game.exe"); File.WriteAllBytes(exe, [0]);
        string save = directory.File("save-" + suffix); Directory.CreateDirectory(save);
        return new() { Definition = new(id ?? Guid.NewGuid().ToString("N"), "Novel", "game.exe", "Novel", "不明", [new("save1", "Install", "save")]),
            ExecutablePath = exe, WorkingDirectory = install, Locations = [new("save1", save)] };
    }
    public static SnapshotComplete Complete(string? hash = null, string? id = null, string? gameId = null, DateTimeOffset? time = null) =>
        new(id ?? RunIds.New(), gameId ?? new string('a', 32), time ?? DateTimeOffset.UtcNow, null, hash ?? new string('b', 64), "device");
    public static KnownFolders Known(TempDirectory directory)
    {
        var paths = new Dictionary<string, string>();
        foreach (string name in new[] { "Documents", "SavedGames", "Roaming", "Local", "LocalLow", "ProgramFiles", "ProgramFilesX86" })
        { paths[name] = directory.File(name); Directory.CreateDirectory(paths[name]); }
        return new(paths);
    }
}

public sealed class DecisionTests
{
    [Theory]
    [InlineData(false, null, null, null, null, 0, SyncAction.None)]
    [InlineData(false, "local", null, null, null, 0, SyncAction.Upload)]
    [InlineData(true, null, null, null, "cloud", 0, SyncAction.Download)]
    [InlineData(true, "cloud", null, null, "cloud", 0, SyncAction.None)]
    [InlineData(true, "base", "previous", "base", "cloud", 10, SyncAction.Download)]
    [InlineData(true, "changed", "current", "base", "cloud", -10, SyncAction.Upload)]
    [InlineData(true, "changed", "previous", "base", "cloud", 10, SyncAction.Upload)]
    [InlineData(true, "changed", "previous", "base", "cloud", -10, SyncAction.Download)]
    [InlineData(true, "local", null, null, "cloud", 10, SyncAction.Upload)]
    [InlineData(true, "local", null, null, "cloud", 0, SyncAction.Download)]
    public void DecisionTableIsAppliedInOrder(bool hasCloud, string? local, string? baseId, string? baseHash, string? cloudHash, int seconds, SyncAction expected)
    {
        var time = DateTimeOffset.UtcNow;
        var cloud = hasCloud ? Fixtures.Complete(cloudHash!, "current", time: time) : null;
        Assert.Equal(expected, SyncDecision.Decide(cloud, local, time.AddSeconds(seconds), baseId, baseHash));
    }
}

public sealed class ArchiveTests
{
    [Fact]
    public void ContentHashIsIndependentOfEnumerationOrderAndEmptyMeansNoSave()
    {
        ManifestFile[] files = [new("save1/a", 1, new string('a', 64)), new("save2/b", 2, new string('b', 64))];
        Assert.Equal(SaveArchive.ContentHash(files), SaveArchive.ContentHash(files.Reverse()));
        Assert.Null(SaveArchive.ContentHash([]));
        Assert.NotEqual(SaveArchive.ContentHash(files), SaveArchive.ContentHash([files[0] with { Path = "save1/other" }, files[1]]));
    }
    [Fact]
    public async Task MultiLocationZipRoundTripsAndTimestampsDoNotAffectHash()
    {
        using var directory = new TempDirectory();
        var game = Fixtures.Game(directory);
        string second = directory.File("second"); Directory.CreateDirectory(second);
        SaveLocation[] locations = [.. game.Locations, new("save2", second)];
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "日本語.sav"), "first");
        await File.WriteAllTextAsync(Path.Combine(second, "other.sav"), "second");
        string stage = directory.File("stage");
        var captured = await SaveArchive.CaptureAsync(locations, stage);
        File.SetLastWriteTimeUtc(Path.Combine(second, "other.sav"), DateTime.UtcNow.AddDays(-2));
        Assert.Equal(captured.ContentHash, (await SaveArchive.ScanAsync(locations)).ContentHash);
        string zip = directory.File("save.zip"); await SaveArchive.WriteZipAsync(stage, captured.Files, zip);
        string expanded = directory.File("expanded");
        await SaveArchive.ExtractAsync(zip, new(game.Definition.GameId, RunIds.New(), captured.Files), locations, expanded);
        Assert.Equal(captured.ContentHash, (await SaveArchive.ScanAsync(locations.Select(l => new SaveLocation(l.Id, Path.Combine(expanded, l.Id))))).ContentHash);
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("save1/../../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("save1/file:stream")]
    [InlineData("save1/CON.txt")]
    public async Task MaliciousZipPathsAreRejectedBeforeWriting(string path)
    {
        using var directory = new TempDirectory();
        var game = Fixtures.Game(directory);
        string zipPath = directory.File("bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { using var stream = zip.CreateEntry(path).Open(); stream.WriteByte(1); }
        await Assert.ThrowsAsync<UserError>(() => SaveArchive.ExtractAsync(zipPath, new(game.Definition.GameId, RunIds.New(), [new(path, 1, new string('a', 64))]), game.Locations, directory.File("expanded")));
        Assert.False(File.Exists(directory.File("escape")));
    }
    [Fact]
    public async Task WrongFileHashAndExtraZipEntriesAreRejected()
    {
        using var directory = new TempDirectory();
        var game = Fixtures.Game(directory);
        string zipPath = directory.File("bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { using var stream = zip.CreateEntry("save1/a").Open(); stream.WriteByte(1); }
        await Assert.ThrowsAsync<UserError>(() => SaveArchive.ExtractAsync(zipPath, new(game.Definition.GameId, RunIds.New(), [new("save1/a", 1, new string('a', 64))]), game.Locations, directory.File("expanded")));
        await Assert.ThrowsAsync<UserError>(() => SaveArchive.ExtractAsync(zipPath, new(game.Definition.GameId, RunIds.New(), []), game.Locations, directory.File("expanded2")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyBacksUpReplacesAndRollsBackOnFailure(bool fail)
    {
        using var directory = new TempDirectory();
        using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app"));
        var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        string save = Path.Combine(game.Locations[0].Path, "current.sav");
        await File.WriteAllTextAsync(save, "new data");
        var publisher = new SnapshotStore(new(http, new FakeTokens(), new()), repository, "account");
        var pending = (await publisher.CreateAsync(game))!;
        var manifest = await JsonFormat.ReadAsync<SaveManifest>(Path.Combine(pending.Directory, "manifest.json"));
        await File.WriteAllTextAsync(save, "original data");
        await File.WriteAllTextAsync(Path.Combine(game.Locations[0].Path, "removed.sav"), "must be removed");
        var original = await SaveArchive.ScanAsync(game.Locations);
        bool committed = false;
        string? stored = null;
        var applier = new SaveApplier(repository, _ => false,
            (zip, _) => { stored = directory.File("stored-backup.zip"); File.Copy(zip, stored); return Task.CompletedTask; },
            _ => { if (fail) throw new IOException("injected failure"); });
        Task Apply() => applier.ApplyAsync(game, pending.Publication.Complete, Path.Combine(pending.Directory, "save.zip"), manifest, () => { committed = true; return Task.CompletedTask; });
        if (fail) await Assert.ThrowsAsync<UserError>(Apply); else await Apply();
        Assert.Equal(!fail, committed);
        Assert.Equal(fail ? original.ContentHash : pending.Publication.Complete.ContentHash, (await SaveArchive.ScanAsync(game.Locations)).ContentHash);
        Assert.Equal(fail, File.Exists(Path.Combine(game.Locations[0].Path, "removed.sav")));
        Assert.NotNull(stored);
        using var archive = ZipFile.OpenRead(stored!); Assert.Equal(2, archive.Entries.Count);
        Assert.False(Directory.Exists(Path.Combine(repository.Root, "backups"))); // Backups never stay in the local app folder.
    }
    [Fact]
    public async Task RunningGamePreventsAnyReplacement()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory);
        var repository = new LocalRepository(directory.File("app"));
        await Assert.ThrowsAsync<UserError>(() => new SaveApplier(repository, _ => true, (_, _) => throw new InvalidOperationException("must not back up"))
            .ApplyAsync(game, Fixtures.Complete(gameId: game.Definition.GameId), "does-not-exist", new(game.Definition.GameId, "id", []), () => Task.CompletedTask));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, "backups")));
    }
    [Fact]
    public async Task FailedBackupLeavesLocalSavesUntouched()
    {
        using var directory = new TempDirectory();
        using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        var repository = new LocalRepository(directory.File("app"));
        var game = Fixtures.Game(directory); repository.Library.Games.Add(game);
        string save = Path.Combine(game.Locations[0].Path, "current.sav");
        await File.WriteAllTextAsync(save, "cloud data");
        var pending = (await new SnapshotStore(new(http, new FakeTokens(), new()), repository, "account").CreateAsync(game))!;
        var manifest = await JsonFormat.ReadAsync<SaveManifest>(Path.Combine(pending.Directory, "manifest.json"));
        await File.WriteAllTextAsync(save, "local data");
        bool committed = false;
        var applier = new SaveApplier(repository, _ => false, (_, _) => throw new HttpRequestException("offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() => applier.ApplyAsync(game, pending.Publication.Complete,
            Path.Combine(pending.Directory, "save.zip"), manifest, () => { committed = true; return Task.CompletedTask; }));
        Assert.False(committed);
        Assert.Equal("local data", await File.ReadAllTextAsync(save));
    }
    [Fact]
    public async Task FileMetadataSettlesAfterThreeUnchangedObservations()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory); int waits = 0;
        await SaveSettler.WaitAsync(game.Locations, default, (_, _) => { waits++; return Task.CompletedTask; });
        Assert.Equal(3, waits);
    }

    [Fact]
    public async Task JobObjectLaunchAndExitWorkWithAnIsolatedWindowsCommandProcessor()
    {
        using var directory = new TempDirectory();
        var game = Fixtures.Game(directory);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), game.ExecutablePath, true);
        game.Arguments = "/d /c exit 0";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new GameProcess().RunAsync(game, timeout.Token);
        Assert.False(new GameProcess().IsRunning(game.ExecutablePath));
    }
}
