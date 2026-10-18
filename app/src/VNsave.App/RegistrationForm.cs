using VNsave.Core;

namespace VNsave.App;

public sealed class RegistrationForm : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink, AccessibleName = "ゲームの表示名" };
    private readonly LibraryGrid folders = new() { AccessibleName = "バックアップする保存先" };
    private readonly Label selection = UiTheme.Label("", 9, UiTheme.Muted);
    private readonly Button register = UiTheme.Button("登録する", true);
    private readonly Label empty = UiTheme.Label("保存先が見つかりませんでした。\n「フォルダを追加」で保存先を指定してください。", 11, UiTheme.Muted);
    private bool inspecting;
    public string DisplayName => name.Text.Trim();
    public SaveCandidate[] SelectedLocations => folders.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => (SaveCandidate)r.Tag!).ToArray();

    public RegistrationForm(DetectionResult detection)
    {
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ゲームを登録 · Tsuzuri"; Icon = UiTheme.AppIcon; ClientSize = new(900, 620); MinimumSize = new(800, 530);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        Font = UiTheme.Font(); BackColor = UiTheme.Background; ForeColor = UiTheme.Ink; UiTheme.Window(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28, 24, 28, 20), ColumnCount = 1, RowCount = 8 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int size in new[] { 40, 40, 26, 44, 32 }) layout.RowStyles.Add(new(SizeType.Absolute, size));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.Controls.Add(UiTheme.Label("ゲームと保存先を確認", 20, bold: true), 0, 0);
        layout.Controls.Add(new TextBox { Text = detection.Identity.ExecutablePath, ReadOnly = true, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, BackColor = UiTheme.Background, ForeColor = UiTheme.Muted, AccessibleName = "実行ファイル", TabStop = false }, 0, 1);
        layout.Controls.Add(UiTheme.Label("ゲームの表示名", 10, bold: true), 0, 2);
        name.Text = detection.Identity.DisplayName; name.Margin = new(0, 0, 0, 12); layout.Controls.Add(name, 0, 3);
        layout.Controls.Add(UiTheme.Label("バックアップする保存先  ·  検出: " + detection.Engine, 10, bold: true), 0, 4);
        UiTheme.Grid(folders); folders.RowTemplate.Height = 64;
        folders.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 40 });
        folders.Columns.Add("path", "フォルダ / 検出の理由"); folders.Columns.Add("files", "内容"); folders.Columns.Add("updated", "最終更新");
        folders.Columns["files"]!.AutoSizeMode = folders.Columns["updated"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        folders.Columns["files"]!.Width = 105; folders.Columns["updated"]!.Width = 108;
        foreach (DataGridViewColumn column in folders.Columns) { column.SortMode = DataGridViewColumnSortMode.NotSortable; column.ReadOnly = column.Index != 0; }
        folders.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        folders.CurrentCellDirtyStateChanged += (_, _) => { if (folders.IsCurrentCellDirty) folders.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        folders.CellValueChanged += (_, _) => UpdateSelection();
        folders.CellPainting += PaintPath;
        foreach (var candidate in detection.Candidates.OrderByDescending(c => c.HasFiles).ThenByDescending(c => c.LastWriteUtc))
            AddCandidate(candidate, candidate.HasFiles && !detection.Candidates.Any(other => other.HasFiles && other.Path.Length > candidate.Path.Length && PathSafety.Overlaps(other.Path, candidate.Path)));
        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = new(0) };
        empty.AutoSize = false; empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter;
        body.Controls.Add(folders); body.Controls.Add(empty); layout.Controls.Add(body, 0, 5);
        selection.Anchor = AnchorStyles.Left; layout.Controls.Add(selection, 0, 6);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new(0) };
        actions.ColumnStyles.Add(new(SizeType.Absolute, 150)); actions.ColumnStyles.Add(new(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new(SizeType.Absolute, 132)); actions.ColumnStyles.Add(new(SizeType.Absolute, 140));
        var add = UiTheme.Button("＋ フォルダを追加"); add.Width = 150;
        add.Click += async (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "セーブの保存フォルダを選んでください", UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var duplicate = folders.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ((SaveCandidate)r.Tag!).Path.Equals(picker.SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null) { duplicate.Cells[0].Value = true; folders.CurrentCell = duplicate.Cells["path"]; return; }
            inspecting = true; add.Enabled = register.Enabled = false; selection.Text = "フォルダの内容を確認しています…";
            try
            {
                var candidate = await Task.Run(() => SaveCandidate.Inspect(picker.SelectedPath, "手動指定"));
                if (!IsDisposed) AddCandidate(candidate, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { if (!IsDisposed) MessageBox.Show(this, error.Message, "保存先を確認できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { if (!IsDisposed) { inspecting = false; add.Enabled = true; UpdateSelection(); } }
        };
        register.Click += (_, _) => { folders.EndEdit(); if (SelectedLocations.Length == 0 || DisplayName.Length == 0) return; DialogResult = DialogResult.OK; Close(); };
        var cancel = UiTheme.Button("キャンセル"); cancel.DialogResult = DialogResult.Cancel;
        register.Dock = DockStyle.Fill; register.Margin = new(8, 0, 0, 0);
        actions.Controls.Add(add, 0, 0); actions.Controls.Add(cancel, 2, 0); actions.Controls.Add(register, 3, 0);
        layout.Controls.Add(actions, 0, 7); Controls.Add(layout); CancelButton = cancel; AcceptButton = register;
        name.TextChanged += (_, _) => UpdateSelection(); UpdateSelection();
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    private void AddCandidate(SaveCandidate candidate, bool selected)
    {
        string contents = !candidate.Exists ? "未作成" : !candidate.HasFiles ? (candidate.Pattern is null ? "空のフォルダ" : "セーブなし")
            : $"{candidate.FileCount:N0}{(candidate.CountCapped ? "+" : "")} ファイル";
        int index = folders.Rows.Add(selected, candidate.DisplayPath, contents, candidate.LastWriteUtc?.ToLocalTime().ToString("yyyy/MM/dd") ?? "—");
        folders.Rows[index].Tag = candidate;
        folders.Rows[index].Cells["path"].ToolTipText = candidate.DisplayPath + "\n" + candidate.Reason;
        folders.Rows[index].Cells["updated"].ToolTipText = candidate.LastWriteUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "更新記録なし";
        if (!candidate.HasFiles) folders.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Muted;
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        // Rows.Add raises CellValueChanged before the candidate is attached.
        if (folders.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is null)) return;
        var chosen = SelectedLocations; int selected = chosen.Length;
        selection.Text = selected == 0 ? "バックアップする保存先にチェックを入れてください。" : chosen.Any(c => c.Pattern is not null)
            ? $"{selected} か所をバックアップします。ゲームフォルダの保存先は、名前が一致するセーブファイルだけが対象です。"
            : $"{selected} フォルダをバックアップします。フォルダ全体が対象です。";
        register.Enabled = !inspecting && selected > 0 && DisplayName.Length > 0;
        empty.Visible = folders.Rows.Count == 0; if (empty.Visible) empty.BringToFront();
    }
    private void PaintPath(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 1 || e.Graphics is null || folders.Rows[e.RowIndex].Tag is not SaveCandidate candidate) return;
        e.PaintBackground(e.ClipBounds, true);
        int inset = (int)(8 * DeviceDpi / 96f);
        var first = new Rectangle(e.CellBounds.X + inset, e.CellBounds.Y + inset, e.CellBounds.Width - inset * 2, e.CellBounds.Height / 2 - inset);
        var second = new Rectangle(first.X, e.CellBounds.Y + e.CellBounds.Height / 2 + 2, first.Width, e.CellBounds.Height / 2 - 4);
        using var small = UiTheme.Font(9);
        TextRenderer.DrawText(e.Graphics, candidate.DisplayPath, Font, first, UiTheme.Ink, TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, candidate.Reason, small, second, UiTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus); e.Handled = true;
    }
}
