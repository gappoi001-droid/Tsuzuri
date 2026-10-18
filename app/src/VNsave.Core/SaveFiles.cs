using System.IO.Enumeration;

namespace VNsave.Core;

// A save location is either a whole folder, or only the files directly in a folder that match a pattern.
// The pattern form exists for engines such as TyranoScript that write saves next to the game's executable,
// where every other file in the folder belongs to the game and must never be read into or replaced by a snapshot.
public static class SaveFiles
{
    private static readonly HashSet<string> GameExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".xp3", ".pck", ".arc", ".pak", ".vpk", ".dat2", ".asar", ".lnk", ".bin", ".ico" };

    public static IEnumerable<string> Enumerate(SaveLocation location) =>
        location.Pattern is null ? PathSafety.Files(location.Path) : Matching(location.Path, location.Pattern);

    // Top level only: a pattern location never descends into the game's subfolders.
    public static IEnumerable<string> Matching(string folder, string pattern)
    {
        ValidatePattern(pattern);
        PathSafety.NoLinks(folder);
        if (!Directory.Exists(folder)) yield break;
        foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!Matches(pattern, Path.GetFileName(file))) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new UserError("保存先内のリンクは使えません。");
            yield return file;
        }
    }

    public static bool Matches(string pattern, string name) => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    // A pattern must name one literal extension that is not a game file, so "*", "*.*" or "*.exe" can never select the game.
    public static void ValidatePattern(string pattern)
    {
        string extension = Path.GetExtension(pattern);
        if (pattern.Length is 0 or > 64 || pattern.IndexOfAny(['/', '\\', ':']) >= 0 ||
            pattern.Replace("*", "").Replace("?", "").IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            extension.Length < 2 || extension.IndexOfAny(['*', '?']) >= 0 || GameExtensions.Contains(extension))
            throw new UserError("保存先のファイル名のパターンが不正です（例: edicius_*.sav）。");
    }
}
