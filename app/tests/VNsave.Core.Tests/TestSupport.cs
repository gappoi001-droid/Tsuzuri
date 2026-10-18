using System.Net;
using System.Text;
using System.Text.Json;

namespace VNsave.Core.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNsave.Core.Tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNsave.Core.Tests")) + System.IO.Path.DirectorySeparatorChar;
        string resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test directory");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

internal sealed class MemoryCredentials : ICredentialStore
{
    public string? Value { get; set; }
    public bool FailWrite { get; init; }
    public string? Read() => Value;
    public void Write(string value)
    {
        if (FailWrite) throw new UserError("資格情報の保存に失敗しました。");
        Value = value;
    }
    public void Delete() => Value = null;
}

internal sealed class FakeTokens : IAccessTokens
{
    public int Refreshes { get; private set; }
    public TokenDiagnostics Diagnostics => new(Refreshes > 0 ? "refresh_token" : null, Refreshes > 0,
        Refreshes > 0 ? 200 : null, Refreshes > 0 ? 14400 : null, Refreshes);
    public Task<string> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (forceRefresh) Refreshes++;
        return Task.FromResult("unit-test-token");
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Error(string summary, HttpStatusCode status = HttpStatusCode.Conflict) => Json(new { error_summary = summary }, status);
}

// A deterministic local HTTP double. It performs no Dropbox requests or Windows credential operations.
internal sealed partial class FakeDropboxHandler : HttpMessageHandler
{
    internal sealed record Stored(byte[] Bytes, string Name, string Id, string Rev);
    public Dictionary<string, Stored> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryStream> sessions = [];
    public List<string> Endpoints { get; } = [];
    public bool IncorrectlyAllowReplacement { get; init; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? TokenEndpointHandler { get; init; }
    public int Uploads { get; private set; }
    public int Count => Files.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/oauth2/token" && TokenEndpointHandler is { } tokenEndpoint)
            return await tokenEndpoint(request, ct);
        string endpoint = request.RequestUri!.AbsolutePath.Replace("/2/", "", StringComparison.Ordinal);
        Endpoints.Add(endpoint);
        string args = request.Headers.TryGetValues("Dropbox-API-Arg", out var header)
            ? header.Single() : request.Content is null ? "null" : await request.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(args);
        var argument = json.RootElement;
        if (endpoint.StartsWith("files/list_folder", StringComparison.Ordinal)) return HandleListing(endpoint, argument);
        if (endpoint == "users/get_current_account") return StubHandler.Json(new
        {
            account_id = "dbid:example-test-account", email = "private@example.invalid", name = new { display_name = "Test User" }
        });
        if (endpoint == "users/get_space_usage") return StubHandler.Json(new { used = 0, allocation = new { allocated = 1000000000 } });
        if (endpoint == "auth/token/revoke") return StubHandler.Json((object?)null!);
        if (endpoint == "files/get_metadata")
        {
            string path = argument.GetProperty("path").GetString()!;
            return Files.TryGetValue(path, out var stored) ? StubHandler.Json(Metadata(path, stored)) : StubHandler.Error("path/not_found/...");
        }
        if (endpoint == "files/download")
        {
            string path = argument.GetProperty("path").GetString()!;
            if (!Files.TryGetValue(path, out var stored)) return StubHandler.Error("path/not_found/...");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored.Bytes) };
            response.Headers.Add("Dropbox-API-Result", JsonSerializer.Serialize(Metadata(path, stored)));
            return response;
        }
        if (endpoint == "files/delete_v2")
        {
            if (FailDeletes) return StubHandler.Error("path_write/insufficient_space/...");
            string path = argument.GetProperty("path").GetString()!;
            var matching = Files.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0 && !folders.Contains(path)) return StubHandler.Error("path_lookup/not_found/...");
            TrackDeletion(path);
            foreach (string key in matching) Files.Remove(key);
            return StubHandler.Json(new { metadata = new { name = "deleted" } });
        }
        if (endpoint == "files/upload") return Commit(argument, await request.Content!.ReadAsByteArrayAsync(ct));
        if (endpoint == "files/upload_session/start")
        {
            string id = Guid.NewGuid().ToString("N");
            sessions[id] = new MemoryStream();
            return StubHandler.Json(new { session_id = id });
        }
        if (endpoint is "files/upload_session/append_v2" or "files/upload_session/finish")
        {
            var cursor = argument.GetProperty("cursor");
            string id = cursor.GetProperty("session_id").GetString()!;
            var session = sessions[id];
            if (cursor.GetProperty("offset").GetInt64() != session.Length)
                return StubHandler.Json(new { error_summary = "incorrect_offset/...", error = new { correct_offset = session.Length } }, HttpStatusCode.Conflict);
            await session.WriteAsync(await request.Content!.ReadAsByteArrayAsync(ct), ct);
            if (endpoint.EndsWith("append_v2", StringComparison.Ordinal)) return StubHandler.Json((object?)null!);
            byte[] bytes = session.ToArray();
            session.Dispose();
            sessions.Remove(id);
            return Commit(argument.GetProperty("commit"), bytes);
        }
        throw new InvalidOperationException("Unexpected fake endpoint");
    }

    private HttpResponseMessage Commit(JsonElement commit, byte[] bytes)
    {
        Assert.Equal("add", commit.GetProperty("mode").GetString());
        Assert.False(commit.GetProperty("autorename").GetBoolean());
        Assert.False(commit.GetProperty("strict_conflict").GetBoolean());
        Assert.True(commit.GetProperty("mute").GetBoolean());
        string path = commit.GetProperty("path").GetString()!;
        Uploads++;
        if (FailUploadNumber == Uploads) return StubHandler.Error("path/insufficient_space/...");
        if (Files.TryGetValue(path, out var previous))
        {
            if (previous.Bytes.SequenceEqual(bytes)) return StubHandler.Json(Metadata(path, previous));
            if (!IncorrectlyAllowReplacement) return StubHandler.Error("path/conflict/file/...");
        }
        var stored = new Stored(bytes.ToArray(), path.Split('/')[^1], "id:" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Files[path] = stored;
        TrackFileWrite(path, stored);
        return StubHandler.Json(Metadata(path, stored));
    }

    private static Dictionary<string, object?> Metadata(string path, Stored file) => new()
    {
        [".tag"] = "file", ["id"] = file.Id, ["name"] = file.Name, ["path_lower"] = path.ToLowerInvariant(),
        ["rev"] = file.Rev, ["size"] = file.Bytes.Length, ["content_hash"] = HashAndPkceTests.ReferenceHash(file.Bytes)
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var stream in sessions.Values) stream.Dispose();
        base.Dispose(disposing);
    }
}
