using System.Text.Json;

namespace VNsave.Core;

public sealed record Publication(SnapshotComplete Complete, string ConnectionId, Dictionary<string, FileFingerprint> Files);
public sealed record PendingSnapshot(string Directory, Publication Publication);

public sealed class SnapshotStore(DropboxClient client, LocalRepository repository, string connectionId)
{
    private static readonly string[] Names = ["save.zip", "manifest.json", "complete.json"];
    private string GamePending(string gameId) { LocalRepository.ValidateGameId(gameId); return Path.Combine(repository.PendingRoot, gameId); }
    public bool HasPending(string gameId) => Directory.Exists(GamePending(gameId)) && Directory.EnumerateDirectories(GamePending(gameId)).Any(p => RunIds.IsValid(Path.GetFileName(p)));

    public async Task<PendingSnapshot?> CreateAsync(LocalGame game, CancellationToken ct = default)
    {
        string id = RunIds.New();
        string gameRoot = GamePending(game.Definition.GameId);
        string temporary = Path.Combine(gameRoot, ".creating-" + id);
        string final = Path.Combine(gameRoot, id);
        string stage = Path.Combine(temporary, "stage");
        try
        {
            var save = await SaveArchive.CaptureAsync(game.Locations, stage, ct);
            if (save.ContentHash is null) return null;
            var complete = new SnapshotComplete(id, game.Definition.GameId, DateTimeOffset.UtcNow, game.LastSnapshotId, save.ContentHash, repository.Library.DeviceId);
            await SaveArchive.WriteZipAsync(stage, save.Files, Path.Combine(temporary, "save.zip"), ct);
            await JsonFormat.SaveAtomicAsync(Path.Combine(temporary, "manifest.json"), new SaveManifest(game.Definition.GameId, id, save.Files), ct);
            await JsonFormat.SaveAtomicAsync(Path.Combine(temporary, "complete.json"), complete, ct);
            var fingerprints = new Dictionary<string, FileFingerprint>();
            foreach (string name in Names) fingerprints[name] = await FileHash.ComputeFileAsync(Path.Combine(temporary, name), ct);
            var publication = new Publication(complete, connectionId, fingerprints);
            await JsonFormat.SaveAtomicAsync(Path.Combine(temporary, "publication.json"), publication, ct);
            PathSafety.DeleteTree(stage, temporary);
            Directory.Move(temporary, final);
            return new(final, publication);
        }
        finally { if (Directory.Exists(temporary)) PathSafety.DeleteTree(temporary, gameRoot); }
    }
    public async Task<IReadOnlyList<PendingSnapshot>> FindAsync(string gameId, CancellationToken ct = default)
    {
        var result = new List<PendingSnapshot>();
        string root = GamePending(gameId);
        if (!Directory.Exists(root)) return result;
        foreach (string path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (!RunIds.IsValid(Path.GetFileName(path))) continue;
            PathSafety.NoLinks(path);
            var publication = await JsonFormat.ReadAsync<Publication>(Path.Combine(path, "publication.json"), ct);
            if (publication.ConnectionId != connectionId || publication.Complete.GameId != gameId || publication.Complete.SnapshotId != Path.GetFileName(path) ||
                !Names.ToHashSet(StringComparer.Ordinal).SetEquals(publication.Files.Keys)) throw new UserError("未送信データの接続先または識別情報が一致しません。保持して中止します。");
            foreach (string name in Names)
                if (await FileHash.ComputeFileAsync(Path.Combine(path, name), ct) != publication.Files[name]) throw new UserError("未送信データが変化しています。保持して中止します。");
            result.Add(new(path, publication));
        }
        return result;
    }
    public async Task PublishAsync(LocalGame game, PendingSnapshot pending, CancellationToken ct = default)
    {
        var publication = pending.Publication;
        if (publication.ConnectionId != connectionId || publication.Complete.GameId != game.Definition.GameId || !RunIds.IsValid(publication.Complete.SnapshotId)) throw new UserError("未送信データの接続先が一致しません。");
        string remote = LocalRepository.RemoteGame(game.Definition.GameId) + "/snapshots/" + publication.Complete.SnapshotId;
        foreach (string name in Names)
        {
            string path = Path.Combine(pending.Directory, name);
            var hash = await FileHash.ComputeFileAsync(path, ct);
            if (hash != publication.Files[name]) throw new UserError("未送信データの内容が変化しました。");
            var uploaded = hash.Size >= DropboxClient.SingleUploadLimit
                ? await client.UploadSessionAsync(remote + "/" + name, path, ct)
                : await client.UploadFileAsync(remote + "/" + name, path, ct);
            if (!uploaded.Matches(hash)) throw new UserError("アップロード結果の content_hash が一致しません。");
        }
        await RememberAsync(game, publication.Complete, ct);
        PathSafety.DeleteTree(pending.Directory, GamePending(game.Definition.GameId));
    }
    public async Task RememberAsync(LocalGame game, SnapshotComplete complete, CancellationToken ct = default)
    {
        var previous = (game.LastSnapshotId, game.LastContentHash, game.LastSyncUtc);
        game.LastSnapshotId = complete.SnapshotId;
        game.LastContentHash = complete.ContentHash;
        game.LastSyncUtc = DateTimeOffset.UtcNow;
        try { await repository.SaveAsync(ct); }
        catch { (game.LastSnapshotId, game.LastContentHash, game.LastSyncUtc) = previous; throw; }
    }
    public async Task CancelPendingAsync(LocalGame game, CancellationToken ct = default)
    {
        // Unsent generations are not published; their saves are kept as a Dropbox backup instead.
        foreach (var pending in await FindAsync(game.Definition.GameId, ct))
        {
            await UploadBackupAsync(game.Definition.GameId, Path.Combine(pending.Directory, "save.zip"), "-pending", ct);
            PathSafety.DeleteTree(pending.Directory, GamePending(game.Definition.GameId));
        }
    }
    // Backups live outside snapshots/, so the catalog never treats them as a generation to adopt.
    public async Task UploadBackupAsync(string gameId, string zipPath, string suffix = "", CancellationToken ct = default)
    {
        string remote = LocalRepository.RemoteGame(gameId) + "/backups/" + RunIds.New() + suffix + ".zip";
        var hash = await FileHash.ComputeFileAsync(zipPath, ct);
        var uploaded = hash.Size >= DropboxClient.SingleUploadLimit
            ? await client.UploadSessionAsync(remote, zipPath, ct)
            : await client.UploadFileAsync(remote, zipPath, ct);
        if (!uploaded.Matches(hash)) throw new UserError("バックアップのアップロード結果の content_hash が一致しません。");
    }
}

public sealed class SaveApplier(LocalRepository repository, Func<string, bool> isRunning,
    Func<string, CancellationToken, Task> storeBackup, Action<int>? afterReplace = null)
{
    public async Task ApplyAsync(LocalGame game, SnapshotComplete complete, string zipPath, SaveManifest manifest,
        Func<Task> commit, CancellationToken ct = default)
    {
        if (isRunning(game.ExecutablePath)) throw new UserError("ゲームの起動中はセーブを置き換えられません。");
        if (complete.GameId != game.Definition.GameId || manifest.GameId != complete.GameId || manifest.SnapshotId != complete.SnapshotId ||
            SaveArchive.ContentHash(manifest.Files) != complete.ContentHash) throw new UserError("世代と manifest の内容ハッシュが一致しません。");
        SaveLocationValidator.Validate(game.Locations, repository.Library.Games.Where(g => g != game));
        string work = Path.Combine(repository.WorkRoot, "apply-" + RunIds.New());
        string stage = Path.Combine(work, "incoming");
        string old = Path.Combine(work, "old");
        bool keepWork = false;
        Directory.CreateDirectory(work);
        try
        {
            await SaveArchive.ExtractAsync(zipPath, manifest, game.Locations, stage, ct);
            var original = await SaveArchive.CaptureAsync(game.Locations, old, ct);
            // The local zip only serves the rollback below; the copy that survives goes to Dropbox before anything is replaced.
            string backup = Path.Combine(work, "backup.zip");
            await SaveArchive.WriteZipAsync(old, original.Files, backup, ct);
            if (original.ContentHash is not null) await storeBackup(backup, ct);
            if (isRunning(game.ExecutablePath)) throw new UserError("ゲームが起動されたため適用を中止しました。");
            try
            {
                for (int i = 0; i < game.Locations.Length; i++)
                {
                    Replace(game.Locations[i], stage);
                    afterReplace?.Invoke(i);
                }
                if ((await SaveArchive.ScanAsync(game.Locations, ct)).ContentHash != complete.ContentHash) throw new UserError("適用後の内容ハッシュが一致しません。");
                await commit();
            }
            catch (Exception originalError)
            {
                try
                {
                    string rollback = Path.Combine(work, "rollback");
                    await SaveArchive.ExtractAsync(backup, new(game.Definition.GameId, complete.SnapshotId, original.Files), game.Locations, rollback, CancellationToken.None);
                    foreach (var location in game.Locations) Replace(location, rollback);
                    if ((await SaveArchive.ScanAsync(game.Locations, CancellationToken.None)).ContentHash != original.ContentHash) throw new IOException("Rollback mismatch");
                }
                catch
                {
                    keepWork = true;
                    throw new UserError("適用と自動復元に失敗しました。元のセーブは Dropbox のバックアップと次の場所にあります: " + backup);
                }
                throw new UserError("適用に失敗したため元のセーブに戻しました（" + originalError.GetType().Name + "）。");
            }
        }
        finally { if (!keepWork) PathSafety.DeleteTree(work, repository.WorkRoot); }
    }
    private static void Replace(SaveLocation location, string stage)
    {
        string root = Path.GetFullPath(location.Path);
        PathSafety.NoLinks(root);
        if (location.Pattern is { } pattern)
        {
            ReplaceMatching(root, pattern, Path.Combine(stage, location.Id));
            return;
        }
        if (Directory.Exists(root))
        {
            _ = PathSafety.Files(root).ToArray();
            // The selected root itself remains; every child must resolve below it.
            foreach (string child in Directory.EnumerateFileSystemEntries(root))
            {
                string resolved = Path.GetFullPath(child);
                if (!resolved.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new UserError("保存先の外は削除できません。");
                if (Directory.Exists(resolved)) PathSafety.DeleteTree(resolved, root); else File.Delete(resolved);
            }
        }
        Directory.CreateDirectory(root);
        string sourceRoot = Path.Combine(stage, location.Id);
        foreach (string source in PathSafety.Files(sourceRoot))
        {
            string target = PathSafety.Under(root, Path.GetRelativePath(sourceRoot, source).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }
    // The folder is the game's own folder: only files directly in it that match the pattern are removed or written,
    // and every incoming file is checked before anything is deleted.
    private static void ReplaceMatching(string root, string pattern, string sourceRoot)
    {
        var incoming = PathSafety.Files(sourceRoot).Select(source => (Source: source, Name: Path.GetRelativePath(sourceRoot, source))).ToArray();
        if (incoming.Any(file => file.Name.Contains(Path.DirectorySeparatorChar) || !SaveFiles.Matches(pattern, file.Name)))
            throw new UserError("世代に保存先のパターンと一致しないファイルが含まれています。");
        Directory.CreateDirectory(root);
        foreach (string existing in SaveFiles.Matching(root, pattern).ToArray()) File.Delete(existing);
        foreach (var file in incoming) File.Copy(file.Source, PathSafety.Under(root, file.Name));
    }
}
