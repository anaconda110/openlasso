using System.Runtime.InteropServices;

namespace OpenLasso;

/// <summary>规则编辑器：统一编辑 13 类规则的原始列表（对应 prolasso.ini 各键）</summary>
public class RulesForm : Form
{
    const int SBHorz = 0;

    [DllImport("user32.dll")]
    static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

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
        new("看门狗", "WatchdogRules2", true, "mode=any;cpu>50;mem>800;time>60;action=kill"),
        new("ProBalance 排除", "OocExclusions", false, "不参与 ProBalance 抑制"),
        new("SmartTrim 排除", "SmartTrimExclusions", false, "不参与内存整理"),
    };

    public RulesForm(LassoConfig cfg, GovernorEngine eng)
    {
        _cfg = cfg; _eng = eng;
        Text = "规则编辑器 — OpenLasso";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 620);
        MinimumSize = new Size(760, 520);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        UiTheme.ApplyForm(this);

        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var k in Kinds) _type.Items.Add(k.Title);
        _type.SelectedIndex = 0;
        UiTheme.StyleInput(_type);

        _pattern.PlaceholderText = @"例如 chrome.exe 或 C:\Apps\*";
        _value.PlaceholderText = "规则值";
        UiTheme.StyleInput(_pattern);
        UiTheme.StyleInput(_value);

        var hint = new Label { AutoEllipsis = true };
        void SyncHint() => hint.Text = "提示：" + Kinds[_type.SelectedIndex].Hint + "（模式支持 * ? 通配，可匹配完整路径）";
        _type.SelectedIndexChanged += (s, e) => SyncHint();
        SyncHint();

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(14, 6, 14, 10),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface,
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label FieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.SmallMediumFont,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Margin = new Padding(2, 0, 8, 3),
        };

        var typeLabel = FieldLabel("类型");
        var patternLabel = FieldLabel("匹配模式");
        var valueLabel = FieldLabel("值 / 说明");
        editor.Controls.Add(typeLabel, 0, 0);
        editor.Controls.Add(patternLabel, 1, 0);
        editor.Controls.Add(valueLabel, 2, 0);

        _type.Dock = DockStyle.Fill;
        _type.Margin = new Padding(0, 1, 10, 2);
        _pattern.Dock = DockStyle.Fill;
        _pattern.Margin = new Padding(0, 1, 10, 2);
        _value.Dock = DockStyle.Fill;
        _value.Margin = new Padding(0, 1, 10, 2);
        editor.Controls.Add(_type, 0, 1);
        editor.Controls.Add(_pattern, 1, 1);
        editor.Controls.Add(_value, 2, 1);

        var add = new Button { Text = "添加规则", Dock = DockStyle.Fill, Margin = new Padding(4, 1, 0, 2) };
        UiTheme.StyleButton(add, primary: true);
        add.Click += (s, e) => AddRule();
        editor.Controls.Add(add, 3, 1);

        var editorCard = UiTheme.CreateCard("添加规则", "选择规则类型，填写匹配模式与规则值。", editor);
        editorCard.Dock = DockStyle.Fill;
        editorCard.Height = 124;
        editorCard.Margin = new Padding(0, 0, 0, 12);

        _lv.Dock = DockStyle.Fill;
        _lv.View = View.Details;
        _lv.FullRowSelect = true;
        _lv.Columns.Add("类型", 140);
        _lv.Columns.Add("匹配模式", 240);
        _lv.Columns.Add("值 / 说明", 430);
        UiTheme.StyleListView(_lv);

        var del = new Button { Text = "删除选中", Width = 96 };
        UiTheme.StyleButton(del, danger: true);
        del.Click += (s, e) => { foreach (ListViewItem it in _lv.SelectedItems.Cast<ListViewItem>().ToList()) _lv.Items.Remove(it); };
        var listCard = UiTheme.CreateCard("规则列表", "按类型聚合显示；保存时会写回兼容的 prolasso.ini。", _lv, del);
        listCard.Dock = DockStyle.Fill;
        listCard.Margin = new Padding(0);

        var hintCard = UiTheme.Notice(hint);
        hintCard.Dock = DockStyle.Fill;
        hintCard.Margin = new Padding(0, 10, 0, 8);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            Margin = new Padding(0),
            BackColor = UiTheme.Background,
        };
        var ok = new Button { Text = "保存并应用", Width = 122 };
        var apply = new Button { Text = "应用(不关闭)", Width = 122 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
        UiTheme.StyleButton(ok, primary: true);
        UiTheme.StyleButton(apply);
        UiTheme.StyleButton(cancel);
        apply.Click += (s, e) => Persist();
        ok.Click += (s, e) =>
        {
            if (!Persist()) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        actions.Controls.Add(ok);
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = UiTheme.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.Controls.Add(editorCard, 0, 0);
        root.Controls.Add(listCard, 0, 1);
        root.Controls.Add(hintCard, 0, 2);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);

        void FitColumns()
        {
            int available = _lv.ClientSize.Width;
            if (available <= 100) return;
            float scale = DeviceDpi / 96f;
            int Scale(int px) => (int)Math.Round(px * scale);
            int typeWidth = Scale(132);
            int patternWidth = Math.Clamp((int)(available * 0.30), Scale(190), Scale(310));
            int reserved = Scale(SystemInformation.VerticalScrollBarWidth + 8);
            int valueWidth = Math.Max(Scale(160), available - typeWidth - patternWidth - reserved);
            _lv.Columns[0].Width = typeWidth;
            _lv.Columns[1].Width = patternWidth;
            _lv.Columns[2].Width = valueWidth;
            if (_lv.IsHandleCreated) ShowScrollBar(_lv.Handle, SBHorz, false);
        }

        _lv.Resize += (s, e) => FitColumns();
        DpiChanged += (s, e) => BeginInvoke((Action)FitColumns);
        Shown += (s, e) => FitColumns();
        AcceptButton = ok;
        CancelButton = cancel;
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

    bool Persist()
    {
        var invalid = new List<string>();
        foreach (var row in _lv.Items.Cast<ListViewItem>().Where(i => i.Tag is RuleKind k && k.Key == "WatchdogRules2"))
        {
            string pattern = row.SubItems[1].Text.Trim();
            string value = row.SubItems[2].Text.Trim();
            if (!GovernorEngine.ValidateWatchRule(pattern + ";" + value, out string error))
                invalid.Add($"匹配模式 {pattern}: {error}");
        }
        if (invalid.Count > 0)
        {
            MessageBox.Show(this, "看门狗规则有误：\n\n" + string.Join("\n", invalid), "规则校验",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        foreach (var k in Kinds)
        {
            var rows = _lv.Items.Cast<ListViewItem>().Where(i => (RuleKind)i.Tag == k).ToList();
            string raw = k.IsPair
                ? string.Join(",", rows.Select(r => r.SubItems[1].Text + "," + r.SubItems[2].Text))
                : IniFile.JoinList(rows.Select(r => r.SubItems[1].Text));
            SetRaw(k, raw);
        }

        try
        {
            _cfg.Save();
            _eng.ReloadRules();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存规则失败：\n" + ex.Message, "OpenLasso",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
