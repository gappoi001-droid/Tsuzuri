namespace VNsave.Core;

public enum SyncAction { None, Upload, Download }

public static class SyncDecision
{
    public static SyncAction Decide(SnapshotComplete? cloud, string? localHash, DateTimeOffset? localModified,
        string? baseId, string? baseHash)
    {
        if (cloud is null) return localHash is null ? SyncAction.None : SyncAction.Upload;
        if (localHash is null) return SyncAction.Download;
        if (localHash == cloud.ContentHash) return SyncAction.None;
        if (baseId is not null && localHash == baseHash && cloud.SnapshotId != baseId) return SyncAction.Download;
        if (baseId is not null && localHash != baseHash && cloud.SnapshotId == baseId) return SyncAction.Upload;
        return localModified > cloud.SavedAtUtc ? SyncAction.Upload : SyncAction.Download;
    }
}
