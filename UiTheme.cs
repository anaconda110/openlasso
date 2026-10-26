using System.Drawing.Drawing2D;

namespace OpenLasso;

/// <summary>OpenLasso 的轻量 UI 主题：颜色、字体、卡片与常用控件样式。</summary>
public static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(243, 246, 250);
    public static readonly Color Surface = Color.FromArgb(255, 255, 255);
    public static readonly Color SurfaceAlt = Color.FromArgb(248, 250, 252);
    public static readonly Color Border = Color.FromArgb(220, 227, 236);
    public static readonly Color BorderStrong = Color.FromArgb(203, 213, 225);
    public static readonly Color Text = Color.FromArgb(23, 32, 51);
    public static readonly Color Muted = Color.FromArgb(102, 112, 133);
    public static readonly Color Primary = Color.FromArgb(37, 99, 235);
    public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    public static readonly Color PrimarySoft = Color.FromArgb(232, 240, 255);
    public static readonly Color Success = Color.FromArgb(21, 128, 61);
    public static readonly Color SuccessSoft = Color.FromArgb(220, 252, 231);
    public static readonly Color Warning = Color.FromArgb(180, 83, 9);
    public static readonly Color WarningSoft = Color.FromArgb(255, 247, 237);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color Gpu = Color.FromArgb(124, 58, 237);
    public static readonly Color Memory = Color.FromArgb(217, 119, 6);
    public static readonly Color DangerSoft = Color.FromArgb(254, 242, 242);

    public static readonly Font BodyFont = new("Microsoft YaHei UI", 9F);
    public static readonly Font BodyMediumFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font SmallFont = new("Microsoft YaHei UI", 8.25F);
    public static readonly Font SmallMediumFont = new("Microsoft YaHei UI", 8.25F, FontStyle.Bold);
    public static readonly Font SectionFont = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static readonly Font TitleFont = new("Microsoft YaHei UI", 15F, FontStyle.Bold);
    public static readonly Font BrandFont = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
    public static readonly Font MonoFont = new("Consolas", 8.75F);
    public static readonly Font MonoSmallFont = new("Consolas", 8.25F);

    public static void ApplyForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = BodyFont;
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    public static void StyleButton(Button button, bool primary = false, bool danger = false)
    {
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        button.Font = BodyMediumFont;
        button.Margin = new Padding(8, 0, 0, 0);

        if (danger)
        {
            button.BackColor = Surface;
            button.ForeColor = Danger;
            button.FlatAppearance.BorderColor = Color.FromArgb(254, 202, 202);
            button.FlatAppearance.MouseOverBackColor = DangerSoft;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(254, 226, 226);
        }
        else if (primary)
        {
            button.BackColor = Primary;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Primary;
            button.FlatAppearance.MouseOverBackColor = PrimaryHover;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 64, 175);
        }
        else
        {
            button.BackColor = Surface;
            button.ForeColor = Text;
            button.FlatAppearance.BorderColor = BorderStrong;
            button.FlatAppearance.MouseOverBackColor = SurfaceAlt;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(241, 245, 249);
        }
    }

    public static void StyleInput(Control control)
    {
        control.Font = BodyFont;
        control.BackColor = Surface;
        control.ForeColor = Text;
        control.Margin = new Padding(0, 0, 0, 0);
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        if (control is TextBox textBox) textBox.BorderStyle = BorderStyle.FixedSingle;
        if (control is NumericUpDown numeric) numeric.BorderStyle = BorderStyle.FixedSingle;
    }

    public static CardPanel CreateCard(string title, string subtitle, Control content, Control action = null, int headerHeight = 48)
    {
        var card = new CardPanel { Padding = new Padding(1), Margin = new Padding(0) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, headerHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Surface,
            Margin = new Padding(0),
            Padding = new Padding(14, 7, 12, 4),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Surface,
        };
        left.Controls.Add(new Label
        {
            AutoSize = true,
            Text = title,
            Font = SectionFont,
            ForeColor = Text,
            Margin = new Padding(0, 0, 0, 1),
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            left.Controls.Add(new Label
            {
                AutoSize = true,
                Text = subtitle,
                Font = SmallFont,
                ForeColor = Muted,
                Margin = new Padding(0),
            });
        }

        header.Controls.Add(left, 0, 0);
        if (action != null)
        {
            action.Margin = new Padding(8, 4, 0, 0);
            header.Controls.Add(action, 1, 0);
        }

        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    public static CardPanel Notice(Label label)
    {
        var card = new CardPanel
        {
            FillColor = PrimarySoft,
            BorderColor = Color.FromArgb(191, 219, 254),
            Padding = new Padding(1),
            Margin = new Padding(0),
        };
        label.Dock = DockStyle.Fill;
        label.BackColor = PrimarySoft;
        label.ForeColor = Color.FromArgb(30, 64, 175);
        label.Font = SmallFont;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Padding = new Padding(12, 0, 12, 0);
        card.Controls.Add(label);
        return card;
    }

    public static void StyleListView(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.Font = BodyFont;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = true;
        list.GridLines = false;
        list.OwnerDraw = true;
        list.DrawColumnHeader += (s, e) =>
        {
            using var bg = new SolidBrush(SurfaceAlt);
            using var pen = new Pen(Border);
            e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, SmallMediumFont, e.Bounds, Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        list.DrawItem += (s, e) =>
        {
            if (e.Item == null) return;
            var selected = e.Item.Selected;
            var background = selected
                ? PrimarySoft
                : e.ItemIndex % 2 == 0 ? Surface : SurfaceAlt;
            using var backgroundBrush = new SolidBrush(background);
            using var borderPen = new Pen(Border);
            e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
            e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if ((e.State & ListViewItemStates.Focused) != 0)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), Text, background);
        };
        list.DrawSubItem += (s, e) =>
        {
            if (e.Item == null) return;
            var selected = e.Item.Selected;
            var color = selected ? PrimaryHover : Text;
            var bounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, selected ? BodyMediumFont : BodyFont, bounds, color, flags);
        };
    }

    public static void StyleDataGridView(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.GridColor = Color.FromArgb(238, 242, 247);
        grid.RowHeadersVisible = false;
        grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.RowTemplate.Height = 32;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.Font = BodyFont;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = PrimarySoft;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(246, 249, 252);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(246, 249, 252);
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Font = SmallMediumFont;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
    }

    static readonly ToolStripRenderer MenuRenderer = new ToolStripProfessionalRenderer(new UiMenuColorTable())
    {
        RoundedEdges = false,
    };

    public static void StyleMenu(MenuStrip menu)
    {
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = BodyFont;
        menu.Padding = new Padding(8, 2, 0, 2);
        menu.GripStyle = ToolStripGripStyle.Hidden;
        menu.Renderer = MenuRenderer;
        foreach (ToolStripItem item in menu.Items)
        {
            item.Padding = new Padding(7, 2, 7, 2);
            item.ForeColor = Text;
        }
    }

    public static void StyleContextMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = BodyFont;
        menu.ShowImageMargin = false;
        menu.Renderer = MenuRenderer;
    }

    sealed class UiMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => BorderStrong;
        public override Color MenuItemBorder => PrimarySoft;
        public override Color MenuItemSelected => PrimarySoft;
        public override Color MenuItemSelectedGradientBegin => PrimarySoft;
        public override Color MenuItemSelectedGradientEnd => PrimarySoft;
        public override Color MenuItemPressedGradientBegin => SurfaceAlt;
        public override Color MenuItemPressedGradientEnd => SurfaceAlt;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }

    public static Label Label(string text, bool muted = false, bool medium = false)
        => new()
        {
            AutoSize = true,
            Text = text,
            ForeColor = muted ? Muted : Text,
            Font = medium ? BodyMediumFont : BodyFont,
            Margin = new Padding(0),
        };
}

/// <summary>带轻量圆角和细边框的面板，用于承载各功能区。</summary>
public class CardPanel : Panel
{
    Color _fillColor = UiTheme.Surface;
    Color _borderColor = UiTheme.Border;
    int _radius = 10;

    public Color FillColor
    {
        get => _fillColor;
        set { _fillColor = value; Invalidate(); }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set { _borderColor = value; Invalidate(); }
    }

    public int Radius
    {
        get => _radius;
        set { _radius = Math.Max(0, value); UpdateRegion(); Invalidate(); }
    }

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Surface;
        Padding = new Padding(1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        if (rect.Width <= 1 || rect.Height <= 1) return;
        using var path = CreateRoundedPath(rect, Radius);
        using var fill = new SolidBrush(FillColor);
        using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateRegion();
    }

    void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        using var path = CreateRoundedPath(rect, Radius);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || rect.Width <= 1 || rect.Height <= 1)
        {
            path.AddRectangle(rect);
            return path;
        }

        int diameter = Math.Min(Math.Min(radius * 2, rect.Width), rect.Height);
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>启用双缓冲的 DataGridView，减少高刷新率下的闪烁。</summary>
public sealed class SmoothDataGridView : DataGridView
{
    public SmoothDataGridView()
    {
        DoubleBuffered = true;
    }
}
