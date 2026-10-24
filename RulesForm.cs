namespace OpenLasso;

/// <summary>规则编辑器：统一编辑 13 类规则的原始列表（对应 prolasso.ini 各键）</summary>
public class RulesForm : Form
{
    readonly LassoConfig _cfg;
    readonly GovernorEngine _eng;
    readonly ListView _lv = new();
    readonly ComboBox _type = new();
    readonly TextBox _pattern = new(), _value = new();

    record RuleKind(string Title, string Key, bool IsPair, string Hint);

    static readonly RuleKind[] Kinds =
    {
        new("默认优先级", "DefaultPriorities", true, "值: real time/high/above normal/normal/below normal/idle"),
        new("IO 优先级", "DefaultIOPriorities", true, "值: 0=极低 1=低 2=正常 3=高"),
        new("内存优先级", "DefaultMemoryPriorities", true, "值: 0..7（5=默认）"),
        new("亲和性", "DefaultAffinitiesEx", true, "值: 0xFF 掩码 或 0-3,6 核列表"),
        new("CPU 集", "CPUSets", true, "值: 0-3,6 核列表"),
        new("能效模式", "EfficiencyModes", false, "命中即启用 Eco"),
        new("实例数限制", "InstanceLimitedProcesses", true, "值: 最大实例数，超出终止最新"),
        new("CPU 限额%", "CPULimitRules", true, "值: 1-99，占空比限速"),
        new("黑名单", "DisallowedProcesses", false, "命中即终止"),
        new("保持运行", "KeepRunningProcessesEx", false, "退出后自动重启"),
        new("看门狗", "WatchdogRules2", true, "值: cpu>50;mem>800;time>60;action=kill|trim|priority:low"),
        new("ProBalance 排除", "OocExclusions", false, "不参与 ProBalance 抑制"),
        new("SmartTrim 排除", "SmartTrimExclusions", false, "不参与内存整理"),
    };

    public RulesForm(LassoConfig cfg, GovernorEngine eng)
    {
        _cfg = cfg; _eng = eng;
        Text = "规则编辑器 — OpenLasso";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 520);
        Font = new Font("Microsoft YaHei UI", 9F);

        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var k in Kinds) _type.Items.Add(k.Title);
        _type.SelectedIndex = 0;
        _lv.Dock = DockStyle.Fill;
        _lv.View = View.Details;
        _lv.FullRowSelect = true;
        _lv.Columns.Add("类型", 130);
        _lv.Columns.Add("匹配模式", 200);
        _lv.Columns.Add("值 / 说明", 380);
        var hint = new Label { Dock = DockStyle.Bottom, Height = 26, ForeColor = Color.DimGray, Padding = new Padding(6, 5, 0, 0) };
        void SyncHint() => hint.Text = "提示：" + Kinds[_type.SelectedIndex].Hint + "（模式支持 * ? 通配，可匹配完整路径）";
        _type.SelectedIndexChanged += (s, e) => SyncHint();
        SyncHint();

        var top = new Panel { Dock = DockStyle.Top, Height = 96 };
        var l1 = new Label { Text = "类型", AutoSize = true, Location = new Point(12, 12) };
        _type.Location = new Point(60, 9); _type.Width = 150;
        var l2 = new Label { Text = "模式", AutoSize = true, Location = new Point(220, 12) };
        _pattern.Location = new Point(258, 9); _pattern.Width = 180;
        var l3 = new Label { Text = "值", AutoSize = true, Location = new Point(448, 12) };
        _value.Location = new Point(476, 9); _value.Width = 150;
        var add = new Button { Text = "添加", Location = new Point(640, 7), Width = 96 };
        add.Click += (s, e) => AddRule();
        var del = new Button { Text = "删除选中", Location = new Point(12, 44), Width = 96 };
        del.Click += (s, e) => { foreach (ListViewItem it in _lv.SelectedItems) _lv.Items.Remove(it); };
        top.Controls.AddRange(new Control[] { l1, _type, l2, _pattern, l3, _value, add, del });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var ok = new Button { Text = "保存并应用", DialogResult = DialogResult.OK, Width = 120, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Location = new Point(ClientSize.Width - 270, 8) };
        var apply = new Button { Text = "应用(不关闭)", Width = 120, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Location = new Point(ClientSize.Width - 140, 8) };
        apply.Click += (s, e) => Persist();
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 96, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Location = new Point(ClientSize.Width - 380, 8) };
        bottom.Controls.AddRange(new Control[] { ok, apply, cancel });
        ok.Click += (s, e) => Persist();

        Controls.Add(_lv);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(hint);
        LoadRules();
    }

    void LoadRules()
    {
        _lv.BeginUpdate();
        _lv.Items.Clear();
        foreach (var k in Kinds)
        {
            var raw = Raw(k);
            if (k.IsPair)
            {
                foreach (var (n, v) in IniFile.ParsePairs(raw)) _lv.Items.Add(MakeItem(k, n, v));
            }
            else
            {
                foreach (var n in IniFile.SplitList(raw)) _lv.Items.Add(MakeItem(k, n, "—"));
            }
        }
        _lv.EndUpdate();
    }

    static ListViewItem MakeItem(RuleKind k, string n, string v)
    {
        var it = new ListViewItem(k.Title);
        it.SubItems.Add(n);
        it.SubItems.Add(v);
        it.Tag = k;
        return it;
    }

    void AddRule()
    {
        var k = Kinds[_type.SelectedIndex];
        var p = _pattern.Text.Trim();
        if (p.Length == 0) { MessageBox.Show("请填写匹配模式"); return; }
        var v = k.IsPair ? _value.Text.Trim() : "—";
        if (k.IsPair && v.Length == 0) { MessageBox.Show("请填写规则值。\n" + k.Hint); return; }
        _lv.Items.Add(MakeItem(k, p, v));
    }

    string Raw(RuleKind k) => k.Key switch
    {
        "DefaultPriorities" => _cfg.DefaultPriorities,
        "DefaultIOPriorities" => _cfg.DefaultIoPriorities,
        "DefaultMemoryPriorities" => _cfg.DefaultMemPriorities,
        "DefaultAffinitiesEx" => _cfg.DefaultAffinities,
        "CPUSets" => _cfg.DefaultCpuSets,
        "EfficiencyModes" => _cfg.EfficiencyModes,
        "InstanceLimitedProcesses" => _cfg.InstanceLimits,
        "CPULimitRules" => _cfg.CpuLimits,
        "DisallowedProcesses" => _cfg.Disallowed,
        "KeepRunningProcessesEx" => _cfg.KeepRunning,
        "WatchdogRules2" => _cfg.WatchdogRules,
        "OocExclusions" => IniFile.JoinList(_cfg.ProBalanceExclusions),
        "SmartTrimExclusions" => IniFile.JoinList(_cfg.SmartTrimExclusions),
        _ => "",
    };

    void SetRaw(RuleKind k, string v)
    {
        switch (k.Key)
        {
            case "DefaultPriorities": _cfg.DefaultPriorities = v; break;
            case "DefaultIOPriorities": _cfg.DefaultIoPriorities = v; break;
            case "DefaultMemoryPriorities": _cfg.DefaultMemPriorities = v; break;
            case "DefaultAffinitiesEx": _cfg.DefaultAffinities = v; break;
            case "CPUSets": _cfg.DefaultCpuSets = v; break;
            case "EfficiencyModes": _cfg.EfficiencyModes = v; break;
            case "InstanceLimitedProcesses": _cfg.InstanceLimits = v; break;
            case "CPULimitRules": _cfg.CpuLimits = v; break;
            case "DisallowedProcesses": _cfg.Disallowed = v; break;
            case "KeepRunningProcessesEx": _cfg.KeepRunning = v; break;
            case "WatchdogRules2": _cfg.WatchdogRules = v; break;
            case "OocExclusions": _cfg.ProBalanceExclusions = IniFile.SplitList(v); break;
            case "SmartTrimExclusions": _cfg.SmartTrimExclusions = IniFile.SplitList(v); break;
        }
    }

    void Persist()
    {
        foreach (var k in Kinds)
        {
            var rows = _lv.Items.Cast<ListViewItem>().Where(i => (RuleKind)i.Tag == k).ToList();
            string raw = k.IsPair
                ? string.Join(",", rows.Select(r => r.SubItems[1].Text + "," + r.SubItems[2].Text))
                : IniFile.JoinList(rows.Select(r => r.SubItems[1].Text));
            SetRaw(k, raw);
        }
        _cfg.Save();
        _cfg.Reload();
        _eng.ReloadRules();
    }
}
