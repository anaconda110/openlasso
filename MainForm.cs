using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OpenLasso;

public class MainForm : Form
{
    readonly LassoConfig _cfg;
    readonly ActionLog _log;
    readonly GovernorEngine _eng;

    readonly DataGridView _grid = new();
    readonly DataTable _table = new();
    readonly GraphControl _cpuGraph = new() { Title = "总 CPU", Dock = DockStyle.Fill };
    readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro };
    readonly TextBox _infoBox = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro };
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly ToolStripStatusLabel _lblCpu = new("CPU -"), _lblRam = new("内存 -"), _lblPlan = new("电源 -"), _lblPb = new("ProBalance -"), _lblEng = new("引擎 -"), _lblFg = new("前台 -");
    bool _reallyExit;
    int _tick;
    IntPtr _hIcon;
    readonly bool _startHidden;
    volatile bool _allowShow;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static readonly (string cn, string token, ProcessPriorityClass cls)[] PriTable =
    {
        ("实时", "real time", ProcessPriorityClass.RealTime), ("高", "high", ProcessPriorityClass.High),
        ("高于正常", "above normal", ProcessPriorityClass.AboveNormal), ("正常", "normal", ProcessPriorityClass.Normal),
        ("低于正常", "below normal", ProcessPriorityClass.BelowNormal), ("空闲", "idle", ProcessPriorityClass.Idle),
    };

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        _cfg = new LassoConfig(AppPaths.ConfigPath);
        _log = new ActionLog(AppPaths.LogDir);
        _eng = new GovernorEngine(_cfg, _log);
        Text = $"OpenLasso v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} — 开源进程优化器（Process Lasso 复刻）";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1250, 780);
        MinimumSize = new Size(900, 560);
        BuildMenu();
        BuildUi();
        _tray = new NotifyIcon { Text = "OpenLasso", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _hIcon = MakeIcon(0);
        _tray.Icon = Icon.FromHandle(_hIcon);
        _tray.DoubleClick += (s, e) => ShowFromTray();
        _timer.Tick += (s, e) => RefreshView();
        _eng.Start();
        _timer.Start();
        FormClosing += (s, e) => { if (!_reallyExit) { e.Cancel = true; Hide(); } };
        FormClosed += (s, e) => { _tray.Visible = false; _eng.Dispose(); };
    }

    // --minimized 启动：首次不显示窗口（托盘可用），从托盘打开后恢复常规显示语义
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !_allowShow) value = false;
        base.SetVisibleCore(value);
    }
    void ShowFromTray() { _allowShow = true; Show(); Activate(); }

    void BuildMenu()
    {
        var ms = new MenuStrip();
        var mFile = new ToolStripMenuItem("文件(&F)");
        mFile.DropDownItems.Add("规则编辑器…", null, (s, e) => { using var f = new RulesForm(_cfg, _eng); f.ShowDialog(this); });
        mFile.DropDownItems.Add("设置…", null, (s, e) => { using var f = new SettingsForm(_cfg); if (f.ShowDialog(this) == DialogResult.OK) { _eng.ReloadRules(); _timer.Interval = _cfg.UpdateSpeedMs; } });
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("立即智能整理", null, (s, e) => Task.Run(() => _eng.RunSmartTrim()));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        var mEng = new ToolStripMenuItem("引擎(&E)");
        var mPause = new ToolStripMenuItem("暂停引擎");
        mPause.Click += (s, e) => { _eng.Paused = !_eng.Paused; mPause.Text = _eng.Paused ? "恢复引擎" : "暂停引擎"; };
        mEng.DropDownItems.Add(mPause);
        mEng.DropDownItems.Add("重载配置与规则", null, (s, e) => { _cfg.Reload(); _eng.ReloadRules(); });
        var mHelp = new ToolStripMenuItem("帮助(&H)");
        mHelp.DropDownItems.Add("关于 OpenLasso", null, (s, e) => MessageBox.Show(
            "OpenLasso — 开源进程优化器\nProcess Lasso (Bitsum) 核心功能的复刻实现\n\n引擎：进程规则 / ProBalance / SmartTrim / 电源自动化\n配置：兼容读取 prolasso.ini\n日志：openlasso.log（与 PL 同列结构）\n\n以管理员身份运行可管理其他用户的进程。", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information));
        ms.Items.AddRange(new ToolStripItem[] { mFile, mEng, mHelp });
        MainMenuStrip = ms;
        Controls.Add(ms);
    }

    void BuildUi()
    {
        var status = new StatusStrip();
        status.Items.AddRange(new ToolStripItem[] { _lblCpu, new ToolStripSeparator(), _lblRam, new ToolStripSeparator(), _lblPlan, new ToolStripSeparator(), _lblPb, new ToolStripSeparator(), _lblFg, new ToolStripSeparator(), _lblEng });
        Controls.Add(status);

        _table.Columns.Add("名称", typeof(string));
        _table.Columns.Add("PID", typeof(int));
        _table.Columns.Add("CPU%", typeof(double));
        _table.Columns.Add("优先级", typeof(string));
        _table.Columns.Add("亲和", typeof(string));
        _table.Columns.Add("内存MB", typeof(double));
        _table.Columns.Add("会话", typeof(int));
        _table.Columns.Add("描述", typeof(string));
        _table.Columns.Add("规则", typeof(string));
        _table.Columns.Add("pid_key", typeof(int));
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        void Col(string header, string prop, int fill)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = prop, HeaderText = header, DataPropertyName = prop, FillWeight = fill, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        Col("名称", "名称", 26); Col("PID", "PID", 10); Col("CPU%", "CPU%", 16); Col("优先级", "优先级", 14);
        Col("亲和", "亲和", 10); Col("内存MB", "内存MB", 12); Col("会话", "会话", 8); Col("描述", "描述", 40); Col("规则", "规则", 20);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid_key", HeaderText = "pid_key", DataPropertyName = "pid_key", Visible = false });
        _grid.DataSource = _table;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ContextMenuStrip = BuildGridMenu();
        _grid.CellFormatting += (s, e) =>
        {
            var col = _grid.Columns["CPU%"];
            if (col != null && e.ColumnIndex == col.Index && e.Value is double d && d >= 5)
                e.CellStyle.ForeColor = d >= 30 ? Color.Firebrick : Color.Chocolate;
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 880, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_grid);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        var graphHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        graphHost.Controls.Add(_cpuGraph);
        var infoHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2) };
        infoHost.Controls.Add(_infoBox);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2, 0, 2, 2) };
        logHost.Controls.Add(_logBox);
        right.Controls.Add(graphHost, 0, 0);
        right.Controls.Add(infoHost, 0, 1);
        right.Controls.Add(logHost, 0, 2);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        split.BringToFront();
    }

    ContextMenuStrip BuildGridMenu()
    {
        var menu = new ContextMenuStrip();
        var mPri = new ToolStripMenuItem("设置优先级");
        foreach (var (cn, _, cls) in PriTable)
            mPri.DropDownItems.Add(cn, null, (s, e) => WithSel(pi => _eng.SetPriorityInternal(pi.Pid, cls, "手动")));
        menu.Items.Add(mPri);
        menu.Items.Add("设置亲和性…", null, (s, e) => WithSel(pi =>
        {
            using var dlg = new AffinityDialog(Environment.ProcessorCount, pi.AffinityMask);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Mask != 0) _eng.SetAffinityInternal(pi.Pid, dlg.Mask, "手动");
        }));
        var mEco = new ToolStripMenuItem("能效模式");
        mEco.DropDownItems.Add("开启", null, (s, e) => WithSel(pi => _eng.EcoInternal(pi.Pid, true)));
        mEco.DropDownItems.Add("关闭", null, (s, e) => WithSel(pi => _eng.EcoInternal(pi.Pid, false)));
        menu.Items.Add(mEco);
        menu.Items.Add("立即整理工作集", null, (s, e) => WithSel(pi => { if (_eng.TrimInternal(pi.Pid)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_TRIM_ONE, "手动整理工作集", pi.Path); }));
        menu.Items.Add("结束进程", null, (s, e) => WithSel(pi => _eng.KillInternal(pi.Pid, ActionLog.ACT_KILL, "手动终止")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("始终(默认)设置优先级…", null, (s, e) => WithSel(pi =>
        {
            var v = PromptCombo("始终设置优先级 — " + pi.Name, PriTable.Select(x => x.cn).ToArray(), "低于正常");
            if (v != null) AddPairRule("DefaultPriorities", pi.Name, Array.Find(PriTable, x => x.cn == v).token);
        }));
        menu.Items.Add("始终限制 CPU 百分比…", null, (s, e) => WithSel(pi =>
        {
            var v = PromptCombo("CPU 限额（%） — " + pi.Name, new[] { "10", "20", "30", "50", "70", "90" }, "50");
            if (v != null) AddPairRule("CPULimitRules", pi.Name, v);
        }));
        menu.Items.Add("排除出 ProBalance", null, (s, e) => WithSel(pi =>
        {
            if (!_cfg.ProBalanceExclusions.Contains(pi.Name, StringComparer.OrdinalIgnoreCase)) _cfg.ProBalanceExclusions.Add(pi.Name);
            _cfg.Ini.Set("OutOfControlProcessRestraint", "OocExclusions", IniFile.JoinList(_cfg.ProBalanceExclusions));
            SaveAndReload();
        }));
        menu.Items.Add("禁止此进程（黑名单）", null, (s, e) => WithSel(pi => AddListRule("DisallowedProcesses", pi.Name)));
        menu.Items.Add("保持运行（自动重启）", null, (s, e) => WithSel(pi => AddListRule("KeepRunningProcessesEx", pi.Name)));
        return menu;
    }

    string GetStringField(string key) => key switch
    {
        "DefaultPriorities" => _cfg.DefaultPriorities,
        "CPULimitRules" => _cfg.CpuLimits,
        "DisallowedProcesses" => _cfg.Disallowed,
        "KeepRunningProcessesEx" => _cfg.KeepRunning,
        _ => "",
    };
    void SetStringField(string key, string v)
    {
        switch (key)
        {
            case "DefaultPriorities": _cfg.DefaultPriorities = v; break;
            case "CPULimitRules": _cfg.CpuLimits = v; break;
            case "DisallowedProcesses": _cfg.Disallowed = v; break;
            case "KeepRunningProcessesEx": _cfg.KeepRunning = v; break;
        }
    }
    void AddPairRule(string key, string name, string value)
    {
        var cur = IniFile.ParsePairs(GetStringField(key));
        cur.RemoveAll(x => x.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        cur.Add((name, value));
        SetStringField(key, string.Join(",", cur.Select(x => x.name + "," + x.val)));
        SaveAndReload();
    }
    void AddListRule(string key, string name)
    {
        var cur = IniFile.SplitList(GetStringField(key));
        if (!cur.Contains(name, StringComparer.OrdinalIgnoreCase)) cur.Add(name);
        SetStringField(key, IniFile.JoinList(cur));
        SaveAndReload();
    }
    void SaveAndReload() { _cfg.Save(); _cfg.Reload(); _eng.ReloadRules(); }

    void WithSel(Action<ProcInfo> f)
    {
        if (_grid.CurrentRow?.Cells["pid_key"].Value is not int pid) return;
        var pi = _eng.Current.FirstOrDefault(x => x.Pid == pid);
        if (pi != null) f(pi);
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("显示主窗口", null, (s, e) => ShowFromTray());
        var mPause = new ToolStripMenuItem("暂停引擎");
        mPause.Click += (s, e) => { _eng.Paused = !_eng.Paused; mPause.Text = _eng.Paused ? "恢复引擎" : "暂停引擎"; };
        m.Items.Add(mPause);
        m.Items.Add("立即智能整理", null, (s, e) => Task.Run(() => _eng.RunSmartTrim()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        return m;
    }

    void RefreshView()
    {
        var list = _eng.Current;
        int selPid = _grid.CurrentRow?.Cells["pid_key"].Value is int p ? p : 0;
        _table.BeginLoadData();
        _table.Rows.Clear();
        foreach (var pi in list)
            _table.Rows.Add(pi.Name, pi.Pid, pi.CpuPercent, PriName(pi.Priority), PopCount(pi.AffinityMask) + " 核",
                pi.WorkingSetMB, pi.SessionId, pi.Desc, pi.Note.Trim(), pi.Pid);
        _table.EndLoadData();
        if (selPid != 0)
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Cells["pid_key"].Value is int q && q == selPid) { _grid.CurrentCell = row.Cells["名称"]; break; }

        _cpuGraph.Push(_eng.TotalCpu);
        _lblCpu.Text = $"CPU {_eng.TotalCpu:0.0}%";
        _lblRam.Text = $"内存 {_eng.RamLoad:0}%（可用 {_eng.AvailRamMB:0}/{_eng.TotalRamMB:0} MB）";
        _lblPlan.Text = "电源 " + (Native.ActivePlanName() ?? "-");
        _lblPb.Text = _eng.ProBalanceActive ? $"ProBalance 激活（{_eng.ProBalanceInfo}）" : (_cfg.ProBalanceEnabled ? "ProBalance 待命" : "ProBalance 关闭");
        _lblFg.Text = string.IsNullOrEmpty(_eng.ForegroundName) ? "前台 -" : $"前台 {_eng.ForegroundName}";
        _lblEng.Text = _eng.Paused ? "引擎已暂停" : "引擎运行中";
        _lblPb.ForeColor = _eng.ProBalanceActive ? Color.Firebrick : SystemColors.ControlText;
        var top5 = string.Join("  ", list.Take(5).Select(x => $"{x.Name}={x.CpuPercent:0.0}%"));
        _infoBox.Text = $"进程总数: {list.Count}\nCPU 前 5: {top5}\n前台 PID: {_eng.ForegroundPid} ({_eng.ForegroundName})\n" +
            $"规则: 优先级 {IniFile.ParsePairs(_cfg.DefaultPriorities).Count} 亲和 {IniFile.ParsePairs(_cfg.DefaultAffinities).Count} 限额 {IniFile.ParsePairs(_cfg.CpuLimits).Count} 黑名单 {IniFile.SplitList(_cfg.Disallowed).Count}\n" +
            $"配置: {_cfg.ConfigPath}{(_cfg.ImportedFrom != null ? "\n导入自: " + _cfg.ImportedFrom : "")}";

        _tick++;
        if (_tick % 3 == 0)
        {
            _logBox.Text = string.Join("\r\n", _log.Tail(200).TakeLast(40));
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        UpdateTrayIcon();
    }

    void UpdateTrayIcon()
    {
        int cpu = (int)Math.Round(_eng.TotalCpu);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = MakeIcon(cpu);
        _tray.Icon = Icon.FromHandle(_hIcon);
        _tray.Text = $"OpenLasso — CPU {cpu}%  内存 {_eng.RamLoad:0}%" + (_eng.ProBalanceActive ? "  [ProBalance]" : "");
    }

    static IntPtr MakeIcon(int cpu)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = cpu < 40 ? Color.FromArgb(46, 160, 67) : cpu < 70 ? Color.FromArgb(204, 120, 0) : Color.Firebrick;
        using var b = new SolidBrush(color);
        g.FillRectangle(b, 0, 16 - Math.Max(2, cpu * 16 / 100), 16, 16);
        using var f = new Font("Segoe UI", 7f, FontStyle.Bold);
        using var w = new SolidBrush(Color.White);
        var txt = cpu > 99 ? "99" : cpu.ToString();
        var sz = g.MeasureString(txt, f);
        g.DrawString(txt, f, w, Math.Max(0, (16 - sz.Width) / 2), Math.Max(0, (16 - sz.Height) / 2));
        return bmp.GetHicon();
    }

    static string PriName(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.RealTime => "实时", ProcessPriorityClass.High => "高",
        ProcessPriorityClass.AboveNormal => "高于正常", ProcessPriorityClass.BelowNormal => "低于正常",
        ProcessPriorityClass.Idle => "空闲", _ => "正常",
    };
    static int PopCount(long v) { int c = 0; while (v != 0) { c += (int)(v & 1); v >>= 1; } return c; }

    static string PromptCombo(string title, string[] options, string def)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(340, 132), MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = "选择规则值：", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 7, 0, 0) };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        foreach (var o in options) combo.Items.Add(o);
        int idx = Array.IndexOf(options, def);
        combo.SelectedIndex = idx >= 0 ? idx : 0;
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom };
        f.Controls.Add(combo); f.Controls.Add(lbl); f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? combo.SelectedItem?.ToString() : null;
    }
}

/// <summary>亲和性对话框：按逻辑核勾选</summary>
public class AffinityDialog : Form
{
    public long Mask;
    readonly CheckBox[] _boxes;

    public AffinityDialog(int cores, long cur)
    {
        cores = Math.Clamp(cores <= 0 ? Environment.ProcessorCount : cores, 1, 64);
        Text = "设置 CPU 亲和性";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        int cols = Math.Min(8, cores);
        int rows = (cores + cols - 1) / cols;
        ClientSize = new Size(cols * 52 + 24, rows * 34 + 76);
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = rows * 34 + 8, ColumnCount = cols, RowCount = rows, Padding = new Padding(8) };
        _boxes = new CheckBox[cores];
        for (int i = 0; i < cores; i++)
        {
            _boxes[i] = new CheckBox { Text = i.ToString(), Checked = (cur & (1L << i)) != 0, AutoSize = true, Margin = new Padding(4, 6, 4, 2) };
            panel.Controls.Add(_boxes[i], i % cols, i / cols);
        }
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 196, ClientSize.Height - 40) };
        var all = new Button { Text = "全选", Width = 60, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Location = new Point(12, ClientSize.Height - 40) };
        all.Click += (s, e) => Array.ForEach(_boxes, b => b.Checked = true);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 96, ClientSize.Height - 40) };
        Controls.Add(panel); Controls.Add(ok); Controls.Add(cancel); Controls.Add(all);
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += (s, e) =>
        {
            if (DialogResult == DialogResult.OK)
            { Mask = 0; for (int i = 0; i < cores; i++) if (_boxes[i].Checked) Mask |= 1L << i; }
        };
    }
}
