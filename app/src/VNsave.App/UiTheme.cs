using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNsave.App;

internal sealed record Palette(Color Background, Color Surface, Color Ink, Color Muted, Color Line, Color Accent, Color OnAccent,
    Color AccentHover, Color AccentPressed, Color Selection, Color SelectionPressed,
    Color Green, Color GreenBack, Color Amber, Color AmberBack, Color Red, Color RedBack);

internal static class UiTheme
{
    public static readonly Palette Light = new(Color.FromArgb(245, 246, 250), Color.White, Color.FromArgb(32, 38, 56), Color.FromArgb(101, 110, 130),
        Color.FromArgb(226, 230, 239), Color.FromArgb(83, 72, 199), Color.White, Color.FromArgb(69, 58, 178), Color.FromArgb(58, 48, 154),
        Color.FromArgb(238, 236, 253), Color.FromArgb(222, 218, 247), Color.FromArgb(29, 112, 86), Color.FromArgb(230, 244, 236),
        Color.FromArgb(147, 91, 17), Color.FromArgb(255, 244, 222), Color.FromArgb(173, 58, 70), Color.FromArgb(252, 235, 237));
    // Every text color keeps at least 4.5:1 against the backgrounds it is drawn on.
    public static readonly Palette Night = new(Color.FromArgb(24, 26, 33), Color.FromArgb(33, 36, 46), Color.FromArgb(230, 232, 240), Color.FromArgb(150, 158, 178),
        Color.FromArgb(55, 60, 75), Color.FromArgb(146, 136, 250), Color.FromArgb(22, 20, 44), Color.FromArgb(164, 155, 252), Color.FromArgb(128, 118, 236),
        Color.FromArgb(46, 43, 78), Color.FromArgb(58, 54, 96), Color.FromArgb(104, 206, 160), Color.FromArgb(27, 54, 45),
        Color.FromArgb(236, 182, 96), Color.FromArgb(60, 47, 24), Color.FromArgb(244, 134, 146), Color.FromArgb(66, 33, 40));
    public static Palette Current { get; private set; } = Light;
    public static bool Dark => Current == Night;
    public static Color Background => Current.Background;
    public static Color Surface => Current.Surface;
    public static Color Ink => Current.Ink;
    public static Color Muted => Current.Muted;
    public static Color Line => Current.Line;
    public static Color Accent => Current.Accent;
    public static Color OnAccent => Current.OnAccent;
    public static Color Selection => Current.Selection;
    public static Color Green => Current.Green;
    public static Color Amber => Current.Amber;
    public static Color Red => Current.Red;
    public static readonly Icon AppIcon = new(Resource("Tsuzuri.ico"));
    public static readonly Image AppImage = new Icon(Resource("Tsuzuri.ico"), 64, 64).ToBitmap();

    public static Stream Resource(string name) => typeof(UiTheme).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");

    // The choice is kept per PC beside games.json; until one is made, the Windows app mode decides.
    private static string PreferencePath(string root) => Path.Combine(root, "theme.txt");
    public static void Load(string root)
    {
        bool dark;
        try { dark = File.ReadAllText(PreferencePath(root)).Trim() == "dark"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0; }
        Current = dark ? Night : Light;
    }
    public static void Toggle(string root)
    {
        Current = Dark ? Light : Night;
        // A failed write only means the next start asks Windows again.
        try { File.WriteAllText(PreferencePath(root), Dark ? "dark" : "light"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static Font Font(float size = 10, FontStyle style = FontStyle.Regular) => new("Yu Gothic UI", size, style);
    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Ink, BackColor = Color.Transparent,
        Font = Font(size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new(0), UseMnemonic = false
    };
    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
        Text = text, AutoSize = false, Size = new(132, 40), FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = Line,
            MouseOverBackColor = primary ? Current.AccentHover : Selection,
            MouseDownBackColor = primary ? Current.AccentPressed : Current.SelectionPressed },
        BackColor = primary ? Accent : Surface, ForeColor = primary ? OnAccent : Ink,
        Font = Font(10, primary ? FontStyle.Bold : FontStyle.Regular), Cursor = Cursors.Hand,
        Margin = new(0), UseVisualStyleBackColor = false
        };
        if (primary) button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled ? Accent : Selection;
            button.ForeColor = button.Enabled ? OnAccent : Muted;
        };
        return button;
    }
    public static void Grid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill; grid.BackgroundColor = Surface; grid.BorderStyle = BorderStyle.None;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.AllowUserToResizeColumns = false; grid.RowHeadersVisible = false; grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Line;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 38; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Surface, ForeColor = Muted, Font = Font(9), Padding = new(8, 0, 0, 0), SelectionBackColor = Surface };
        grid.DefaultCellStyle = new() { BackColor = Surface, ForeColor = Ink, Font = Font(),
            SelectionBackColor = Selection, SelectionForeColor = Ink, Padding = new(8, 0, 8, 0), NullValue = "" };
        grid.RowTemplate.Height = 68;
        grid.ShowCellToolTips = true;
    }
    public static ContextMenuStrip Menu()
    {
        var menu = new ContextMenuStrip { Font = Font(), BackColor = Surface, ForeColor = Ink, ShowImageMargin = false, Padding = new(4) };
        MenuRenderer(menu);
        return menu;
    }
    // The default menu renderer highlights with the light Windows colors, which hide light text.
    // WinForms' own dark mode is not used: it redraws flat buttons in its own style.
    private static void MenuRenderer(ToolStrip strip)
    {
        if (Dark) strip.Renderer = new NightMenuRenderer();
        else strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
    }
    private sealed class NightMenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var brush = new SolidBrush(Selection);
            e.Graphics.FillRectangle(brush, new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height));
        }
    }
    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
    }
    public static (Color Fore, Color Back) StatusColors(string text) => text switch
    {
        "準備完了" => (Green, Current.GreenBack),
        "未送信あり" => (Amber, Current.AmberBack),
        "エラー" or "実行ファイル不明" => (Red, Current.RedBack),
        "プレイ中" or "同期中" => (Accent, Selection),
        _ => (Muted, Background)
    };

    // Title and scroll bars are drawn by Windows, so they follow the palette once the window exists.
    public static void Window(Form form)
    {
        form.HandleCreated += (_, _) => { if (Dark) TitleBar(form); };
        form.Shown += (_, _) => { if (Dark) ScrollBars(form); };
    }
    // DWMWA_USE_IMMERSIVE_DARK_MODE; Windows versions without it ignore the call.
    private static void TitleBar(Form form) { int dark = Dark ? 1 : 0; _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)); }
    private static void ScrollBars(Control root)
    {
        foreach (Control child in root.Controls) ScrollBars(child);
        if (root is ScrollBar or ScrollableControl { AutoScroll: true }) _ = SetWindowTheme(root.Handle, Dark ? "DarkMode_Explorer" : null, null);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? application, string? classes);

    // Open windows keep the colors they were given, so each one is swapped for the same role in the current palette.
    // Custom painting reads the palette directly and only needs the repaint at the end.
    public static void Restyle(Control root, Palette from)
    {
        foreach (Control child in root.Controls) Restyle(child, from);
        if (Swap(root.BackColor, from, BackRoles) is { } back) root.BackColor = back;
        if (Swap(root.ForeColor, from, ForeRoles) is { } fore) root.ForeColor = fore;
        if (root is Button { FlatAppearance: var flat })
        {
            if (Swap(flat.BorderColor, from, BackRoles) is { } border) flat.BorderColor = border;
            if (Swap(flat.MouseOverBackColor, from, BackRoles) is { } over) flat.MouseOverBackColor = over;
            if (Swap(flat.MouseDownBackColor, from, BackRoles) is { } down) flat.MouseDownBackColor = down;
        }
        if (root is DataGridView grid)
        {
            if (Swap(grid.BackgroundColor, from, BackRoles) is { } background) grid.BackgroundColor = background;
            if (Swap(grid.GridColor, from, BackRoles) is { } line) grid.GridColor = line;
            grid.DefaultCellStyle = Restyle(grid.DefaultCellStyle, from);
            grid.ColumnHeadersDefaultCellStyle = Restyle(grid.ColumnHeadersDefaultCellStyle, from);
        }
        if (root is ToolStrip strip)
        {
            foreach (ToolStripItem item in strip.Items) if (Swap(item.ForeColor, from, ForeRoles) is { } itemFore) item.ForeColor = itemFore;
            MenuRenderer(strip);
        }
        if (root is DropDownBox box) box.DrawMode = Dark ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
        if (root is Form form) { TitleBar(form); ScrollBars(form); }
        root.Invalidate();
    }
    // The grid may hand out a filled-in copy of its style, so the swapped copy is assigned back.
    private static DataGridViewCellStyle Restyle(DataGridViewCellStyle source, Palette from)
    {
        var style = new DataGridViewCellStyle(source);
        if (Swap(style.BackColor, from, BackRoles) is { } back) style.BackColor = back;
        if (Swap(style.SelectionBackColor, from, BackRoles) is { } selected) style.SelectionBackColor = selected;
        if (Swap(style.ForeColor, from, ForeRoles) is { } fore) style.ForeColor = fore;
        if (Swap(style.SelectionForeColor, from, ForeRoles) is { } selectedFore) style.SelectionForeColor = selectedFore;
        return style;
    }
    private static readonly Func<Palette, Color>[] BackRoles = [p => p.Background, p => p.Surface, p => p.Line, p => p.Accent, p => p.AccentHover,
        p => p.AccentPressed, p => p.Selection, p => p.SelectionPressed, p => p.GreenBack, p => p.AmberBack, p => p.RedBack];
    private static readonly Func<Palette, Color>[] ForeRoles = [p => p.Ink, p => p.Muted, p => p.Accent, p => p.OnAccent, p => p.Green, p => p.Amber, p => p.Red];
    private static Color? Swap(Color color, Palette from, Func<Palette, Color>[] roles) =>
        roles.FirstOrDefault(role => role(from).ToArgb() == color.ToArgb()) is { } match ? match(Current) : null;
}

// A drop-down list ignores BackColor and draws its box and arrow in light system colors,
// so with the dark palette it draws its items and paints over the box.
internal sealed class DropDownBox : ComboBox
{
    public DropDownBox() { DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; DrawMode = UiTheme.Dark ? DrawMode.OwnerDrawFixed : DrawMode.Normal; }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool highlighted = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var brush = new SolidBrush(highlighted ? UiTheme.Selection : UiTheme.Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -2, 0),
            Enabled ? UiTheme.Ink : UiTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || !UiTheme.Dark) return; // WM_PAINT
        using var g = CreateGraphics();
        int width = SystemInformation.GetHorizontalScrollBarArrowWidthForDpi(DeviceDpi);
        var button = new Rectangle(ClientSize.Width - width - 2, 1, width + 1, ClientSize.Height - 2);
        using var fill = new SolidBrush(UiTheme.Surface); g.FillRectangle(fill, button);
        using var border = new Pen(UiTheme.Line); g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        int x = button.X + button.Width / 2, y = button.Y + button.Height / 2, size = Math.Max(3, width / 5);
        using var arrow = new SolidBrush(Enabled ? UiTheme.Ink : UiTheme.Muted);
        g.FillPolygon(arrow, [new Point(x - size, y - size / 2), new Point(x + size + 1, y - size / 2), new Point(x, y + size / 2 + 1)]);
    }
}

internal sealed class LibraryGrid : DataGridView
{
    public LibraryGrid() => DoubleBuffered = true;
}

internal sealed class GameGlyph : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? GameImage { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string GameName { get; set; } = "V";
    public GameGlyph() { DoubleBuffered = true; Size = new(64, 64); TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(UiTheme.Selection);
        e.Graphics.FillRectangle(brush, ClientRectangle);
        if (GameImage is { } img)
        {
            int inset = Math.Max(6, Width / 6);
            e.Graphics.DrawImage(img, new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2));
        }
        else
        {
            using var font = UiTheme.Font(22, FontStyle.Bold);
            string initial = string.IsNullOrWhiteSpace(GameName) ? "V" : System.Globalization.StringInfo.GetNextTextElement(GameName);
            TextRenderer.DrawText(e.Graphics, initial, font, ClientRectangle, UiTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
