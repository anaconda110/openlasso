namespace OpenLasso;

/// <summary>设置窗体：ProBalance / 前台提升 / SmartTrim / 电源自动化 / 引擎</summary>
public class SettingsForm : Form
{
    readonly LassoConfig _cfg;
    readonly Dictionary<string, Control> _fields = new();

    public SettingsForm(LassoConfig cfg)
    {
        _cfg = cfg;
        Text = "设置 — OpenLasso";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 640);
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var scroll = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };

        GroupBox ProBalance() => Group("ProBalance（CPU 超载自动抑制）",
            Check("启用 ProBalance", "pb_on", _cfg.ProBalanceEnabled),
            Num("总 CPU 触发阈值 (%)", "pb_total", _cfg.TotalCpuBeforeRestraint, 10, 100),
            Num("单进程 CPU 触发阈值 (%)", "pb_pp", _cfg.PerProcessCpuBeforeRestraint, 1, 100),
            Num("持续超载时间 (ms)", "pb_time", _cfg.TimeOverQuotaMs, 100, 60000),
            Num("单进程恢复阈值 (%)", "pb_restore", _cfg.PerProcessCpuForRestore, 0, 100),
            Num("最短抑制时间 (ms)", "pb_min", _cfg.MinRestraintMs, 100, 600000),
            Check("抑制时同时降低 IO 优先级", "pb_io", _cfg.LowerIoOnRestraint),
            Check("只抑制 Normal 优先级进程", "pb_normal", _cfg.TameOnlyNormal),
            Check("排除前台进程", "pb_fg", _cfg.ExcludeForeground),
            Check("排除前台进程的子进程", "pb_child", _cfg.ExcludeChildrenOfForeground),
            Num("系统空闲多久后暂停 (ms, 0=不暂停)", "pb_idle", _cfg.DisableWhenIdleMs, 0, 600000));

        GroupBox FgBoost() => Group("前台提升",
            Check("启用（前台进程升为高于正常）", "fg_on", _cfg.FgBoostEnabled),
            Check("只提升 Normal 优先级进程", "fg_normal", _cfg.FgBoostOnlyNormal));

        GroupBox Trim() => Group("SmartTrim 智能内存整理",
            Check("启用 SmartTrim", "st_on", _cfg.SmartTrimEnabled),
            Num("内存负载阈值 (%)", "st_load", _cfg.SmartTrimMinRamLoad, 10, 100),
            Num("工作集下限 (MB)", "st_wss", _cfg.SmartTrimMinWssMb, 8, 8192),
            Num("整理间隔 (分钟)", "st_int", _cfg.SmartTrimIntervalMin, 1, 720));

        GroupBox Power() => Group("电源自动化",
            Check("IdleSaver：空闲切换省电计划", "ps_on", _cfg.IdleSaverEnabled),
            Num("空闲判定 (秒)", "ps_idle", _cfg.IdleSaverIdleSec, 30, 3600),
            Txt("省电计划名（包含即可）", "ps_plan", _cfg.IdleSaverPlan),
            Check("按 CPU 负载切换电源计划", "lp_on", _cfg.LoadPowerEnabled),
            Num("低负载阈值 (%)", "lp_low", (int)_cfg.LoadLowPct, 1, 99),
            Num("高负载阈值 (%)", "lp_high", (int)_cfg.LoadHighPct, 2, 100),
            Txt("高性能计划名", "lp_high_plan", _cfg.LoadHighPlan));

        GroupBox Poll() => Group("引擎",
            Num("轮询间隔 (ms)", "tick", _cfg.UpdateSpeedMs, 250, 10000),
            Check("只管理当前用户的进程", "mocu", _cfg.ManageOnlyCurrentUser));

        scroll.Controls.AddRange(new Control[] { ProBalance(), FgBoost(), Trim(), Power(), Poll() });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 100, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Location = new Point(ClientSize.Width - 230, 8) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 100, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Location = new Point(ClientSize.Width - 120, 8) };
        ok.Click += (s, e) => Save();
        bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
        Controls.Add(scroll); Controls.Add(bottom);
        AcceptButton = ok; CancelButton = cancel;
    }

    static GroupBox Group(string title, params Control[] items)
    {
        var g = new GroupBox { Text = title, Width = 560, Margin = new Padding(4, 8, 4, 4) };
        var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(8, 4, 4, 4) };
        inner.Controls.AddRange(items);
        g.Controls.Add(inner);
        int h = 30;
        foreach (var c in items) h += c.PreferredSize.Height + 4;
        g.Height = Math.Max(h, 60);
        return g;
    }

    Control Check(string text, string key, bool val)
    {
        var c = new CheckBox { Text = text, Checked = val, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _fields[key] = c;
        return c;
    }
    Control Num(string label, string key, int val, int min, int max)
    {
        var host = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        host.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 8, 0), Width = 250 });
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = 100 };
        host.Controls.Add(n);
        _fields[key] = n;
        return host;
    }
    Control Txt(string label, string key, string val)
    {
        var host = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        host.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 8, 0), Width = 250 });
        var t = new TextBox { Text = val, Width = 200 };
        host.Controls.Add(t);
        _fields[key] = t;
        return host;
    }

    bool B(string k) => ((CheckBox)_fields[k]).Checked;
    int I(string k) => (int)((NumericUpDown)_fields[k]).Value;
    string S(string k) => ((TextBox)_fields[k]).Text;

    void Save()
    {
        _cfg.ProBalanceEnabled = B("pb_on");
        _cfg.TotalCpuBeforeRestraint = I("pb_total");
        _cfg.PerProcessCpuBeforeRestraint = I("pb_pp");
        _cfg.TimeOverQuotaMs = I("pb_time");
        _cfg.PerProcessCpuForRestore = I("pb_restore");
        _cfg.MinRestraintMs = I("pb_min");
        _cfg.LowerIoOnRestraint = B("pb_io");
        _cfg.TameOnlyNormal = B("pb_normal");
        _cfg.ExcludeForeground = B("pb_fg");
        _cfg.ExcludeChildrenOfForeground = B("pb_child");
        _cfg.DisableWhenIdleMs = I("pb_idle");
        _cfg.FgBoostEnabled = B("fg_on");
        _cfg.FgBoostOnlyNormal = B("fg_normal");
        _cfg.SmartTrimEnabled = B("st_on");
        _cfg.SmartTrimMinRamLoad = I("st_load");
        _cfg.SmartTrimMinWssMb = I("st_wss");
        _cfg.SmartTrimIntervalMin = I("st_int");
        _cfg.IdleSaverEnabled = B("ps_on");
        _cfg.IdleSaverIdleSec = I("ps_idle");
        _cfg.IdleSaverPlan = string.IsNullOrWhiteSpace(S("ps_plan")) ? "节能" : S("ps_plan");
        _cfg.LoadPowerEnabled = B("lp_on");
        _cfg.LoadLowPct = I("lp_low");
        _cfg.LoadHighPct = I("lp_high");
        _cfg.LoadHighPlan = string.IsNullOrWhiteSpace(S("lp_high_plan")) ? "高性能" : S("lp_high_plan");
        _cfg.UpdateSpeedMs = I("tick");
        _cfg.ManageOnlyCurrentUser = B("mocu");
        _cfg.Save();
    }
}
