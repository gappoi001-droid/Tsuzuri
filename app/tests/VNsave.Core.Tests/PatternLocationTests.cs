using System.Text.Json;

namespace VNsave.Core.Tests;

public sealed class PatternLocationTests
{
    private const string Pattern = "edicius_*.sav";

    [Theory]
    [InlineData("edicius_*.sav", true)]
    [InlineData("*.sav", true)]
    [InlineData("save??.dat", true)]
    [InlineData("*", false)]
    [InlineData("*.*", false)]
    [InlineData("*.exe", false)]
    [InlineData("*.DLL", false)]
    [InlineData("*.asar", false)]
    [InlineData("*.s?v", false)]
    [InlineData("data", false)]
    [InlineData("sub/*.sav", false)]
    [InlineData("..\\*.sav", false)]
    [InlineData("", false)]
    public void PatternsThatCouldSelectGameFilesAreRejected(string pattern, bool valid)
    {
        if (valid) SaveFiles.ValidatePattern(pattern);
        else Assert.Throws<UserError>(() => SaveFiles.ValidatePattern(pattern));
    }

    [Fact]
    public void MatchingIsTopLevelOnly()
    {
        using var directory = new TempDirectory();
        string game = GameFolder(directory, "pc1", "flags", "slots");
        Assert.Equal(["edicius_sf.sav", "edicius_tyrano_data.sav"], SaveFiles.Matching(game, Pattern).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SnapshotHoldsOnlyTheMatchingSaves()
    {
        using var directory = new TempDirectory();
        var game = Game(directory, GameFolder(directory, "pc1", "flags", "slots"), Guid.NewGuid().ToString("N"));
        var state = await SaveArchive.ScanAsync(game.Locations);
        Assert.Equal(["save1/edicius_sf.sav", "save1/edicius_tyrano_data.sav"], state.Files.Select(f => f.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyReplacesOnlyMatchingFilesAndNeverTouchesTheGame(bool fail)
    {
        using var directory = new TempDirectory();
        using var handler = new FakeDropboxHandler(); using var http = new HttpClient(handler);
        string id = Guid.NewGuid().ToString("N");
        var source = Game(directory, GameFolder(directory, "pc1", "flags v2", "slots v2"), id);
        var sourceRepository = new LocalRepository(directory.File("app1")); sourceRepository.Library.Games.Add(source);
        var pending = (await new SnapshotStore(new(http, new FakeTokens(), new()), sourceRepository, "account").CreateAsync(source))!;
        var manifest = await JsonFormat.ReadAsync<SaveManifest>(Path.Combine(pending.Directory, "manifest.json"));

        string target = GameFolder(directory, "pc2", "flags v1", null);
        File.WriteAllText(Path.Combine(target, "edicius_old.sav"), "stale save");
        var game = Game(directory, target, id);
        var repository = new LocalRepository(directory.File("app2")); repository.Library.Games.Add(game);
        var before = GameFiles(target);
        var original = await SaveArchive.ScanAsync(game.Locations);

        var applier = new SaveApplier(repository, _ => false, (_, _) => Task.CompletedTask, _ => { if (fail) throw new IOException("injected failure"); });
        Task Apply() => applier.ApplyAsync(game, pending.Publication.Complete, Path.Combine(pending.Directory, "save.zip"), manifest, () => Task.CompletedTask);
        if (fail) await Assert.ThrowsAsync<UserError>(Apply); else await Apply();

        Assert.Equal(before, GameFiles(target)); // Executable, archives, other files and subfolders are byte-for-byte unchanged.
        if (fail)
            Assert.Equal(original.ContentHash, (await SaveArchive.ScanAsync(game.Locations)).ContentHash);
        else
        {
            Assert.Equal("flags v2", File.ReadAllText(Path.Combine(target, "edicius_sf.sav")));
            Assert.Equal("slots v2", File.ReadAllText(Path.Combine(target, "edicius_tyrano_data.sav")));
            Assert.False(File.Exists(Path.Combine(target, "edicius_old.sav"))); // A matching save the generation does not have is removed.
        }
    }

    [Fact]
    public void ValidatorAcceptsAPatternInTheGameFolderButNotTheWholeFolder()
    {
        using var directory = new TempDirectory();
        string game = GameFolder(directory, "pc1", "flags", "slots");
        SaveLocationValidator.Validate([new("save1", game, Pattern)], []);
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate([new("save1", game)], []));
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate([new("save1", game, "*.exe")], []));
    }

    [Fact]
    public void InstallFolderPatternResolvesOnAnotherPc()
    {
        using var directory = new TempDirectory(); var known = Fixtures.Known(directory);
        string install = GameFolder(directory, "pc1", "flags", "slots");
        var definition = known.Describe("save1", install, install, Pattern);
        Assert.Equal(("Install", "", Pattern), (definition.Base, definition.RelativePath, definition.Pattern));
        string other = directory.File("pc2-install");
        Assert.Equal(Path.GetFullPath(other), known.Resolve(definition, other));
        Assert.Null(known.Describe("save1", directory.File("pc1-saves"), install).Pattern); // Folder locations stay as before.
    }

    [Fact]
    public void FolderLocationsAreWrittenAsBeforeAndUnknownFieldsAreIgnored()
    {
        string folder = JsonSerializer.Serialize(new SaveLocation("save1", "C:\\saves"), JsonFormat.Options);
        Assert.DoesNotContain("pattern", folder);
        var restored = JsonSerializer.Deserialize<LocationDefinition>(
            JsonSerializer.Serialize(new LocationDefinition("save1", "Install", "", Pattern), JsonFormat.Options), JsonFormat.Options)!;
        Assert.Equal(Pattern, restored.Pattern);
        var future = JsonSerializer.Deserialize<LocationDefinition>("{\"id\":\"save1\",\"base\":\"Documents\",\"relative_path\":\"a\",\"added_later\":1}", JsonFormat.Options)!;
        Assert.Equal(("Documents", "a", (string?)null), (future.Base, future.RelativePath, future.Pattern));
    }

    [Fact]
    public void TyranoFileModeSuggestsTheProjectPatternAndTitle()
    {
        using var directory = new TempDirectory(); var known = Fixtures.Known(directory);
        string game = GameFolder(directory, "pc1", "flags", "slots");
        WriteConfig(game, ";;セーブデータの保存方法", ";configSave=file", ";System.title=望却のエディシウス", ";projectID=edicius");
        var result = new GameDetector(known).Detect(Path.Combine(game, "edicius.exe"));
        Assert.Equal("TyranoScript", result.Engine);
        Assert.Equal("望却のエディシウス", result.Identity.DisplayName);
        var candidate = Assert.Single(result.Candidates, c => c.Pattern is not null);
        Assert.Equal((Path.GetFullPath(game), Pattern, 2), (candidate.Path, candidate.Pattern, candidate.FileCount));
        Assert.EndsWith("edicius_*.sav", candidate.DisplayPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TyranoBrowserStorageIsReportedAndNotSuggested()
    {
        using var directory = new TempDirectory(); var known = Fixtures.Known(directory);
        string game = GameFolder(directory, "pc1", null, null);
        WriteConfig(game, ";configSave=webstorage", ";projectID=edicius");
        var result = new GameDetector(known).Detect(Path.Combine(game, "edicius.exe"));
        Assert.Contains("未対応", result.Engine, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Candidates, c => c.Pattern is not null);
    }

    [Fact]
    public void SavesNextToAnElectronExecutableAreSuggestedWithoutConfig()
    {
        using var directory = new TempDirectory(); var known = Fixtures.Known(directory);
        string game = GameFolder(directory, "pc1", "flags", null);
        var result = new GameDetector(known).Detect(Path.Combine(game, "edicius.exe"));
        Assert.Equal("*.sav", Assert.Single(result.Candidates, c => c.Pattern is not null).Pattern);
    }

    [Fact]
    public void ConfigParsingIgnoresCommentsAndOddProjectIds()
    {
        Assert.Equal(new TyranoConfig(true, null, null), Tyrano.Parse([";;configSave=webstorage", "; configSave = file ", ";projectID=名前"]));
        Assert.False(Tyrano.Parse([";projectID=abc"]).SavesToFile); // TyranoScript's default is webstorage.
    }

    // An Electron TyranoScript game folder like D:\望却のエディシウス, with game files around the saves.
    private static string GameFolder(TempDirectory directory, string name, string? flags, string? slots)
    {
        string game = directory.File(name); Directory.CreateDirectory(Path.Combine(game, "resources", "app_unpacked", "data"));
        Directory.CreateDirectory(Path.Combine(game, "sub"));
        File.WriteAllText(Path.Combine(game, "edicius.exe"), "game executable " + name);
        File.WriteAllText(Path.Combine(game, "resources", "app.asar"), "game archive " + name);
        File.WriteAllText(Path.Combine(game, "readme.txt"), "readme " + name);
        File.WriteAllText(Path.Combine(game, "sub", "edicius_nested.sav"), "nested, not a save location " + name);
        if (flags is not null) File.WriteAllText(Path.Combine(game, "edicius_sf.sav"), flags);
        if (slots is not null) File.WriteAllText(Path.Combine(game, "edicius_tyrano_data.sav"), slots);
        return game;
    }

    private static void WriteConfig(string game, params string[] lines)
    {
        string system = Path.Combine(game, "resources", "app_unpacked", "data", "system"); Directory.CreateDirectory(system);
        File.WriteAllLines(Path.Combine(system, "Config.tjs"), lines);
    }

    private static LocalGame Game(TempDirectory directory, string install, string id) => new()
    {
        Definition = new(id, "望却のエディシウス", "edicius.exe", "", "TyranoScript", [new("save1", "Install", "", Pattern)]),
        ExecutablePath = Path.Combine(install, "edicius.exe"), WorkingDirectory = install, Locations = [new("save1", install, Pattern)]
    };

    // Every file in the game folder that is not a matching top-level save, with its content.
    private static Dictionary<string, string> GameFiles(string game) => Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
        .Where(path => !(Path.GetDirectoryName(path) == game && SaveFiles.Matches(Pattern, Path.GetFileName(path))))
        .ToDictionary(path => Path.GetRelativePath(game, path), File.ReadAllText);
}
