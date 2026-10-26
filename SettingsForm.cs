namespace OpenLasso;

/// <summary>设置窗体：ProBalance / 前台提升 / SmartTrim / 电源自动化 / 引擎</summary>
public class SettingsForm : Form
{
    readonly LassoConfig _cfg;
    readonly Dictionary<string, Control> _fields = new();
    readonly FlowLayoutPanel _sections = new();
    bool _fittingSections;
    int _nextTabIndex;

    record SettingRow(string Label, Control Control, bool FullWidth = false);
    sealed record PriorityChoice(string Label, int Token)
    {
        public override string ToString() => Label;
    }

    static readonly PriorityChoice[] FgPriorities =
    {
        new("高于正常", 0x8000),
        new("高", 0x80),
        new("正常", 0x20),
    };

    public SettingsForm(LassoConfig cfg)
    {
        _cfg = cfg;
        Text = "设置 — OpenLasso";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 720);
        MinimumSize = new Size(700, 580);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        DoubleBuffered = true;
        UiTheme.ApplyForm(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Background,
            Padding = new Padding(18, 12, 18, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Background,
            Margin = new Padding(0),
            Padding = new Padding(2, 2, 2, 8),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.Background,
        };
        heading.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "设置",
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.Text,
            Margin = new Padding(0, 0, 0, 3),
        });
        heading.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "调整进程优化策略与引擎行为，保存后立即应用到当前会话。",
            Font = UiTheme.SmallFont,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0),
        });
        var badge = new Label
        {
            AutoSize = true,
            Text = "兼容 prolasso.ini",
            Font = UiTheme.SmallMediumFont,
            ForeColor = UiTheme.PrimaryHover,
            BackColor = UiTheme.PrimarySoft,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(10, 3, 2, 0),
        };
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(badge, 1, 0);

        _sections.Dock = DockStyle.Fill;
        _sections.AutoScroll = true;
        _sections.FlowDirection = FlowDirection.TopDown;
        _sections.WrapContents = false;
        _sections.Padding = new Padding(2, 0, 4, 12);
        _sections.Margin = new Padding(0);
        _sections.BackColor = UiTheme.Background;

        _sections.Controls.Add(Section(
            "ProBalance",
            "CPU 超载时自动抑制高占用进程，并在负载下降后恢复优先级。",
            CheckRow("启用 ProBalance", "pb_on", _cfg.ProBalanceEnabled),
            NumRow("总 CPU 触发阈值 (%)", "pb_total", _cfg.TotalCpuBeforeRestraint, 10, 100),
            NumRow("单进程 CPU 触发阈值 (%)", "pb_pp", _cfg.PerProcessCpuBeforeRestraint, 1, 100),
            NumRow("持续超载时间 (ms)", "pb_time", _cfg.TimeOverQuotaMs, 100, 60000),
            NumRow("单进程恢复阈值 (%)", "pb_restore", _cfg.PerProcessCpuForRestore, 0, 100),
            NumRow("最短抑制时间 (ms)", "pb_min", _cfg.MinRestraintMs, 100, 600000),
            CheckRow("抑制时同时降低 IO 优先级", "pb_io", _cfg.LowerIoOnRestraint),
            CheckRow("抑制时降到空闲级（否则低于正常）", "pb_lower_idle", _cfg.LowerToIdle),
            CheckRow("只抑制 Normal 优先级进程", "pb_normal", _cfg.TameOnlyNormal),
            CheckRow("排除前台进程", "pb_fg", _cfg.ExcludeForeground),
            CheckRow("排除前台进程的子进程", "pb_child", _cfg.ExcludeChildrenOfForeground),
            CheckRow("排除服务进程", "pb_services", _cfg.ExcludeServices),
            NumRow("系统空闲多久后暂停 (ms，0 = 不暂停)", "pb_idle", _cfg.DisableWhenIdleMs, 0, 600000)));

        _sections.Controls.Add(Section(
            "前台提升",
            "检测前台窗口所属进程，并按所选等级提升其优先级。",
            CheckRow("启用前台进程优先级提升", "fg_on", _cfg.FgBoostEnabled),
            CheckRow("只提升 Normal 优先级进程", "fg_normal", _cfg.FgBoostOnlyNormal),
            PriorityRow("前台提升优先级", "fg_priority", _cfg.FgBoostPriority)));

        _sections.Controls.Add(Section(
            "SmartTrim 智能内存整理",
            "在内存负载较高时修剪工作集，同时避开低于下限的关键进程。",
            CheckRow("启用 SmartTrim", "st_on", _cfg.SmartTrimEnabled),
            NumRow("内存负载阈值 (%)", "st_load", _cfg.SmartTrimMinRamLoad, 10, 100),
            NumRow("工作集下限 (MB)", "st_wss", _cfg.SmartTrimMinWssMb, 8, 8192),
            NumRow("整理间隔 (分钟)", "st_int", _cfg.SmartTrimIntervalMin, 1, 720),
            TextRow("排除进程（逗号分隔）", "st_ex", IniFile.JoinList(_cfg.SmartTrimExclusions))));

        _sections.Controls.Add(Section(
            "电源自动化",
            "按空闲状态或 CPU 负载自动切换 Windows 电源计划。",
            CheckRow("IdleSaver：空闲切换省电计划", "ps_on", _cfg.IdleSaverEnabled),
            NumRow("空闲判定 (秒)", "ps_idle", _cfg.IdleSaverIdleSec, 30, 3600),
            TextRow("省电计划名（包含即可）", "ps_plan", _cfg.IdleSaverPlan),
            CheckRow("按 CPU 负载切换电源计划", "lp_on", _cfg.LoadPowerEnabled),
            NumRow("低负载阈值 (%)", "lp_low", (int)_cfg.LoadLowPct, 1, 99),
            NumRow("高负载阈值 (%)", "lp_high", (int)_cfg.LoadHighPct, 2, 100),
            TextRow("低负载计划名", "lp_low_plan", _cfg.LoadLowPlan),
            TextRow("高性能计划名", "lp_high_plan", _cfg.LoadHighPlan)));

        _sections.Controls.Add(Section(
            "引擎",
            "控制采样频率与进程管理范围；更高频率会增加少量系统开销。",
            NumRow("轮询间隔 (ms)", "tick", _cfg.UpdateSpeedMs, 250, 10000),
            CheckRow("只管理当前用户的进程", "mocu", _cfg.ManageOnlyCurrentUser)));

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Background,
            Margin = new Padding(0),
            Padding = new Padding(2, 10, 2, 10),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "提示：数值修改仅在点击“保存”后写入配置。",
            Font = UiTheme.SmallFont,
            ForeColor = UiTheme.Muted,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        }, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.Background,
        };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 102 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 96 };
        UiTheme.StyleButton(save, primary: true);
        UiTheme.StyleButton(cancel);
        save.Click += (s, e) => save.DialogResult = Save() ? DialogResult.OK : DialogResult.None;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_sections, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        _sections.Resize += (s, e) => FitSections();
        Shown += (s, e) => BeginInvoke((Action)FitSections);
        AcceptButton = save;
        CancelButton = cancel;
    }

    CardPanel Section(string title, string subtitle, params SettingRow[] rows)
    {
        var body = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rows.Length,
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(14, 2, 14, 10),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));

        for (int i = 0; i < rows.Length; i++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var row = rows[i];
            if (row.FullWidth)
            {
                row.Control.Dock = DockStyle.Fill;
                row.Control.Margin = new Padding(0, 1, 0, 1);
                body.Controls.Add(row.Control, 0, i);
                body.SetColumnSpan(row.Control, 2);
                continue;
            }

            var label = new Label
            {
                Text = row.Label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Text,
                Font = UiTheme.BodyFont,
                AutoEllipsis = true,
                Margin = new Padding(0),
            };
            row.Control.Margin = new Padding(0, 4, 0, 4);
            if (row.Control is NumericUpDown)
                row.Control.Anchor = AnchorStyles.Right;
            else
                row.Control.Dock = DockStyle.Fill;

            body.Controls.Add(label, 0, i);
            body.Controls.Add(row.Control, 1, i);
        }

        var card = UiTheme.CreateCard(title, subtitle, body);
        card.Margin = new Padding(0, 0, 0, 10);
        card.Height = 58 + rows.Length * 36;
        card.MinimumSize = new Size(480, card.Height);
        return card;
    }

    SettingRow CheckRow(string label, string key, bool value)
    {
        var check = new CheckBox
        {
            Text = label,
            Checked = value,
            AutoSize = false,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Text,
            Font = UiTheme.BodyFont,
            Cursor = Cursors.Hand,
            Margin = new Padding(0),
            TabIndex = _nextTabIndex++,
        };
        _fields[key] = check;
        return new SettingRow(label, check, true);
    }

    SettingRow NumRow(string label, string key, int value, int min, int max)
    {
        var input = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 132,
            TextAlign = HorizontalAlignment.Right,
            ThousandsSeparator = true,
            TabIndex = _nextTabIndex++,
        };
        UiTheme.StyleInput(input);
        _fields[key] = input;
        return new SettingRow(label, input);
    }

    SettingRow TextRow(string label, string key, string value)
    {
        var input = new TextBox
        {
            Text = value ?? "",
            Width = 220,
            TabIndex = _nextTabIndex++,
        };
        UiTheme.StyleInput(input);
        _fields[key] = input;
        return new SettingRow(label, input);
    }

    SettingRow PriorityRow(string label, string key, int value)
    {
        var input = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            TabIndex = _nextTabIndex++,
        };
        foreach (var choice in FgPriorities) input.Items.Add(choice);
        input.SelectedItem = FgPriorities.FirstOrDefault(x => x.Token == value) ?? FgPriorities[0];
        UiTheme.StyleInput(input);
        _fields[key] = input;
        return new SettingRow(label, input);
    }

    void FitSections()
    {
        if (_fittingSections || _sections.ClientSize.Width <= 0) return;
        _fittingSections = true;
        try
        {
            int width = _sections.ClientSize.Width - _sections.Padding.Horizontal
                - SystemInformation.VerticalScrollBarWidth - 2;
            width = Math.Max(480, width);

            _sections.SuspendLayout();
            foreach (Control child in _sections.Controls)
            {
                child.Width = width;
                child.Height = Math.Max(child.Height, child.MinimumSize.Height);
            }
        }
        finally
        {
            _sections.ResumeLayout(true);
            _fittingSections = false;
        }
    }

    bool B(string key) => ((CheckBox)_fields[key]).Checked;
    int I(string key) => (int)((NumericUpDown)_fields[key]).Value;
    string S(string key) => ((TextBox)_fields[key]).Text.Trim();
    int P(string key) => ((PriorityChoice)((ComboBox)_fields[key]).SelectedItem).Token;

    bool Save()
    {
        if (B("lp_on") && I("lp_low") >= I("lp_high"))
        {
            MessageBox.Show(this, "低负载阈值必须小于高负载阈值。", "设置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _cfg.ProBalanceEnabled = B("pb_on");
        _cfg.TotalCpuBeforeRestraint = I("pb_total");
        _cfg.PerProcessCpuBeforeRestraint = I("pb_pp");
        _cfg.TimeOverQuotaMs = I("pb_time");
        _cfg.PerProcessCpuForRestore = I("pb_restore");
        _cfg.MinRestraintMs = I("pb_min");
        _cfg.LowerIoOnRestraint = B("pb_io");
        _cfg.LowerToIdle = B("pb_lower_idle");
        _cfg.TameOnlyNormal = B("pb_normal");
        _cfg.ExcludeForeground = B("pb_fg");
        _cfg.ExcludeChildrenOfForeground = B("pb_child");
        _cfg.ExcludeServices = B("pb_services");
        _cfg.DisableWhenIdleMs = I("pb_idle");

        _cfg.FgBoostEnabled = B("fg_on");
        _cfg.FgBoostOnlyNormal = B("fg_normal");
        _cfg.FgBoostPriority = P("fg_priority");

        _cfg.SmartTrimEnabled = B("st_on");
        _cfg.SmartTrimMinRamLoad = I("st_load");
        _cfg.SmartTrimMinWssMb = I("st_wss");
        _cfg.SmartTrimIntervalMin = I("st_int");
        _cfg.SmartTrimExclusions = IniFile.SplitList(S("st_ex"));

        _cfg.IdleSaverEnabled = B("ps_on");
        _cfg.IdleSaverIdleSec = I("ps_idle");
        _cfg.IdleSaverPlan = string.IsNullOrWhiteSpace(S("ps_plan")) ? "节能" : S("ps_plan");
        _cfg.LoadPowerEnabled = B("lp_on");
        _cfg.LoadLowPct = I("lp_low");
        _cfg.LoadHighPct = I("lp_high");
        _cfg.LoadLowPlan = string.IsNullOrWhiteSpace(S("lp_low_plan")) ? "节能" : S("lp_low_plan");
        _cfg.LoadHighPlan = string.IsNullOrWhiteSpace(S("lp_high_plan")) ? "高性能" : S("lp_high_plan");

        _cfg.UpdateSpeedMs = I("tick");
        _cfg.ManageOnlyCurrentUser = B("mocu");
        _cfg.Save();
        return true;
    }
}
