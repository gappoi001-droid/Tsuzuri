using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VNsave.Core;

public static class JsonFormat
{
    // Unknown fields are skipped so a record written by a newer version (e.g. a save pattern) does not stop an older one.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true
    };
    public static string ApiArgument(object value) => JsonSerializer.Serialize(value);
    public static async Task SaveAtomicAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Options), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct = default) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), Options) ?? throw new UserError("記録が空です。");
}

public sealed record AppSettings
{
    public string AppKey { get; init; } = "";
    public string RedirectHost { get; init; } = "127.0.0.1";
    public int[] RedirectPorts { get; init; } = [53134, 53135, 53136];
    public string[] Scopes { get; init; } = [];
    public static AppSettings Load(string path) => Parse(File.ReadAllText(path));
    public static AppSettings Parse(string json)
    {
        var value = JsonSerializer.Deserialize<AppSettings>(json, JsonFormat.Options) ?? throw new UserError("設定が空です。");
        if (string.IsNullOrWhiteSpace(value.AppKey) || value.AppKey.Any(c => !char.IsAsciiLetterOrDigit(c)) ||
            value.RedirectHost is not ("127.0.0.1" or "localhost") || value.RedirectPorts.Length == 0 ||
            value.RedirectPorts.Any(p => p is < 1024 or > 65535)) throw new UserError("Dropbox 接続設定が不正です。");
        return value;
    }
}

public static partial class RunIds
{
    public static string New() => DateTimeOffset.UtcNow.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    public static bool IsValid(string value) => Pattern().IsMatch(value);
    [GeneratedRegex(@"^\d{8}t\d{6}z-[0-9a-f]{8}$")]
    private static partial Regex Pattern();
}

public class UserError(string message) : Exception(message);
public sealed class ConnectionUnavailableException() : UserError("Dropbox に未接続です。接続してから再試行してください。");
// Pattern null = the whole folder; otherwise only the files directly in the folder that match it (see SaveFiles).
// It is left out of the JSON when null, so folder locations are written exactly as before.
public sealed record SaveLocation(string Id, string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pattern = null);
public sealed record LocationDefinition(string Id, string Base, string RelativePath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pattern = null);
public sealed record CloudGame(string GameId, string DisplayName, string ExecutableName, string ProductName,
    string Engine, LocationDefinition[] Locations);
public sealed record LocalGame
{
    public required CloudGame Definition { get; init; }
    public required string ExecutablePath { get; init; }
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; init; } = "";
    public required SaveLocation[] Locations { get; init; }
    public string? LastSnapshotId { get; set; }
    public string? LastContentHash { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
}
public sealed record Library
{
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string? AccountId { get; set; }
    public List<LocalGame> Games { get; init; } = [];
}
public sealed record ManifestFile(string Path, long Size, string Sha256);
public sealed record SaveManifest(string GameId, string SnapshotId, ManifestFile[] Files);
public sealed record SnapshotComplete(string SnapshotId, string GameId, DateTimeOffset SavedAtUtc,
    string? BaseSnapshotId, string ContentHash, string DeviceId);

public sealed class LocalRepository(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string GamesPath => Path.Combine(Root, "games.json");
    public string PendingRoot => Path.Combine(Root, "pending");
    public string WorkRoot => Path.Combine(Root, "work");
    public Library Library { get; private set; } = new();
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (File.Exists(GamesPath)) Library = await JsonFormat.ReadAsync<Library>(GamesPath, ct);
    }
    public Task SaveAsync(CancellationToken ct = default) => JsonFormat.SaveAtomicAsync(GamesPath, Library, ct);
    public FileStream AcquireLock()
    {
        Directory.CreateDirectory(Root);
        try { return new(Path.Combine(Root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UserError("VNsave は既に起動しています。"); }
    }
    public static string RemoteGame(string gameId) { ValidateGameId(gameId); return "/vnsave/games/" + gameId; }
    public static void ValidateGameId(string id)
    {
        if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) throw new UserError("ゲーム ID が不正です。");
    }
}

public sealed class ActivityLog(string root, SecretRedactor redactor)
{
    private readonly object gate = new();
    public void Write(string message)
    {
        lock (gate)
        {
            string directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "vnsave.log"), DateTimeOffset.Now.ToString("O") + " " + redactor.Redact(message).Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
        }
    }
}
