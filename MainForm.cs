using System.ComponentModel;
using System.Data;
using System.Diagnostics;

namespace OpenLasso;

public class MainForm : Form
{
    readonly LassoConfig _cfg;
    readonly ActionLog _log;
    readonly GovernorEngine _eng;

    readonly SplitContainer _split = new();
    readonly SmoothDataGridView _grid = new();
    readonly DataTable _table = new();
    readonly Dictionary<ProcessIdentity, DataRow> _rowByIdentity = new();
    readonly Queue<string> _logLines = new();
    readonly GraphControl _usageGraph = new() { Title = "系统占用", Dock = DockStyle.Fill };
    readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = UiTheme.MonoFont, BackColor = UiTheme.SurfaceAlt, ForeColor = UiTheme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox _infoBox = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = UiTheme.MonoFont, BackColor = UiTheme.SurfaceAlt, ForeColor = UiTheme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly NotifyIcon _tray;
    readonly Icon _windowIcon;
    readonly Icon _trayIcon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly ToolStripStatusLabel _lblCpu = new("CPU -"), _lblRam = new("内存 -"), _lblGpu = new("GPU -"),
        _lblPlan = new("电源 -"), _lblPb = new("ProBalance -"), _lblEng = new("引擎 -"),
        _lblFg = new("前台 -"), _lblScope = new("权限 -") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    ToolStripSeparator _sepPlan, _sepFg;
    ToolStripMenuItem _menuPause, _trayPause;
    bool _reallyExit;
    int _tick;
    long _lastSnapshotSequence = -1;
    DateTime _lastProcessTableRefreshUtc = DateTime.MinValue;
    string _trayText = "";
    readonly bool _startHidden;
    volatile bool _allowShow;

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
        Text = $"OpenLasso v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} — 开源进程优化器";
        _windowIcon = AppIcon.Load();
        _trayIcon = AppIcon.Load();
        Icon = _windowIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1250, 780);
        MinimumSize = new Size(900, 560);
        UiTheme.ApplyForm(this);
        BuildMenu();
        BuildUi();
        Shown += (s, e) => { InitializeSplitLayout(); UpdateStatusDensity(); };
        _tray = new NotifyIcon { Text = "OpenLasso", Visible = true, Icon = _trayIcon, ContextMenuStrip = BuildTrayMenu() };
        _tray.DoubleClick += (s, e) => ShowFromTray();
        _timer.Interval = _cfg.UpdateSpeedMs;
        _usageGraph.SamplingInterval = TimeSpan.FromMilliseconds(_cfg.UpdateSpeedMs);
        _timer.Tick += (s, e) => RefreshView();
        _eng.Start();
        _timer.Start();
        FormClosing += (s, e) => { if (!_reallyExit) { e.Cancel = true; Hide(); } };
        FormClosed += (s, e) =>
        {
            _tray.Visible = false;
            _eng.Dispose();
            _tray.ContextMenuStrip?.Dispose();
            _tray.Icon = null;
            _tray.Dispose();
            _trayIcon.Dispose();
            Icon = null;
            _windowIcon.Dispose();
        };
    }

    // --minimized 启动：首次不显示窗口（托盘可用），从托盘打开后恢复常规显示语义
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !_allowShow) value = false;
        base.SetVisibleCore(value);
    }
    void ShowFromTray() { _allowShow = true; Show(); Activate(); }

    void TogglePause()
    {
        _eng.SetPaused(!_eng.Paused);
        SyncPauseText(_eng.Snapshot);
    }

    void SyncPauseText(EngineSnapshot snapshot)
    {
        string text = snapshot?.Paused == true ? "恢复引擎" : "暂停引擎";
        if (_menuPause != null) _menuPause.Text = text;
        if (_trayPause != null) _trayPause.Text = text;
    }

    void RestartElevated()
    {
        try
        {
            string executable = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string entryAssembly = Path.Combine(AppContext.BaseDirectory, "OpenLasso.dll");
                if (!File.Exists(entryAssembly))
                    throw new InvalidOperationException("无法确定 OpenLasso 程序集路径。");
                psi.ArgumentList.Add(entryAssembly);
            }
            foreach (string arg in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(arg);
            if (Process.Start(psi) == null) throw new InvalidOperationException("未能启动提升权限的新实例。");
            _reallyExit = true;
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在系统授权界面取消，无需提示。
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法以管理员身份重启：\n" + ex.Message, "OpenLasso",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void BuildMenu()
    {
        var ms = new MenuStrip();
        var mFile = new ToolStripMenuItem("文件(&F)");
        mFile.DropDownItems.Add("规则编辑器…", null, (s, e) => { using var f = new RulesForm(_cfg, _eng); f.ShowDialog(this); });
        mFile.DropDownItems.Add("设置…", null, (s, e) =>
        {
            using var f = new SettingsForm(_cfg);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            _eng.ReloadRules();
            _timer.Interval = _cfg.UpdateSpeedMs;
            _usageGraph.SamplingInterval = TimeSpan.FromMilliseconds(_cfg.UpdateSpeedMs);
        });
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("立即智能整理", null, (s, e) => Task.Run(() => _eng.RunSmartTrim()));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        var elevate = new ToolStripMenuItem(Native.IsAdministrator() ? "已以管理员身份运行" : "以管理员身份重启…");
        elevate.Enabled = !Native.IsAdministrator();
        elevate.Click += (s, e) => RestartElevated();
        mFile.DropDownItems.Add(elevate);
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        var mEng = new ToolStripMenuItem("引擎(&E)");
        _menuPause = new ToolStripMenuItem("暂停引擎");
        _menuPause.Click += (s, e) => TogglePause();
        mEng.DropDownItems.Add(_menuPause);
        mEng.DropDownItems.Add("重载配置与规则", null, (s, e) => { _cfg.Reload(); _eng.ReloadRules(); });
        var mHelp = new ToolStripMenuItem("帮助(&H)");
        mHelp.DropDownItems.Add("关于 OpenLasso", null, (s, e) => MessageBox.Show(
            "OpenLasso — 开源进程优化器\n兼容 Process Lasso (Bitsum) 核心功能的开源实现\n\n引擎：进程规则 / ProBalance / SmartTrim / 电源自动化\n配置：兼容读取 prolasso.ini\n日志：openlasso.log（与 PL 同列结构）\n\n以管理员身份运行可管理其他用户的进程。", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information));
        ms.Items.AddRange(new ToolStripItem[] { mFile, mEng, mHelp });
        UiTheme.StyleMenu(ms);
        MainMenuStrip = ms;
        Controls.Add(ms);
    }

    void BuildUi()
    {
        var status = new StatusStrip
        {
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.SmallFont,
            SizingGrip = false,
        };
        status.Items.AddRange(new ToolStripItem[]
        {
            _lblCpu, new ToolStripSeparator(), _lblRam, new ToolStripSeparator(), _lblGpu,
            new ToolStripSeparator(), _lblPlan, _sepPlan = new ToolStripSeparator(), _lblPb,
            new ToolStripSeparator(), _lblEng, _sepFg = new ToolStripSeparator(), _lblFg,
            new ToolStripSeparator(), _lblScope,
        });
        foreach (ToolStripItem item in status.Items) item.ForeColor = UiTheme.Muted;
        _lblEng.ForeColor = UiTheme.Success;
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
        _table.Columns.Add("start_key", typeof(long));
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        void Col(string header, string prop, int fill)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = prop, HeaderText = header, DataPropertyName = prop, FillWeight = fill, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        Col("名称", "名称", 26); Col("PID", "PID", 10); Col("CPU%", "CPU%", 16); Col("优先级", "优先级", 14);
        Col("亲和", "亲和", 10); Col("内存MB", "内存MB", 12); Col("会话", "会话", 8); Col("描述", "描述", 40); Col("规则", "规则", 20);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid_key", HeaderText = "pid_key", DataPropertyName = "pid_key", Visible = false });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "start_key", HeaderText = "start_key", DataPropertyName = "start_key", Visible = false });
        _grid.DataSource = _table;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        UiTheme.StyleDataGridView(_grid);
        _grid.ContextMenuStrip = BuildGridMenu();
        _grid.CellFormatting += (s, e) =>
        {
            var col = _grid.Columns["CPU%"];
            if (col != null && e.ColumnIndex == col.Index && e.Value is double d && d >= 5)
                e.CellStyle.ForeColor = d >= 30 ? UiTheme.Danger : UiTheme.Warning;
        };

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.FixedPanel = FixedPanel.Panel2;
        _split.BackColor = UiTheme.Border;
        _split.Panel1.Controls.Add(_grid);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Background, Padding = new Padding(8, 4, 4, 4) };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        var graphHost = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 8) };
        graphHost.Controls.Add(_usageGraph);
        var infoHost = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 8) };
        infoHost.Controls.Add(_infoBox);
        var logHost = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0) };
        logHost.Controls.Add(_logBox);
        right.Controls.Add(graphHost, 0, 0);
        right.Controls.Add(infoHost, 0, 1);
        right.Controls.Add(logHost, 0, 2);
        _split.Panel2.Controls.Add(right);
        Controls.Add(_split);
        _split.BringToFront();
        Resize += (s, e) => UpdateStatusDensity();
    }

    void UpdateStatusDensity()
    {
        bool compact = ClientSize.Width < 1100;
        _lblPlan.Visible = !compact;
        _sepPlan.Visible = !compact;
        _lblFg.Visible = !compact;
        _sepFg.Visible = !compact;
    }

    void InitializeSplitLayout()
    {
        ConfigureSplitLayout(_split, DeviceDpi);
    }

    static void ConfigureSplitLayout(SplitContainer split, int dpi)
    {
        int width = split.ClientSize.Width;
        if (width <= 0) return;

        float scale = dpi / 96f;
        int minRight = (int)Math.Round(320 * scale);
        int maxRight = (int)Math.Round(400 * scale);
        int minLeft = (int)Math.Round(360 * scale);
        int minRightSize = (int)Math.Round(300 * scale);
        if (width < minLeft + minRightSize + split.SplitterWidth) return;

        int rightWidth = Math.Clamp((int)Math.Round(width * 0.30), minRight, maxRight);
        split.SplitterDistance = Math.Max(minLeft, width - split.SplitterWidth - rightWidth);
        split.Panel1MinSize = minLeft;
        split.Panel2MinSize = minRightSize;
    }

    internal static int ProcessTableRefreshInterval(int pollIntervalMs) => Math.Max(1000, pollIntervalMs);
    internal static bool ShouldRefreshProcessDetails(bool visible, FormWindowState state) =>
        visible && state != FormWindowState.Minimized;

    ContextMenuStrip BuildGridMenu()
    {
        var menu = new ContextMenuStrip();
        var mPri = new ToolStripMenuItem("设置优先级");
        foreach (var (cn, _, cls) in PriTable)
            mPri.DropDownItems.Add(cn, null, (s, e) => WithSel(pi => _eng.SetPriorityInternal(pi.Identity, cls, "手动")));
        menu.Items.Add(mPri);
        menu.Items.Add("设置亲和性…", null, (s, e) => WithSel(pi =>
        {
            using var dlg = new AffinityDialog(Environment.ProcessorCount, pi.AffinityMask);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Mask != 0) _eng.SetAffinityInternal(pi.Identity, dlg.Mask, "手动");
        }));
        var mEco = new ToolStripMenuItem("能效模式");
        mEco.DropDownItems.Add("开启", null, (s, e) => WithSel(pi => _eng.EcoInternal(pi.Identity, true)));
        mEco.DropDownItems.Add("关闭", null, (s, e) => WithSel(pi => _eng.EcoInternal(pi.Identity, false)));
        menu.Items.Add(mEco);
        menu.Items.Add("立即整理工作集", null, (s, e) => WithSel(pi => { if (_eng.TrimInternal(pi.Identity)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_TRIM_ONE, "手动整理工作集", pi.Path); }));
        menu.Items.Add("结束进程", null, (s, e) => WithSel(pi => _eng.KillInternal(pi.Identity, ActionLog.ACT_KILL, "手动终止")));
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
        UiTheme.StyleContextMenu(menu);
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
        if (_grid.CurrentRow?.Cells["pid_key"].Value is not int pid ||
            _grid.CurrentRow.Cells["start_key"].Value is not long startTicks) return;
        var identity = new ProcessIdentity(pid, startTicks);
        var pi = _eng.Current.FirstOrDefault(x => x.Identity == identity);
        if (pi == null) return;
        if (!pi.CanManage)
        {
            MessageBox.Show(this, "该进程不在当前管理范围内，或当前权限不足。", "OpenLasso",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        f(pi);
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("显示主窗口", null, (s, e) => ShowFromTray());
        _trayPause = new ToolStripMenuItem("暂停引擎");
        _trayPause.Click += (s, e) => TogglePause();
        m.Items.Add(_trayPause);
        m.Items.Add("立即智能整理", null, (s, e) => Task.Run(() => _eng.RunSmartTrim()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        UiTheme.StyleContextMenu(m);
        return m;
    }

    void RefreshView()
    {
        var snapshot = _eng.Snapshot;
        if (snapshot == null || snapshot.Sequence == _lastSnapshotSequence) return;
        _lastSnapshotSequence = snapshot.Sequence;

        var list = snapshot.Processes;
        var now = DateTime.UtcNow;
        bool detailsVisible = ShouldRefreshProcessDetails(Visible, WindowState);
        if (detailsVisible && (_lastProcessTableRefreshUtc == DateTime.MinValue ||
            (now - _lastProcessTableRefreshUtc).TotalMilliseconds >= ProcessTableRefreshInterval(_timer.Interval)))
        {
            var selected = SelectedIdentity();
            UpdateProcessTable(list);
            if (selected.HasValue) SelectIdentity(selected.Value);
            _lastProcessTableRefreshUtc = now;
        }
        else if (!detailsVisible)
        {
            // 窗口隐藏或最小化时保留曲线采样，但跳过昂贵的表格批更新；恢复显示后立即刷新。
            _lastProcessTableRefreshUtc = DateTime.MinValue;
        }

        _usageGraph.GpuEngine = snapshot.GpuEngine;
        _usageGraph.GpuStatus = snapshot.GpuStatus;
        _usageGraph.Push(snapshot.TotalCpu, snapshot.GpuLoad, snapshot.RamLoad);
        _lblCpu.Text = $"CPU {snapshot.TotalCpu:0.0}%";
        _lblRam.Text = $"内存 {snapshot.RamLoad:0}%（可用 {snapshot.AvailRamMB:0}/{snapshot.TotalRamMB:0} MB）";
        _lblGpu.Text = snapshot.GpuLoad.HasValue ? $"GPU {snapshot.GpuLoad.Value:0.0}%" : "GPU 不可用";
        _lblGpu.ToolTipText = snapshot.GpuStatus;
        _lblPlan.Text = "电源 " + (string.IsNullOrWhiteSpace(snapshot.PowerPlan) ? "-" : snapshot.PowerPlan);
        _lblPb.Text = snapshot.ProBalanceActive
            ? $"ProBalance 激活（{snapshot.ProBalanceInfo}）"
            : (_cfg.ProBalanceEnabled ? "ProBalance 待命" : "ProBalance 关闭");
        _lblFg.Text = string.IsNullOrEmpty(snapshot.ForegroundName) ? "前台 -" : $"前台 {snapshot.ForegroundName}";
        _lblEng.Text = snapshot.Paused ? "引擎已暂停" : "引擎运行中";
        string permission = snapshot.IsAdministrator ? "管理员权限" : "标准权限";
        string scope = snapshot.ManageOnlyCurrentUser ? "仅当前用户" : "所有用户";
        _lblScope.Text = $"{permission} · {scope}";
        _lblScope.ToolTipText = snapshot.ManageOnlyCurrentUser
            ? "仅管理当前用户进程；无法读取所属用户的进程会标记为权限受限。"
            : "管理所有可访问进程；受保护进程需要管理员权限。";
        _lblCpu.ForeColor = snapshot.TotalCpu >= 70 ? UiTheme.Danger : snapshot.TotalCpu >= 40 ? UiTheme.Warning : UiTheme.Text;
        _lblRam.ForeColor = snapshot.RamLoad >= 85 ? UiTheme.Danger : snapshot.RamLoad >= 65 ? UiTheme.Warning : UiTheme.Text;
        _lblGpu.ForeColor = snapshot.GpuLoad is >= 90 ? UiTheme.Danger : snapshot.GpuLoad is >= 65 ? UiTheme.Warning : snapshot.GpuLoad.HasValue ? UiTheme.Text : UiTheme.Muted;
        _lblPb.ForeColor = snapshot.ProBalanceActive ? UiTheme.Danger : UiTheme.Muted;
        _lblEng.ForeColor = snapshot.Paused ? UiTheme.Warning : UiTheme.Success;
        _lblScope.ForeColor = snapshot.IsAdministrator ? UiTheme.Success : UiTheme.Warning;
        var top5 = string.Join("  ", list.Take(5).Select(x => $"{x.Name}={x.CpuPercent:0.0}%"));
        int manageable = list.Count(x => x.CanManage);
        int limited = list.Count(x => x.AccessLimited);
        string gpuDetail = snapshot.GpuLoad.HasValue && !string.IsNullOrWhiteSpace(snapshot.GpuEngine)
            ? $"{snapshot.GpuLoad.Value:0.0}% · {snapshot.GpuEngine}最繁忙"
            : snapshot.GpuStatus;
        _infoBox.Text = $"进程总数: {list.Count}  （可管理 {manageable}，权限受限 {limited}）\n" +
            $"CPU 前 5: {top5}\n前台 PID: {snapshot.ForegroundPid} ({snapshot.ForegroundName})\n" +
            $"权限: {permission}；范围: {scope}\nGPU: {gpuDetail}\n" +
            $"规则: 优先级 {IniFile.ParsePairs(_cfg.DefaultPriorities).Count} 亲和 {IniFile.ParsePairs(_cfg.DefaultAffinities).Count} 限额 {IniFile.ParsePairs(_cfg.CpuLimits).Count} 黑名单 {IniFile.SplitList(_cfg.Disallowed).Count}\n" +
            $"配置: {_cfg.ConfigPath}{(_cfg.ImportedFrom != null ? "\n导入自: " + _cfg.ImportedFrom : "")}";

        _tick++;
        if (detailsVisible && _tick % 3 == 0) RefreshLogView();
        UpdateTrayStatus(snapshot);
        SyncPauseText(snapshot);
    }

    ProcessIdentity? SelectedIdentity()
    {
        try
        {
            if (_grid.CurrentRow?.Cells["pid_key"].Value is int pid &&
                _grid.CurrentRow.Cells["start_key"].Value is long startTicks)
                return new ProcessIdentity(pid, startTicks);
        }
        catch { }
        return null;
    }

    void UpdateProcessTable(IReadOnlyList<ProcInfo> list)
    {
        var live = list.Select(x => x.Identity).ToHashSet();
        _grid.SuspendLayout();
        try
        {
            _table.BeginLoadData();
            try
            {
                foreach (var gone in _rowByIdentity.Keys.Where(x => !live.Contains(x)).ToList())
                {
                    _table.Rows.Remove(_rowByIdentity[gone]);
                    _rowByIdentity.Remove(gone);
                }
                foreach (var pi in list)
                {
                    if (!_rowByIdentity.TryGetValue(pi.Identity, out var row))
                    {
                        row = _table.NewRow();
                        SetProcessRow(row, pi);
                        _table.Rows.Add(row);
                        _rowByIdentity[pi.Identity] = row;
                    }
                    else
                    {
                        SetProcessRow(row, pi);
                    }
                }
            }
            finally { _table.EndLoadData(); }
        }
        finally { _grid.ResumeLayout(); }
        try
        {
            var column = _grid.SortedColumn ?? _grid.Columns["CPU%"];
            var direction = _grid.SortOrder == SortOrder.Ascending
                ? ListSortDirection.Ascending : ListSortDirection.Descending;
            if (column != null) _grid.Sort(column, direction);
        }
        catch { }
    }

    static void SetProcessRow(DataRow row, ProcInfo pi)
    {
        row["名称"] = pi.Name;
        row["PID"] = pi.Pid;
        row["CPU%"] = pi.CpuPercent;
        row["优先级"] = PriName(pi.Priority);
        row["亲和"] = PopCount(pi.AffinityMask) + " 核";
        row["内存MB"] = pi.WorkingSetMB;
        row["会话"] = pi.SessionId;
        row["描述"] = pi.Desc;
        row["规则"] = pi.Note.Trim();
        row["pid_key"] = pi.Pid;
        row["start_key"] = pi.StartTimeUtcTicks;
    }

    void SelectIdentity(ProcessIdentity identity)
    {
        if (!_rowByIdentity.ContainsKey(identity)) return;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["pid_key"].Value is not int pid || pid != identity.Pid ||
                row.Cells["start_key"].Value is not long ticks || ticks != identity.StartTimeUtcTicks) continue;
            _grid.CurrentCell = row.Cells["名称"];
            break;
        }
    }

    void RefreshLogView()
    {
        var fresh = _log.ReadNewLines(40, out bool reset);
        if (fresh.Count == 0 && !reset) return;
        if (reset) _logLines.Clear();
        foreach (string line in fresh) _logLines.Enqueue(line);
        while (_logLines.Count > 40) _logLines.Dequeue();
        _logBox.Text = string.Join("\r\n", _logLines);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    void UpdateTrayStatus(EngineSnapshot snapshot)
    {
        int cpu = (int)Math.Round(snapshot.TotalCpu);
        string text = $"OpenLasso — CPU {cpu}%  内存 {snapshot.RamLoad:0}%" +
                      (snapshot.ProBalanceActive ? "  [ProBalance]" : "");
        if (text != _trayText)
        {
            _tray.Text = text;
            _trayText = text;
        }
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
        UiTheme.ApplyForm(f);
        var lbl = new Label { Text = "选择规则值：", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 7, 0, 0), ForeColor = UiTheme.Text, Font = UiTheme.BodyFont };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        UiTheme.StyleInput(combo);
        foreach (var o in options) combo.Items.Add(o);
        int idx = Array.IndexOf(options, def);
        combo.SelectedIndex = idx >= 0 ? idx : 0;
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom };
        UiTheme.StyleButton(ok, primary: true);
        UiTheme.StyleButton(cancel);
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
        UiTheme.ApplyForm(this);
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = rows * 34 + 8, ColumnCount = cols, RowCount = rows, Padding = new Padding(8), BackColor = UiTheme.Background };
        _boxes = new CheckBox[cores];
        for (int i = 0; i < cores; i++)
        {
            _boxes[i] = new CheckBox { Text = i.ToString(), Checked = (cur & (1L << i)) != 0, AutoSize = true, Margin = new Padding(4, 6, 4, 2), ForeColor = UiTheme.Text, Font = UiTheme.BodyFont, Cursor = Cursors.Hand };
            panel.Controls.Add(_boxes[i], i % cols, i / cols);
        }
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 196, ClientSize.Height - 40) };
        var all = new Button { Text = "全选", Width = 60, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Location = new Point(12, ClientSize.Height - 40) };
        all.Click += (s, e) => Array.ForEach(_boxes, b => b.Checked = true);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 96, ClientSize.Height - 40) };
        UiTheme.StyleButton(ok, primary: true);
        UiTheme.StyleButton(cancel);
        UiTheme.StyleButton(all);
        Controls.Add(panel); Controls.Add(ok); Controls.Add(cancel); Controls.Add(all);
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += (s, e) =>
        {
            if (DialogResult == DialogResult.OK)
            { Mask = 0; for (int i = 0; i < cores; i++) if (_boxes[i].Checked) Mask |= 1L << i; }
        };
    }
}
