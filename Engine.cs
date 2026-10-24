using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenLasso;

public class ProcInfo
{
    public int Pid; public string Name = "", Path = "", Desc = ""; public int SessionId;
    public double CpuPercent; public ProcessPriorityClass Priority;
    public long AffinityMask; public double WorkingSetMB; public DateTime StartTime;
    public bool IsCurrent; public bool Trimmed; public bool ProBalanced; public bool Throttled;
    public string Note = "";
}

public class EngineEvent { public DateTime Time; public int ActionId; public string Msg = ""; public int Pid; public string Name = ""; }

/// <summary>核心引擎：对应 ProcessGovernor.exe 的进程管理循环（默认 1s 轮询 + CPU 增量统计），
/// 实现：默认规则（优先级/IO/内存优先级/亲和性/CPU 集/能效模式）、ProBalance、前台提升、SmartTrim、
/// 实例限制、黑名单、CPU 限额（占空比）、看门狗、保持运行、电源自动化。</summary>
public class GovernorEngine : IDisposable
{
    readonly LassoConfig _cfg;
    readonly ActionLog _log;
    CancellationTokenSource _cts;
    Task _loop, _throttleLoop;
    int _paused;

    // 快照状态
    Dictionary<int, Process> _procs = new();
    Dictionary<int, long> _lastCpuMs = new();
    Dictionary<int, DateTime> _lastWall = new();
    public volatile List<ProcInfo> Current = new();
    public double TotalCpu; public double RamLoad; public double TotalRamMB, AvailRamMB;
    public int ForegroundPid; public string ForegroundName = "";
    readonly Queue<EngineEvent> _events = new(); readonly object _evLock = new();

    // 规则（编译后）
    List<(string p, ProcessPriorityClass v)> _rPriority = new();
    List<(string p, int v)> _rIo = new(), _rMem = new();
    List<(string p, long v)> _rAff = new();
    List<(string p, string v)> _rSets = new();
    List<string> _rEco = new();
    List<(string p, int v)> _rInstance = new();
    List<(string p, int v)> _rLimit = new();
    List<string> _rDisallow = new(), _rAllow = new(), _rKeep = new();
    List<WatchRule> _rWatch = new();
    HashSet<int> _ruleApplied = new();
    readonly List<(string pat, string name, string path)> _keepLaunch = new();

    // ProBalance 状态
    DateTime? _overQuotaSince;
    readonly Dictionary<int, (ProcessPriorityClass pri, DateTime since, string name)> _restrained = new();
    bool _pbActive;
    public bool ProBalanceActive => _pbActive;
    public string ProBalanceInfo = "";

    // 前台提升
    int _boostedPid; ProcessPriorityClass _boostOrig;

    // CPU 限额
    readonly Dictionary<int, (int activeMs, DateTime cycleStart, bool suspended)> _throttled = new();

    // SmartTrim / 电源
    DateTime _nextTrim = DateTime.MinValue;
    readonly List<double> _cpuSamples = new();
    DateTime? _idlePlanSince;
    string _lastPlan = "";

    public GovernorEngine(LassoConfig cfg, ActionLog log) { _cfg = cfg; _log = log; ReloadRules(); }
    public bool EngineRunning { get; private set; }
    public bool Paused { get => Volatile.Read(ref _paused) != 0; set => Volatile.Write(ref _paused, value ? 1 : 0); }
    public LassoConfig Config => _cfg;
    public ActionLog Log => _log;

    public void ReloadRules()
    {
        _rPriority = IniFile.ParsePairs(_cfg.DefaultPriorities).Select(x => (x.name, LassoConfig.ParsePriority(x.val))).ToList();
        _rIo = IniFile.ParsePairs(_cfg.DefaultIoPriorities).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 2)).ToList();
        _rMem = IniFile.ParsePairs(_cfg.DefaultMemPriorities).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 5)).ToList();
        _rAff = IniFile.ParsePairs(_cfg.DefaultAffinities).Select(x => (x.name, ParseMask(x.val))).Where(x => x.Item2 != 0).ToList();
        _rSets = IniFile.ParsePairs(_cfg.DefaultCpuSets).ToList();
        _rEco = IniFile.SplitList(_cfg.EfficiencyModes);
        _rInstance = IniFile.ParsePairs(_cfg.InstanceLimits).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 1)).ToList();
        _rLimit = IniFile.ParsePairs(_cfg.CpuLimits).Select(x => (x.name, ParsePercent(x.val))).Where(x => x.Item2 is > 0 and < 100).ToList();
        _rDisallow = IniFile.SplitList(_cfg.Disallowed);
        _rAllow = IniFile.SplitList(_cfg.Allowed);
        _rKeep = IniFile.SplitList(_cfg.KeepRunning);
        _rWatch = IniFile.SplitList(_cfg.WatchdogRules).Select(WatchRule.Parse).Where(r => r != null).ToList();
        _ruleApplied.Clear();
    }

    public static long ParseMask(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var hx)) return hx;
        if (long.TryParse(s, out var d)) return d;
        long mask = 0;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Contains('-') && int.TryParse(p[..p.IndexOf('-')], out var a) && int.TryParse(p[(p.IndexOf('-') + 1)..], out var b))
                for (int i = a; i <= b && i < 63; i++) mask |= 1L << i;
            else if (int.TryParse(p, out var c) && c is >= 0 and < 63) mask |= 1L << c;
        }
        return mask;
    }

    static int ParsePercent(string s) { s = s.Trim().TrimEnd('%'); return int.TryParse(s, out var v) ? v : 0; }

    // ---------------- 生命周期 ----------------
    public void Start()
    {
        if (EngineRunning) return;
        EngineRunning = true;
        _cts = new CancellationTokenSource();
        _log.Write(0, "opengovernor", ActionLog.ACT_START, $"OpenLasso v{Ver()} 开源进程优化器 — 进程管理已开始");
        _log.Write(0, "opengovernor", ActionLog.ACT_CONFIG_LOADED, _cfg.ImportedFrom != null
            ? $"配置已从 Process Lasso 导入（{ImportedNote()}）并落盘：{_cfg.ConfigPath}" : "配置来自：" + _cfg.ConfigPath);
        _log.Write(0, "opengovernor", ActionLog.ACT_SCOPE, _cfg.ManageOnlyCurrentUser ? "管理用户的进程：仅当前用户" : "管理用户的进程：所有用户（需管理员权限完整生效）");
        _loop = Task.Run(() => Loop(_cts.Token));
        _throttleLoop = Task.Run(() => ThrottleLoop(_cts.Token));
    }

    string ImportedNote() => _cfg.ImportedFrom;
    static string Ver() => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public void Stop()
    {
        if (!EngineRunning) return;
        EngineRunning = false;
        _cts?.Cancel();
        try { _loop?.Wait(3000); _throttleLoop?.Wait(1000); } catch { }
        RestoreAllRestraints();
        RestoreFgBoost();
    }

    public void Dispose() { Stop(); _cts?.Dispose(); }

    // ---------------- 主循环 ----------------
    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Emit(ActionLog.ACT_START, "引擎异常(已忽略): " + ex.Message, 0, "engine"); }
            try { await Task.Delay(Math.Max(250, _cfg.UpdateSpeedMs), ct); } catch (TaskCanceledException) { break; }
        }
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        var fresh = new Dictionary<int, Process>();
        var list = new List<ProcInfo>();
        var cores = Environment.ProcessorCount;
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.HasExited) { p.Dispose(); continue; } } catch { /* 受保护进程保留 */ }
            fresh[p.Id] = p;
            double cpu = 0;
            try
            {
                long ms = (long)p.TotalProcessorTime.TotalMilliseconds;
                if (_lastCpuMs.TryGetValue(p.Id, out var om) && _lastWall.TryGetValue(p.Id, out var ow))
                {
                    double wall = (now - ow).TotalMilliseconds;
                    if (wall > 0) cpu = Math.Clamp(100.0 * (ms - om) / (wall * cores), 0, 100 * cores);
                }
                _lastCpuMs[p.Id] = ms; _lastWall[p.Id] = now;
            }
            catch { }
            string path = "", desc = "";
            try { path = p.MainModule?.FileName ?? ""; desc = p.MainModule?.FileVersionInfo?.FileDescription ?? ""; } catch { }
            try
            {
                var pi = new ProcInfo
                {
                    Pid = p.Id, Name = p.ProcessName.ToLowerInvariant() + ".exe", Path = path, Desc = desc,
                    SessionId = p.SessionId, CpuPercent = Math.Round(cpu, 1),
                    Priority = TryGet(() => p.PriorityClass, ProcessPriorityClass.Normal),
                    AffinityMask = TryGet(() => (long)p.ProcessorAffinity, (1L << cores) - 1),
                    WorkingSetMB = Math.Round(p.WorkingSet64 / 1048576.0, 1),
                    StartTime = TryGet(() => p.StartTime, DateTime.Now),
                    IsCurrent = p.Id == Environment.ProcessId,
                    ProBalanced = _restrained.ContainsKey(p.Id),
                    Throttled = _throttled.ContainsKey(p.Id),
                };
                ApplyNotes(pi);
                list.Add(pi);
            }
            catch { p.Dispose(); fresh.Remove(p.Id); }
        }
        foreach (var pid in _lastCpuMs.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) { _lastCpuMs.Remove(pid); _lastWall.Remove(pid); }
        foreach (var pid in _ruleApplied.Where(k => !fresh.ContainsKey(k)).ToList()) _ruleApplied.Remove(pid);
        foreach (var pid in _restrained.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _restrained.Remove(pid);
        foreach (var pid in _throttled.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _throttled.Remove(pid);
        var old = _procs; _procs = fresh;
        Current = list.OrderByDescending(x => x.CpuPercent).ToList();
        foreach (var op in old.Values) try { op.Dispose(); } catch { }

        TotalCpu = Math.Round(Native.QueryTotalCpuPercent(ref _lastIdle, ref _lastKn, ref _lastUs), 1);
        QueryMemory();
        ForegroundPid = Native.ForegroundPid();
        ForegroundName = Current.FirstOrDefault(x => x.Pid == ForegroundPid)?.Name ?? "";

        if (Paused) return;

        foreach (var pi in Current) if (_ruleApplied.Add(pi.Pid)) ApplyDefaults(pi);
        CheckDisallowed();
        CheckInstanceLimits();
        CheckKeepRunning();
        CheckWatchdogs();
        ProBalanceTick();
        ForegroundBoostTick();
        PowerTick();
        if (DateTime.UtcNow >= _nextTrim) { _nextTrim = DateTime.UtcNow.AddMinutes(Math.Max(1, _cfg.SmartTrimIntervalMin)); RunSmartTrim(); }
    }

    static T TryGet<T>(Func<T> f, T def) { try { return f(); } catch { return def; } }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);
    ulong _lastIdle, _lastKn, _lastUs;

    void QueryMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) { TotalRamMB = m.TotalPhys / 1048576.0; AvailRamMB = m.AvailPhys / 1048576.0; RamLoad = m.dwMemoryLoad; }
    }

    void ApplyNotes(ProcInfo pi)
    {
        if (_rPriority.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "优先级 ";
        if (_rAff.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "亲和 ";
        if (_rLimit.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "限速 ";
        if (_rDisallow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "黑名单 ";
        if (_rKeep.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "保活 ";
        if (_rEco.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "能效 ";
    }

    // ---------------- 默认规则 ----------------
    void ApplyDefaults(ProcInfo pi)
    {
        if (pi.IsCurrent) return;
        foreach (var (pat, pri) in _rPriority)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path)) { SetPriorityInternal(pi.Pid, pri, "默认规则"); break; }
        foreach (var (pat, io) in _rIo)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (WithHandle(pi.Pid, h => Native.SetIoPriority(h, Math.Clamp(io, 0, 3)))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_IO_PRIORITY, $"IO 优先级 → {IoName(io)}", pi.Path); break; }
        foreach (var (pat, mp) in _rMem)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (WithHandle(pi.Pid, h => Native.SetMemoryPriority(h, (uint)Math.Clamp(mp, 0, 7)))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_MEM_PRIORITY, $"内存优先级 → {mp}", pi.Path); break; }
        foreach (var (pat, mask) in _rAff)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path)) { SetAffinityInternal(pi.Pid, mask, "默认规则"); break; }
        foreach (var (pat, sets) in _rSets)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            {
                var cs = ParseCoreList(sets);
                if (cs.Count > 0 && WithHandle(pi.Pid, h => Native.SetCpuSets(h, cs))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_CPUSETS, $"CPU 集 → {sets}", pi.Path);
                break;
            }
        foreach (var pat in _rEco)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (EcoInternal(pi.Pid, true)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_ECO, "能效模式已启用", pi.Path); break; }
        foreach (var (pat, pct) in _rLimit)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { _throttled[pi.Pid] = (pct * 10, DateTime.UtcNow, false); _log.Write(pi.Pid, pi.Name, ActionLog.ACT_THROTTLE, $"CPU 限额 → {pct}%", pi.Path); Emit(ActionLog.ACT_THROTTLE, $"{pi.Name} 限额 {pct}%", pi.Pid, pi.Name); break; }
        foreach (var pat in _rKeep)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path) && !string.IsNullOrEmpty(pi.Path))
            { _keepLaunch.RemoveAll(k => k.pat == pat); _keepLaunch.Add((pat, pi.Name, pi.Path)); break; }
    }

    static List<int> ParseCoreList(string s)
    {
        var r = new List<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Contains('-') && int.TryParse(p[..p.IndexOf('-')], out var a) && int.TryParse(p[(p.IndexOf('-') + 1)..], out var b))
                for (int i = a; i <= b; i++) r.Add(i);
            else if (int.TryParse(p, out var c)) r.Add(c);
        }
        return r;
    }

    static string IoName(int io) => io switch { 0 => "极低", 1 => "低", 2 => "正常", 3 => "高", _ => io.ToString() };

    // ---------------- 黑名单 / 实例限制 / 保活 / 看门狗 ----------------
    void CheckDisallowed()
    {
        if (_rDisallow.Count == 0) return;
        foreach (var pi in Current)
        {
            if (pi.IsCurrent) continue;
            if (_rAllow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) continue;
            if (_rDisallow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path)))
                KillInternal(pi.Pid, ActionLog.ACT_DISALLOWED, "命中黑名单规则");
        }
    }

    void CheckInstanceLimits()
    {
        foreach (var (pat, max) in _rInstance)
        {
            var hits = Current.Where(x => LassoConfig.MatchPattern(pat, x.Name, x.Path)).OrderBy(x => x.StartTime).ToList();
            while (hits.Count > max)
            {
                var victim = hits[^1];
                hits.RemoveAt(hits.Count - 1);
                KillInternal(victim.Pid, ActionLog.ACT_INSTANCE_KILL, $"实例数超过限制 {max}");
            }
        }
    }

    void CheckKeepRunning()
    {
        if (_rKeep.Count == 0) return;
        foreach (var entry in _keepLaunch.ToList())
        {
            bool alive = Current.Any(x => LassoConfig.MatchPattern(entry.pat, x.Name, x.Path));
            if (alive || !File.Exists(entry.path)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(entry.path) { UseShellExecute = true });
                _log.Write(0, entry.name, ActionLog.ACT_RESTART, $"进程退出，已自动重启：{entry.path}");
                Emit(ActionLog.ACT_RESTART, $"{entry.name} 已自动重启", 0, entry.name);
            }
            catch (Exception ex) { _log.Write(0, entry.name, ActionLog.ACT_RESTART, "重启失败: " + ex.Message); }
        }
    }

    class WatchRule
    {
        public string Pattern; public double CpuAbove = -1, MemAboveMb = -1, TimeAboveSec = -1;
        public string Action = "kill", ActionArg = "";
        public static WatchRule Parse(string s)
        {
            var parts = s.Split(';');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) return null;
            var r = new WatchRule { Pattern = parts[0].Trim() };
            foreach (var seg in parts.Skip(1))
            {
                var kv = seg.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant(); var v = kv[1].Trim();
                double Num() { double.TryParse(v.TrimEnd('%', 'M', 'B', 's'), out var d); return d; }
                if (k == "cpu") r.CpuAbove = Num();
                else if (k == "mem") r.MemAboveMb = Num();
                else if (k == "time") r.TimeAboveSec = Num();
                else if (k == "action") { var ax = v.Split(':', 2); r.Action = ax[0].Trim().ToLowerInvariant(); if (ax.Length == 2) r.ActionArg = ax[1].Trim(); }
            }
            return r;
        }
    }

    void CheckWatchdogs()
    {
        foreach (var r in _rWatch)
            foreach (var pi in Current.Where(x => LassoConfig.MatchPattern(r.Pattern, x.Name, x.Path)))
            {
                bool hit = (r.CpuAbove >= 0 && pi.CpuPercent >= r.CpuAbove)
                        || (r.MemAboveMb >= 0 && pi.WorkingSetMB >= r.MemAboveMb)
                        || (r.TimeAboveSec >= 0 && (DateTime.Now - pi.StartTime).TotalSeconds >= r.TimeAboveSec);
                if (!hit) continue;
                switch (r.Action)
                {
                    case "kill": KillInternal(pi.Pid, ActionLog.ACT_WATCHDOG, $"看门狗触发（CPU≥{r.CpuAbove}% 内存≥{r.MemAboveMb}MB 运行≥{r.TimeAboveSec}s）"); break;
                    case "trim": if (TrimInternal(pi.Pid)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_WATCHDOG, "看门狗触发工作集整理", pi.Path); break;
                    case "priority": SetPriorityInternal(pi.Pid, LassoConfig.ParsePriority(r.ActionArg), "看门狗"); break;
                }
            }
    }

    // ---------------- ProBalance ----------------
    void ProBalanceTick()
    {
        if (!_cfg.ProBalanceEnabled) { RestoreAllRestraints(); return; }
        if (_cfg.DisableWhenIdleMs > 0 && Native.IdleMilliseconds() > (uint)_cfg.DisableWhenIdleMs) { RestoreAllRestraints(); _overQuotaSince = null; return; }

        bool overQuota = TotalCpu >= _cfg.TotalCpuBeforeRestraint;
        if (overQuota)
        {
            _overQuotaSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _overQuotaSince.Value).TotalMilliseconds >= _cfg.TimeOverQuotaMs)
                foreach (var pi in Current)
                {
                    if (_restrained.ContainsKey(pi.Pid)) continue;
                    if (pi.CpuPercent < _cfg.PerProcessCpuBeforeRestraint) continue;
                    if (!IsRestrainable(pi)) continue;
                    Restrain(pi);
                }
        }
        else _overQuotaSince = null;

        foreach (var pid in _restrained.Keys.ToList())
        {
            var st = _restrained[pid];
            var pi = Current.FirstOrDefault(x => x.Pid == pid);
            bool restore = pi == null
                || (pi.CpuPercent <= _cfg.PerProcessCpuForRestore && (DateTime.UtcNow - st.since).TotalMilliseconds >= _cfg.MinRestraintMs)
                || (!overQuota && (DateTime.UtcNow - st.since).TotalMilliseconds >= _cfg.MinRestraintMs);
            if (restore) Unrestrain(pid, pi);
        }

        bool nowActive = _restrained.Count > 0;
        if (nowActive != _pbActive)
        {
            _pbActive = nowActive;
            ProBalanceInfo = nowActive ? $"抑制 {_restrained.Count} 个进程" : "";
            Emit(ActionLog.ACT_PB_BEGIN, nowActive ? ProBalanceInfo : "ProBalance：全部恢复", 0, "probalance");
        }
    }

    bool IsRestrainable(ProcInfo pi)
    {
        if (pi.IsCurrent) return false;
        if (_cfg.TameOnlyNormal && pi.Priority != ProcessPriorityClass.Normal) return false;
        if (_cfg.ExcludeServices && pi.SessionId == 0) return false;
        if (_cfg.ExcludeForeground && pi.Pid == ForegroundPid) return false;
        if (_cfg.ExcludeChildrenOfForeground && ForegroundPid != 0)
        {
            var proc = _procs.GetValueOrDefault(pi.Pid);
            if (proc != null && Native.ParentPidOf(proc) == ForegroundPid) return false;
        }
        if (_cfg.ProBalanceExclusions.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) return false;
        if (_throttled.ContainsKey(pi.Pid)) return false;
        return true;
    }

    void Restrain(ProcInfo pi)
    {
        var target = _cfg.LowerToIdle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
        if (!SetPriorityInternal(pi.Pid, target, "ProBalance")) return;
        if (_cfg.LowerIoOnRestraint) WithHandle(pi.Pid, h => Native.SetIoPriority(h, 0));
        _restrained[pi.Pid] = (pi.Priority, DateTime.UtcNow, pi.Name);
        _log.Write(pi.Pid, pi.Name, ActionLog.ACT_PB_BEGIN, $"ProBalance 抑制：优先级 {pi.Priority}→{target}（CPU {pi.CpuPercent}%）", pi.Path);
        Emit(ActionLog.ACT_PB_BEGIN, $"{pi.Name} 被抑制（CPU {pi.CpuPercent}%）", pi.Pid, pi.Name);
    }

    void Unrestrain(int pid, ProcInfo pi)
    {
        if (!_restrained.TryGetValue(pid, out var st)) return;
        if (pi != null)
        {
            SetPriorityInternal(pid, st.pri, "ProBalance恢复");
            if (_cfg.LowerIoOnRestraint) WithHandle(pid, h => Native.SetIoPriority(h, 2));
        }
        _restrained.Remove(pid);
        _log.Write(pid, st.name, ActionLog.ACT_PB_END, "ProBalance 恢复原优先级 " + st.pri, pi?.Path ?? "");
    }

    void RestoreAllRestraints()
    {
        foreach (var pid in _restrained.Keys.ToList()) Unrestrain(pid, Current.FirstOrDefault(x => x.Pid == pid));
        if (_pbActive) { _pbActive = false; ProBalanceInfo = ""; }
    }

    // ---------------- 前台提升 ----------------
    void ForegroundBoostTick()
    {
        if (!_cfg.FgBoostEnabled) { RestoreFgBoost(); return; }
        if (ForegroundPid == _boostedPid) return;
        RestoreFgBoost();
        var pi = Current.FirstOrDefault(x => x.Pid == ForegroundPid);
        if (pi == null || pi.IsCurrent) return;
        if (_cfg.FgBoostOnlyNormal && pi.Priority != ProcessPriorityClass.Normal) return;
        var target = LassoConfig.ParsePriority("0x" + _cfg.FgBoostPriority.ToString("X"));
        if (SetPriorityInternal(pi.Pid, target, "前台提升")) { _boostedPid = pi.Pid; _boostOrig = pi.Priority; }
    }

    void RestoreFgBoost()
    {
        if (_boostedPid == 0) return;
        var pi = Current.FirstOrDefault(x => x.Pid == _boostedPid);
        if (pi != null) SetPriorityInternal(_boostedPid, _boostOrig, "前台提升恢复");
        _boostedPid = 0;
    }

    // ---------------- SmartTrim ----------------
    public void RunSmartTrim()
    {
        if (!_cfg.SmartTrimEnabled) return;
        if (RamLoad < _cfg.SmartTrimMinRamLoad)
        { _log.Write(0, "opengovernor", ActionLog.ACT_SKIP_STANDBY, $"内存负载 {RamLoad:0}% 低于阈值 {_cfg.SmartTrimMinRamLoad}%，本轮跳过整理"); return; }
        int trimmed = 0; double recovered = 0;
        var targets = Current.Where(x => x.Pid != Environment.ProcessId).OrderByDescending(x => x.WorkingSetMB).ToList();
        foreach (var pi in targets)
        {
            if (pi.WorkingSetMB < _cfg.SmartTrimMinWssMb) break;
            if (_cfg.SmartTrimExclusions.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) continue;
            double before = pi.WorkingSetMB, after = before;
            if (!TrimInternal(pi.Pid)) continue;
            WithProc(pi.Pid, p => after = p.WorkingSet64 / 1048576.0);
            trimmed++; recovered += Math.Max(0, before - after);
            _log.Write(pi.Pid, pi.Name, ActionLog.ACT_TRIM, $"已恢复 {Math.Max(0, before - after):0} MB RAM。工作组原为 {before:0} MB - 现在为 {after:0} MB。", pi.Path);
        }
        _log.Write(0, "opengovernor", ActionLog.ACT_TRIM_DONE, $"已整理 {trimmed} 个进程，共有 {Current.Count} 个。");
        if (trimmed > 0) Emit(ActionLog.ACT_TRIM_DONE, $"智能整理 {trimmed} 个进程，释放约 {recovered:0} MB", 0, "smarttrim");
    }

    // ---------------- 电源自动化 ----------------
    void PowerTick()
    {
        if (!_cfg.IdleSaverEnabled && !_cfg.LoadPowerEnabled) return;
        _cpuSamples.Add(TotalCpu);
        while (_cpuSamples.Count > 5) _cpuSamples.RemoveAt(0);
        var avg = _cpuSamples.Average();
        var plan = Native.ActivePlanName();
        if (plan != null && plan != _lastPlan) { _lastPlan = plan; Emit(ActionLog.ACT_POWER, "当前电源计划: " + plan, 0, "power"); }

        if (_cfg.IdleSaverEnabled)
        {
            uint idle = Native.IdleMilliseconds();
            if (idle >= (uint)Math.Max(30, _cfg.IdleSaverIdleSec) * 1000 && _idlePlanSince == null)
            { if (SwitchPlan(_cfg.IdleSaverPlan, $"空闲 {idle / 1000:0}s")) _idlePlanSince = DateTime.UtcNow; }
            else if (_idlePlanSince != null && idle < 1000)
            { _idlePlanSince = null; SwitchPlan(_cfg.LoadHighPlan, "用户恢复活动"); }
        }
        if (_cfg.LoadPowerEnabled && _idlePlanSince == null)
        {
            if (avg <= _cfg.LoadLowPct && !PlanMatches(_cfg.LoadLowPlan)) SwitchPlan(_cfg.LoadLowPlan, $"平均 CPU {avg:0.0}% ≤ {_cfg.LoadLowPct}%");
            else if (avg >= _cfg.LoadHighPct && !PlanMatches(_cfg.LoadHighPlan)) SwitchPlan(_cfg.LoadHighPlan, $"平均 CPU {avg:0.0}% ≥ {_cfg.LoadHighPct}%");
        }
    }

    bool PlanMatches(string name) { var cur = Native.ActivePlanName() ?? ""; return cur.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0; }

    bool SwitchPlan(string name, string reason)
    {
        try
        {
            var hit = Native.EnumPowerSchemes().FirstOrDefault(s => s.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit.name == null) { _log.Write(0, "opengovernor", ActionLog.ACT_POWER, $"未找到名为“{name}”的电源计划"); return false; }
            if (Native.SetActiveScheme(hit.guid))
            {
                _log.Write(0, "opengovernor", ActionLog.ACT_POWER, $"{reason} → 切换电源计划“{hit.name}”");
                Emit(ActionLog.ACT_POWER, $"{reason} → {hit.name}", 0, "power");
                return true;
            }
        }
        catch (Exception ex) { _log.Write(0, "opengovernor", ActionLog.ACT_POWER, "切换失败: " + ex.Message); }
        return false;
    }

    // ---------------- CPU 限额（占空比挂起/恢复） ----------------
    async Task ThrottleLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var pid in _throttled.Keys.ToList())
                {
                    var st = _throttled[pid];
                    const int period = 1000;
                    var el = (DateTime.UtcNow - st.cycleStart).TotalMilliseconds;
                    if (el >= period) { _throttled[pid] = (st.activeMs, st.cycleStart.AddMilliseconds(period), false); WithHandle(pid, h => Native.NtResumeProcess(h)); continue; }
                    bool shouldSuspend = el >= st.activeMs;
                    if (shouldSuspend != st.suspended)
                    {
                        WithHandle(pid, h => { if (shouldSuspend) Native.NtSuspendProcess(h); else Native.NtResumeProcess(h); });
                        _throttled[pid] = (st.activeMs, st.cycleStart, shouldSuspend);
                    }
                }
            }
            catch { }
            try { await Task.Delay(50, ct); } catch (TaskCanceledException) { break; }
        }
        foreach (var pid in _throttled.Keys.ToList()) WithHandle(pid, h => Native.NtResumeProcess(h));
    }

    // ---------------- 手动操作（GUI 调用） ----------------
    public bool SetPriorityInternal(int pid, ProcessPriorityClass pri, string why)
    {
        if (!WithProc(pid, p => p.PriorityClass = pri)) return false;
        var pi = Current.FirstOrDefault(x => x.Pid == pid);
        _log.Write(pid, pi?.Name ?? pid.ToString(), ActionLog.ACT_PRIORITY, $"优先级 → {pri}（{why}）", pi?.Path ?? "");
        return true;
    }

    public bool SetAffinityInternal(int pid, long mask, string why)
    {
        if (!WithProc(pid, p => p.ProcessorAffinity = (IntPtr)mask)) return false;
        var pi = Current.FirstOrDefault(x => x.Pid == pid);
        _log.Write(pid, pi?.Name ?? pid.ToString(), ActionLog.ACT_AFFINITY, $"亲和性掩码 → 0x{mask:X}（{why}）", pi?.Path ?? "");
        return true;
    }

    public bool KillInternal(int pid, int actId, string why)
    {
        if (!WithProc(pid, p => { if (!p.HasExited) p.Kill(true); })) return false;
        var pi = Current.FirstOrDefault(x => x.Pid == pid);
        _log.Write(pid, pi?.Name ?? pid.ToString(), actId, why, pi?.Path ?? "");
        Emit(actId, $"已终止 {pi?.Name ?? pid.ToString()}（{why}）", pid, pi?.Name ?? "");
        return true;
    }

    public bool TrimInternal(int pid) => WithHandle(pid, h => Native.EmptyWorkingSet(h));
    public bool EcoInternal(int pid, bool on) => WithHandle(pid, h => Native.SetEcoMode(h, on));

    bool WithHandle(int pid, Action<IntPtr> f) => WithProc(pid, p => f(p.Handle));
    bool WithProc(int pid, Action<Process> f)
    {
        try { using var p = Process.GetProcessById(pid); f(p); return true; }
        catch { return false; }
    }

    // ---------------- 事件通道 ----------------
    public void Emit(int actionId, string msg, int pid, string name)
    {
        lock (_evLock)
        {
            _events.Enqueue(new EngineEvent { Time = DateTime.Now, ActionId = actionId, Msg = msg, Pid = pid, Name = name });
            while (_events.Count > 300) _events.Dequeue();
        }
    }

    public List<EngineEvent> DrainEvents()
    { lock (_evLock) { var r = _events.ToList(); _events.Clear(); return r; } }
}
