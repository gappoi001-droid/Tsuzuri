using System.Text.Json;

namespace VNsave.Core;

public sealed record CloudEntry(string Tag, string PathLower, long? Size = null, string? ContentHash = null,
    string? Rev = null, string? Name = null)
{
    public static CloudEntry Parse(JsonElement json)
    {
        string tag = json.GetProperty(".tag").GetString()!;
        if (tag is not ("file" or "folder" or "deleted")) throw new UserError("一覧に未対応の種類の項目があります。");
        string path = json.GetProperty("path_lower").GetString() ?? throw new UserError("一覧の path_lower がありません。");
        return new(tag, path.ToLowerInvariant(), tag == "file" ? json.GetProperty("size").GetInt64() : null,
            tag == "file" ? json.GetProperty("content_hash").GetString() : null,
            tag == "file" ? json.GetProperty("rev").GetString() : null,
            json.TryGetProperty("name", out var name) ? name.GetString() : null);
    }
}

public sealed record CompletedSnapshot(string PathLower, SnapshotComplete Complete, string Reason = "normal")
{
    public string GameId => Complete.GameId;
    public string SnapshotId => Complete.SnapshotId;
    public DateTimeOffset SavedAtUtc => Complete.SavedAtUtc;
}

public sealed class CloudIndex
{
    private readonly Dictionary<string, CloudEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, string Rev), CompletedSnapshot> completions = [];
    public IReadOnlyDictionary<string, CloudEntry> Entries => entries;
    public int CompletionFetchCount { get; private set; }

    public void Clear()
    {
        entries.Clear();
        completions.Clear();
    }

    public void Apply(IEnumerable<CloudEntry> changes)
    {
        foreach (var change in changes) Apply(change);
    }

    public void Apply(CloudEntry change)
    {
        string path = change.PathLower.ToLowerInvariant().TrimEnd('/');
        if (!path.StartsWith('/') || path.Length == 0) throw new UserError("一覧のパス形式が不正です。");
        if (change.Tag == "deleted") { RemoveSubtree(path); return; }
        if (change.Tag is not ("file" or "folder")) throw new UserError("一覧に未対応の種類の項目があります。");
        if (change.Tag == "file")
        {
            bool sameRevision = entries.TryGetValue(path, out var previous) && previous.Rev == change.Rev &&
                previous.ContentHash == change.ContentHash && previous.Size == change.Size;
            RemoveSubtree(path, preserveExactCache: sameRevision);
        }
        // FolderMetadata replaces the folder itself but preserves its children (Dropbox's rule).
        EnsureParents(path);
        entries[path] = change with { PathLower = path };
    }

    private void EnsureParents(string path)
    {
        int position = path.LastIndexOf('/');
        while (position > 0)
        {
            string parent = path[..position];
            if (!entries.ContainsKey(parent)) entries[parent] = new("folder", parent);
            position = parent.LastIndexOf('/');
        }
    }

    private void RemoveSubtree(string path, bool preserveExactCache = false)
    {
        foreach (string key in entries.Keys.Where(key => AtOrBelow(key, path)).ToArray()) entries.Remove(key);
        foreach (var key in completions.Keys.Where(key => AtOrBelow(key.Path, path) && (!preserveExactCache || key.Path != path)).ToArray()) completions.Remove(key);
    }

    public static bool AtOrBelow(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CompletedSnapshot>> ReadCompletionsAsync(DropboxClient client, string root, CancellationToken ct)
    {
        string prefix = root.TrimEnd('/').ToLowerInvariant() + "/";
        var result = new List<CompletedSnapshot>();
        foreach (var entry in entries.Values.Where(e => e.Tag == "file").OrderBy(e => e.PathLower, StringComparer.Ordinal))
        {
            if (!entry.PathLower.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = entry.PathLower[prefix.Length..].Split('/');
            if (parts.Length != 5 || parts[0] != "games" || parts[2] != "snapshots" || parts[4] != "complete.json") continue;
            if (string.IsNullOrEmpty(entry.Rev)) throw new UserError("完了マーカーの rev がありません。");
            var key = (entry.PathLower, entry.Rev);
            if (!completions.TryGetValue(key, out var completed))
            {
                CompletionFetchCount++;
                var downloaded = await client.DownloadSmallAsync(entry.PathLower, ct);
                using var memory = new MemoryStream(downloaded.Bytes);
                var hash = await FileHash.ComputeAsync(memory, ct);
                if (downloaded.Metadata.Rev != entry.Rev || hash.Size != entry.Size || hash.ContentHash != entry.ContentHash ||
                    !downloaded.Metadata.Matches(hash))
                    throw new UserError("完了マーカーが一覧と一致しません。新しい一覧から確認し直してください。");
                var complete = JsonSerializer.Deserialize<SnapshotComplete>(downloaded.Bytes, JsonFormat.Options)
                    ?? throw new UserError("完了マーカーが空です。");
                LocalRepository.ValidateGameId(complete.GameId);
                if (complete.GameId != parts[1] || complete.SnapshotId != parts[3] || !RunIds.IsValid(complete.SnapshotId) ||
                    complete.ContentHash is null || complete.ContentHash.Length != 64 || complete.ContentHash.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new UserError("完了マーカーの識別情報が一致しません。");
                completed = new(entry.PathLower, complete);
                completions[key] = completed;
            }
            result.Add(completed);
        }
        return result;
    }

    public static IReadOnlyDictionary<string, CompletedSnapshot> Latest(IEnumerable<CompletedSnapshot> snapshots) =>
        snapshots.Where(s => s.Reason != "evacuated").GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.SavedAtUtc)
                .ThenByDescending(s => s.SnapshotId, StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
}
