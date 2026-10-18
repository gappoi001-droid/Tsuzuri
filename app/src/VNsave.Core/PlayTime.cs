using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VNsave.Core;

public sealed record PlaySession(string GameId, DateTimeOffset StartedAtUtc, long Seconds, string DeviceId);
public sealed record PlayTotal(TimeSpan Total, DateTimeOffset LastPlayedUtc);

// Each play session is one small append-only file under /vnsave/games/<id>/playtime/. The start time and the
// seconds are part of the file name, so the totals of both PCs come from the folder listing without downloads.
public sealed partial class PlayTimeLog(DropboxClient client, LocalRepository repository)
{
    private string PendingDirectory(string gameId) { LocalRepository.ValidateGameId(gameId); return Path.Combine(repository.Root, "playtime", gameId); }

    public async Task RecordAsync(string gameId, DateTimeOffset startedUtc, TimeSpan duration, CancellationToken ct = default)
    {
        long seconds = (long)Math.Round(duration.TotalSeconds);
        if (seconds < 1) return;
        string name = startedUtc.UtcDateTime.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture) + "-" +
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + "-" + seconds.ToString(CultureInfo.InvariantCulture) + ".json";
        await JsonFormat.SaveAtomicAsync(Path.Combine(PendingDirectory(gameId), name),
            new PlaySession(gameId, startedUtc, seconds, repository.Library.DeviceId), ct);
    }

    public async Task FlushAsync(string gameId, CancellationToken ct = default)
    {
        foreach (string path in PendingFiles(gameId))
        {
            await client.UploadBytesAsync(LocalRepository.RemoteGame(gameId) + "/playtime/" + Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct), ct);
            File.Delete(path);
        }
    }

    public IEnumerable<(string GameId, string Name)> Pending(IEnumerable<string> gameIds) =>
        gameIds.SelectMany(id => PendingFiles(id).Select(path => (id, Path.GetFileName(path))));

    private IEnumerable<string> PendingFiles(string gameId)
    {
        string directory = PendingDirectory(gameId);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Where(path => TryParse(Path.GetFileName(path), out _, out _)).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public static bool TryParse(string name, out DateTimeOffset startedUtc, out long seconds)
    {
        startedUtc = default; seconds = 0;
        var match = NamePattern().Match(name);
        return match.Success &&
            DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedUtc) &&
            long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }

    // Names are unique per session, so a file that is both pending locally and already uploaded counts once.
    public static IReadOnlyDictionary<string, PlayTotal> Totals(IEnumerable<(string GameId, string Name)> files) =>
        files.Distinct().Select(f => (f.GameId, Ok: TryParse(f.Name, out var started, out long seconds), started, seconds))
            .Where(f => f.Ok).GroupBy(f => f.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new PlayTotal(TimeSpan.FromSeconds(g.Sum(f => f.seconds)), g.Max(f => f.started)), StringComparer.OrdinalIgnoreCase);

    public static string Format(TimeSpan total) => total.TotalMinutes < 1 ? "1分未満"
        : total.TotalHours < 1 ? $"{(int)total.TotalMinutes}分" : $"{(int)total.TotalHours}時間{total.Minutes}分";

    [GeneratedRegex(@"^(\d{8}t\d{6}z)-[0-9a-f]{8}-(\d{1,9})\.json$")]
    private static partial Regex NamePattern();
}
