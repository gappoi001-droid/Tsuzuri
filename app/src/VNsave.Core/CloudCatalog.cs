using System.Text.Json;

namespace VNsave.Core;

public sealed class CloudCatalog
{
    private readonly DropboxClient client;
    private readonly FolderListingReader reader;
    private readonly CloudIndex index = new();
    private readonly Dictionary<(string, string), CloudGame> gamesCache = [];
    public IReadOnlyList<CloudGame> Games { get; private set; } = [];
    public IReadOnlyDictionary<string, CompletedSnapshot> Latest { get; private set; } = new Dictionary<string, CompletedSnapshot>();
    public IReadOnlyList<CompletedSnapshot> Completed { get; private set; } = [];
    public IReadOnlyList<(string GameId, string Name)> PlayTimeFiles { get; private set; } = [];
    public IReadOnlyList<(string GameId, string Name)> Backups { get; private set; } = [];
    public CloudCatalog(DropboxClient client)
    {
        this.client = client;
        reader = new(client, index, "/vnsave");
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var batch = reader.Cursor is null ? await reader.ReadAllAsync(ct) : await reader.ContinueAsync(ct);
            if (batch.Rebuilt) gamesCache.Clear();
        }
        catch (DropboxApiException error) when (error.IsNotFound)
        { index.Clear(); Games = []; Completed = []; Latest = new Dictionary<string, CompletedSnapshot>(); PlayTimeFiles = []; Backups = []; return; }
        var games = new List<CloudGame>();
        foreach (var entry in index.Entries.Values.Where(e => e.Tag == "file"))
        {
            string[] parts = entry.PathLower.Split('/');
            if (parts.Length != 5 || parts[1] != "vnsave" || parts[2] != "games" || parts[4] != "game.json") continue;
            var key = (entry.PathLower, entry.Rev!);
            if (!gamesCache.TryGetValue(key, out var game))
            {
                var download = await client.DownloadSmallAsync(entry.PathLower, ct);
                using var bytes = new MemoryStream(download.Bytes);
                var fingerprint = await FileHash.ComputeAsync(bytes, ct);
                if (download.Metadata.Rev != entry.Rev || fingerprint.ContentHash != entry.ContentHash || fingerprint.Size != entry.Size) throw new UserError("ゲーム定義が一覧と一致しません。");
                game = JsonSerializer.Deserialize<CloudGame>(download.Bytes, JsonFormat.Options) ?? throw new UserError("ゲーム定義が空です。");
                LocalRepository.ValidateGameId(game.GameId);
                if (game.GameId != parts[3] || game.Locations.Length == 0 || game.Locations.Select(l => l.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != game.Locations.Length)
                    throw new UserError("ゲーム定義が不正です。");
                gamesCache[key] = game;
            }
            games.Add(game);
        }
        Games = games;
        Completed = await index.ReadCompletionsAsync(client, "/vnsave", ct);
        Latest = CloudIndex.Latest(Completed);
        PlayTimeFiles = FilesIn("playtime");
        Backups = FilesIn("backups");
    }
    // Files directly in /vnsave/games/<id>/<folder>/.
    private (string GameId, string Name)[] FilesIn(string folder) => index.Entries.Values.Where(e => e.Tag == "file").Select(e => e.PathLower.Split('/'))
        .Where(p => p.Length == 6 && p[1] == "vnsave" && p[2] == "games" && p[4] == folder).Select(p => (p[3], p[5])).ToArray();
    public async Task RegisterAsync(CloudGame game, CancellationToken ct = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(game, JsonFormat.Options);
        using var stream = new MemoryStream(bytes);
        var fingerprint = await FileHash.ComputeAsync(stream, ct);
        var metadata = await client.UploadBytesAsync(LocalRepository.RemoteGame(game.GameId) + "/game.json", bytes, ct);
        if (!metadata.Matches(fingerprint)) throw new UserError("ゲーム定義のアップロード結果が一致しません。");
    }
}
