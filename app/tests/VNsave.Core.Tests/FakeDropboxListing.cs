using System.Text.Json;

namespace VNsave.Core.Tests;

internal sealed partial class FakeDropboxHandler
{
    private sealed record FakeCursor(string Root, int Limit, CloudEntry[] Remaining, int HistoryOffset);
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CloudEntry> history = [];
    private readonly Dictionary<string, FakeCursor> cursors = [];
    private int cursorNumber;
    public int PageExtraEntries { get; init; }
    public bool RepeatCursorOnContinue { get; init; }
    public bool DuplicateFirstFile { get; init; }
    public bool IgnoreAllIncrementalChanges { get; init; }
    public int FailUploadNumber { get; init; }
    public bool FailDeletes { get; init; }
    public int ResetsRemaining { get; set; }
    public List<int> RequestedPageLimits { get; } = [];
    public List<string> RequestedListPaths { get; } = [];

    private void TrackFileWrite(string path, Stored file)
    {
        string parent = path[..path.LastIndexOf('/')];
        var missing = new Stack<string>();
        while (parent.Length > 0)
        {
            if (!folders.Contains(parent)) missing.Push(parent);
            parent = parent[..parent.LastIndexOf('/')];
        }
        foreach (string folder in missing)
            if (folders.Add(folder)) history.Add(new("folder", folder.ToLowerInvariant()));
        history.Add(ToEntry(path, file));
    }

    private void TrackDeletion(string path)
    {
        history.Add(new("deleted", path.ToLowerInvariant()));
        folders.RemoveWhere(p => CloudIndex.AtOrBelow(p, path));
    }

    private HttpResponseMessage HandleListing(string endpoint, JsonElement argument)
    {
        if (endpoint == "files/list_folder")
        {
            string root = argument.GetProperty("path").GetString()!;
            Assert.NotEqual("/", root);
            Assert.True(argument.GetProperty("recursive").GetBoolean());
            int limit = argument.GetProperty("limit").GetInt32();
            RequestedPageLimits.Add(limit);
            RequestedListPaths.Add(root);
            var snapshot = Current(root).OrderBy(e => e.PathLower, StringComparer.Ordinal).ToList();
            if (DuplicateFirstFile && snapshot.FirstOrDefault(e => e.Tag == "file") is { } duplicate) snapshot.Add(duplicate);
            return Page(new(root, limit, snapshot.ToArray(), history.Count), null);
        }
        if (endpoint != "files/list_folder/continue") throw new InvalidOperationException("Unknown listing endpoint");
        string cursor = argument.GetProperty("cursor").GetString()!;
        if (ResetsRemaining > 0)
        {
            ResetsRemaining--;
            return StubHandler.Error("reset/...");
        }
        if (!cursors.TryGetValue(cursor, out var state)) return StubHandler.Error("reset/...");
        if (state.Remaining.Length > 0) return Page(state, cursor);
        if (IgnoreAllIncrementalChanges) return Response([], cursor, false);
        var changes = history.Select((entry, position) => (entry, position)).Skip(state.HistoryOffset)
            .Where(item => Under(item.entry.PathLower, state.Root)).ToArray();
        int count = Math.Min(changes.Length, state.Limit + PageExtraEntries);
        var taken = changes.Take(count).Select(item => item.entry).ToArray();
        bool more = count < changes.Length;
        int offset = more ? changes[count - 1].position + 1 : history.Count;
        string next = taken.Length == 0 && offset == state.HistoryOffset ? cursor :
            RepeatCursorOnContinue ? cursor : StoreCursor(state with { HistoryOffset = offset });
        return Response(taken, next, more);
    }

    private HttpResponseMessage Page(FakeCursor state, string? requested)
    {
        int count = Math.Min(state.Remaining.Length, state.Limit + PageExtraEntries);
        var taken = state.Remaining.Take(count).ToArray();
        var nextState = state with { Remaining = state.Remaining.Skip(count).ToArray() };
        string cursor = RepeatCursorOnContinue && requested is not null ? requested : StoreCursor(nextState);
        return Response(taken, cursor, nextState.Remaining.Length > 0);
    }

    private string StoreCursor(FakeCursor state)
    {
        string cursor = "fake-cursor-" + ++cursorNumber;
        cursors[cursor] = state;
        return cursor;
    }

    private IEnumerable<CloudEntry> Current(string root) => folders.Where(p => Under(p, root))
        .Select(p => new CloudEntry("folder", p.ToLowerInvariant())).Concat(Files.Where(kv => Under(kv.Key, root)).Select(kv => ToEntry(kv.Key, kv.Value)));

    private static bool Under(string path, string root) => root == "" || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static CloudEntry ToEntry(string path, Stored file) => new("file", path.ToLowerInvariant(), file.Bytes.Length,
        HashAndPkceTests.ReferenceHash(file.Bytes), file.Rev, file.Name);

    private static HttpResponseMessage Response(CloudEntry[] entries, string cursor, bool more) => StubHandler.Json(new
    {
        entries = entries.Select(entry => new Dictionary<string, object?>
        {
            [".tag"] = entry.Tag, ["path_lower"] = entry.PathLower, ["name"] = entry.Name ?? entry.PathLower.Split('/')[^1],
            ["size"] = entry.Size, ["content_hash"] = entry.ContentHash, ["rev"] = entry.Rev
        }).ToArray(), cursor, has_more = more
    });
}
