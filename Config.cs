using System.Text;

namespace OpenLasso;

/// <summary>可回写的 INI（保持段落顺序与未知键，UTF-8 BOM，兼容 Process Lasso 的 prolasso.ini）</summary>
public class IniFile
{
    private readonly List<(string section, string key, string value)> _items = new();
    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        if (File.Exists(path))
        {
            var bom = File.ReadAllBytes(path);
            Encoding enc = new UTF8Encoding(true);
            if (bom.Length >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) enc = Encoding.Unicode;          // UTF-16 LE（prolasso.ini 实际编码）
            else if (bom.Length >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) enc = Encoding.BigEndianUnicode;
            else if (bom.Length >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) enc = new UTF8Encoding(false);
            Parse(File.ReadAllLines(path, enc));
        }
    }

    private void Parse(string[] lines)
    {
        string sec = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { sec = line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            _items.Add((sec, line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
    }

    public string Get(string sec, string key, string def = "")
    { var i = IndexOf(sec, key); return i >= 0 ? _items[i].value : def; }

    public bool GetBool(string sec, string key, bool def)
    { var s = Get(sec, key); return bool.TryParse(s, out var b) ? b : def; }

    public int GetInt(string sec, string key, int def)
    { var s = Get(sec, key); return int.TryParse(s, out var v) ? v : def; }

    public double GetDouble(string sec, string key, double def)
    { var s = Get(sec, key); return double.TryParse(s, out var v) ? v : def; }

    public void Set(string sec, string key, string value)
    {
        var i = IndexOf(sec, key);
        if (i >= 0) _items[i] = (sec, key, value);
        else
        {
            int insert = _items.FindLastIndex(x => x.section.Equals(sec, StringComparison.OrdinalIgnoreCase));
            _items.Insert(insert + 1, (sec, key, value));
        }
    }

    private int IndexOf(string sec, string key)
        => _items.FindIndex(x => x.section.Equals(sec, StringComparison.OrdinalIgnoreCase) && x.key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>逗号分隔对列表："a.exe,v1,b.exe,v2" → [(a.exe,v1),(b.exe,v2)]</summary>
    public static List<(string name, string val)> ParsePairs(string s)
    {
        var r = new List<(string, string)>();
        var parts = SplitList(s);
        for (int i = 0; i + 1 < parts.Count; i += 2) r.Add((parts[i], parts[i + 1]));
        return r;
    }

    /// <summary>逗号分隔列表（空项剔除，转义 \, 视为字面逗号——与 PL 转义规则一致）</summary>
    public static List<string> SplitList(string s)
    {
        var outp = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return outp;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == ',') { sb.Append(','); i++; }
            else if (s[i] == ',') { outp.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(s[i]);
        }
        outp.Add(sb.ToString().Trim());
        outp.RemoveAll(string.IsNullOrEmpty);
        return outp;
    }

    public static string JoinList(IEnumerable<string> items) => string.Join(",", items.Select(Escape));

    private static string Escape(string s) => s.Replace(",", "\\,");

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        string cur = null;
        foreach (var (sec, key, value) in _items)
        {
            if (!string.Equals(sec, cur, StringComparison.OrdinalIgnoreCase))
            { sb.Append('[').Append(sec).Append(']').AppendLine(); cur = sec; }
            sb.Append(key).Append('=').Append(value).AppendLine();
        }
        File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(true));
    }
}

/// <summary>类型化配置：节名与键名与 Process Lasso prolasso.ini 对齐（可直接读取既有配置）</summary>
public class LassoConfig
{
    public IniFile Ini;
    public string ConfigDir;
    public string ConfigPath;

    // —— 功能开关/参数（与 prolasso.ini 同名键）——
    public bool ProBalanceEnabled;          // OocOn
    public int TotalCpuBeforeRestraint;     // 60
    public int PerProcessCpuBeforeRestraint;// 5
    public int TimeOverQuotaMs;             // 900
    public int PerProcessCpuForRestore;     // 2
    public int MinRestraintMs;              // 4200
    public bool TameOnlyNormal;             // 只抑制 Normal 级
    public bool LowerIoOnRestraint;
    public bool LowerToIdle;
    public bool ExcludeForeground;
    public bool ExcludeChildrenOfForeground;
    public bool ExcludeServices;
    public int DisableWhenIdleMs;           // 30000
    public List<string> ProBalanceExclusions = new();

    public bool FgBoostEnabled;             // BoostForegroundProcess
    public int FgBoostPriority;             // 0x8000
    public bool FgBoostOnlyNormal;

    public bool SmartTrimEnabled;
    public int SmartTrimMinRamLoad;         // 65
    public int SmartTrimMinWssMb;           // 196
    public int SmartTrimIntervalMin;        // 15
    public List<string> SmartTrimExclusions = new();

    public int UpdateSpeedMs = 1000;
    public bool ManageOnlyCurrentUser;

    // 规则串（引擎逐项解析）
    public string DefaultPriorities = "", DefaultIoPriorities = "", DefaultMemPriorities = "";
    public string DefaultAffinities = "", DefaultCpuSets = "", EfficiencyModes = "";
    public string InstanceLimits = "", CpuLimits = "", Disallowed = "", Allowed = "";
    public string KeepRunning = "", WatchdogRules = "";

    // 电源
    public bool IdleSaverEnabled; public int IdleSaverIdleSec = 300; public string IdleSaverPlan = "节能";
    public bool LoadPowerEnabled; public double LoadLowPct = 10, LoadHighPct = 50; public string LoadLowPlan = "节能", LoadHighPlan = "高性能";

    public LassoConfig(string path)
    {
        ConfigPath = path;
        ConfigDir = Path.GetDirectoryName(path);
        Ini = new IniFile(path);
        Reload();
        // 首次运行且无配置：尝试从 Process Lasso 导入（只读）
        if (!File.Exists(path))
        {
            string plIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessLasso", "config", "prolasso.ini");
            if (File.Exists(plIni))
            {
                Ini = new IniFile(plIni);
                Reload();
                ConfigPath = path; // 保持自己的落盘路径
                ImportedFrom = plIni;
            }
        }
    }
    public string ImportedFrom;

    public void Reload()
    {
        var i = Ini;
        ProBalanceEnabled = i.GetBool("OutOfControlProcessRestraint", "OocOn", true);
        TotalCpuBeforeRestraint = i.GetInt("OutOfControlProcessRestraint", "TotalProcessorUsageBeforeRestraint", 60);
        PerProcessCpuBeforeRestraint = i.GetInt("OutOfControlProcessRestraint", "PerProcessUsageBeforeRestraint", 5);
        TimeOverQuotaMs = i.GetInt("OutOfControlProcessRestraint", "TimeOverQuotaBeforeRestraint", 900);
        PerProcessCpuForRestore = i.GetInt("OutOfControlProcessRestraint", "PerProcessUsageForRestore", 2);
        MinRestraintMs = i.GetInt("OutOfControlProcessRestraint", "MinimumTimeOfRestraint", 4200);
        TameOnlyNormal = i.GetBool("OutOfControlProcessRestraint", "TameOnlyNormal", true);
        LowerIoOnRestraint = i.GetBool("OutOfControlProcessRestraint", "LowerIOPriorityDuringRestraint", true);
        LowerToIdle = i.GetBool("OutOfControlProcessRestraint", "LowerToIdleInsteadOfBelowNormal", false);
        ExcludeForeground = i.GetBool("OutOfControlProcessRestraint", "ExcludeForegroundProcesses2", true);
        ExcludeChildrenOfForeground = i.GetBool("OutOfControlProcessRestraint", "ExcludeChildrenOfForeground", true);
        ExcludeServices = i.GetBool("OutOfControlProcessRestraint", "ExcludeServices", false);
        DisableWhenIdleMs = i.GetInt("OutOfControlProcessRestraint", "DisableProBalanceIfSysIdleThisManyMS", 30000);
        ProBalanceExclusions = IniFile.SplitList(i.Get("OutOfControlProcessRestraint", "OocExclusions"));

        FgBoostEnabled = i.GetBool("ForegroundBoosting", "BoostForegroundProcess", false);
        FgBoostPriority = ParsePriorityToken(i.Get("ForegroundBoosting", "ForegroundBoostPriorityClass", "0x8000"));
        FgBoostOnlyNormal = i.GetBool("ForegroundBoosting", "BoostOnlyNormal", true);

        SmartTrimEnabled = i.GetBool("MemoryManagement", "SmartTrimIsEnabled", true);
        SmartTrimMinRamLoad = i.GetInt("MemoryManagement", "SmartTrimAutoMinimumRAMLoad", 65);
        SmartTrimMinWssMb = i.GetInt("MemoryManagement", "MinimumProcessWSSInMb", 196);
        SmartTrimIntervalMin = i.GetInt("MemoryManagement", "SmartTrimIntervalMins", 15);
        SmartTrimExclusions = IniFile.SplitList(i.Get("MemoryManagement", "SmartTrimExclusions"));

        UpdateSpeedMs = Math.Clamp(i.GetInt("Performance", "UpdateSpeedCore", 1000), 250, 10000);
        ManageOnlyCurrentUser = i.GetBool("Performance", "ManageOnlyCurrentUser", false);

        DefaultPriorities = i.Get("ProcessDefaults", "DefaultPriorities");
        DefaultIoPriorities = i.Get("ProcessDefaults", "DefaultIOPriorities");
        DefaultMemPriorities = i.Get("ProcessDefaults", "DefaultMemoryPriorities");
        DefaultAffinities = i.Get("ProcessDefaults", "DefaultAffinitiesEx");
        DefaultCpuSets = i.Get("ProcessDefaults", "CPUSets");
        EfficiencyModes = i.Get("ProcessAllowances", "EfficiencyMode");
        InstanceLimits = i.Get("ProcessAllowances", "InstanceLimitedProcesses");
        CpuLimits = i.Get("ProcessAllowances", "CPULimitRules", i.Get("ProcessAllowances", "ProcessThrottles"));
        Disallowed = i.Get("ProcessAllowances", "DisallowedProcesses");
        Allowed = i.Get("ProcessAllowances", "AllowedProcesses");
        KeepRunning = i.Get("ProcessDefaults", "KeepRunningProcessesEx");
        WatchdogRules = i.Get("AdvancedRules", "WatchdogRules2");

        IdleSaverEnabled = i.GetBool("PowerSaver", "EnergySaverEnabled", false);
        IdleSaverIdleSec = i.GetInt("PowerSaver", "PowerSaver_IdleTime", 300);
        LoadPowerEnabled = i.GetBool("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherEnabled", false);
        LoadLowPct = i.GetDouble("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherLowCPULoadThreshold", 10);
        LoadHighPct = i.GetDouble("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherHighCPULoadThreshold", 50);
    }

    public void Save()
    {
        var i = Ini;
        i.Set("OutOfControlProcessRestraint", "OocOn", ProBalanceEnabled.ToString());
        i.Set("OutOfControlProcessRestraint", "TotalProcessorUsageBeforeRestraint", TotalCpuBeforeRestraint.ToString());
        i.Set("OutOfControlProcessRestraint", "PerProcessUsageBeforeRestraint", PerProcessCpuBeforeRestraint.ToString());
        i.Set("OutOfControlProcessRestraint", "TimeOverQuotaBeforeRestraint", TimeOverQuotaMs.ToString());
        i.Set("OutOfControlProcessRestraint", "PerProcessUsageForRestore", PerProcessCpuForRestore.ToString());
        i.Set("OutOfControlProcessRestraint", "MinimumTimeOfRestraint", MinRestraintMs.ToString());
        i.Set("OutOfControlProcessRestraint", "OocExclusions", IniFile.JoinList(ProBalanceExclusions));
        i.Set("ForegroundBoosting", "BoostForegroundProcess", FgBoostEnabled.ToString());
        i.Set("MemoryManagement", "SmartTrimIsEnabled", SmartTrimEnabled.ToString());
        i.Set("MemoryManagement", "SmartTrimAutoMinimumRAMLoad", SmartTrimMinRamLoad.ToString());
        i.Set("MemoryManagement", "MinimumProcessWSSInMb", SmartTrimMinWssMb.ToString());
        i.Set("MemoryManagement", "SmartTrimIntervalMins", SmartTrimIntervalMin.ToString());
        i.Set("Performance", "UpdateSpeedCore", UpdateSpeedMs.ToString());
        i.Set("ProcessDefaults", "DefaultPriorities", DefaultPriorities);
        i.Set("ProcessDefaults", "DefaultIOPriorities", DefaultIoPriorities);
        i.Set("ProcessDefaults", "DefaultMemoryPriorities", DefaultMemPriorities);
        i.Set("ProcessDefaults", "DefaultAffinitiesEx", DefaultAffinities);
        i.Set("ProcessDefaults", "CPUSets", DefaultCpuSets);
        i.Set("ProcessDefaults", "KeepRunningProcessesEx", KeepRunning);
        i.Set("ProcessAllowances", "EfficiencyModes", EfficiencyModes);
        i.Set("ProcessAllowances", "InstanceLimitedProcesses", InstanceLimits);
        i.Set("ProcessAllowances", "CPULimitRules", CpuLimits);
        i.Set("ProcessAllowances", "DisallowedProcesses", Disallowed);
        i.Set("ProcessAllowances", "AllowedProcesses", Allowed);
        i.Set("AdvancedRules", "WatchdogRules2", WatchdogRules);
        i.Set("PowerSaver", "EnergySaverEnabled", IdleSaverEnabled.ToString());
        i.Set("PowerSaver", "PowerSaver_IdleTime", IdleSaverIdleSec.ToString());
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherEnabled", LoadPowerEnabled.ToString());
        i.Set("Administration", "Version", "1000000");
        i.Save();
    }

    /// <summary>优先级文本/数字 → ProcessPriorityClass（"real time"/"high"/"above normal"/"normal"/"below normal"/"idle" 或 0x 值）</summary>
    public static System.Diagnostics.ProcessPriorityClass ParsePriority(string tok)
    {
        int v = ParsePriorityToken(tok);
        return v switch
        {
            0x100 => System.Diagnostics.ProcessPriorityClass.RealTime,
            0x80 => System.Diagnostics.ProcessPriorityClass.High,
            0x8000 => System.Diagnostics.ProcessPriorityClass.AboveNormal,
            0x40 => System.Diagnostics.ProcessPriorityClass.Idle,
            0x4000 => System.Diagnostics.ProcessPriorityClass.BelowNormal,
            _ => System.Diagnostics.ProcessPriorityClass.Normal,
        };
    }

    static int ParsePriorityToken(string tok)
    {
        tok = tok.Trim();
        if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(tok[2..], System.Globalization.NumberStyles.HexNumber, null, out var hx)) return hx;
        if (int.TryParse(tok, out var d))
            return d switch { 4 => 0x100, 3 => 0x80, 2 => 0x8000, 1 => 0x4000, 0 => 0x40, _ => d };
        return tok.ToLowerInvariant() switch
        {
            "real time" or "realtime" => 0x100, "high" => 0x80, "above normal" => 0x8000,
            "below normal" => 0x4000, "idle" => 0x40, _ => 0x20,
        };
    }

    /// <summary>进程名/路径匹配（支持 * ? 通配，大小写不敏感；pattern 可匹配 basename 或完整路径）</summary>
    public static bool MatchPattern(string pattern, string procName, string procPath)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        var opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        if (System.Text.RegularExpressions.Regex.IsMatch(procName, rx, opts)) return true;
        if (!string.IsNullOrEmpty(procPath) && System.Text.RegularExpressions.Regex.IsMatch(procPath, rx, opts)) return true;
        // 无通配时做后缀匹配：pattern 不带 .exe 也命中
        if (!pattern.Contains('*') && !pattern.Contains('?') && !pattern.Contains('\\') && !pattern.Contains('/'))
            return procName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                   procName.Equals(pattern + ".exe", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
