namespace OpenLasso;

/// <summary>CPU 历史曲线（0..100 填充折线 + 网格）</summary>
public class GraphControl : Control
{
    readonly List<double> _hist = new();
    public int Capacity { get; set; } = 120;
    public string Title { get; set; } = "";
    public Color LineColor { get; set; } = Color.FromArgb(0, 122, 204);

    public void Push(double v) { _hist.Add(Math.Clamp(v, 0, 100)); while (_hist.Count > Capacity) _hist.RemoveAt(0); Invalidate(); }

    public GraphControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(32, 32, 32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fg = new SolidBrush(ForeColor);
        // 网格
        using var grid = new Pen(Color.FromArgb(50, 50, 50), 1);
        for (int i = 1; i < 4; i++) g.DrawLine(grid, 0, Height * i / 4, Width, Height * i / 4);
        // 标题与当前值
        var cur = _hist.Count > 0 ? _hist[^1] : 0;
        g.DrawString($"{Title}  {cur:0.0}%", Font, fg, 4, 2);
        if (_hist.Count < 2) return;
        // 横轴按当前采样数铺满全宽（从左生长），攒满 Capacity 后自然过渡为整幅滚动曲线
        int n = _hist.Count;
        float step = Width / (float)(n - 1);
        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float y = Height - 4 - (float)(_hist[i] / 100.0 * (Height - 16));
            pts[i] = new PointF(i * step, y);
        }
        var fillPts = pts.ToList();
        fillPts.Insert(0, new PointF(fillPts[0].X, Height));
        fillPts.Add(new PointF(pts[^1].X, Height));
        using var fill = new SolidBrush(Color.FromArgb(40, LineColor));
        g.FillPolygon(fill, fillPts.ToArray());
        using var pen = new Pen(LineColor, 1.6f);
        g.DrawLines(pen, pts);
    }
}
