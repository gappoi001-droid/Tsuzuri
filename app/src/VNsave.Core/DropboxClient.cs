using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNsave.Core;

public sealed class DropboxApiException : Exception
{
    public int HttpStatus { get; }
    public string ErrorSummary { get; }
    public long? CorrectOffset { get; }
    public bool IsFileConflict => HttpStatus == 409 && ErrorSummary.StartsWith("path/conflict/file", StringComparison.Ordinal);
    public bool IsNotFound => HttpStatus == 409 && ErrorSummary.StartsWith("path/not_found", StringComparison.Ordinal);

    public DropboxApiException(int status, string summary, long? correctOffset = null)
        : base($"Dropbox API: HTTP {status}, {summary}")
    { HttpStatus = status; ErrorSummary = summary; CorrectOffset = correctOffset; }

    public static DropboxApiException Parse(int status, string body, SecretRedactor redactor)
    {
        string summary = "unclassified_error";
        long? offset = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_summary", out var value))
            {
                // Error paths contain tags, never arbitrary response text/error_description.
                string candidate = redactor.Redact(value.GetString() ?? "").TrimEnd('.', '/');
                if (candidate.Length is > 0 and <= 160 && Regex.IsMatch(candidate, @"^[a-z_]+(?:/[a-z_]+)*$")) summary = candidate;
            }
            offset = FindOffset(document.RootElement);
        }
        catch (JsonException) { }
        return new(status, summary, offset);
    }

    private static long? FindOffset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty("correct_offset", out var value) && value.TryGetInt64(out long offset)) return offset;
        foreach (var property in element.EnumerateObject())
        {
            var nested = FindOffset(property.Value);
            if (nested.HasValue) return nested;
        }
        return null;
    }
}

public sealed record RemoteFile(string Id, string Name, string PathLower, string Rev, long Size, string ContentHash, int HttpStatus = 200)
{
    public static RemoteFile Parse(JsonElement json, int status = 200)
    {
        if (json.TryGetProperty(".tag", out var tag) && tag.GetString() != "file")
            throw new UserError("ファイルを想定したパスに別の種類の項目があります。上書きは行いません。");
        return new(json.GetProperty("id").GetString()!, json.GetProperty("name").GetString()!,
            json.GetProperty("path_lower").GetString()!, json.GetProperty("rev").GetString()!,
            json.GetProperty("size").GetInt64(), json.GetProperty("content_hash").GetString()!, status);
    }

    public bool Matches(FileFingerprint local) => Size == local.Size && ContentHash == local.ContentHash;
}

public sealed record RequestObservation(string Endpoint, int HttpStatus, int Attempt);

public sealed class DropboxClient(
    HttpClient http, IAccessTokens tokens, SecretRedactor redactor,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int SessionChunkSize = 8 * 1024 * 1024;
    public IAccessTokens AccessTokens => tokens;
    public const long SingleUploadLimit = 150_000_000;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    public List<RequestObservation> Observations { get; } = [];

    public Task<JsonElement> GetCurrentAccountAsync(CancellationToken ct) => RpcAsync("users/get_current_account", null, ct);
    public Task<JsonElement> GetSpaceUsageAsync(CancellationToken ct) => RpcAsync("users/get_space_usage", null, ct);
    public async Task<ListFolderPage> ListFolderAsync(string path, int limit, CancellationToken ct) =>
        ParseListPage(await RpcAsync("files/list_folder", ListArguments(path, limit), ct));

    public async Task<ListFolderPage> ContinueFolderAsync(string cursor, CancellationToken ct) =>
        ParseListPage(await RpcAsync("files/list_folder/continue", new { cursor }, ct));

    private static object ListArguments(string path, int limit)
    {
        if (limit is < 1 or > 2000) throw new UserError("ページ上限は 1〜2000 を指定してください。");
        return new { path = path == "/" ? "" : path, recursive = true, limit };
    }

    private static ListFolderPage ParseListPage(JsonElement json) => new(
        json.GetProperty("entries").EnumerateArray().Select(CloudEntry.Parse).ToArray(),
        json.GetProperty("cursor").GetString()!, json.GetProperty("has_more").GetBoolean());
    public async Task RevokeAsync(CancellationToken ct) => _ = await RpcAsync("auth/token/revoke", null, ct);

    public async Task<RemoteFile> GetMetadataAsync(string path, CancellationToken ct)
        => RemoteFile.Parse(await RpcAsync("files/get_metadata", new { path }, ct));

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        // A repeated delete after an ambiguous response is already successful when not_found.
        try { _ = await RpcAsync("files/delete_v2", new { path }, ct); }
        catch (DropboxApiException error) when (error.HttpStatus == 409 && error.ErrorSummary.StartsWith("path_lookup/not_found", StringComparison.Ordinal)) { }
    }

    public async Task<RemoteFile> UploadFileAsync(string remotePath, string localPath, CancellationToken ct)
    {
        if (new FileInfo(localPath).Length >= SingleUploadLimit)
            throw new UserError("150MB 以上はアップロードセッションを使用してください。");
        using var response = await SendAsync("files/upload", () => ContentRequest("files/upload", Commit(remotePath),
            new StreamContent(File.OpenRead(localPath))), ct);
        return await ReadMetadataAsync(response, ct);
    }

    public async Task<RemoteFile> UploadBytesAsync(string remotePath, byte[] data, CancellationToken ct)
    {
        if (data.LongLength >= SingleUploadLimit) throw new UserError("150MB 以上はアップロードセッションを使用してください。");
        using var response = await SendAsync("files/upload", () => ContentRequest("files/upload", Commit(remotePath), new ByteArrayContent(data)), ct);
        return await ReadMetadataAsync(response, ct);
    }

    public async Task<RemoteFile> UploadSessionAsync(string remotePath, string localPath, CancellationToken ct)
    {
        using var start = await SendAsync("files/upload_session/start", () => ContentRequest("files/upload_session/start",
            new { close = false }, new ByteArrayContent([])), ct);
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
        string sessionId = startJson.RootElement.GetProperty("session_id").GetString()!;
        redactor.Register(sessionId);
        await using var file = File.OpenRead(localPath);
        var buffer = new byte[SessionChunkSize];
        long offset = 0;
        while (true)
        {
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await file.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            var cursor = new { session_id = sessionId, offset };
            if (count == 0)
            {
                // Append every chunk first, then finish with an empty body and final offset.
                // Do not blindly replay finish after 5xx: the commit may already be complete.
                using var finish = await SendAsync("files/upload_session/finish", () => ContentRequest("files/upload_session/finish",
                    new { cursor, commit = Commit(remotePath) }, new ByteArrayContent([])), ct, retryServerErrors: false);
                return await ReadMetadataAsync(finish, ct);
            }
            try
            {
                using var append = await SendAsync("files/upload_session/append_v2", () => ContentRequest("files/upload_session/append_v2",
                    new { cursor, close = false }, new ByteArrayContent(buffer, 0, count)), ct);
            }
            catch (DropboxApiException error) when (error.HttpStatus == 409 &&
                error.ErrorSummary == "incorrect_offset" && error.CorrectOffset == offset + count)
            {
                // The identical chunk was accepted, but its previous response was lost.
            }
            offset += count;
        }
    }

    public async Task<RemoteFile> DownloadAsync(string remotePath, Stream destination, CancellationToken ct)
    {
        using var response = await SendAsync("files/download", () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            request.Headers.Add("Dropbox-API-Arg", JsonFormat.ApiArgument(new { path = remotePath }));
            return request;
        }, ct);
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var headers))
            throw new UserError("ダウンロードのメタデータがありません。");
        using var metadata = JsonDocument.Parse(headers.Single());
        var result = RemoteFile.Parse(metadata.RootElement, (int)response.StatusCode);
        await response.Content.CopyToAsync(destination, ct);
        return result;
    }

    public async Task<(byte[] Bytes, RemoteFile Metadata)> DownloadSmallAsync(string path, CancellationToken ct)
    {
        var metadata = await GetMetadataAsync(path, ct);
        if (metadata.Size > 65536) throw new UserError("小さな JSON を想定したパスのファイルが大きすぎます。上書きは行いません。");
        using var memory = new MemoryStream();
        using var limited = new LimitedWriteStream(memory, 65536);
        var downloaded = await DownloadAsync(path, limited, ct);
        return (memory.ToArray(), downloaded);
    }

    private static object Commit(string path) => new { path, mode = "add", autorename = false, strict_conflict = false, mute = true };

    private static HttpRequestMessage ContentRequest(string endpoint, object argument, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/" + endpoint) { Content = content };
        request.Headers.Add("Dropbox-API-Arg", JsonFormat.ApiArgument(argument));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private async Task<JsonElement> RpcAsync(string endpoint, object? arguments, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, () => new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/" + endpoint)
        {
            Content = arguments is null ? null : new StringContent(JsonSerializer.Serialize(arguments), Encoding.UTF8, "application/json")
        }, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
        return json.RootElement.Clone();
    }

    private static async Task<RemoteFile> ReadMetadataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return RemoteFile.Parse(document.RootElement, (int)response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, Func<HttpRequestMessage> create,
        CancellationToken ct, bool retryServerErrors = true)
    {
        int transientAttempts = 0;
        bool refreshed = false;
        int attempts = 0;
        while (true)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(false, ct));
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) when (retryServerErrors && transientAttempts < 3)
            {
                await wait(TimeSpan.FromSeconds(1 << transientAttempts++), ct);
                continue;
            }
            Observations.Add(new(endpoint, (int)response.StatusCode, ++attempts));
            if (response.IsSuccessStatusCode) return response;
            string body = await response.Content.ReadAsStringAsync(ct);
            var error = DropboxApiException.Parse((int)response.StatusCode, body, redactor);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                await tokens.GetAsync(true, ct);
                refreshed = true;
                continue;
            }
            bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                error.ErrorSummary.EndsWith("too_many_write_operations", StringComparison.Ordinal);
            if ((rateLimited || (retryServerErrors && (int)response.StatusCode >= 500)) && transientAttempts < 3)
            {
                var retryAfter = response.Headers.RetryAfter;
                TimeSpan interval = retryAfter?.Delta ??
                    (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << transientAttempts));
                if (interval < TimeSpan.FromMilliseconds(250)) interval = TimeSpan.FromMilliseconds(250);
                transientAttempts++;
                response.Dispose();
                await wait(interval, ct);
                continue;
            }
            response.Dispose();
            throw error;
        }
    }

    private sealed class LimitedWriteStream(Stream inner, long limit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count); inner.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Check(buffer.Length); await inner.WriteAsync(buffer, ct);
        }
        private void Check(int count)
        {
            if (written + count > limit) throw new UserError("JSON の取得サイズが上限を超えました。");
            written += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
