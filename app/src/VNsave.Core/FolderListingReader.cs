namespace VNsave.Core;

public sealed record ListFolderPage(IReadOnlyList<CloudEntry> Entries, string Cursor, bool HasMore);

public sealed class ListingBatch
{
    // A reset discards entries from the incomplete traversal.
    public List<CloudEntry> Entries { get; } = [];
    public int PageCount { get; internal set; }
    public int ResetCount { get; internal set; }
    public bool Rebuilt => ResetCount != 0;
}

public sealed class FolderListingReader(DropboxClient client, CloudIndex index, string root, int pageLimit = 2000,
    int maxPages = 10_000, int maxResets = 3)
{
    public string? Cursor { get; private set; }

    public Task<ListingBatch> ReadAllAsync(CancellationToken ct)
    {
        index.Clear();
        Cursor = null;
        return ReadAsync(true, ct);
    }

    public Task<ListingBatch> ContinueAsync(CancellationToken ct)
    {
        if (Cursor is null) throw new UserError("先に一覧の全件取得が必要です。");
        return ReadAsync(false, ct);
    }

    private async Task<ListingBatch> ReadAsync(bool start, CancellationToken ct)
    {
        if (maxPages < 1 || maxResets < 0) throw new UserError("一覧の取得上限が不正です。");
        var batch = new ListingBatch();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (batch.PageCount >= maxPages) throw new UserError("一覧のページ数が上限を超えました。");
            string? requestedCursor = Cursor;
            ListFolderPage page;
            try
            {
                page = start ? await client.ListFolderAsync(root, pageLimit, ct)
                    : await client.ContinueFolderAsync(Cursor!, ct);
            }
            catch (DropboxApiException error) when (!start && error.HttpStatus == 409 && error.ErrorSummary == "reset")
            {
                if (++batch.ResetCount > maxResets) throw new UserError("カーソルの reset が繰り返されたため中止します。");
                index.Clear();
                batch.Entries.Clear();
                Cursor = null;
                start = true;
                continue;
            }
            if (string.IsNullOrEmpty(page.Cursor)) throw new UserError("一覧のカーソルが空です。");
            // An idle, final empty response is allowed to retain the cursor.
            if (!start && page.Cursor == requestedCursor && (page.HasMore || page.Entries.Count != 0))
                throw new UserError("一覧のカーソルが進まないため中止します。");
            batch.PageCount++;
            batch.Entries.AddRange(page.Entries);
            index.Apply(page.Entries);
            Cursor = page.Cursor;
            start = false;
            if (!page.HasMore) return batch;
        }
    }
}
