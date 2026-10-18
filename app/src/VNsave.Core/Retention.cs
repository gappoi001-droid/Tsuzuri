using System.Text.RegularExpressions;

namespace VNsave.Core;

// Old generations are only ever compared by id: the decision reads the cloud latest and this PC's record in games.json,
// and only the latest's files are downloaded. So only the latest is required; the rest that is kept is margin.
public static partial class Retention
{
    public const int KeepSnapshots = 10;
    public const int KeepBackups = 10;

    // Only completed generations are candidates, so a folder another PC is still uploading is never touched.
    public static IReadOnlyList<string> SnapshotsToDelete(string gameId, IEnumerable<CompletedSnapshot> completed, string? ownBase)
    {
        var generations = completed.Where(s => s.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.SavedAtUtc).ThenByDescending(s => s.SnapshotId, StringComparer.Ordinal).ToArray();
        var keep = generations.Take(KeepSnapshots).Select(s => s.SnapshotId).ToHashSet(StringComparer.Ordinal);
        if (CloudIndex.Latest(generations).GetValueOrDefault(gameId) is { } latest) keep.Add(latest.SnapshotId);
        if (ownBase is not null) keep.Add(ownBase);
        // The other PC's base is not in Dropbox; its newest own generation stands in for it.
        foreach (var device in generations.GroupBy(s => s.Complete.DeviceId)) keep.Add(device.First().SnapshotId);
        return generations.Select(s => s.SnapshotId).Where(id => !keep.Contains(id) && RunIds.IsValid(id)).ToArray();
    }

    // Names start with a UTC run id, so the ordinal order is the upload order. Other names are left alone.
    public static IReadOnlyList<string> BackupsToDelete(string gameId, IEnumerable<(string GameId, string Name)> backups) =>
        backups.Where(b => b.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase) && BackupName().IsMatch(b.Name))
            .Select(b => b.Name).OrderByDescending(name => name, StringComparer.Ordinal).Skip(KeepBackups).ToArray();

    [GeneratedRegex(@"^\d{8}t\d{6}z-[0-9a-f]{8}(-pending)?\.zip$")]
    private static partial Regex BackupName();
}
