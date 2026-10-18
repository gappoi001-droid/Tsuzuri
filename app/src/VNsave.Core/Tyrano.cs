using System.Text.RegularExpressions;

namespace VNsave.Core;

public sealed record TyranoConfig(bool SavesToFile, string? ProjectId, string? Title);

// TyranoScript keeps its settings in data/system/Config.tjs as ";key=value" lines. With ";configSave=file" the
// Electron / NW.js builds write "<projectID>_tyrano_data.sav" (slots) and "<projectID>_sf.sav" (system flags) next to the executable.
public static partial class Tyrano
{
    private static readonly string[] ConfigPaths =
        ["resources/app_unpacked/data/system/Config.tjs", "resources/app/data/system/Config.tjs", "data/system/Config.tjs", "package.nw/data/system/Config.tjs"];

    public static TyranoConfig? Find(string installDirectory)
    {
        foreach (string relative in ConfigPaths)
        {
            string path = Path.Combine(installDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return Parse(File.ReadAllLines(path));
        }
        return null;
    }

    public static TyranoConfig Parse(IEnumerable<string> lines)
    {
        string? save = null, project = null, title = null;
        foreach (string line in lines)
        {
            var match = Setting().Match(line);
            if (!match.Success) continue;
            string key = match.Groups[1].Value, value = match.Groups[2].Value;
            if (key.Equals("configSave", StringComparison.OrdinalIgnoreCase)) save = value;
            else if (key.Equals("projectID", StringComparison.OrdinalIgnoreCase)) project = value;
            else if (key.Equals("System.title", StringComparison.OrdinalIgnoreCase)) title = value;
        }
        // The default is webstorage; only an explicit "file" writes .sav files. An unusual project ID falls back to "*.sav".
        return new(string.Equals(save, "file", StringComparison.OrdinalIgnoreCase),
            project is not null && ProjectId().IsMatch(project) ? project : null, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    public static bool LooksLikeWebRuntime(string directory) => File.Exists(Path.Combine(directory, "resources", "app.asar")) ||
        File.Exists(Path.Combine(directory, "package.nw")) || File.Exists(Path.Combine(directory, "nw.dll"));

    [GeneratedRegex(@"^\s*;\s*([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*?)\s*$")]
    private static partial Regex Setting();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ProjectId();
}
