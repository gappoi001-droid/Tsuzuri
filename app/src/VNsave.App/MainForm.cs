using System.Diagnostics;
using VNsave.Core;

namespace VNsave.App;

public sealed partial class MainForm : Form
{
    private sealed record Row(CloudGame Cloud, LocalGame? Local, string Status, PlayTotal? Play);
    private readonly AppRuntime runtime;
    private readonly CancellationTokenSource closing = new();
    // Read from this assembly rather than Application.ProductVersion, which reports whichever executable hosts the form.
    private static readonly string AppVersion = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
        typeof(MainForm).Assembly)?.InformationalVersion ?? "").Split('+')[0];
    private readonly LibraryGrid grid = new() { ReadOnly = true, AccessibleName = "ゲームライブラリ" };
    private readonly DropDownBox filter = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink, AccessibleName = "表示するゲーム" };
    private readonly Label count = UiTheme.Label("ライブラリ", 9, UiTheme.Muted);
    private readonly Label account = UiTheme.Label("Dropbox · 未接続", 10, UiTheme.Muted);
    private readonly Label result = UiTheme.Label("ライブラリを読み込んでいます…", 10, UiTheme.Muted);
    private readonly Label activity = UiTheme.Label("準備中", 9, UiTheme.Accent, true);
    private readonly Label title = UiTheme.Label("ゲームを追加しましょう", 19, bold: true);
    private readonly Label engine = UiTheme.Label("", 9, UiTheme.Muted);
    private readonly Label badge = UiTheme.Label("未選択", 9, bold: true);
    private readonly Label hint = UiTheme.Label("", 10, UiTheme.Muted);
    private readonly Label playTime = UiTheme.Label("—", 13, bold: true);
    private readonly Label lastPlayed = UiTheme.Label("—", 11, bold: true);
    private readonly Label lastSync = UiTheme.Label("—", 11, bold: true);
    private readonly Label paths = UiTheme.Label("", 9.5f, UiTheme.Muted);
    private readonly Panel detailScroll = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, AutoScroll = true, Margin = new(0) };
    private readonly GameGlyph glyph = new();
    private readonly Button launch = UiTheme.Button("ゲームを起動", true);
    private readonly Button add = UiTheme.Button("＋ ゲームを追加", true);
    private readonly Button connection = UiTheme.Button("接続する");
    private readonly Button saveActions = UiTheme.Button("セーブの操作  ▾");
    private readonly Button more = UiTheme.Button("その他  ▾");
    private readonly Button refresh = UiTheme.Button("更新");
    private readonly Button errorDetails = UiTheme.Button("詳細");
    private readonly Button theme = UiTheme.Button("");
    private readonly ContextMenuStrip saveMenu = UiTheme.Menu();
    private readonly ContextMenuStrip moreMenu = UiTheme.Menu();
    private readonly ContextMenuStrip accountMenu = UiTheme.Menu();
    private readonly ToolStripMenuItem upload = new("この PC のセーブをアップロード");
    private readonly ToolStripMenuItem download = new("クラウドの最新セーブをダウンロード");
    private readonly ToolStripMenuItem backups = new("Dropbox のバックアップを開く");
    private readonly ToolStripMenuItem unregister = new("この PC の登録を解除");
    private readonly ToolStripMenuItem openFolder = new("ゲームフォルダを開く");
    private readonly ToolStripMenuItem openSaves = new("セーブデータのフォルダを開く");
    private readonly ToolTip tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 3, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false };
    private readonly Panel empty = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Visible = false };
    private readonly Label emptyTitle = UiTheme.Label("まだゲームがありません", 15, bold: true);
    private readonly Label emptyHint = UiTheme.Label("「ゲームを追加」から登録できます。", 10, UiTheme.Muted);
    private readonly Dictionary<string, string> states = [];
    private readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Row> rows = [];
    private bool busy, initialized, rebuilding, operationCanceled;
    private string? lastError, preferredSelection;
    private Func<Color> accountDot = () => UiTheme.Muted;
    private Row? Selected => grid.CurrentRow?.Tag as Row;

    public MainForm(AppRuntime runtime)
    {
        this.runtime = runtime;
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.Font(); ForeColor = UiTheme.Ink; BackColor = UiTheme.Background; UiTheme.Window(this);
        Text = "Tsuzuri · " + AppVersion; Icon = UiTheme.AppIcon; ClientSize = new(1160, 760); MinimumSize = new(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout(); ConfigureActions();
        filter.SelectedIndexChanged += (_, _) => FilterRows();
        grid.CurrentCellChanged += (_, _) => { if (!rebuilding) { preferredSelection = Selected?.Cloud.GameId; UpdateButtons(); } };
        grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && launch.Enabled) await PrimaryAsync(); };
        grid.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; if (launch.Enabled) await PrimaryAsync(); } };
        grid.CellPainting += PaintCell;
        Shown += async (_, _) =>
        {
            await RunAsync(async () =>
            {
                try { await Task.Run(() => runtime.InitializeAsync(closing.Token)); }
                finally { initialized = true; }
            }, "ライブラリを読み込みました。", "ライブラリを読み込んでいます…",
                offlineMessage: "オフラインです。この PC のセーブで遊べます。つながったら自動で同期します。");
            if (!IsDisposed) { grid.Focus(); detailScroll.AutoScrollPosition = Point.Empty; }
        };
        FormClosing += (_, e) =>
        {
            if (busy && MessageBox.Show(this, "現在の処理を中断して Tsuzuri を閉じますか？\nゲームは終了しませんが、終了後の自動アップロードも停止します。", "処理中です", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            { e.Cancel = true; return; }
            closing.Cancel();
        };
        UpdateButtons();
    }

    private void ConfigureActions()
    {
        add.Click += async (_, _) => await RunAsync(AddGameAsync, "ゲームを登録しました。", "ゲームを登録しています…");
        launch.Click += async (_, _) => await PrimaryAsync();
        refresh.Click += async (_, _) => await RefreshCloudAsync();
        connection.Click += async (_, _) =>
        {
            // Signed in but offline still gets the menu: signing in again would not help, and its refresh retries the connection.
            if (runtime.AccountName is not null || runtime.ConnectionFailed) accountMenu.Show(connection, new Point(0, connection.Height));
            else await RunAsync(() => runtime.ConnectAsync(closing.Token), "Dropbox に接続しました。", "ブラウザで Dropbox の接続を完了してください…");
        };
        accountMenu.Items.Add("接続とライブラリを更新", null, async (_, _) => await RefreshCloudAsync());
        accountMenu.Items.Add(new ToolStripSeparator());
        accountMenu.Items.Add("Dropbox の接続を解除…", null, async (_, _) =>
        {
            if (MessageBox.Show(this, "Dropbox との接続を解除しますか？", "接続を解除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await RunAsync(() => runtime.DisconnectAsync(closing.Token), "Dropbox の接続を解除しました。", "接続設定を変更しています…");
        });
        upload.Click += async (_, _) => await RunAsync(async () =>
        {
            var game = RequireLocal(); await Task.Run(() => runtime.Service().UploadAsync(game, closing.Token));
        }, "この PC のセーブをアップロードしました。", "セーブをアップロードしています…", true);
        download.Click += async (_, _) =>
        {
            if (Selected is not { } row || MessageBox.Show(this, row.Cloud.DisplayName + " の最新セーブをダウンロードしますか？\n現在のセーブは先にバックアップされます。", "最新セーブをダウンロード", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await RunAsync(async () => { var game = RequireLocal(); await Task.Run(() => runtime.Service().DownloadAsync(game, closing.Token)); }, "クラウドの最新セーブを適用しました。", "セーブをバックアップしてダウンロードしています…", true);
        };
        backups.Click += async (_, _) => await RunAsync(() =>
        {
            var row = Selected ?? throw new UserError("ゲームを選んでください。");
            MessageBox.Show(this, row.Cloud.DisplayName + " のバックアップは、Dropbox の「アプリ」フォルダ内の Tsuzuri 用フォルダにある\n" +
                "vnsave/games/" + row.Cloud.GameId + "/backups にあります。", "バックアップの場所");
            Process.Start(new ProcessStartInfo("https://www.dropbox.com/home/Apps") { UseShellExecute = true }); return Task.CompletedTask;
        }, "Dropbox のバックアップ用フォルダを開きました。", "バックアップを開いています…");
        unregister.ForeColor = UiTheme.Red;
        unregister.Click += async (_, _) => await RunAsync(async () =>
        {
            var game = Selected?.Local ?? throw new UserError("この PC で設定済みのゲームを選んでください。");
            if (runtime.Process.IsRunning(game.ExecutablePath)) throw new UserError("ゲームの起動中は登録を解除できません。");
            if (MessageBox.Show(this, game.Definition.DisplayName + " をこの PC の登録から外しますか？\nセーブ・クラウドの履歴・バックアップは残ります。", "この PC の登録を解除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { operationCanceled = true; return; }
            runtime.Repository.Library.Games.Remove(game);
            try { await runtime.Repository.SaveAsync(closing.Token); } catch { runtime.Repository.Library.Games.Add(game); throw; }
        }, "この PC の登録を解除しました。", "ゲームの登録を変更しています…");
        saveMenu.Items.AddRange([upload, download, new ToolStripSeparator(), backups]);
        openFolder.Click += (_, _) =>
        {
            if (Selected?.Local is not { } game || Path.GetDirectoryName(game.ExecutablePath) is not { } folder || !Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        };
        // One location opens directly; several are listed as a submenu, rebuilt on each opening for the selected game and theme.
        openSaves.Click += (_, _) => { if (Selected?.Local?.Locations is [var only]) OpenSaveFolder(only); };
        moreMenu.Opening += (_, _) =>
        {
            openSaves.DropDownItems.Clear();
            if (Selected?.Local?.Locations is not { Length: > 1 } locations) return;
            openSaves.DropDown.BackColor = UiTheme.Surface;
            foreach (var location in locations)
                openSaves.DropDownItems.Add(new ToolStripMenuItem(LocationText(location), null, (_, _) => OpenSaveFolder(location)) { ForeColor = UiTheme.Ink });
        };
        moreMenu.Items.Add(openFolder); moreMenu.Items.Add(openSaves);
        moreMenu.Items.Add("登録情報を見る", null, (_, _) =>
        {
            if (Selected is not { } row) return;
            MessageBox.Show(this, row.Cloud.DisplayName + "\n\n実行ファイル\n" + (row.Local?.ExecutablePath ?? "この PC では未設定") +
                "\n\nセーブの保存先\n" + (row.Local is null ? "この PC で設定すると表示されます。" : string.Join("\n", row.Local.Locations.Select(LocationText))), "登録情報");
        });
        moreMenu.Items.Add(new ToolStripSeparator()); moreMenu.Items.Add(unregister);
        saveActions.Click += (_, _) => saveMenu.Show(saveActions, new Point(0, saveActions.Height));
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));
        errorDetails.Click += (_, _) => { if (lastError is not null) MessageBox.Show(this, lastError, "処理の詳細", MessageBoxButtons.OK, MessageBoxIcon.Information); };
        tips.SetToolTip(refresh, "Dropbox の一覧とプレイ時間を更新 · F5");
        tips.SetToolTip(launch, "一覧の「▶ 起動」、Enter、ダブルクリックでも起動できます。");
        // The button drawn in each row starts that row's game directly, without selecting it first.
        grid.CellClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["action"]!.Index || grid.Rows[e.RowIndex].Tag is not Row row || !RowAction(row).Enabled) return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells["name"];
            if (launch.Enabled) await PrimaryAsync();
        };
        grid.CellMouseMove += (_, e) => grid.Cursor = e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["action"]!.Index &&
            grid.Rows[e.RowIndex].Tag is Row row && RowAction(row).Enabled ? Cursors.Hand : Cursors.Default;
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;
        tips.SetToolTip(saveActions, "手動アップロード・ダウンロード・バックアップ");
        theme.Click += (_, _) =>
        {
            var previous = UiTheme.Current; UiTheme.Toggle(runtime.Repository.Root);
            UiTheme.Restyle(this, previous); foreach (var menu in new[] { saveMenu, moreMenu, accountMenu }) UiTheme.Restyle(menu, previous);
            ShowThemeChoice(); UpdateButtons();
        };
        ShowThemeChoice();
    }
    private void ShowThemeChoice()
    {
        theme.Text = UiTheme.Dark ? "☀" : "☾"; theme.AccessibleName = UiTheme.Dark ? "ライトモードにする" : "ダークモードにする";
        tips.SetToolTip(theme, theme.AccessibleName);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { if (refresh.Enabled) refresh.PerformClick(); return true; }
        if (keyData == (Keys.Control | Keys.N)) { if (add.Enabled) add.PerformClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private async Task RefreshCloudAsync() => await RunAsync(async () =>
    {
        await Task.Run(async () => { await runtime.CheckConnectionAsync(closing.Token); await runtime.Catalog.RefreshAsync(closing.Token); });
    }, "ライブラリとプレイ時間を更新しました。", "Dropbox のライブラリを確認しています…");
    private async Task PrimaryAsync()
    {
        if (Selected is not { } row) await RunAsync(AddGameAsync, "ゲームを登録しました。", "ゲームを登録しています…");
        else if (row.Local is null) await RunAsync(() => SetupAsync(row.Cloud), "この PC で設定しました。", "この PC のゲームを設定しています…");
        // Offline before launch is a choice in LaunchAsync; offline after the game exits leaves the saves pending and only says so.
        else await RunAsync(LaunchAsync, "プレイ記録とセーブを同期しました。", "起動前に最新のセーブを確認しています…", true,
            "オフラインのため、セーブは未送信としてこの PC に残しました。つながったら自動で送ります。");
    }
    // offlineMessage makes an unreachable Dropbox a quiet footer line instead of an error dialog.
    // It is used where offline is expected (start-up, the sync after a game exits), not for actions the user asked for.
    private async Task RunAsync(Func<Task> action, string message, string processing, bool gameOperation = false, string? offlineMessage = null)
    {
        if (busy) return;
        busy = true; lastError = null; operationCanceled = false;
        var selected = gameOperation ? Selected?.Local : null;
        if (selected is not null) states[selected.Definition.GameId] = "同期中";
        SetActivity("処理中", processing); UpdateButtons();
        try
        {
            RefreshRows();
            await action();
            if (selected is not null && !operationCanceled) runtime.Errors.Remove(selected.Definition.GameId);
            SetActivity(operationCanceled ? "待機中" : "完了", operationCanceled ? "操作をキャンセルしました。" : message);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error) when (offlineMessage is not null && IsOffline(error))
        {
            runtime.MarkConnectionFailed(); SetActivity("オフライン", offlineMessage);
            try { runtime.Log.Write(runtime.Redactor.FormatError(error)); } catch (IOException) { }
        }
        catch (Exception error)
        {
            string messageText = runtime.Redactor.FormatError(error);
            if (selected is not null) runtime.Errors[selected.Definition.GameId] = messageText;
            lastError = messageText; SetActivity("確認が必要です", messageText, true);
            try { runtime.Log.Write(messageText); } catch (IOException) { }
            if (!IsDisposed && !closing.IsCancellationRequested) MessageBox.Show(this, messageText, "処理を完了できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (selected is not null) states.Remove(selected.Definition.GameId);
            busy = false;
            if (!IsDisposed) { progress.Visible = false; progress.MarqueeAnimationSpeed = 0; RefreshRows(); }
        }
    }
    private void SetActivity(string stage, string message, bool error = false)
    {
        if (IsDisposed) return;
        activity.Text = stage; activity.ForeColor = error ? UiTheme.Red : UiTheme.Accent;
        result.Text = message; result.ForeColor = error ? UiTheme.Red : UiTheme.Muted;
        tips.SetToolTip(result, message); errorDetails.Visible = error;
        progress.Visible = busy && stage != "プレイ中"; progress.MarqueeAnimationSpeed = progress.Visible ? 30 : 0;
    }
    private LocalGame RequireLocal()
    {
        var game = Selected?.Local ?? throw new UserError("この PC で設定済みのゲームを選んでください。");
        runtime.Service().EnsureIdle(game); return game;
    }
    private string StatusFor(CloudGame cloud, LocalGame? game) => game is null ? "この PC で未設定" :
        states.TryGetValue(cloud.GameId, out string? state) ? state : !File.Exists(game.ExecutablePath) ? "実行ファイル不明" :
        runtime.Process.IsRunning(game.ExecutablePath) ? "プレイ中" : runtime.Snapshots?.HasPending(cloud.GameId) == true ? "未送信あり" :
        runtime.Errors.ContainsKey(cloud.GameId) ? "エラー" : "準備完了";
    private void RefreshRows()
    {
        if (IsDisposed) return;
        var local = runtime.Repository.Library.Games;
        var totals = PlayTimeLog.Totals((runtime.Catalog?.PlayTimeFiles ?? []).Concat(runtime.PlayTime?.Pending(local.Select(g => g.Definition.GameId)) ?? []));
        rows.Clear();
        foreach (var cloud in (runtime.Catalog?.Games ?? []).Concat(local.Select(g => g.Definition)).DistinctBy(g => g.GameId))
        {
            var game = local.SingleOrDefault(g => g.Definition.GameId == cloud.GameId);
            totals.TryGetValue(cloud.GameId, out var play); rows.Add(new(cloud, game, StatusFor(cloud, game), play));
        }
        FilterRows();
        bool signedIn = runtime.SignedIn, failed = signedIn && runtime.ConnectionFailed, connected = !failed && runtime.AccountName is not null;
        account.Text = connected ? "Dropbox: " + runtime.AccountName : failed ? "Dropbox · 接続できません" : signedIn ? "Dropbox · 確認中…" : "Dropbox · 未接続";
        account.ForeColor = connected ? UiTheme.Ink : failed ? UiTheme.Red : UiTheme.Muted;
        // Read at paint time so the dot follows a theme switch.
        accountDot = connected ? () => UiTheme.Green : failed ? () => UiTheme.Red : () => UiTheme.Muted;
        account.Invalidate();
        tips.SetToolTip(account, connected ? "Dropbox: " + runtime.AccountName : failed ? "Dropbox に接続できませんでした。「接続設定」の「接続とライブラリを更新」で再確認できます。" : "接続してセーブを共有します。");
        connection.Text = connected || failed ? "接続設定  ▾" : "接続する";
    }
    private void FilterRows()
    {
        if (IsDisposed) return;
        string? selected = preferredSelection ?? Selected?.Cloud.GameId;
        var visible = rows
            .Where(r => states.ContainsKey(r.Cloud.GameId) || (filter.SelectedIndex switch { 1 => r.Local is not null, 2 => r.Status is "この PC で未設定" or "実行ファイル不明" or "未送信あり" or "エラー", _ => true }))
            .OrderByDescending(r => r.Play?.LastPlayedUtc).ThenBy(r => r.Cloud.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        rebuilding = true;
        try
        {
            grid.Rows.Clear();
            foreach (var row in visible)
            {
                int index = grid.Rows.Add(IconFor(row.Local) ?? (object)DBNull.Value, row.Cloud.DisplayName, row.Status, "");
                var item = grid.Rows[index]; item.Tag = row;
                item.Cells["name"].ToolTipText = row.Cloud.DisplayName + "\n" + PlaySummary(row);
                item.Cells["state"].ToolTipText = StatusHint(row);
                item.Cells["action"].ToolTipText = row.Local is null ? "この PC の実行ファイルを選んで設定します。" : "起動前とゲーム終了後に、自動で同期します。";
                if (row.Cloud.GameId == selected) grid.CurrentCell = item.Cells["name"];
            }
        }
        finally { rebuilding = false; }
        bool noGames = rows.Count == 0;
        empty.Visible = visible.Length == 0; if (empty.Visible) empty.BringToFront();
        emptyTitle.Text = noGames ? "まだゲームがありません" : "該当するゲームがありません";
        emptyHint.Text = noGames ? "「ゲームを追加」から登録できます。" : "表示条件を変えてください。";
        count.Text = visible.Length == rows.Count ? $"{rows.Count} ゲーム  ·  最近プレイした順" : $"{visible.Length} / {rows.Count} ゲーム  ·  最近プレイした順";
        UpdateButtons();
    }
    private (string Text, bool Enabled) RowAction(Row row)
    {
        bool available = initialized && !busy;
        if (row.Local is null) return ("設定する →", available);
        if (row.Status is "プレイ中" or "同期中") return (row.Status, false);
        bool idle = File.Exists(row.Local.ExecutablePath) && !runtime.Process.IsRunning(row.Local.ExecutablePath);
        return ("▶ 起動", available && idle);
    }
    private static string PlaySummary(Row row) => row.Play is null ? "プレイ記録なし" : PlayTimeLog.Format(row.Play.Total) + "  ·  " + row.Play.LastPlayedUtc.ToLocalTime().ToString("yyyy/MM/dd") + " プレイ";
    private string StatusHint(Row row) => row.Status switch
    {
        "この PC で未設定" => "この PC の実行ファイルを指定すると、続きから遊べます。",
        "実行ファイル不明" => "実行ファイルが見つかりません。「その他」で登録情報を確認してください。",
        "未送信あり" => "アップロード待ちのセーブがあります。次の同期時に再送します。",
        "エラー" => runtime.Errors.GetValueOrDefault(row.Cloud.GameId, "前回の処理を完了できませんでした。"),
        "プレイ中" => busy ? "ゲームの終了を待っています。終了後にセーブをアップロードします。" : "ゲームが起動しています。終了後に同期操作ができます。",
        "同期中" => "セーブの同期を処理しています。完了までお待ちください。",
        _ => runtime.ConnectionFailed ? "Dropbox に接続できません。この PC のセーブで遊べます。" :
            runtime.AccountName is null ? "Dropbox に未接続です。接続すると最新のセーブを確認できます。" : "起動前とゲーム終了後に、自動でセーブを同期します。"
    };
    private void UpdateButtons()
    {
        if (IsDisposed) return;
        var row = Selected;
        bool idle = row?.Local is { } game && File.Exists(game.ExecutablePath) && !runtime.Process.IsRunning(game.ExecutablePath);
        bool available = initialized && !busy;
        title.Text = row?.Cloud.DisplayName ?? (rows.Count == 0 ? "ゲームを追加しましょう" : "ゲームを選んでください");
        engine.Text = row is null ? "あなたのセーブライブラリ" : (row.Cloud.Engine == "不明" ? "保存先を個別設定" : row.Cloud.Engine);
        glyph.GameName = row?.Cloud.DisplayName ?? "T"; glyph.GameImage = row is null ? UiTheme.AppImage : IconFor(row.Local); glyph.Invalidate();
        badge.Text = row?.Status ?? "未選択"; var colors = UiTheme.StatusColors(badge.Text); badge.ForeColor = colors.Fore; badge.BackColor = colors.Back;
        hint.Text = row is null ? rows.Count == 0 ? "「ゲームを追加」で実行ファイルを選ぶと、保存先を検出します。" : "表示条件を変えて、ゲームを選んでください。" : StatusHint(row);
        tips.SetToolTip(hint, hint.Text);
        playTime.Text = row?.Play is { } play ? PlayTimeLog.Format(play.Total) : "—";
        lastPlayed.Text = row?.Play?.LastPlayedUtc.ToLocalTime().ToString("yyyy/MM/dd") ?? "—";
        lastSync.Text = row?.Local?.LastSyncUtc?.ToLocalTime().ToString("MM/dd HH:mm") ?? "まだ";
        tips.SetToolTip(lastSync, row?.Local?.LastSyncUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "まだ同期していません");
        // One line keeps the pane from scrolling; every path is in the tooltip.
        paths.Text = row?.Local is { } local ? LocationText(local.Locations[0]) + (local.Locations.Length > 1 ? $"（ほか {local.Locations.Length - 1} 件）" : "") : "この PC で設定すると表示されます。";
        tips.SetToolTip(paths, row?.Local is { } all ? string.Join(Environment.NewLine, all.Locations.Select(LocationText)) : "");
        launch.Text = busy && row?.Status == "プレイ中" ? "プレイ中" : busy && row?.Status == "同期中" ? "同期中…" :
            row is null ? rows.Count == 0 ? "＋ ゲームを追加" : "ゲームを選んでください" : row.Local is null ? "この PC で設定" : "▶  ゲームを起動";
        launch.Enabled = available && (row is null ? rows.Count == 0 : row.Local is null || idle);
        add.Enabled = connection.Enabled = available; refresh.Enabled = available && (runtime.AccountName is not null || runtime.ConnectionFailed);
        grid.Enabled = filter.Enabled = !busy;
        grid.InvalidateColumn(grid.Columns["action"]!.Index);
        saveActions.Enabled = more.Enabled = available && row is not null;
        openFolder.Enabled = row?.Local is { } installed && Directory.Exists(Path.GetDirectoryName(installed.ExecutablePath));
        openSaves.Enabled = row?.Local is not null;
        upload.Enabled = download.Enabled = available && idle; backups.Enabled = available && row is not null;
        unregister.Enabled = available && row?.Local is { } registered && !runtime.Process.IsRunning(registered.ExecutablePath);
        detailScroll.AutoScrollPosition = Point.Empty;
        // AutoScroll can change the visible region while a long title relays out the card.
        // Repaint transparent child labels after the content and selection have settled.
        detailScroll.Invalidate(true);
    }
    // A pattern location shares the game folder, so its first save is selected to show which files are synced.
    private void OpenSaveFolder(SaveLocation location)
    {
        if (!Directory.Exists(location.Path)) { MessageBox.Show(this, "保存先のフォルダはまだ作られていません。\n" + location.Path, "セーブデータのフォルダ"); return; }
        string? save = null;
        if (location.Pattern is not null)
            try { save = SaveFiles.Matching(location.Path, location.Pattern).FirstOrDefault(); }
            catch (Exception error) when (error is UserError or IOException or UnauthorizedAccessException) { }
        Process.Start(save is null ? new ProcessStartInfo(location.Path) { UseShellExecute = true } : new ProcessStartInfo("explorer.exe", "/select,\"" + save + "\""));
    }
    private static string LocationText(SaveLocation location) => location.Pattern is null ? location.Path : Path.Combine(location.Path, location.Pattern);
    private Image? IconFor(LocalGame? game)
    {
        if (game is null || !File.Exists(game.ExecutablePath)) return null;
        if (!icons.TryGetValue(game.ExecutablePath, out var image))
        {
            try
            {
                string? file = GameDetector.FindIconFile(game.ExecutablePath, game.Definition.DisplayName);
                using var icon = file is not null ? new Icon(file, new Size(64, 64)) : Icon.ExtractAssociatedIcon(game.ExecutablePath);
                image = icon?.ToBitmap();
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { image = null; }
            icons[game.ExecutablePath] = image;
        }
        return image;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing.Cancel(); tips.Dispose(); saveMenu.Dispose(); moreMenu.Dispose(); accountMenu.Dispose();
            foreach (var image in icons.Values) image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
