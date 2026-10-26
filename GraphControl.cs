using System.Drawing.Drawing2D;

namespace OpenLasso;

/// <summary>CPU、GPU 与内存占用历史曲线，带时间轴、悬停读数和暂停/清空操作。</summary>
public sealed class GraphControl : Control
{
    readonly record struct Sample(DateTime TimestampUtc, double Cpu, double? Gpu, double Memory);

    Sample[] _samples = new Sample[120];
    int _head;
    int _count;
    int _capacity = 120;
    int _hoverIndex = -1;
    string _gpuEngine = "";
    string _gpuStatus = "GPU 监控初始化中";
    bool _paused;
    TimeSpan _timeWindow = TimeSpan.FromMinutes(2);
    TimeSpan _samplingInterval = TimeSpan.FromSeconds(1);
    float _plotTop;

    public int Capacity
    {
        get => _capacity;
        set
        {
            int next = Math.Max(2, value);
            if (next == _capacity) return;
            int keep = Math.Min(_count, next);
            int skip = _count - keep;
            var resized = new Sample[next];
            for (int i = 0; i < keep; i++) resized[i] = At(skip + i);
            _samples = resized;
            _head = 0;
            _count = keep;
            _capacity = next;
            Invalidate();
        }
    }

    public string Title { get; set; } = "系统占用";
    public TimeSpan TimeWindow
    {
        get => _timeWindow;
        set
        {
            var next = value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value;
            if (next == _timeWindow) return;
            _timeWindow = next;
            ApplySamplingCapacity();
            Invalidate();
        }
    }

    public TimeSpan SamplingInterval
    {
        get => _samplingInterval;
        set
        {
            var next = value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
            if (next == _samplingInterval) return;
            _samplingInterval = next;
            ApplySamplingCapacity();
            Invalidate();
        }
    }
    public bool Paused => _paused;
    public int SampleCount => _count;
    public double? CurrentCpu => _count > 0 ? At(_count - 1).Cpu : null;
    public double? CurrentGpu => _count > 0 ? At(_count - 1).Gpu : null;
    public double? CurrentMemory => _count > 0 ? At(_count - 1).Memory : null;

    void ApplySamplingCapacity()
    {
        int desired = (int)Math.Ceiling(_timeWindow.TotalMilliseconds / _samplingInterval.TotalMilliseconds) + 2;
        Capacity = desired;
    }

    public string GpuStatus
    {
        get => _gpuStatus;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "GPU 监控状态未知" : value;
            if (next == _gpuStatus) return;
            _gpuStatus = next;
            Invalidate();
        }
    }

    public string GpuEngine
    {
        get => _gpuEngine;
        set
        {
            string next = value?.Trim() ?? "";
            if (next == _gpuEngine) return;
            _gpuEngine = next;
            Invalidate();
        }
    }

    public GraphControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Text;
        Font = UiTheme.BodyFont;

        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("暂停曲线");
        pause.Click += (s, e) => SetPaused(!Paused);
        var clear = new ToolStripMenuItem("清空曲线");
        clear.Click += (s, e) => Clear();
        menu.Items.Add(pause);
        menu.Items.Add(clear);
        menu.Opening += (s, e) => pause.Text = Paused ? "继续曲线" : "暂停曲线";
        UiTheme.StyleContextMenu(menu);
        ContextMenuStrip = menu;
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused) _hoverIndex = -1;
        Invalidate();
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _hoverIndex = -1;
        Invalidate();
    }

    public void Push(double cpu, double? gpu, double memory)
    {
        if (_paused) return;
        var sample = new Sample(DateTime.UtcNow,
            Math.Clamp(cpu, 0d, 100d),
            gpu.HasValue ? Math.Clamp(gpu.Value, 0d, 100d) : null,
            Math.Clamp(memory, 0d, 100d));
        if (_count < _capacity)
        {
            _samples[(_head + _count) % _capacity] = sample;
            _count++;
        }
        else
        {
            _samples[_head] = sample;
            _head = (_head + 1) % _capacity;
        }
        Invalidate();
    }

    Sample At(int chronologicalIndex) => _samples[(_head + chronologicalIndex) % _capacity];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = FindNearestSample(e.X);
        if (hover == _hoverIndex) return;
        _hoverIndex = hover;
        Cursor = hover >= 0 ? Cursors.Cross : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    int FindNearestSample(int mouseX)
    {
        if (_count == 0) return -1;
        var plot = PlotRect(DeviceDpi / 96f);
        if (!plot.Contains(mouseX, plot.Top + plot.Height / 2f)) return -1;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            float x = SampleX(i, plot);
            float distance = Math.Abs(x - mouseX);
            if (distance < bestDistance) { best = i; bestDistance = distance; }
        }
        return bestDistance <= 18f * DeviceDpi / 96f ? best : -1;
    }

    RectangleF PlotRect(float scale, Size client)
    {
        float left = 44f * scale;
        float right = 12f * scale;
        float top = Math.Max(58f * scale, _plotTop);
        float bottom = 30f * scale;
        return new RectangleF(left, top, Math.Max(1f, client.Width - left - right),
            Math.Max(1f, client.Height - top - bottom));
    }

    RectangleF PlotRect(float scale) => PlotRect(scale, ClientSize);

    float SampleX(int index, RectangleF plot)
    {
        if (_count <= 1) return plot.Right;
        var latest = At(_count - 1).TimestampUtc;
        double windowMs = Math.Max(1000d, TimeWindow.TotalMilliseconds);
        double ageMs = Math.Max(0d, (latest - At(index).TimestampUtc).TotalMilliseconds);
        return plot.Right - (float)Math.Clamp(ageMs / windowMs, 0d, 1d) * plot.Width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(UiTheme.Surface);

        float scale = DeviceDpi / 96f;
        TextRenderer.DrawText(g, Title, UiTheme.BodyMediumFont,
            new Point((int)(12f * scale), (int)(8f * scale)), UiTheme.Text,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        var current = _count > 0 ? At(_count - 1) : (Sample?)null;
        bool compact = ClientSize.Width < (int)(470 * scale);
        float legendX = 12f * scale;
        float legendY = 31f * scale;
        float legendRight = ClientSize.Width - 12f * scale;
        var legend = DrawLegend(g, legendX, legendY, legendRight, scale,
            "CPU", current?.Cpu, UiTheme.Primary);
        string gpuLabel = compact ? "GPU"
            : string.IsNullOrWhiteSpace(GpuEngine) ? "GPU（最繁忙引擎）" : $"GPU（{GpuEngine}）";
        legend = DrawLegend(g, legend.X, legend.Y, legendRight, scale,
            gpuLabel, current?.Gpu, UiTheme.Gpu);
        legend = DrawLegend(g, legend.X, legend.Y, legendRight, scale,
            compact ? "内存" : "内存（系统 RAM）", current?.Memory, UiTheme.Memory);
        _plotTop = Math.Max(58f * scale, legend.Y + 8f * scale);

        var plot = PlotRect(scale);
        using var plotFill = new SolidBrush(Color.FromArgb(250, 252, 255));
        g.FillRectangle(plotFill, plot);
        using var gridPen = new Pen(Color.FromArgb(226, 232, 240), 1f) { DashStyle = DashStyle.Dot };
        using var axisPen = new Pen(UiTheme.Border, 1f);
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            string label = (100 - i * 25).ToString();
            var size = TextRenderer.MeasureText(g, label, UiTheme.MonoSmallFont);
            TextRenderer.DrawText(g, label, UiTheme.MonoSmallFont,
                new Point((int)(plot.Left - size.Width - 8f * scale), (int)y - size.Height / 2),
                UiTheme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
        g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        DrawTimeline(g, plot, scale);

        if (_count == 0)
        {
            string emptyText = Paused ? "曲线已暂停" : "数据积累中…";
            var size = TextRenderer.MeasureText(g, emptyText, UiTheme.SmallFont);
            TextRenderer.DrawText(g, emptyText, UiTheme.SmallFont,
                new Point((int)(plot.Left + (plot.Width - size.Width) / 2),
                          (int)(plot.Top + (plot.Height - size.Height) / 2)),
                UiTheme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            DrawGpuStatus(g, plot, scale);
            return;
        }

        DrawSeries(s => s.Memory, UiTheme.Memory, plot, scale);
        DrawSeries(s => s.Gpu, UiTheme.Gpu, plot, scale);
        DrawSeries(s => s.Cpu, UiTheme.Primary, plot, scale);
        DrawGpuStatus(g, plot, scale);
        DrawHover(g, plot, scale);
        return;

        void DrawSeries(Func<Sample, double?> select, Color color, RectangleF area, float dpiScale)
        {
            var segment = new List<PointF>();
            for (int i = 0; i < _count; i++)
            {
                double? value = select(At(i));
                if (!value.HasValue)
                {
                    Flush();
                    continue;
                }

                float y = area.Bottom - (float)(value.Value / 100d * area.Height);
                segment.Add(new PointF(SampleX(i, area), y));
            }
            Flush();

            void Flush()
            {
                if (segment.Count >= 2)
                {
                    using var line = new Pen(color, 2f * dpiScale)
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawLines(line, segment.ToArray());
                }
                else if (segment.Count == 1)
                {
                    using var dot = new SolidBrush(color);
                    var p = segment[0];
                    g.FillEllipse(dot, p.X - 2.5f * dpiScale, p.Y - 2.5f * dpiScale,
                        5f * dpiScale, 5f * dpiScale);
                }
                segment.Clear();
            }
        }
    }

    void DrawTimeline(Graphics g, RectangleF plot, float scale)
    {
        using var tickPen = new Pen(Color.FromArgb(214, 222, 232), 1f);
        (double fraction, string label)[] ticks =
        {
            (0d, "-" + DurationLabel(TimeWindow)),
            (0.5d, "-" + DurationLabel(TimeWindow / 2)),
            (1d, "现在"),
        };
        foreach (var (fraction, label) in ticks)
        {
            float x = plot.Left + (float)(plot.Width * fraction);
            g.DrawLine(tickPen, x, plot.Bottom, x, plot.Bottom + 4f * scale);
            var size = TextRenderer.MeasureText(g, label, UiTheme.MonoSmallFont);
            float tx = fraction == 1d ? x - size.Width : fraction == 0.5d ? x - size.Width / 2f : x;
            TextRenderer.DrawText(g, label, UiTheme.MonoSmallFont,
                new Point((int)tx, (int)(plot.Bottom + 7f * scale)), UiTheme.Muted,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    static string DurationLabel(TimeSpan span)
    {
        if (span.TotalMinutes >= 1)
        {
            double minutes = span.TotalMinutes;
            return minutes == Math.Truncate(minutes) ? $"{minutes:0}m" : $"{minutes:0.#}m";
        }
        return $"{Math.Max(1, span.TotalSeconds):0}s";
    }

    void DrawGpuStatus(Graphics g, RectangleF plot, float scale)
    {
        if (CurrentGpu.HasValue) return;
        string text = GpuStatus;
        if (string.IsNullOrWhiteSpace(text)) return;
        var area = new Rectangle((int)(plot.Left + 8f * scale), (int)(plot.Top + 8f * scale),
            Math.Max(1, (int)(plot.Width - 16f * scale)), (int)(20f * scale));
        TextRenderer.DrawText(g, text, UiTheme.SmallFont, area, UiTheme.Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    void DrawHover(Graphics g, RectangleF plot, float scale)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _count) return;
        var sample = At(_hoverIndex);
        float x = SampleX(_hoverIndex, plot);
        if (x < plot.Left - 1 || x > plot.Right + 1) return;

        using var hoverPen = new Pen(Color.FromArgb(120, 71, 85, 105), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(hoverPen, x, plot.Top, x, plot.Bottom);
        DrawPoint(sample.Cpu, UiTheme.Primary);
        if (sample.Gpu.HasValue) DrawPoint(sample.Gpu.Value, UiTheme.Gpu);
        DrawPoint(sample.Memory, UiTheme.Memory);

        string gpu = sample.Gpu.HasValue ? $"{sample.Gpu.Value:0.0}%" : "--";
        string text = $"{sample.TimestampUtc.ToLocalTime():HH:mm:ss}  CPU {sample.Cpu:0.0}%  GPU {gpu}  内存 {sample.Memory:0.0}%";
        var measured = TextRenderer.MeasureText(g, text, UiTheme.SmallMediumFont);
        int width = Math.Min((int)plot.Width - 8, measured.Width + (int)(16f * scale));
        int height = measured.Height + (int)(10f * scale);
        float minX = plot.Left + 4f * scale;
        float maxX = Math.Max(minX, plot.Right - width - 4f * scale);
        float bx = Math.Clamp(x + 8f * scale, minX, maxX);
        float by = plot.Top + 4f * scale;
        using var bg = new SolidBrush(Color.FromArgb(232, 15, 23, 42));
        var box = new RectangleF(bx, by, Math.Max(1, width), height);
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, text, UiTheme.SmallMediumFont,
            new Rectangle((int)(box.X + 7f * scale), (int)box.Y, Math.Max(1, width - (int)(14f * scale)), height),
            Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        void DrawPoint(double value, Color color)
        {
            float y = plot.Bottom - (float)(Math.Clamp(value, 0d, 100d) / 100d * plot.Height);
            using var fill = new SolidBrush(color);
            using var inner = new SolidBrush(UiTheme.Surface);
            g.FillEllipse(fill, x - 4f * scale, y - 4f * scale, 8f * scale, 8f * scale);
            g.FillEllipse(inner, x - 1.5f * scale, y - 1.5f * scale, 3f * scale, 3f * scale);
        }
    }

    static (float X, float Y) DrawLegend(Graphics g, float x, float y, float right, float scale,
        string label, double? value, Color color)
    {
        string text = value.HasValue ? $"{label} {value.Value:0.0}%" : $"{label} --";
        var size = TextRenderer.MeasureText(g, text, UiTheme.SmallMediumFont);
        float itemWidth = 7f * scale + 5f * scale + size.Width + 12f * scale;
        if (x > 12f * scale && x + itemWidth > right)
        {
            x = 12f * scale;
            y += 20f * scale;
        }

        float dot = 7f * scale;
        using var dotBrush = new SolidBrush(color);
        g.FillEllipse(dotBrush, x, y + 3f * scale, dot, dot);

        float textX = x + dot + 5f * scale;
        var textColor = !value.HasValue ? UiTheme.Muted
            : value.Value >= 90 ? UiTheme.Danger
            : value.Value >= 65 ? UiTheme.Warning
            : color;
        TextRenderer.DrawText(g, text, UiTheme.SmallMediumFont,
            new Rectangle((int)textX, (int)y, size.Width + 2, size.Height),
            textColor, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        return (textX + size.Width + 12f * scale, y + size.Height);
    }
}
