namespace VNsave.Core.Tests;

public sealed class DetectionTests
{
    [Theory]
    [InlineData("data.xp3", "吉里吉里", "savedata")]
    [InlineData("Scene.pck", "SiglusEngine", "savedata")]
    [InlineData("Gameexe.dat", "SiglusEngine", "savedata")]
    [InlineData("www/js/rpg_core.js", "RPG ツクール MV", "www/save")]
    [InlineData("js/rmmz_core.js", "RPG ツクール MZ", "save")]
    public void EngineRulesSuggestExpectedPaths(string marker, string engine, string save)
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory); var known = Fixtures.Known(directory);
        string install = Path.GetDirectoryName(game.ExecutablePath)!;
        string path = Path.Combine(install, marker.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]);
        var result = new GameDetector(known).Detect(new GameIdentity(game.ExecutablePath, "Novel", "Novel", "Company"));
        Assert.Equal(engine, result.Engine);
        Assert.Contains(result.Candidates, c => c.Path.Equals(Path.Combine(install, save.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void RenpySuggestsBothLocationsAndUnityReadsAppInfo()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory); var known = Fixtures.Known(directory);
        string install = Path.GetDirectoryName(game.ExecutablePath)!;
        Directory.CreateDirectory(Path.Combine(install, "renpy")); Directory.CreateDirectory(Path.Combine(install, "game"));
        File.WriteAllText(Path.Combine(install, "game", "options.rpy"), "define config.save_directory = 'Novel-1234'");
        var detector = new GameDetector(known); var identity = new GameIdentity(game.ExecutablePath, "Novel", "Novel", "Company");
        var renpy = detector.Detect(identity);
        Assert.Equal("Ren'Py", renpy.Engine);
        Assert.Contains(renpy.Candidates, c => c.Path == Path.Combine(known.Paths["Roaming"], "RenPy", "Novel-1234"));
        Assert.Contains(renpy.Candidates, c => c.Path == Path.Combine(install, "game", "saves"));
        File.WriteAllBytes(Path.Combine(install, "UnityPlayer.dll"), [0]);
        Directory.CreateDirectory(Path.Combine(install, "game_Data")); File.WriteAllLines(Path.Combine(install, "game_Data", "app.info"), ["Company", "Novel"]);
        Assert.Contains(detector.Detect(identity).Candidates, c => c.Path == Path.Combine(known.Paths["LocalLow"], "Company", "Novel"));
    }
    [Fact]
    public void NameRulesIncludeKnownFoldersAndLocationsResolveOnSecondPc()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory); var known = Fixtures.Known(directory);
        string path = Path.Combine(known.Paths["Documents"], "Company", "Novel"); Directory.CreateDirectory(path);
        var result = new GameDetector(known).Detect(new GameIdentity(game.ExecutablePath, "Novel", "Novel", "Company"));
        Assert.Contains(result.Candidates, c => c.Path == path && c.Reason.Contains("1 段下", StringComparison.Ordinal));
        var definition = known.Describe("save1", path, game.WorkingDirectory);
        Assert.Equal("Documents", definition.Base);
        Assert.Equal(path, known.Resolve(definition, directory.File("another-install")));
        Assert.Null(known.Resolve(new("save2", "Manual", ""), game.WorkingDirectory));
    }
    [Theory]
    [InlineData("game.exe")]
    [InlineData("engine.dll")]
    [InlineData("data.xp3")]
    [InlineData("Scene.pck")]
    [InlineData("game.arc")]
    public void GameFilesCannotBeSaveLocations(string name)
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory);
        File.WriteAllBytes(Path.Combine(game.Locations[0].Path, name), [0]);
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate(game.Locations, []));
    }
    [Fact]
    public void OverlapsAndFoldersLargerThanOneGbAreRejected()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory);
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate(game.Locations, [game]));
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate([new("second", Path.Combine(game.Locations[0].Path, "nested"))], [game]));
        string huge = Path.Combine(game.Locations[0].Path, "large.sav");
        using (var file = new FileStream(huge, FileMode.CreateNew)) file.SetLength(SaveArchive.MaxFolderBytes + 1);
        Assert.Throws<UserError>(() => SaveLocationValidator.Validate(game.Locations, []));
    }
    [Theory]
    [InlineData("Siglus", "key/anemoi/SiglusEngine.exe", "anemoi")]
    [InlineData("TVP(KIRIKIRI) Z", "Brand/Novel/Novel.exe", "Novel")]
    [InlineData("nw.js", "Brand/Title/nw.exe", "Title")]
    [InlineData("", "Brand/MyNovel/game/Game.exe", "MyNovel")]
    [InlineData("Novel Title", "Brand/Folder/SiglusEngine.exe", "Novel Title")]
    public void DisplayNameSkipsEngineAndGenericNames(string product, string relative, string expected)
    {
        string exe = Path.Combine(Path.GetTempPath(), "VNsave.Name", relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expected, GameDetector.GuessDisplayName(product, exe));
    }
    [Theory]
    [InlineData("anemoi", "SiglusEngine.exe", new[] { "anemoi.ico" }, "anemoi.ico")]
    [InlineData("Novel", "SiglusEngine.exe", new[] { "title.ico" }, "title.ico")]
    [InlineData("Novel", "Novel.exe", new[] { "Novel.ico", "other.ico" }, "Novel.ico")]
    [InlineData("Novel", "Novel.exe", new[] { "title.ico" }, null)]
    [InlineData("Novel", "SiglusEngine.exe", new[] { "unins000.ico" }, null)]
    [InlineData("Novel", "SiglusEngine.exe", new[] { "a.ico", "b.ico" }, null)]
    public void SeparateIconFilesArePreferredForEngineExecutables(string folder, string exe, string[] icons, string? expected)
    {
        using var directory = new TempDirectory();
        string install = directory.File(folder); Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, exe), [0]);
        foreach (string icon in icons) File.WriteAllBytes(Path.Combine(install, icon), [0]);
        string? found = GameDetector.FindIconFile(Path.Combine(install, exe), folder);
        Assert.Equal(expected, found is null ? null : Path.GetFileName(found));
    }
    [Fact]
    public void CandidatesReportMissingEmptyAndFileDetails()
    {
        using var directory = new TempDirectory();
        Assert.False(SaveCandidate.Inspect(directory.File("missing"), "rule").Exists);
        Assert.Contains("未作成", SaveCandidate.Inspect(directory.File("missing"), "rule").ToString(), StringComparison.Ordinal);
        string empty = directory.File("empty"); Directory.CreateDirectory(empty);
        var emptyCandidate = SaveCandidate.Inspect(empty, "rule");
        Assert.True(emptyCandidate.Exists); Assert.False(emptyCandidate.HasFiles); Assert.Contains("空", emptyCandidate.ToString(), StringComparison.Ordinal);
        string saves = directory.File("saves/savedata"); Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "0000.sav"), [1]); File.WriteAllBytes(Path.Combine(saves, "read.sav"), [2]);
        var latest = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "read.sav"), latest);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "0000.sav"), latest.AddMonths(-5));
        var filled = SaveCandidate.Inspect(directory.File("saves"), "rule");
        Assert.Equal(2, filled.FileCount);
        Assert.Equal(new DateTimeOffset(latest), filled.LastWriteUtc);
        Assert.Contains("2 ファイル、最終更新", filled.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public void EngineGuessIsMarkedMissingWhileRealSavesHaveFiles()
    {
        using var directory = new TempDirectory(); var game = Fixtures.Game(directory); var known = Fixtures.Known(directory);
        string install = Path.GetDirectoryName(game.ExecutablePath)!;
        File.WriteAllBytes(Path.Combine(install, "Scene.pck"), [0]);
        string real = Path.Combine(known.Paths["Documents"], "Company", "Novel", "savedata"); Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "global.sav"), [0]);
        var result = new GameDetector(known).Detect(new GameIdentity(game.ExecutablePath, "Novel", "Novel", "Company"));
        var guess = Assert.Single(result.Candidates, c => c.Path == Path.Combine(install, "savedata"));
        Assert.False(guess.Exists);
        Assert.Contains(result.Candidates, c => c.Path == Path.GetDirectoryName(real) && c.HasFiles);
    }
    [Fact]
    public void SteamInstallationIsRejected()
    {
        using var directory = new TempDirectory(); string path = directory.File("steamapps/common/Novel/game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]);
        Assert.Throws<UserError>(() => GameDetector.ValidateExecutable(path));
    }
}
