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
        // 折线
        var pts = new PointF[_hist.Count];
        for (int i = 0; i < _hist.Count; i++)
        {
            float x = Width - (_hist.Count - 1 - i) * (Width / (float)(Capacity - 1));
            float y = Height - 4 - (float)(_hist[i] / 100.0 * (Height - 16));
            pts[i] = new PointF(Math.Max(0, x), y);
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
