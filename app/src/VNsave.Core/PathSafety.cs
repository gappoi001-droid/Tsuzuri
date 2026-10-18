namespace VNsave.Core;

public static class PathSafety
{
    public static string Under(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
            relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDevice(part))) throw new UserError("保存データの相対パスが不正です。");
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new UserError("保存先の外を操作するパスは使えません。");
        return result;
    }
    private static bool IsDevice(string name)
    {
        string value = name.Split('.')[0].ToUpperInvariant();
        return value is "CON" or "PRN" or "AUX" or "NUL" || value.Length == 4 &&
            (value.StartsWith("COM", StringComparison.Ordinal) || value.StartsWith("LPT", StringComparison.Ordinal)) && value[3] is >= '1' and <= '9';
    }
    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UserError("リンクやジャンクションを含む保存先は使えません。");
    }
    public static IEnumerable<string> Files(string root)
    {
        NoLinks(root);
        if (!Directory.Exists(root)) yield break;
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new UserError("保存先内のリンクは使えません。");
            if (Directory.Exists(entry)) foreach (string file in Files(entry)) yield return file;
            else yield return entry;
        }
    }
    public static bool Overlaps(string first, string second)
    {
        string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar);
        string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static void DeleteTree(string path, string allowedRoot)
    {
        string resolved = Path.GetFullPath(path);
        string boundary = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new UserError("作業領域の外は削除できません。");
        NoLinks(resolved);
        if (Directory.Exists(resolved)) { _ = Files(resolved).ToArray(); Directory.Delete(resolved, true); }
    }
}
