namespace VNsave.App;

public sealed partial class MainForm
{
    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28, 12, 28, 0), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 92)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 66));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new(0) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 256));
        header.ColumnStyles.Add(new(SizeType.Absolute, 52)); header.ColumnStyles.Add(new(SizeType.Absolute, 116));
        var brand = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = new(0), Padding = new(0, 6, 0, 0) };
        brand.Controls.Add(UiTheme.Label("Tsuzuri", 24, bold: true)); brand.Controls.Add(UiTheme.Label("v" + AppVersion, 10, UiTheme.Muted));
        account.AutoSize = false; account.Dock = DockStyle.Fill; account.TextAlign = ContentAlignment.MiddleRight; account.Padding = new(24, 0, 16, 10); account.AutoEllipsis = true;
        account.Paint += PaintAccountDot;
        // A rare switch, so it stays a small symbol beside the connection.
        theme.Size = new(40, 40); theme.Font = new("Segoe UI Symbol", 13); theme.Anchor = AnchorStyles.Right; theme.Margin = new(0, 0, 10, 10);
        connection.Width = 116; connection.Anchor = AnchorStyles.Right; connection.Margin = new(0, 0, 0, 10);
        header.Controls.Add(brand, 0, 0); header.Controls.Add(account, 1, 0); header.Controls.Add(theme, 2, 0); header.Controls.Add(connection, 3, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0) };
        content.ColumnStyles.Add(new(SizeType.Percent, 56)); content.ColumnStyles.Add(new(SizeType.Percent, 44));
        content.Controls.Add(BuildLibrary(), 0, 0); content.Controls.Add(BuildDetails(), 1, 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new(0), Padding = new(0, 10, 0, 6) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 62));
        footer.RowStyles.Add(new(SizeType.Absolute, 19)); footer.RowStyles.Add(new(SizeType.Percent, 100));
        result.AutoSize = false; result.Dock = DockStyle.Fill; result.AutoEllipsis = true;
        errorDetails.Size = new(58, 32); errorDetails.Visible = false; errorDetails.Anchor = AnchorStyles.Right;
        footer.Controls.Add(activity, 0, 0); footer.Controls.Add(result, 0, 1); footer.Controls.Add(errorDetails, 1, 0); footer.SetRowSpan(errorDetails, 2);
        root.Controls.Add(header, 0, 0); root.Controls.Add(content, 0, 1); root.Controls.Add(footer, 0, 2);
        Controls.Add(root); Controls.Add(progress);
    }

    private Control BuildLibrary()
    {
        var library = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new(0, 0, 24, 0) };
        library.RowStyles.Add(new(SizeType.Absolute, 60));
        library.RowStyles.Add(new(SizeType.Percent, 100)); library.RowStyles.Add(new(SizeType.Absolute, 32));
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new(0) };
        bar.ColumnStyles.Add(new(SizeType.Percent, 100)); bar.ColumnStyles.Add(new(SizeType.Absolute, 120));
        bar.ColumnStyles.Add(new(SizeType.Absolute, 72)); bar.ColumnStyles.Add(new(SizeType.Absolute, 154));
        var heading = UiTheme.Label("ライブラリ", 17, bold: true); heading.Anchor = AnchorStyles.Left; heading.Margin = new(0, 0, 0, 8);
        filter.Items.AddRange(["すべて", "この PC", "要確認"]); filter.SelectedIndex = 0; filter.Dock = DockStyle.None; filter.Width = 110;
        filter.Anchor = AnchorStyles.Right; filter.Margin = new(0, 0, 10, 8); tips.SetToolTip(filter, "表示するゲーム");
        refresh.Size = new(62, 36); refresh.Margin = new(0, 0, 8, 8); refresh.Anchor = AnchorStyles.Right;
        add.Size = new(154, 40); add.Anchor = AnchorStyles.Right; add.Margin = new(0, 0, 0, 8);
        bar.Controls.Add(heading, 0, 0); bar.Controls.Add(filter, 1, 0); bar.Controls.Add(refresh, 2, 0); bar.Controls.Add(add, 3, 0);
        UiTheme.Grid(grid);
        var iconColumn = new DataGridViewImageColumn { Name = "icon", HeaderText = "", ImageLayout = DataGridViewImageCellLayout.Zoom,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 56, Resizable = DataGridViewTriState.False };
        iconColumn.DefaultCellStyle.NullValue = null; iconColumn.DefaultCellStyle.Padding = new(12, 16, 8, 16);
        grid.Columns.Add(iconColumn); grid.Columns.Add("name", "ゲーム / プレイ時間"); grid.Columns.Add("state", "状態"); grid.Columns.Add("action", "");
        grid.Columns["state"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; grid.Columns["state"]!.Width = 112;
        grid.Columns["action"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; grid.Columns["action"]!.Width = 96;
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        var list = new Panel { Dock = DockStyle.Fill, Margin = new(0), BackColor = UiTheme.Surface };
        var emptyContent = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(26) };
        emptyContent.RowStyles.Add(new(SizeType.Percent, 45)); emptyContent.RowStyles.Add(new(SizeType.AutoSize)); emptyContent.RowStyles.Add(new(SizeType.AutoSize)); emptyContent.RowStyles.Add(new(SizeType.Percent, 55));
        emptyTitle.Anchor = AnchorStyles.None; emptyHint.Anchor = AnchorStyles.None; emptyHint.Margin = new(0, 12, 0, 0);
        emptyContent.Controls.Add(emptyTitle, 0, 1); emptyContent.Controls.Add(emptyHint, 0, 2); empty.Controls.Add(emptyContent);
        list.Controls.Add(grid); list.Controls.Add(empty);
        count.Anchor = AnchorStyles.Left;
        library.Controls.Add(bar, 0, 0); library.Controls.Add(list, 0, 1); library.Controls.Add(count, 0, 2);
        return library;
    }

    private Control BuildDetails()
    {
        var scroll = detailScroll;
        var detail = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 0, Padding = new(24), Margin = new(0), BackColor = UiTheme.Surface };
        detail.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Add(Control control, int after = 16)
        {
            int row = detail.RowCount++; detail.RowStyles.Add(new(SizeType.AutoSize)); control.Margin = new(0, 0, 0, after); detail.Controls.Add(control, 0, row);
        }
        // Compact order so the default window shows everything without scrolling:
        // icon + name + engine/status, one-line hint, launch, three stats in a row, one-line save path.
        var hero = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Top, AutoSize = true, Margin = new(0) };
        hero.ColumnStyles.Add(new(SizeType.Absolute, 80)); hero.ColumnStyles.Add(new(SizeType.Percent, 100)); glyph.Margin = new(0, 0, 16, 0);
        var names = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new(0) };
        names.ColumnStyles.Add(new(SizeType.Percent, 100)); title.Dock = DockStyle.Top;
        var meta = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new(0, 8, 0, 0), BackColor = Color.Transparent };
        badge.Padding = new(8, 3, 8, 3); badge.Margin = new(0, 0, 10, 0); engine.Margin = new(0, 4, 0, 0);
        meta.Controls.Add(badge); meta.Controls.Add(engine);
        names.Controls.Add(title, 0, 0); names.Controls.Add(meta, 0, 1);
        names.SizeChanged += (_, _) => title.MaximumSize = new(Math.Max(1, names.ClientSize.Width), 0);
        hero.Controls.Add(glyph, 0, 0); hero.Controls.Add(names, 1, 0); Add(hero, 16);
        hint.AutoSize = false; hint.AutoEllipsis = true; hint.Dock = DockStyle.Top; hint.Height = 24; Add(hint, 10);
        launch.Dock = DockStyle.Top; launch.Height = 50; launch.Font = UiTheme.Font(12, FontStyle.Bold); Add(launch, 20);
        var stats = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new(0) };
        stats.ColumnStyles.Add(new(SizeType.Percent, 38)); stats.ColumnStyles.Add(new(SizeType.Percent, 31)); stats.ColumnStyles.Add(new(SizeType.Percent, 31));
        stats.Controls.Add(UiTheme.Label("総プレイ時間", 9, UiTheme.Muted), 0, 0); stats.Controls.Add(UiTheme.Label("最終プレイ", 9, UiTheme.Muted), 1, 0);
        stats.Controls.Add(UiTheme.Label("最終同期", 9, UiTheme.Muted), 2, 0);
        playTime.Margin = new(0, 5, 8, 0); lastPlayed.Margin = new(0, 7, 8, 0); lastSync.Margin = new(0, 7, 0, 0);
        stats.Controls.Add(playTime, 0, 1); stats.Controls.Add(lastPlayed, 1, 1); stats.Controls.Add(lastSync, 2, 1); Add(stats, 16);
        Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Line }, 12);
        Add(UiTheme.Label("この PC の保存先", 9, UiTheme.Muted), 4);
        paths.AutoSize = false; paths.AutoEllipsis = true; paths.Dock = DockStyle.Top; paths.Height = 22; Add(paths, 0);
        var actions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 40, Dock = DockStyle.Top, Margin = new(0) };
        actions.ColumnStyles.Add(new(SizeType.Percent, 58)); actions.ColumnStyles.Add(new(SizeType.Percent, 42));
        saveActions.Dock = DockStyle.Fill; more.Dock = DockStyle.Fill; saveActions.Margin = new(0, 0, 8, 0);
        actions.Controls.Add(saveActions, 0, 0); actions.Controls.Add(more, 1, 0);
        scroll.Controls.Add(detail);
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Surface, Margin = new(0) };
        card.RowStyles.Add(new(SizeType.Percent, 100)); card.RowStyles.Add(new(SizeType.Absolute, 72));
        var footer = new Panel { Dock = DockStyle.Fill, Padding = new(24, 8, 24, 24), Margin = new(0), BackColor = UiTheme.Surface };
        footer.Controls.Add(actions); card.Controls.Add(scroll, 0, 0); card.Controls.Add(footer, 0, 1);
        return card;
    }

    // The connection state as a colored dot just left of the right-aligned account text.
    private void PaintAccountDot(object? sender, PaintEventArgs e)
    {
        int available = account.ClientSize.Width - account.Padding.Horizontal;
        int textWidth = Math.Min(available, TextRenderer.MeasureText(e.Graphics, account.Text, account.Font, new(available, account.Height), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width);
        int size = (int)(9 * DeviceDpi / 96f), gap = (int)(7 * DeviceDpi / 96f);
        int centerY = account.Padding.Top + (account.ClientSize.Height - account.Padding.Vertical) / 2;
        var dot = new Rectangle(account.ClientSize.Width - account.Padding.Right - textWidth - gap - size, centerY - size / 2, size, size);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(accountDot()); e.Graphics.FillEllipse(brush, dot);
    }
    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null || grid.Rows[e.RowIndex].Tag is not Row row) return;
        if (e.ColumnIndex == 1)
        {
            e.PaintBackground(e.ClipBounds, true);
            var bounds = Rectangle.Inflate(e.CellBounds, -(int)(8 * DeviceDpi / 96f), 0);
            var top = new Rectangle(bounds.X, bounds.Y + (int)(12 * DeviceDpi / 96f), bounds.Width, bounds.Height / 2 - (int)(4 * DeviceDpi / 96f));
            using var nameFont = UiTheme.Font(10, FontStyle.Bold); using var metaFont = UiTheme.Font(9);
            TextRenderer.DrawText(e.Graphics, row.Cloud.DisplayName, nameFont, top, UiTheme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var bottom = new Rectangle(bounds.X, e.CellBounds.Y + e.CellBounds.Height / 2 + 2, bounds.Width, e.CellBounds.Height / 2 - 4);
            TextRenderer.DrawText(e.Graphics, PlaySummary(row), metaFont, bottom, UiTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus); e.Handled = true;
        }
        else if (e.ColumnIndex == 2)
        {
            e.PaintBackground(e.ClipBounds, true);
            var colors = UiTheme.StatusColors(row.Status); using var brush = new SolidBrush(colors.Back); using var font = UiTheme.Font(9);
            int inset = (int)(6 * DeviceDpi / 96f), height = (int)(28 * DeviceDpi / 96f);
            var box = new Rectangle(e.CellBounds.X + inset, e.CellBounds.Y + (e.CellBounds.Height - height) / 2, e.CellBounds.Width - inset * 2, height);
            e.Graphics.FillRectangle(brush, box);
            TextRenderer.DrawText(e.Graphics, row.Status, font, box, colors.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus); e.Handled = true;
        }
        else if (e.ColumnIndex == 3)
        {
            e.PaintBackground(e.ClipBounds, true);
            var (text, enabled) = RowAction(row);
            // Launch is an outline so the large button in the detail pane stays the main action.
            // Setup is plain text, so a row that still needs setting up never looks like one that can start.
            bool setup = row.Local is null;
            int insetX = (int)(8 * DeviceDpi / 96f), height = (int)(32 * DeviceDpi / 96f);
            var box = new Rectangle(e.CellBounds.X + insetX, e.CellBounds.Y + (e.CellBounds.Height - height) / 2, e.CellBounds.Width - insetX * 2, height);
            if (!setup || !enabled)
            {
                using var brush = new SolidBrush(enabled ? UiTheme.Surface : UiTheme.Background); e.Graphics.FillRectangle(brush, box);
                if (enabled) { using var pen = new Pen(UiTheme.Accent); e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1); }
            }
            using var font = UiTheme.Font(9.5f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, text, font, box, enabled ? UiTheme.Accent : UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus); e.Handled = true;
        }
        else if (e.ColumnIndex == 0 && IconFor(row.Local) is null)
        {
            e.PaintBackground(e.ClipBounds, true);
            int size = (int)(34 * DeviceDpi / 96f); var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2, e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
            using var brush = new SolidBrush(UiTheme.Selection); using var font = UiTheme.Font(13, FontStyle.Bold); e.Graphics.FillRectangle(brush, box);
            string initial = string.IsNullOrWhiteSpace(row.Cloud.DisplayName) ? "V" : System.Globalization.StringInfo.GetNextTextElement(row.Cloud.DisplayName);
            TextRenderer.DrawText(e.Graphics, initial, font, box, UiTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus); e.Handled = true;
        }
    }
}
