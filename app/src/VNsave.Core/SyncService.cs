namespace VNsave.Core;

public sealed class SyncService(DropboxClient client, LocalRepository repository, CloudCatalog catalog,
    SnapshotStore snapshots, IGameProcess process, ActivityLog log, Func<CancellationToken, Task>? validateConnection = null,
    PlayTimeLog? playTime = null)
{
    // Play time is best effort: a failed upload stays pending locally and never blocks save sync.
    private async Task FlushPlayTimeAsync(LocalGame game, CancellationToken ct)
    {
        if (playTime is null) return;
        try { await playTime.FlushAsync(game.Definition.GameId, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        { log.Write(game.Definition.DisplayName + ": プレイ時間の送信を後で再試行します（" + error.GetType().Name + "）。"); }
    }
    // Pruning runs only after a publication succeeded and is best effort: old data simply waits for the next sync.
    private async Task PruneAsync(LocalGame game, CancellationToken ct)
    {
        try
        {
            await catalog.RefreshAsync(ct);
            string gameId = game.Definition.GameId, remote = LocalRepository.RemoteGame(gameId);
            var snapshotIds = Retention.SnapshotsToDelete(gameId, catalog.Completed, game.LastSnapshotId);
            var backupNames = Retention.BackupsToDelete(gameId, catalog.Backups);
            foreach (string id in snapshotIds) await client.DeleteAsync(remote + "/snapshots/" + id, ct);
            foreach (string name in backupNames) await client.DeleteAsync(remote + "/backups/" + name, ct);
            if (snapshotIds.Count + backupNames.Count > 0)
                log.Write(game.Definition.DisplayName + ": 古い世代 " + snapshotIds.Count + " 件とバックアップ " + backupNames.Count + " 件を整理しました。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { log.Write(game.Definition.DisplayName + ": 古いデータの整理を次回に回します（" + error.GetType().Name + "）。"); }
    }
    public void EnsureIdle(LocalGame game)
    {
        GameDetector.ValidateExecutable(game.ExecutablePath);
        if (process.IsRunning(game.ExecutablePath)) throw new UserError("ゲームの起動中は同期できません。");
    }
    public async Task RetryPendingAsync(LocalGame game, CancellationToken ct = default)
    {
        EnsureIdle(game);
        if (validateConnection is not null) await validateConnection(ct);
        bool published = false;
        foreach (var pending in await snapshots.FindAsync(game.Definition.GameId, ct))
        {
            await snapshots.PublishAsync(game, pending, ct);
            published = true;
            log.Write(game.Definition.DisplayName + ": 未送信の世代を公開しました。");
        }
        if (published) await PruneAsync(game, ct);
        await FlushPlayTimeAsync(game, ct);
    }
    public async Task BeforeLaunchAsync(LocalGame game, CancellationToken ct = default)
    {
        EnsureIdle(game);
        await RetryPendingAsync(game, ct);
        await catalog.RefreshAsync(ct);
        var cloud = catalog.Latest.GetValueOrDefault(game.Definition.GameId)?.Complete;
        var local = await SaveArchive.ScanAsync(game.Locations, ct);
        var action = SyncDecision.Decide(cloud, local.ContentHash, local.ModifiedUtc, game.LastSnapshotId, game.LastContentHash);
        if (action == SyncAction.Download) await ApplyAsync(game, cloud!, ct);
        else if (action == SyncAction.Upload) await UploadLocalAsync(game, ct);
        else if (cloud is not null) await snapshots.RememberAsync(game, cloud, ct);
        log.Write(game.Definition.DisplayName + ": 起動前同期が完了しました（" + action + "）。");
    }
    public async Task UploadAsync(LocalGame game, CancellationToken ct = default)
    {
        EnsureIdle(game);
        await RetryPendingAsync(game, ct);
        await catalog.RefreshAsync(ct);
        var local = await SaveArchive.ScanAsync(game.Locations, ct);
        var cloud = catalog.Latest.GetValueOrDefault(game.Definition.GameId)?.Complete;
        if (cloud is not null && local.ContentHash == cloud.ContentHash) await snapshots.RememberAsync(game, cloud, ct);
        else await UploadLocalAsync(game, ct);
    }
    public async Task AfterExitAsync(LocalGame game, CancellationToken ct = default)
    {
        EnsureIdle(game);
        await SaveSettler.WaitAsync(game.Locations, ct);
        var local = await SaveArchive.ScanAsync(game.Locations, ct);
        if (local.ContentHash is not null && local.ContentHash != game.LastContentHash) await UploadLocalAsync(game, ct);
        await FlushPlayTimeAsync(game, ct);
        log.Write(game.Definition.DisplayName + ": 終了後の同期が完了しました。");
    }
    private async Task UploadLocalAsync(LocalGame game, CancellationToken ct)
    {
        EnsureIdle(game);
        var pending = await snapshots.CreateAsync(game, ct);
        if (pending is not null)
        {
            if (validateConnection is not null) await validateConnection(ct);
            await snapshots.PublishAsync(game, pending, ct);
            await PruneAsync(game, ct);
        }
    }
    public async Task DownloadAsync(LocalGame game, CancellationToken ct = default)
    {
        EnsureIdle(game);
        if (validateConnection is not null) await validateConnection(ct);
        await catalog.RefreshAsync(ct);
        var latest = catalog.Latest.GetValueOrDefault(game.Definition.GameId)?.Complete ?? throw new UserError("クラウドに完了済みのセーブがありません。");
        await snapshots.CancelPendingAsync(game, ct);
        await ApplyAsync(game, latest, ct);
    }
    private async Task ApplyAsync(LocalGame game, SnapshotComplete complete, CancellationToken ct)
    {
        EnsureIdle(game);
        string remote = LocalRepository.RemoteGame(game.Definition.GameId) + "/snapshots/" + complete.SnapshotId;
        string work = Path.Combine(repository.WorkRoot, "download-" + RunIds.New());
        Directory.CreateDirectory(work);
        try
        {
            foreach (string name in new[] { "manifest.json", "save.zip" })
            {
                string path = Path.Combine(work, name);
                RemoteFile metadata;
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    metadata = await client.DownloadAsync(remote + "/" + name, output, ct);
                if (!metadata.Matches(await FileHash.ComputeFileAsync(path, ct))) throw new UserError("ダウンロードしたデータの content_hash が一致しません。");
            }
            var manifest = await JsonFormat.ReadAsync<SaveManifest>(Path.Combine(work, "manifest.json"), ct);
            await new SaveApplier(repository, process.IsRunning,
                (backup, token) => snapshots.UploadBackupAsync(game.Definition.GameId, backup, "", token)).ApplyAsync(game, complete, Path.Combine(work, "save.zip"), manifest,
                () => snapshots.RememberAsync(game, complete, ct), ct);
        }
        finally { PathSafety.DeleteTree(work, repository.WorkRoot); }
    }
}
