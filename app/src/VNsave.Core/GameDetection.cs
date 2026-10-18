using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VNsave.Core;

public sealed class KnownFolders
{
    public Dictionary<string, string> Paths { get; }
    public KnownFolders(Dictionary<string, string> paths) => Paths = paths;
    public static KnownFolders Windows() => new(new()
    {
        ["Documents"] = Get("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["SavedGames"] = Get("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
        ["Roaming"] = Get("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
        ["Local"] = Get("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
        ["LocalLow"] = Get("A520A1A4-1780-4FF6-BD18-167343C5AF16"),
        ["ProgramFiles"] = Get("905E63B6-C1BF-494E-B29C-65B732D3D21A"),
        ["ProgramFilesX86"] = Get("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E")
    });
    private static string Get(string value)
    {
        var id = new Guid(value);
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out var ptr));
        try { return Marshal.PtrToStringUni(ptr)!; } finally { Marshal.FreeCoTaskMem(ptr); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    public LocationDefinition Describe(string id, string path, string install, string? pattern = null)
    {
        // Saves written next to the executable are the install folder itself plus a pattern.
        if (pattern is not null && Path.GetFullPath(path).TrimEnd('\\').Equals(Path.GetFullPath(install).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return new(id, "Install", "", pattern);
        foreach (var item in new[] { new KeyValuePair<string, string>("Install", install) }.Concat(Paths.Where(kv => !kv.Key.StartsWith("ProgramFiles", StringComparison.Ordinal))))
            if (PathSafety.Overlaps(item.Value, path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(item.Value).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return new(id, item.Key, Path.GetRelativePath(item.Value, path).Replace('\\', '/'), pattern);
        return new(id, "Manual", "", pattern);
    }
    public string? Resolve(LocationDefinition location, string install)
    {
        if (location.Base == "Install" && location.RelativePath.Length == 0 && location.Pattern is not null) return Path.GetFullPath(install);
        string? root = location.Base == "Install" ? install : Paths.GetValueOrDefault(location.Base);
        return root is null || string.IsNullOrEmpty(location.RelativePath) ? null : PathSafety.Under(root, location.RelativePath);
    }
}

public sealed record GameIdentity(string ExecutablePath, string DisplayName, string ProductName, string CompanyName,
    string Arguments = "", string WorkingDirectory = "");
public sealed record SaveCandidate(string Path, string Reason, bool Exists = true, int FileCount = 0,
    DateTimeOffset? LastWriteUtc = null, bool CountCapped = false, string? Pattern = null)
{
    public const int CountLimit = 10_000;
    public bool HasFiles => FileCount > 0;
    // What the user sees: the folder, or the folder with the file pattern for saves kept next to the executable.
    public string DisplayPath => Pattern is null ? Path : System.IO.Path.Combine(Path, Pattern);
    // Shows what is actually in the folder so the user can tell a guessed, unused location from the real one.
    public static SaveCandidate Inspect(string path, string reason, string? pattern = null)
    {
        if (!Directory.Exists(path)) return new(path, reason, Exists: false, Pattern: pattern);
        int count = 0;
        var latest = DateTime.MinValue;
        bool capped = false;
        try
        {
            IEnumerable<FileInfo> files = pattern is null
                ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                : SaveFiles.Matching(path, pattern).Select(file => new FileInfo(file));
            foreach (var file in files)
            {
                if (count == CountLimit) { capped = true; break; }
                count++;
                if (file.LastWriteTimeUtc > latest) latest = file.LastWriteTimeUtc;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(path, reason, true, count, count > 0 ? new DateTimeOffset(latest, TimeSpan.Zero) : null, capped, pattern);
    }
    public string Summary => !Exists ? "未作成" : FileCount == 0 ? "空"
        : $"{FileCount}{(CountCapped ? "+" : "")} ファイル、最終更新 {LastWriteUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    public override string ToString() => DisplayPath + "（" + Summary + "）  — " + Reason;
}
public sealed record DetectionResult(GameIdentity Identity, string Engine, SaveCandidate[] Candidates);

public sealed class GameDetector(KnownFolders known)
{
    public static string ResolveShortcut(string path, out string arguments, out string workingDirectory)
    {
        arguments = ""; workingDirectory = "";
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(path);
        Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new UserError("ショートカットを開けません。");
        dynamic shell = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(Path.GetFullPath(path));
            dynamic link = shortcut;
            arguments = (string)link.Arguments;
            workingDirectory = (string)link.WorkingDirectory;
            return Path.GetFullPath((string)link.TargetPath);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
    public static void ValidateExecutable(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new UserError("実行ファイルが見つかりません。");
        if (Path.GetFullPath(path).Split(Path.DirectorySeparatorChar).Any(p => p.Equals("steamapps", StringComparison.OrdinalIgnoreCase)))
            throw new UserError("Steam 版は対象外です。");
    }
    public DetectionResult Detect(string selected)
    {
        string exe = ResolveShortcut(selected, out string arguments, out string work);
        ValidateExecutable(exe);
        var info = FileVersionInfo.GetVersionInfo(exe);
        string directory = Path.GetDirectoryName(exe)!;
        // TyranoScript records the real title in Config.tjs; the Electron executable itself is often just named after the project.
        string display = Tyrano.Find(directory)?.Title ?? GuessDisplayName(info.ProductName, exe);
        var identity = new GameIdentity(exe, display, info.ProductName ?? "", info.CompanyName ?? "", arguments,
            string.IsNullOrWhiteSpace(work) ? directory : work);
        return Detect(identity);
    }
    public DetectionResult Detect(GameIdentity identity)
    {
        ValidateExecutable(identity.ExecutablePath);
        string directory = Path.GetDirectoryName(identity.ExecutablePath)!;
        var candidates = new Dictionary<string, SaveCandidate>(StringComparer.OrdinalIgnoreCase);
        string engine = "不明";
        void Add(string path, string reason, bool required = false)
        {
            path = Path.GetFullPath(path);
            if ((required || Directory.Exists(path)) && !candidates.ContainsKey(path)) candidates[path] = SaveCandidate.Inspect(path, reason);
        }
        if (Directory.EnumerateFiles(directory, "*.xp3").Any()) { engine = "吉里吉里"; Add(Path.Combine(directory, "savedata"), "吉里吉里 .xp3", true); }
        if (File.Exists(Path.Combine(directory, "Scene.pck")) || File.Exists(Path.Combine(directory, "Gameexe.dat")))
        { engine = "SiglusEngine"; Add(Path.Combine(directory, "savedata"), "SiglusEngine", true); }
        if (Directory.Exists(Path.Combine(directory, "renpy")))
        {
            engine = "Ren'Py";
            Add(Path.Combine(directory, "game", "saves"), "Ren'Py game/saves", true);
            string renpy = Path.Combine(known.Paths["Roaming"], "RenPy");
            string? saveName = null;
            string script = Path.Combine(directory, "game", "options.rpy");
            if (File.Exists(script))
            {
                var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(script), "config\\.save_directory\\s*=\\s*[\"']([^\"']+)[\"']");
                if (match.Success && !match.Groups[1].Value.Contains('\\') && !match.Groups[1].Value.Contains('/')) saveName = match.Groups[1].Value;
            }
            if (saveName is not null) Add(PathSafety.Under(renpy, saveName), "Ren'Py config.save_directory", true);
            else if (Directory.Exists(renpy))
                foreach (string folder in Directory.EnumerateDirectories(renpy))
                    if (Matches(Path.GetFileName(folder), identity)) Add(folder, "Ren'Py 保存先の名前");
        }
        if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")))
        {
            engine = "Unity";
            string data = Path.Combine(directory, Path.GetFileNameWithoutExtension(identity.ExecutablePath) + "_Data", "app.info");
            if (File.Exists(data))
            {
                string[] lines = File.ReadAllLines(data);
                if (lines.Length >= 2) Add(PathSafety.Under(known.Paths["LocalLow"], lines[0] + "/" + lines[1]), "Unity app.info", true);
            }
        }
        void AddPattern(string pattern, string reason)
        {
            string key = Path.GetFullPath(directory) + "|" + pattern;
            if (!candidates.ContainsKey(key)) candidates[key] = SaveCandidate.Inspect(Path.GetFullPath(directory), reason, pattern);
        }
        if (Tyrano.Find(directory) is { } tyrano)
        {
            // configSave=file writes <projectID>_*.sav next to the executable; webstorage keeps saves in the browser storage, which is not supported.
            engine = tyrano.SavesToFile ? "TyranoScript" : "TyranoScript（ブラウザ保存・未対応）";
            if (tyrano.SavesToFile) AddPattern(tyrano.ProjectId is { } id ? id + "_*.sav" : "*.sav", "TyranoScript（ファイル保存）");
        }
        else if (Tyrano.LooksLikeWebRuntime(directory) && Directory.EnumerateFiles(directory, "*.sav").Any())
        {
            engine = "Electron / NW.js";
            AddPattern("*.sav", "実行ファイルと同じフォルダの .sav");
        }
        if (File.Exists(Path.Combine(directory, "www", "js", "rpg_core.js"))) { engine = "RPG ツクール MV"; Add(Path.Combine(directory, "www", "save"), "RPG ツクール MV", true); }
        if (File.Exists(Path.Combine(directory, "js", "rmmz_core.js"))) { engine = "RPG ツクール MZ"; Add(Path.Combine(directory, "save"), "RPG ツクール MZ", true); }
        foreach (string name in new[] { "save", "savedata", "SaveData", "UserData" }) Add(Path.Combine(directory, name), "インストール先の保存フォルダ名");
        foreach (var item in known.Paths.Where(kv => !kv.Key.StartsWith("ProgramFiles", StringComparison.Ordinal)))
        {
            try
            {
                if (!Directory.Exists(item.Value)) continue;
                foreach (string first in Directory.EnumerateDirectories(item.Value))
                {
                    if (Matches(Path.GetFileName(first), identity)) Add(first, item.Key + " の名前一致");
                    if ((File.GetAttributes(first) & FileAttributes.ReparsePoint) != 0) continue;
                    try { foreach (string second in Directory.EnumerateDirectories(first)) if (Matches(Path.GetFileName(second), identity)) Add(second, item.Key + " の 1 段下の名前一致"); }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        if (known.Paths.Where(kv => kv.Key.StartsWith("ProgramFiles", StringComparison.Ordinal)).Any(kv => Path.GetFullPath(directory).StartsWith(kv.Value.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            string virtualPath = Path.Combine(known.Paths["Local"], "VirtualStore", directory[Path.GetPathRoot(directory)!.Length..]);
            if (Directory.Exists(virtualPath))
            {
                Add(virtualPath, "VirtualStore の対応フォルダ");
                foreach (string name in new[] { "save", "savedata", "SaveData", "UserData" }) Add(Path.Combine(virtualPath, name), "VirtualStore の対応先");
            }
        }
        return new(identity, engine, candidates.Values.ToArray());
    }
    // Engine executables often carry the engine's name ("Siglus", "TVP(KIRIKIRI)", "nw.js") instead of the title,
    // so fall back to the executable name and then the install folders, skipping engine and generic names.
    private static readonly string[] EngineNames = ["siglus", "kirikiri", "krkr", "吉里吉里", "tvp(", "nscripter", "onscripter",
        "catsystem", "ethornell", "yu-ris", "livemaker", "ren'py", "renpy", "nw.js", "nwjs", "electron", "tyrano",
        "rpg maker", "rpgmaker", "ツクール"];
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
        { "game", "games", "nw", "bin", "x64", "x86", "win64", "win32", "app", "data", "release", "program", "launcher" };
    public static bool IsEngineName(string name) => EngineNames.Any(engine => name.Contains(engine, StringComparison.OrdinalIgnoreCase));
    // Engines such as SiglusEngine ship a generic executable and keep the title's icon as a separate .ico next to it.
    public static string? FindIconFile(string executablePath, string displayName)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (!Directory.Exists(directory)) return null;
        var icons = Directory.EnumerateFiles(directory, "*.ico")
            .Where(path => !Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).StartsWith("setup", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] names = [displayName, Path.GetFileName(directory), Path.GetFileNameWithoutExtension(executablePath)];
        string? named = icons.FirstOrDefault(path => names.Any(name => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (named is not null) return named;
        return icons.Length == 1 && IsEngineName(Path.GetFileNameWithoutExtension(executablePath)) ? icons[0] : null;
    }
    public static string GuessDisplayName(string? productName, string executablePath)
    {
        static bool Usable(string? name) => !string.IsNullOrWhiteSpace(name) && !GenericNames.Contains(name.Trim()) && !IsEngineName(name);
        if (Usable(productName)) return productName!.Trim();
        string exeName = Path.GetFileNameWithoutExtension(executablePath);
        if (Usable(exeName)) return exeName;
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executablePath))!); folder.Parent is not null; folder = folder.Parent)
            if (Usable(folder.Name)) return folder.Name;
        return string.IsNullOrWhiteSpace(productName) ? exeName : productName.Trim();
    }
    private static bool Matches(string name, GameIdentity identity) => new[] { identity.DisplayName, identity.ProductName,
        identity.CompanyName, Path.GetFileName(Path.GetDirectoryName(identity.ExecutablePath)) }.Any(value => !string.IsNullOrWhiteSpace(value) && name.Equals(value, StringComparison.OrdinalIgnoreCase));
}

public static class SaveLocationValidator
{
    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".xp3", ".pck", ".arc", ".pak", ".vpk", ".dat2" };
    public static void Validate(SaveLocation[] selected, IEnumerable<LocalGame> registered)
    {
        if (selected.Length == 0) throw new UserError("保存先を 1 つ以上選んでください。");
        if (selected.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length) throw new UserError("保存先 ID が重複しています。");
        for (int i = 0; i < selected.Length; i++)
        {
            string path = Path.GetFullPath(selected[i].Path);
            if (Path.GetPathRoot(path)!.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new UserError("ドライブ全体は保存先にできません。");
            if (File.Exists(path)) throw new UserError("保存先にはフォルダを指定してください。");
            PathSafety.NoLinks(path);
            if (selected.Take(i).Concat(registered.SelectMany(g => g.Locations)).Any(s => PathSafety.Overlaps(path, s.Path))) throw new UserError("保存先が他の保存先と重複または入れ子になっています。");
            long total = 0;
            // A pattern location lives in the game's own folder, so only the matching files are checked, never the game files around them.
            bool whole = selected[i].Pattern is null;
            foreach (string file in SaveFiles.Enumerate(selected[i] with { Path = path }))
            {
                if (whole && Rejected.Contains(Path.GetExtension(file))) throw new UserError("実行ファイルやゲームのアーカイブを含むフォルダは登録できません。");
                total += new FileInfo(file).Length;
                if (total > SaveArchive.MaxFolderBytes) throw new UserError("保存先が 1 GB を超えています。");
            }
        }
    }
}
