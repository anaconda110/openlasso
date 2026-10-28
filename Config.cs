using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenLasso;

/// <summary>可回写的 INI：保留注释、空行、段落顺序、未知键和原文件编码。</summary>
public class IniFile
{
    private sealed class Entry
    {
        public string Section = "";
        public string Key = "";
        public string Value = "";
        public int LineIndex;
    }

    private readonly List<string> _lines = new();
    private readonly List<string> _lineEndings = new();
    private readonly List<Entry> _items = new();
    private readonly Encoding _encoding;
    private readonly bool _emitPreamble;
    private readonly string _defaultLineEnding;
    private readonly bool _endsWithLineEnding;

    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        if (!File.Exists(path))
        {
            _encoding = new UTF8Encoding(true);
            _emitPreamble = true;
            _defaultLineEnding = "\r\n";
            _endsWithLineEnding = true;
            return;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { _encoding = Encoding.Unicode; _emitPreamble = true; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { _encoding = Encoding.BigEndianUnicode; _emitPreamble = true; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { _encoding = new UTF8Encoding(true); _emitPreamble = true; }
        else
        { _encoding = new UTF8Encoding(false); _emitPreamble = false; }

        int preambleLength = _emitPreamble && bytes.Length >= 3 && bytes[0] == 0xEF ? 3 :
            (_emitPreamble && bytes.Length >= 2 ? 2 : 0);
        string text = _encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        ParseText(text, out _defaultLineEnding, out _endsWithLineEnding);
        RebuildIndex();
    }

    private IniFile(string path, IEnumerable<string> lines, IEnumerable<string> lineEndings,
        Encoding encoding, bool emitPreamble, string defaultLineEnding, bool endsWithLineEnding)
    {
        Path = path;
        _lines.AddRange(lines);
        _lineEndings.AddRange(lineEndings);
        _encoding = encoding;
        _emitPreamble = emitPreamble;
        _defaultLineEnding = defaultLineEnding;
        _endsWithLineEnding = endsWithLineEnding;
        if (_lines.Count != _lineEndings.Count) throw new InvalidDataException("INI 行与换行索引数量不一致。");
        RebuildIndex();
    }

    public IniFile Clone() => new(Path, _lines, _lineEndings, _encoding, _emitPreamble,
        _defaultLineEnding, _endsWithLineEnding);

    private void ParseText(string text, out string defaultLineEnding, out bool endsWithLineEnding)
    {
        defaultLineEnding = "";
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            string ending;
            if (text[i] == '\r')
            {
                ending = i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
                if (ending == "\r\n") i++;
            }
            else if (text[i] == '\n') ending = "\n";
            else continue;

            if (defaultLineEnding.Length == 0) defaultLineEnding = ending;
            _lines.Add(text[start..(i - ending.Length + 1)]);
            _lineEndings.Add(ending);
            start = i + 1;
        }
        if (start < text.Length)
        {
            _lines.Add(text[start..]);
            _lineEndings.Add("");
        }
        endsWithLineEnding = text.Length > 0 && (text[^1] == '\r' || text[^1] == '\n');
        if (defaultLineEnding.Length == 0) defaultLineEnding = "\r\n";
    }

    private void RebuildIndex()
    {
        _items.Clear();
        string section = "";
        for (int i = 0; i < _lines.Count; i++)
        {
            string syntax = _lines[i].Trim().TrimStart('\uFEFF');
            if (syntax.Length == 0 || syntax.StartsWith(';') || syntax.StartsWith('#')) continue;
            if (syntax.StartsWith('[') && syntax.EndsWith(']'))
            { section = syntax[1..^1].Trim(); continue; }
            int eq = syntax.IndexOf('=');
            if (eq <= 0) continue;
            _items.Add(new Entry
            {
                Section = section,
                Key = syntax[..eq].Trim(),
                Value = syntax[(eq + 1)..].Trim(),
                LineIndex = i,
            });
        }
    }

    public string Get(string sec, string key, string def = "")
    {
        int i = _items.FindLastIndex(x => x.Section.Equals(sec, StringComparison.OrdinalIgnoreCase) &&
                                          x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? _items[i].Value : def;
    }

    public bool GetBool(string sec, string key, bool def)
    { var s = Get(sec, key); return bool.TryParse(s, out var b) ? b : def; }

    public int GetInt(string sec, string key, int def)
    { var s = Get(sec, key); return int.TryParse(s, out var v) ? v : def; }

    public double GetDouble(string sec, string key, double def)
    {
        var s = Get(sec, key);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
               double.TryParse(s, out v) ? v : def;
    }

    public void Set(string sec, string key, string value)
    {
        int itemIndex = _items.FindLastIndex(x => x.Section.Equals(sec, StringComparison.OrdinalIgnoreCase) &&
                                                  x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (itemIndex >= 0)
        {
            var item = _items[itemIndex];
            int eq = _lines[item.LineIndex].IndexOf('=');
            string prefix = eq >= 0 ? _lines[item.LineIndex][..(eq + 1)] : item.Key + "=";
            _lines[item.LineIndex] = prefix + value;
            item.Value = value;
            return;
        }

        int sectionLine = FindSectionLine(sec);
        string newLine = key + "=" + value;
        if (sectionLine < 0)
        {
            _lines.Add("[" + sec + "]");
            _lineEndings.Add(_defaultLineEnding);
            _lines.Add(newLine);
            _lineEndings.Add(_defaultLineEnding);
        }
        else
        {
            int insertAt = sectionLine + 1;
            int lastDataLine = sectionLine;
            for (int i = sectionLine + 1; i < _lines.Count; i++)
            {
                string syntax = _lines[i].Trim();
                if (syntax.StartsWith('[') && syntax.EndsWith(']')) break;
                if (syntax.Length > 0 && !syntax.StartsWith(';') && !syntax.StartsWith('#')) lastDataLine = i;
            }
            if (lastDataLine > sectionLine) insertAt = lastDataLine + 1;
            _lines.Insert(insertAt, newLine);
            _lineEndings.Insert(insertAt, _defaultLineEnding);
        }
        NormalizeFinalLineEnding();
        RebuildIndex();
    }

    private int FindSectionLine(string sec)
    {
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            string syntax = _lines[i].Trim().TrimStart('\uFEFF');
            if (syntax.StartsWith('[') && syntax.EndsWith(']') &&
                syntax[1..^1].Trim().Equals(sec, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private void NormalizeFinalLineEnding()
    {
        if (_lines.Count == 0) return;
        for (int i = 0; i < _lines.Count - 1; i++)
            if (_lineEndings[i].Length == 0) _lineEndings[i] = _defaultLineEnding;
        _lineEndings[^1] = _endsWithLineEnding ? _defaultLineEnding : "";
    }

    /// <summary>逗号分隔对列表："a.exe,v1,b.exe,v2" → [(a.exe,v1),(b.exe,v2)]</summary>
    public static List<(string name, string val)> ParsePairs(string s)
    {
        var r = new List<(string, string)>();
        var parts = SplitList(s);
        for (int i = 0; i + 1 < parts.Count; i += 2) r.Add((parts[i], parts[i + 1]));
        return r;
    }

    /// <summary>逗号分隔列表（空项剔除，转义 \, 视为字面逗号）。</summary>
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
        string dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (_emitPreamble)
                {
                    byte[] preamble = _encoding.GetPreamble();
                    if (preamble.Length > 0) stream.Write(preamble, 0, preamble.Length);
                }
                for (int i = 0; i < _lines.Count; i++)
                {
                    byte[] line = _encoding.GetBytes(_lines[i] + _lineEndings[i]);
                    stream.Write(line, 0, line.Length);
                }
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                try { File.Replace(temp, Path, null, ignoreMetadataErrors: true); }
                catch
                {
                    string backup = Path + ".bak-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(Path, backup, overwrite: true);
                        File.Copy(temp, Path, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(backup)) File.Copy(backup, Path, overwrite: true); }
                        catch { }
                        throw;
                    }
                    finally { try { if (File.Exists(backup)) File.Delete(backup); } catch { } }
                }
            }
            else File.Move(temp, Path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
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

    public LassoConfig(string path, bool importFromProcessLasso = true, string processLassoConfigPath = null)
    {
        ConfigPath = path;
        ConfigDir = Path.GetDirectoryName(path);
        Ini = new IniFile(path);
        Reload();
        // 首次运行且无配置：复制一份 Process Lasso 配置用于导入。
        // Ini 始终绑定 OpenLasso 自有路径，后续保存绝不能写回真实 prolasso.ini。
        if (!File.Exists(path) && importFromProcessLasso)
        {
            string plIni = processLassoConfigPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ProcessLasso", "config", "prolasso.ini");
            if (File.Exists(plIni))
            {
                try
                {
                    if (!string.IsNullOrEmpty(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    File.Copy(plIni, ConfigPath, overwrite: false);
                    Ini = new IniFile(ConfigPath);
                    Reload();
                    ImportedFrom = plIni;
                }
                catch
                {
                    // 导入失败时继续使用 OpenLasso 自己的配置路径，不影响后续保存。
                    ImportedFrom = null;
                    Ini = new IniFile(ConfigPath);
                    Reload();
                }
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
        {
            string boost = i.Get("ForegroundBoosting", "ForegroundBoostPriorityClass", "0x8000");
            FgBoostPriority = TryParsePriority(boost, out var boostPriority)
                ? (int)boostPriority : (int)System.Diagnostics.ProcessPriorityClass.AboveNormal;
        }
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
        EfficiencyModes = i.Get("ProcessAllowances", "EfficiencyMode",
            i.Get("ProcessAllowances", "EfficiencyModes"));
        InstanceLimits = i.Get("ProcessAllowances", "InstanceLimitedProcesses");
        CpuLimits = i.Get("ProcessAllowances", "CPULimitRules", i.Get("ProcessAllowances", "ProcessThrottles"));
        Disallowed = i.Get("ProcessAllowances", "DisallowedProcesses");
        Allowed = i.Get("ProcessAllowances", "AllowedProcesses");
        KeepRunning = i.Get("ProcessDefaults", "KeepRunningProcessesEx");
        WatchdogRules = i.Get("AdvancedRules", "WatchdogRules2");

        IdleSaverEnabled = i.GetBool("PowerSaver", "EnergySaverEnabled", false);
        IdleSaverIdleSec = i.GetInt("PowerSaver", "PowerSaver_IdleTime", 300);
        IdleSaverPlan = i.Get("PowerSaver", "PowerSaver_IdlePlan", "节能");
        LoadPowerEnabled = i.GetBool("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherEnabled", false);
        LoadLowPct = i.GetDouble("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherLowCPULoadThreshold", 10);
        LoadHighPct = i.GetDouble("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherHighCPULoadThreshold", 50);
        LoadLowPlan = i.Get("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherLowPlan", "节能");
        LoadHighPlan = i.Get("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherHighPlan", "高性能");
    }

    public void Save()
    {
        // 所有写入先落在副本上，失败时保持当前内存配置和原文件不变。
        var i = Ini.Clone();
        i.Set("OutOfControlProcessRestraint", "OocOn", ProBalanceEnabled.ToString());
        i.Set("OutOfControlProcessRestraint", "TotalProcessorUsageBeforeRestraint", TotalCpuBeforeRestraint.ToString());
        i.Set("OutOfControlProcessRestraint", "PerProcessUsageBeforeRestraint", PerProcessCpuBeforeRestraint.ToString());
        i.Set("OutOfControlProcessRestraint", "TimeOverQuotaBeforeRestraint", TimeOverQuotaMs.ToString());
        i.Set("OutOfControlProcessRestraint", "PerProcessUsageForRestore", PerProcessCpuForRestore.ToString());
        i.Set("OutOfControlProcessRestraint", "MinimumTimeOfRestraint", MinRestraintMs.ToString());
        i.Set("OutOfControlProcessRestraint", "TameOnlyNormal", TameOnlyNormal.ToString());
        i.Set("OutOfControlProcessRestraint", "LowerIOPriorityDuringRestraint", LowerIoOnRestraint.ToString());
        i.Set("OutOfControlProcessRestraint", "LowerToIdleInsteadOfBelowNormal", LowerToIdle.ToString());
        i.Set("OutOfControlProcessRestraint", "ExcludeForegroundProcesses2", ExcludeForeground.ToString());
        i.Set("OutOfControlProcessRestraint", "ExcludeChildrenOfForeground", ExcludeChildrenOfForeground.ToString());
        i.Set("OutOfControlProcessRestraint", "ExcludeServices", ExcludeServices.ToString());
        i.Set("OutOfControlProcessRestraint", "DisableProBalanceIfSysIdleThisManyMS", DisableWhenIdleMs.ToString());
        i.Set("OutOfControlProcessRestraint", "OocExclusions", IniFile.JoinList(ProBalanceExclusions));
        i.Set("ForegroundBoosting", "BoostForegroundProcess", FgBoostEnabled.ToString());
        i.Set("ForegroundBoosting", "ForegroundBoostPriorityClass", "0x" + FgBoostPriority.ToString("X"));
        i.Set("ForegroundBoosting", "BoostOnlyNormal", FgBoostOnlyNormal.ToString());
        i.Set("MemoryManagement", "SmartTrimIsEnabled", SmartTrimEnabled.ToString());
        i.Set("MemoryManagement", "SmartTrimAutoMinimumRAMLoad", SmartTrimMinRamLoad.ToString());
        i.Set("MemoryManagement", "MinimumProcessWSSInMb", SmartTrimMinWssMb.ToString());
        i.Set("MemoryManagement", "SmartTrimIntervalMins", SmartTrimIntervalMin.ToString());
        i.Set("MemoryManagement", "SmartTrimExclusions", IniFile.JoinList(SmartTrimExclusions));
        i.Set("Performance", "UpdateSpeedCore", UpdateSpeedMs.ToString());
        i.Set("Performance", "ManageOnlyCurrentUser", ManageOnlyCurrentUser.ToString());
        i.Set("ProcessDefaults", "DefaultPriorities", DefaultPriorities);
        i.Set("ProcessDefaults", "DefaultIOPriorities", DefaultIoPriorities);
        i.Set("ProcessDefaults", "DefaultMemoryPriorities", DefaultMemPriorities);
        i.Set("ProcessDefaults", "DefaultAffinitiesEx", DefaultAffinities);
        i.Set("ProcessDefaults", "CPUSets", DefaultCpuSets);
        i.Set("ProcessDefaults", "KeepRunningProcessesEx", KeepRunning);
        i.Set("ProcessAllowances", "EfficiencyMode", EfficiencyModes);
        i.Set("ProcessAllowances", "InstanceLimitedProcesses", InstanceLimits);
        i.Set("ProcessAllowances", "CPULimitRules", CpuLimits);
        i.Set("ProcessAllowances", "DisallowedProcesses", Disallowed);
        i.Set("ProcessAllowances", "AllowedProcesses", Allowed);
        i.Set("AdvancedRules", "WatchdogRules2", WatchdogRules);
        i.Set("PowerSaver", "EnergySaverEnabled", IdleSaverEnabled.ToString());
        i.Set("PowerSaver", "PowerSaver_IdleTime", IdleSaverIdleSec.ToString());
        i.Set("PowerSaver", "PowerSaver_IdlePlan", IdleSaverPlan);
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherEnabled", LoadPowerEnabled.ToString());
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherLowCPULoadThreshold",
            LoadLowPct.ToString(CultureInfo.InvariantCulture));
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherHighCPULoadThreshold",
            LoadHighPct.ToString(CultureInfo.InvariantCulture));
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherLowPlan", LoadLowPlan);
        i.Set("LoadBasedPowerSwitcher", "LoadBasedPowerSwitcherHighPlan", LoadHighPlan);
        i.Set("Administration", "Version", "1000000");
        i.Save();
        Ini = new IniFile(ConfigPath);
        // 回读实际落盘内容，确保调用方看到的是持久化后的值。
        Reload();
    }

    /// <summary>优先级文本/数字 → ProcessPriorityClass；非法值沿用 Normal 以兼容旧配置。</summary>
    public static System.Diagnostics.ProcessPriorityClass ParsePriority(string tok)
        => TryParsePriority(tok, out var pri) ? pri : System.Diagnostics.ProcessPriorityClass.Normal;

    public static bool TryParsePriority(string tok, out System.Diagnostics.ProcessPriorityClass priority)
    {
        priority = System.Diagnostics.ProcessPriorityClass.Normal;
        if (string.IsNullOrWhiteSpace(tok)) return false;
        string t = tok.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)) return false;
            return TryMapNativePriority(hex, out priority);
        }
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            // 兼容旧版 0..4 序号。
            int native = number switch { 4 => 0x100, 3 => 0x80, 2 => 0x8000, 1 => 0x4000, 0 => 0x40, _ => number };
            return TryMapNativePriority(native, out priority);
        }
        priority = t switch
        {
            "real time" or "realtime" => System.Diagnostics.ProcessPriorityClass.RealTime,
            "high" => System.Diagnostics.ProcessPriorityClass.High,
            "above normal" => System.Diagnostics.ProcessPriorityClass.AboveNormal,
            "normal" => System.Diagnostics.ProcessPriorityClass.Normal,
            "below normal" => System.Diagnostics.ProcessPriorityClass.BelowNormal,
            "idle" or "low" => System.Diagnostics.ProcessPriorityClass.Idle,
            _ => (System.Diagnostics.ProcessPriorityClass)(-1),
        };
        if ((int)priority == -1)
        {
            priority = System.Diagnostics.ProcessPriorityClass.Normal;
            return false;
        }
        return true;
    }

    private static bool TryMapNativePriority(int value, out System.Diagnostics.ProcessPriorityClass priority)
    {
        priority = value switch
        {
            0x20 => System.Diagnostics.ProcessPriorityClass.Normal,
            0x40 => System.Diagnostics.ProcessPriorityClass.Idle,
            0x80 => System.Diagnostics.ProcessPriorityClass.High,
            0x100 => System.Diagnostics.ProcessPriorityClass.RealTime,
            0x4000 => System.Diagnostics.ProcessPriorityClass.BelowNormal,
            0x8000 => System.Diagnostics.ProcessPriorityClass.AboveNormal,
            _ => (System.Diagnostics.ProcessPriorityClass)(-1),
        };
        if ((int)priority == -1)
        {
            priority = System.Diagnostics.ProcessPriorityClass.Normal;
            return false;
        }
        return true;
    }

    private static readonly ConcurrentDictionary<string, Regex> PatternCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进程名/路径匹配（支持 * ? 通配，大小写不敏感；无通配名称也接受省略 .exe）。</summary>
    public static bool MatchPattern(string pattern, string procName, string procPath)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim();
        procName ??= "";
        procPath ??= "";

        bool hasWildcard = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
        if (!hasWildcard && pattern.IndexOfAny(new[] { '\\', '/' }) < 0)
        {
            return procName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                   (!pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    procName.Equals(pattern + ".exe", StringComparison.OrdinalIgnoreCase));
        }

        var regex = PatternCache.GetOrAdd(pattern, static p =>
            new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        return regex.IsMatch(procName) || (procPath.Length > 0 && regex.IsMatch(procPath));
    }
}
