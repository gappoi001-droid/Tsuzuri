using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VNsave.Core;

public sealed record SaveState(ManifestFile[] Files, string? ContentHash, DateTimeOffset? ModifiedUtc);

public static class SaveArchive
{
    public const long MaxFolderBytes = 1_000_000_000;
    public static string? ContentHash(IEnumerable<ManifestFile> files)
    {
        var sorted = files.OrderBy(f => f.Path.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(f => new[] { f.Path.ToLowerInvariant(), f.Sha256.ToLowerInvariant() }).ToArray();
        return sorted.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sorted)));
    }
    public static async Task<SaveState> ScanAsync(IEnumerable<SaveLocation> locations, CancellationToken ct = default)
    {
        var files = new List<ManifestFile>();
        DateTimeOffset? latest = null;
        foreach (var location in locations)
        {
            _ = PathSafety.Under(Path.GetTempPath(), location.Id);
            if (location.Id.Contains('/')) throw new UserError("保存先 ID が不正です。");
            long size = 0;
            foreach (string path in SaveFiles.Enumerate(location))
            {
                ct.ThrowIfCancellationRequested();
                string relative = location.Id + "/" + Path.GetRelativePath(location.Path, path).Replace('\\', '/');
                _ = PathSafety.Under(Path.GetTempPath(), relative);
                var info = new FileInfo(path);
                size += info.Length;
                if (size > MaxFolderBytes) throw new UserError("保存先が 1 GB を超えています。");
                await using var stream = File.OpenRead(path);
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                files.Add(new(relative, info.Length, hash));
                var modified = new DateTimeOffset(info.LastWriteTimeUtc);
                if (latest is null || modified > latest) latest = modified;
            }
        }
        if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new UserError("保存先 ID またはファイル名が重複しています。");
        return new(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(), ContentHash(files), latest);
    }

    public static async Task<SaveState> CaptureAsync(SaveLocation[] locations, string stage, CancellationToken ct = default)
    {
        Directory.CreateDirectory(stage);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var before = await ScanAsync(locations, ct);
                foreach (string child in Directory.EnumerateDirectories(stage)) PathSafety.DeleteTree(child, stage);
                foreach (var item in before.Files)
                {
                    string id = item.Path.Split('/')[0];
                    string source = PathSafety.Under(locations.Single(l => l.Id == id).Path, item.Path[(id.Length + 1)..]);
                    string target = PathSafety.Under(stage, item.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = File.OpenRead(source);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await input.CopyToAsync(output, ct);
                }
                var after = await ScanAsync(locations, ct);
                var copied = await ScanAsync(locations.Select(l => new SaveLocation(l.Id, Path.Combine(stage, l.Id))), ct);
                if (before.ContentHash == after.ContentHash && before.ContentHash == copied.ContentHash) return before;
            }
            catch (IOException) when (attempt < 2) { /* A save can disappear or be replaced while copying; retry the whole set. */ }
        }
        throw new UserError("保存データが変化し続けているため固定できませんでした。");
    }

    public static async Task WriteZipAsync(string stage, ManifestFile[] files, string path, CancellationToken ct = default)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
        foreach (var file in files)
        {
            var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await using var source = File.OpenRead(PathSafety.Under(stage, file.Path));
            await source.CopyToAsync(target, ct);
        }
    }

    public static async Task ExtractAsync(string zipPath, SaveManifest manifest, SaveLocation[] locations, string target, CancellationToken ct = default)
    {
        var expected = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            _ = PathSafety.Under(target, file.Path);
            string id = file.Path.Split('/')[0];
            if (!file.Path.Contains('/') || !locations.Any(l => l.Id == id) || file.Size < 0 || file.Size > MaxFolderBytes ||
                file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c)) || !expected.TryAdd(file.Path, file))
                throw new UserError("manifest のパス・サイズ・ハッシュが不正です。");
        }
        foreach (var group in manifest.Files.GroupBy(f => f.Path.Split('/')[0]))
            if (group.Sum(f => f.Size) > MaxFolderBytes) throw new UserError("展開サイズが 1 GB を超えています。");
        using var zip = ZipFile.OpenRead(zipPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string path = PathSafety.Under(target, entry.FullName);
            if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var file) || entry.Length != file.Size ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new UserError("ZIP と manifest の内容が一致しません。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                {
                    written += count;
                    if (written > file.Size) throw new UserError("ZIP の展開サイズが不正です。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (written != file.Size) throw new UserError("ZIP が途中で終わっています。");
            }
            await using var read = File.OpenRead(path);
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UserError("セーブの SHA-256 が一致しません。");
        }
        if (!seen.SetEquals(expected.Keys)) throw new UserError("ZIP に不足したファイルがあります。");
    }
}
