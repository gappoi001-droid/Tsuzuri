using VNsave.Core;

namespace VNsave.App;

public sealed partial class MainForm
{
    private async Task LaunchAsync()
    {
        var game = RequireLocal();
        while (true)
        {
            try
            {
                await Task.Run(async () => { await runtime.CheckConnectionAsync(closing.Token); await runtime.Service().BeforeLaunchAsync(game, closing.Token); });
                break;
            }
            catch (Exception error) when (IsConnectionError(error))
            {
                var answer = OfflineDialog(runtime.Redactor.FormatError(error));
                if (answer == DialogResult.Cancel) { operationCanceled = true; return; }
                if (answer == DialogResult.Ignore) break;
            }
        }
        states[game.Definition.GameId] = "プレイ中"; RefreshRows(); SetActivity("プレイ中", game.Definition.DisplayName + " の終了を待っています。");
        var started = DateTimeOffset.UtcNow;
        await Task.Run(() => runtime.Process.RunAsync(game, closing.Token));
        var played = DateTimeOffset.UtcNow - started;
        await Task.Run(() => runtime.PlayTime.RecordAsync(game.Definition.GameId, started, played, closing.Token));
        states[game.Definition.GameId] = "同期中"; RefreshRows(); SetActivity("終了後の同期", "セーブの書き込みを待ち、アップロードしています。");
        await Task.Run(() => runtime.Service().AfterExitAsync(game, closing.Token));
    }
    // The network or Dropbox itself is unreachable, as opposed to a rejected sign-in that needs the user.
    private bool IsOffline(Exception error) => error is HttpRequestException || error is TaskCanceledException && !closing.IsCancellationRequested ||
        error is DropboxApiException { HttpStatus: 429 or >= 500 } || error is TokenEndpointException { HttpStatus: 429 or >= 500 };
    private static bool IsConnectionError(Exception error) => error is HttpRequestException or TokenEndpointException or TaskCanceledException or ConnectionUnavailableException ||
        error is DropboxApiException api && (api.HttpStatus is 401 or 403 or 429 || api.HttpStatus >= 500);
    private DialogResult OfflineDialog(string error)
    {
        using var dialog = new Form { Text = "クラウドに接続できません", Icon = UiTheme.AppIcon, ClientSize = new(600, 300), MinimumSize = new(560, 300),
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = UiTheme.Font(), BackColor = UiTheme.Background,
            ForeColor = UiTheme.Ink, AutoScaleDimensions = new(96, 96), AutoScaleMode = AutoScaleMode.Dpi };
        UiTheme.Window(dialog);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Absolute, 52)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 54));
        layout.Controls.Add(UiTheme.Label("最新のセーブを確認できませんでした", 15, bold: true), 0, 0);
        layout.Controls.Add(new Label { Text = "同期せずに起動すると、この PC にあるセーブを使います。", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        layout.Controls.Add(new TextBox { Text = error, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, BackColor = UiTheme.Background, ForeColor = UiTheme.Ink, AccessibleName = "接続エラーの詳細", TabStop = false }, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 12, 0, 0) };
        foreach (var (text, answer) in new[] { ("再試行", DialogResult.Retry), ("同期せずに起動", DialogResult.Ignore), ("キャンセル", DialogResult.Cancel) })
        {
            var button = UiTheme.Button(text, answer == DialogResult.Retry); button.DialogResult = answer; button.Margin = new(8, 0, 0, 0); actions.Controls.Add(button);
            if (answer == DialogResult.Retry) dialog.AcceptButton = button;
            if (answer == DialogResult.Cancel) dialog.CancelButton = button;
        }
        layout.Controls.Add(actions, 0, 3); dialog.Controls.Add(layout); return dialog.ShowDialog(this);
    }
    private string? PickExecutable()
    {
        using var dialog = new OpenFileDialog { Filter = "ゲーム実行ファイル・ショートカット|*.exe;*.lnk", Title = "ゲームを選んでください" };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }
    private async Task AddGameAsync()
    {
        string? path = PickExecutable(); if (path is null) { operationCanceled = true; return; }
        var detected = await Task.Run(() => runtime.Detector.Detect(path));
        var identity = detected.Identity;
        var existing = runtime.Repository.Library.Games.FirstOrDefault(g => SameIdentity(g.Definition, identity));
        if (existing is not null) throw new UserError("同じゲームが登録されています。登録先: " + existing.ExecutablePath);
        await Task.Run(async () => { await runtime.CheckConnectionAsync(closing.Token); await runtime.Catalog.RefreshAsync(closing.Token); });
        var shared = runtime.Catalog.Games.FirstOrDefault(g => SameIdentity(g, identity));
        if (shared is not null) { await SetupAsync(shared, identity); return; }
        using var dialog = new RegistrationForm(detected);
        if (dialog.ShowDialog(this) != DialogResult.OK) { operationCanceled = true; return; }
        var locations = dialog.SelectedLocations.Select((c, i) => new SaveLocation("save" + (i + 1), c.Path, c.Pattern)).ToArray();
        var definition = new CloudGame(Guid.NewGuid().ToString("N"), dialog.DisplayName, Path.GetFileName(identity.ExecutablePath), identity.ProductName,
            detected.Engine, locations.Select(l => runtime.Known.Describe(l.Id, l.Path, Path.GetDirectoryName(identity.ExecutablePath)!, l.Pattern)).ToArray());
        var game = new LocalGame { Definition = definition, ExecutablePath = identity.ExecutablePath, Arguments = identity.Arguments, WorkingDirectory = identity.WorkingDirectory, Locations = locations };
        await Task.Run(() => runtime.RegisterAsync(game, true, closing.Token));
        filter.SelectedIndex = 0; preferredSelection = game.Definition.GameId;
    }
    private static bool SameIdentity(CloudGame game, GameIdentity identity) => game.ExecutableName.Equals(Path.GetFileName(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
        game.ProductName.Equals(identity.ProductName, StringComparison.OrdinalIgnoreCase);
    private async Task SetupAsync(CloudGame cloud, GameIdentity? identity = null)
    {
        if (runtime.Repository.Library.Games.Any(g => g.Definition.GameId == cloud.GameId)) throw new UserError("この PC では設定済みです。");
        if (identity is null)
        {
            string? selected = PickExecutable(); if (selected is null) { operationCanceled = true; return; }
            identity = (await Task.Run(() => runtime.Detector.Detect(selected))).Identity;
        }
        if (!cloud.ExecutableName.Equals(Path.GetFileName(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase)) throw new UserError("クラウドに登録された実行ファイル名と一致しません。");
        var locations = new List<SaveLocation>();
        foreach (var location in cloud.Locations)
        {
            string? path = runtime.Known.Resolve(location, Path.GetDirectoryName(identity.ExecutablePath)!);
            if (path is null)
            {
                using var picker = new FolderBrowserDialog { Description = cloud.DisplayName + " の保存先 " + location.Id + (location.Pattern is null ? "" : "（" + location.Pattern + " があるフォルダ）") + " を選んでください", UseDescriptionForTitle = true };
                if (picker.ShowDialog(this) != DialogResult.OK) { operationCanceled = true; return; }
                path = picker.SelectedPath;
            }
            locations.Add(new(location.Id, path, location.Pattern));
        }
        var game = new LocalGame { Definition = cloud, ExecutablePath = identity.ExecutablePath, Arguments = identity.Arguments,
            WorkingDirectory = identity.WorkingDirectory, Locations = locations.ToArray() };
        await Task.Run(() => runtime.RegisterAsync(game, false, closing.Token));
        filter.SelectedIndex = 0; preferredSelection = game.Definition.GameId;
    }
}

