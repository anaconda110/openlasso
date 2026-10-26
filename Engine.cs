using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenLasso;

public class ProcInfo
{
    public int Pid; public string Name = "", Path = "", Desc = ""; public int SessionId;
    public long StartTimeUtcTicks;
    public double CpuPercent; public ProcessPriorityClass Priority;
    public long AffinityMask; public double WorkingSetMB; public DateTime StartTime;
    public bool IsCurrent; public bool Trimmed; public bool ProBalanced; public bool Throttled;
    public bool CanManage = true; public bool AccessLimited;
    public string Note = "";
    public ProcessIdentity Identity => new(Pid, StartTimeUtcTicks);
}

public readonly record struct ProcessIdentity(int Pid, long StartTimeUtcTicks);

public sealed record EngineSnapshot(
    long Sequence,
    DateTime TimestampUtc,
    IReadOnlyList<ProcInfo> Processes,
    double TotalCpu,
    double? GpuLoad,
    string GpuEngine,
    string GpuStatus,
    double RamLoad,
    double TotalRamMB,
    double AvailRamMB,
    int ForegroundPid,
    string ForegroundName,
    bool ProBalanceActive,
    string ProBalanceInfo,
    bool Paused,
    string PowerPlan,
    bool IsAdministrator,
    bool ManageOnlyCurrentUser);

public class EngineEvent { public DateTime Time; public int ActionId; public string Msg = ""; public int Pid; public string Name = ""; }

/// <summary>核心引擎：对应 ProcessGovernor.exe 的进程管理循环（默认 1s 轮询 + CPU 增量统计），
/// 实现：默认规则（优先级/IO/内存优先级/亲和性/CPU 集/能效模式）、ProBalance、前台提升、SmartTrim、
/// 实例限制、黑名单、CPU 限额（占空比）、看门狗、保持运行、电源自动化。</summary>
public class GovernorEngine : IDisposable
{
    readonly LassoConfig _cfg;
    readonly ActionLog _log;
    readonly GpuMonitor _gpuMonitor = new();
    readonly object _lifecycleLock = new();
    CancellationTokenSource _cts;
    Task _loop, _throttleLoop;
    readonly HashSet<Task> _trimTasks = new();
    readonly object _trimTaskLock = new();
    int _paused;
    int _disposed;
    long _snapshotSequence;
    EngineSnapshot _snapshot;

    // 快照状态（只在主采样循环中更新；发布时整体替换引用）
    Dictionary<ProcessIdentity, Process> _procs = new();
    Dictionary<ProcessIdentity, long> _lastCpuMs = new();
    Dictionary<ProcessIdentity, DateTime> _lastWall = new();
    public volatile List<ProcInfo> Current = new();
    public double TotalCpu; public double GpuLoad; public bool GpuAvailable; public double RamLoad; public double TotalRamMB, AvailRamMB;
    public int ForegroundPid; public string ForegroundName = "";
    public string GpuEngine = ""; public string GpuStatus = "初始化中"; public string PowerPlan = "";
    public bool IsAdministrator { get; } = Native.IsAdministrator();
    public bool ManageOnlyCurrentUser => _cfg.ManageOnlyCurrentUser;
    readonly Queue<EngineEvent> _events = new(); readonly object _evLock = new();
    readonly Dictionary<ProcessIdentity, (string Path, string Desc)> _processMetadata = new();
    readonly Dictionary<ProcessIdentity, (bool? Match, DateTime RetryAtUtc)> _currentUserMatches = new();

    // 规则使用不可变快照，热重载期间不会让采样循环看到半更新状态。
    sealed class RuleSnapshot
    {
        public long Version;
        public IReadOnlyList<(string p, ProcessPriorityClass v)> Priority = Array.Empty<(string, ProcessPriorityClass)>();
        public IReadOnlyList<(string p, int v)> Io = Array.Empty<(string, int)>();
        public IReadOnlyList<(string p, int v)> Mem = Array.Empty<(string, int)>();
        public IReadOnlyList<(string p, long v)> Aff = Array.Empty<(string, long)>();
        public IReadOnlyList<(string p, string v)> Sets = Array.Empty<(string, string)>();
        public IReadOnlyList<string> Eco = Array.Empty<string>();
        public IReadOnlyList<(string p, int v)> Instance = Array.Empty<(string, int)>();
        public IReadOnlyList<(string p, int v)> Limit = Array.Empty<(string, int)>();
        public IReadOnlyList<string> Disallow = Array.Empty<string>();
        public IReadOnlyList<string> Allow = Array.Empty<string>();
        public IReadOnlyList<string> Keep = Array.Empty<string>();
        public IReadOnlyList<WatchRule> Watch = Array.Empty<WatchRule>();
        public IReadOnlyList<string> PbExclusions = Array.Empty<string>();
        public IReadOnlyList<string> TrimExclusions = Array.Empty<string>();
    }

    RuleSnapshot _rules = new();
    long _rulesVersion;
    Dictionary<ProcessIdentity, long> _ruleApplied = new();
    readonly List<(string pat, string name, string path)> _keepLaunch = new();

    // ProBalance 状态（采样线程与暂停/停止路径共同访问）
    DateTime? _overQuotaSince;
    readonly Dictionary<ProcessIdentity, (ProcessPriorityClass pri, DateTime since, string name)> _restrained = new();
    readonly object _pbLock = new();
    bool _pbActive;
    public bool ProBalanceActive { get { lock (_pbLock) return _pbActive; } }
    public string ProBalanceInfo = "";

    // 前台提升
    ProcessIdentity? _boosted;
    ProcessPriorityClass _boostOrig;
    readonly object _boostLock = new();

    // CPU 限额：采样线程、50ms 控制循环和 UI 热重载共同访问。
    readonly object _throttleLock = new();
    readonly Dictionary<ProcessIdentity, ThrottleState> _throttled = new();
    sealed class ThrottleState
    {
        public int ActiveMs;
        public DateTime CycleStart;
        public bool Suspended;
        public string Name = "";
    }

    // SmartTrim / 电源
    readonly SemaphoreSlim _trimGate = new(1, 1);
    DateTime _nextTrim = DateTime.MinValue;
    readonly List<double> _cpuSamples = new();
    DateTime? _idlePlanSince;
    DateTime _nextPlanQuery = DateTime.MinValue;
    string _lastPlan = "";

    string _currentUserSid;

    public GovernorEngine(LassoConfig cfg, ActionLog log)
    {
        _cfg = cfg;
        _log = log;
        try { _currentUserSid = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { _currentUserSid = ""; }
        ReloadRules();
        // 预热系统计数器，避免第一帧把自开机以来的累计值当成瞬时值。
        Native.QueryTotalCpuPercent(ref _lastIdle, ref _lastKn, ref _lastUs);
        _gpuMonitor.TrySample(out _);
        PublishSnapshot(DateTime.UtcNow);
    }

    public bool EngineRunning { get; private set; }
    public bool Paused => Volatile.Read(ref _paused) != 0;
    public EngineSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public LassoConfig Config => _cfg;
    public ActionLog Log => _log;

    public void SetPaused(bool paused)
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            int next = paused ? 1 : 0;
            if (Interlocked.Exchange(ref _paused, next) == next) return;
            if (paused)
            {
                RestoreAllThrottles();
                RestoreAllRestraints();
                RestoreFgBoost();
                Emit(ActionLog.ACT_START, "引擎已暂停，所有临时调整已恢复", 0, "engine");
            }
            else
            {
                // 强制在下一个采样周期重新应用默认规则。
                Interlocked.Increment(ref _rulesVersion);
                var old = _rules;
                _rules = CloneRulesWithVersion(old, _rulesVersion);
                Emit(ActionLog.ACT_START, "引擎已恢复", 0, "engine");
            }
            PublishSnapshot(DateTime.UtcNow);
        }
    }

    public void ReloadRules()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var rules = new RuleSnapshot
        {
            Version = Interlocked.Increment(ref _rulesVersion),
            Priority = IniFile.ParsePairs(_cfg.DefaultPriorities).Select(x => (x.name, LassoConfig.ParsePriority(x.val))).ToList(),
            Io = IniFile.ParsePairs(_cfg.DefaultIoPriorities).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 2)).ToList(),
            Mem = IniFile.ParsePairs(_cfg.DefaultMemPriorities).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 5)).ToList(),
            Aff = IniFile.ParsePairs(_cfg.DefaultAffinities).Select(x => (x.name, ParseMask(x.val))).Where(x => x.Item2 != 0).ToList(),
            Sets = IniFile.ParsePairs(_cfg.DefaultCpuSets).ToList(),
            Eco = IniFile.SplitList(_cfg.EfficiencyModes),
            Instance = IniFile.ParsePairs(_cfg.InstanceLimits).Select(x => (x.name, int.TryParse(x.val, out var v) ? v : 1)).ToList(),
            Limit = IniFile.ParsePairs(_cfg.CpuLimits).Select(x => (x.name, ParsePercent(x.val))).Where(x => x.Item2 is > 0 and < 100).ToList(),
            Disallow = IniFile.SplitList(_cfg.Disallowed),
            Allow = IniFile.SplitList(_cfg.Allowed),
            Keep = IniFile.SplitList(_cfg.KeepRunning),
            Watch = IniFile.ParsePairs(_cfg.WatchdogRules)
                .Select(x => WatchRule.Parse(x.name + ";" + x.val)).Where(r => r != null).ToList(),
            PbExclusions = _cfg.ProBalanceExclusions.ToList(),
            TrimExclusions = _cfg.SmartTrimExclusions.ToList(),
        };
        Volatile.Write(ref _rules, rules);
        ReconcileThrottles(rules);
    }

    static RuleSnapshot CloneRulesWithVersion(RuleSnapshot source, long version) => new()
    {
        Version = version,
        Priority = source.Priority, Io = source.Io, Mem = source.Mem, Aff = source.Aff, Sets = source.Sets,
        Eco = source.Eco, Instance = source.Instance, Limit = source.Limit, Disallow = source.Disallow,
        Allow = source.Allow, Keep = source.Keep, Watch = source.Watch, PbExclusions = source.PbExclusions,
        TrimExclusions = source.TrimExclusions,
    };

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
        lock (_lifecycleLock)
        {
            if (EngineRunning || Volatile.Read(ref _disposed) != 0) return;
            _cts?.Dispose();
            EngineRunning = true;
            _cts = new CancellationTokenSource();
            _log.Write(0, "opengovernor", ActionLog.ACT_START, $"OpenLasso v{Ver()} 开源进程优化器 — 进程管理已开始");
            _log.Write(0, "opengovernor", ActionLog.ACT_CONFIG_LOADED, _cfg.ImportedFrom != null
                ? $"配置已从 Process Lasso 导入（{ImportedNote()}）并落盘：{_cfg.ConfigPath}" : "配置来自：" + _cfg.ConfigPath);
            _log.Write(0, "opengovernor", ActionLog.ACT_SCOPE,
                (_cfg.ManageOnlyCurrentUser ? "管理进程范围：仅当前用户" : "管理进程范围：所有用户（受保护进程需管理员权限）") +
                (IsAdministrator ? "；当前为管理员权限" : "；当前为标准权限"));
            _loop = Task.Run(() => Loop(_cts.Token));
            _throttleLoop = Task.Run(() => ThrottleLoop(_cts.Token));
        }
    }

    string ImportedNote() => _cfg.ImportedFrom;
    static string Ver() => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public void Stop()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lifecycleLock)
        {
            StopCore();
        }
    }

    void StopCore()
    {
        bool wasRunning = EngineRunning;
        EngineRunning = false;
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        if (wasRunning)
        {
            try { _loop?.Wait(); } catch { }
            try { _throttleLoop?.Wait(); } catch { }
        }
        RestoreAllThrottles();
        RestoreAllRestraints();
        RestoreFgBoost();
        Interlocked.Exchange(ref _paused, 0);
        WaitForTrimTasks();
        try { cts?.Dispose(); } catch { }
        PublishSnapshot(DateTime.UtcNow);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            StopCore();
            RestoreAllThrottles();
            _gpuMonitor.Dispose();
            _cts?.Dispose();
            _trimGate.Dispose();
            Volatile.Write(ref _disposed, 1);
        }
    }

    // ---------------- 主循环 ----------------
    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(ct); }
            catch (Exception ex) { Emit(ActionLog.ACT_START, "引擎异常(已忽略): " + ex.Message, 0, "engine"); }
            try { await Task.Delay(Math.Max(250, _cfg.UpdateSpeedMs), ct); } catch (TaskCanceledException) { break; }
        }
    }

    void Tick(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rules = Volatile.Read(ref _rules);
        var fresh = new Dictionary<ProcessIdentity, Process>();
        var list = new List<ProcInfo>();
        int cores = Math.Max(1, Environment.ProcessorCount);
        int ownPid = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try { if (p.HasExited) { p.Dispose(); continue; } } catch { }
            ProcessIdentity identity;
            try { identity = IdentityOf(p); }
            catch { p.Dispose(); continue; }
            fresh[identity] = p;

            double cpu = 0;
            try
            {
                long ms = (long)p.TotalProcessorTime.TotalMilliseconds;
                if (_lastCpuMs.TryGetValue(identity, out long oldMs) && _lastWall.TryGetValue(identity, out DateTime oldWall))
                {
                    double wall = (now - oldWall).TotalMilliseconds;
                    if (wall > 0) cpu = Math.Clamp(100.0 * (ms - oldMs) / (wall * cores), 0, 100);
                }
                _lastCpuMs[identity] = ms;
                _lastWall[identity] = now;
            }
            catch { }

            if (!_processMetadata.TryGetValue(identity, out var metadata))
            {
                try
                {
                    var module = p.MainModule;
                    metadata = (module?.FileName ?? "", module?.FileVersionInfo?.FileDescription ?? "");
                }
                catch { metadata = ("", ""); }
                _processMetadata[identity] = metadata;
            }
            string path = metadata.Path, desc = metadata.Desc;

            bool currentUser = true;
            bool accessLimited = false;
            string scopeNote = "";
            if (_cfg.ManageOnlyCurrentUser)
            {
                var match = TryProcessMatchesCurrentUser(p.Id, identity, now);
                currentUser = match == true;
                accessLimited = match == null;
                if (match == false) scopeNote = "非当前用户";
                else if (match == null) scopeNote = "权限受限";
            }

            try
            {
                DateTime start = identity.StartTimeUtcTicks > 0
                    ? new DateTime(identity.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime()
                    : now.ToLocalTime();
                var pi = new ProcInfo
                {
                    Pid = p.Id,
                    StartTimeUtcTicks = identity.StartTimeUtcTicks,
                    Name = SafeProcessName(p),
                    Path = path,
                    Desc = desc,
                    SessionId = TryGet(() => p.SessionId, 0),
                    CpuPercent = Math.Round(cpu, 1),
                    Priority = TryGet(() => p.PriorityClass, ProcessPriorityClass.Normal),
                    AffinityMask = TryGet(() => (long)p.ProcessorAffinity, (1L << Math.Min(cores, 62)) - 1),
                    WorkingSetMB = Math.Round(TryGet(() => p.WorkingSet64, 0L) / 1048576.0, 1),
                    StartTime = start,
                    IsCurrent = p.Id == ownPid,
                    CanManage = currentUser,
                    AccessLimited = accessLimited,
                    ProBalanced = IsRestrained(identity),
                    Throttled = IsThrottled(identity),
                };
                ApplyNotes(pi, rules);
                if (scopeNote.Length > 0) pi.Note = (pi.Note + " " + scopeNote).Trim();
                list.Add(pi);
            }
            catch { p.Dispose(); fresh.Remove(identity); }
        }

        foreach (var id in _lastCpuMs.Keys.Where(k => !fresh.ContainsKey(k)).ToList())
        { _lastCpuMs.Remove(id); _lastWall.Remove(id); }
        foreach (var id in _processMetadata.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _processMetadata.Remove(id);
        foreach (var id in _ruleApplied.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _ruleApplied.Remove(id);
        foreach (var id in _currentUserMatches.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _currentUserMatches.Remove(id);
        lock (_pbLock)
            foreach (var id in _restrained.Keys.Where(k => !fresh.ContainsKey(k)).ToList()) _restrained.Remove(id);
        RemoveGoneThrottles(fresh.Keys);

        var old = _procs;
        _procs = fresh;
        Current = list.OrderByDescending(x => x.CpuPercent).ThenBy(x => x.Pid).ToList();
        foreach (var op in old.Values) try { op.Dispose(); } catch { }

        TotalCpu = Math.Round(Native.QueryTotalCpuPercent(ref _lastIdle, ref _lastKn, ref _lastUs), 1);
        QueryMemory();
        if (_gpuMonitor.TrySample(out double gpuLoad))
        {
            GpuLoad = Math.Round(gpuLoad, 1);
            GpuAvailable = true;
            GpuEngine = _gpuMonitor.EngineName;
            GpuStatus = _gpuMonitor.StatusText;
        }
        else
        {
            GpuAvailable = false;
            GpuEngine = "";
            GpuStatus = _gpuMonitor.StatusText;
        }
        ForegroundPid = Native.ForegroundPid();
        ForegroundName = Current.FirstOrDefault(x => x.Pid == ForegroundPid)?.Name ?? "";
        if (now >= _nextPlanQuery)
        {
            PowerPlan = Native.ActivePlanName() ?? "";
            _nextPlanQuery = now.AddSeconds(5);
        }

        if (!Paused)
        {
            foreach (var pi in Current)
            {
                if (!pi.CanManage) continue;
                if (!_ruleApplied.TryGetValue(pi.Identity, out long appliedVersion) || appliedVersion != rules.Version)
                {
                    ApplyDefaults(pi, rules);
                    _ruleApplied[pi.Identity] = rules.Version;
                }
            }
            CheckDisallowed(rules);
            CheckInstanceLimits(rules);
            CheckKeepRunning(rules);
            CheckWatchdogs(rules);
            ProBalanceTick(rules);
            ForegroundBoostTick();
            PowerTick();
            if (DateTime.UtcNow >= _nextTrim)
            {
                _nextTrim = DateTime.UtcNow.AddMinutes(Math.Max(1, _cfg.SmartTrimIntervalMin));
                QueueSmartTrim(rules, cancellationToken);
            }
        }

        PublishSnapshot(now);
    }

    void PublishSnapshot(DateTime now)
    {
        bool pbActive;
        string pbInfo;
        foreach (var pi in Current)
        {
            pi.ProBalanced = IsRestrained(pi.Identity);
            pi.Throttled = IsThrottled(pi.Identity);
        }
        lock (_pbLock)
        {
            pbActive = _pbActive;
            pbInfo = ProBalanceInfo;
        }
        var snap = new EngineSnapshot(
            Interlocked.Increment(ref _snapshotSequence), now, Current.ToArray(),
            TotalCpu, GpuAvailable ? GpuLoad : null, GpuEngine, GpuStatus,
            RamLoad, TotalRamMB, AvailRamMB, ForegroundPid, ForegroundName,
            pbActive, pbInfo, Paused, PowerPlan, IsAdministrator, _cfg.ManageOnlyCurrentUser);
        Volatile.Write(ref _snapshot, snap);
    }

    static string SafeProcessName(Process p)
    {
        try { return p.ProcessName.ToLowerInvariant() + ".exe"; }
        catch { return "pid-" + p.Id; }
    }

    static ProcessIdentity IdentityOf(Process p)
    {
        long ticks = 0;
        try { ticks = p.StartTime.ToUniversalTime().Ticks; } catch { }
        return new ProcessIdentity(p.Id, ticks);
    }

    static bool MatchesIdentity(Process p, ProcessIdentity identity)
    {
        if (p == null || identity.StartTimeUtcTicks <= 0 || p.Id != identity.Pid) return false;
        try { return p.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks; }
        catch { return false; }
    }

    bool TryResolveIdentity(int pid, out ProcessIdentity identity)
    {
        var snapshot = Snapshot;
        var pi = snapshot?.Processes.FirstOrDefault(x => x.Pid == pid);
        if (pi != null && pi.CanManage && pi.StartTimeUtcTicks > 0)
        {
            identity = pi.Identity;
            return true;
        }
        identity = default;
        return false;
    }

    bool WithIdentityHandle(ProcessIdentity identity, Func<IntPtr, bool> action)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Pid);
            if (!MatchesIdentity(p, identity)) return false;
            return action(p.Handle);
        }
        catch { return false; }
    }

    bool WithIdentityHandle(ProcessIdentity identity, Action<IntPtr> action)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Pid);
            if (!MatchesIdentity(p, identity)) return false;
            action(p.Handle);
            return true;
        }
        catch { return false; }
    }

    bool WithIdentityProcess(ProcessIdentity identity, Action<Process> action)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Pid);
            if (!MatchesIdentity(p, identity)) return false;
            action(p);
            return true;
        }
        catch { return false; }
    }

    T WithIdentityResult<T>(ProcessIdentity identity, Func<Process, T> action, T fallback)
    {
        try
        {
            using var p = Process.GetProcessById(identity.Pid);
            if (!MatchesIdentity(p, identity)) return fallback;
            return action(p);
        }
        catch { return fallback; }
    }

    bool IsRestrained(ProcessIdentity id) { lock (_pbLock) return _restrained.ContainsKey(id); }
    bool IsThrottled(ProcessIdentity id) { lock (_throttleLock) return _throttled.ContainsKey(id); }

    void ReconcileThrottles(RuleSnapshot rules)
    {
        var list = Current;
        lock (_throttleLock)
        {
            foreach (var id in _throttled.Keys.ToList())
            {
                ProcInfo pi = list.FirstOrDefault(x => x.Identity == id);
                int pct = 0;
                if (pi != null)
                    foreach (var (pat, value) in rules.Limit)
                        if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path)) { pct = value; break; }

                if (pct is > 0 and < 100)
                {
                    _throttled[id].ActiveMs = pct * 10;
                    continue;
                }

                if (_throttled[id].Suspended) WithIdentityHandle(id, h => Native.NtResumeProcess(h));
                _throttled.Remove(id);
            }
        }
    }

    void RemoveGoneThrottles(IEnumerable<ProcessIdentity> alive)
    {
        var live = alive as HashSet<ProcessIdentity> ?? alive.ToHashSet();
        lock (_throttleLock)
        {
            foreach (var id in _throttled.Keys.Where(id => !live.Contains(id)).ToList())
            {
                if (_throttled[id].Suspended) WithIdentityHandle(id, h => Native.NtResumeProcess(h));
                _throttled.Remove(id);
            }
        }
    }

    void RestoreAllThrottles()
    {
        lock (_throttleLock)
        {
            foreach (var (id, state) in _throttled)
                if (state.Suspended) WithIdentityHandle(id, h => Native.NtResumeProcess(h));
            _throttled.Clear();
        }
    }

    bool? TryProcessMatchesCurrentUser(int pid, ProcessIdentity identity, DateTime now)
    {
        if (string.IsNullOrEmpty(_currentUserSid)) return null;
        if (_currentUserMatches.TryGetValue(identity, out var cached))
        {
            if (cached.Match.HasValue || now < cached.RetryAtUtc) return cached.Match;
        }
        string sid = Native.ProcessUserSid(pid);
        bool? match = sid == null ? null : sid.Equals(_currentUserSid, StringComparison.OrdinalIgnoreCase);
        _currentUserMatches[identity] = (match, match.HasValue ? DateTime.MinValue : now.AddSeconds(15));
        return match;
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

    void ApplyNotes(ProcInfo pi, RuleSnapshot rules)
    {
        if (rules.Priority.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "优先级 ";
        if (rules.Aff.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "亲和 ";
        if (rules.Limit.Any(r => LassoConfig.MatchPattern(r.p, pi.Name, pi.Path))) pi.Note += "限速 ";
        if (rules.Disallow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "黑名单 ";
        if (rules.Keep.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "保活 ";
        if (rules.Eco.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) pi.Note += "能效 ";
    }

    // ---------------- 默认规则 ----------------
    void ApplyDefaults(ProcInfo pi, RuleSnapshot rules)
    {
        if (pi.IsCurrent || !pi.CanManage) return;
        foreach (var (pat, pri) in rules.Priority)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path)) { SetPriorityInternal(pi.Identity, pri, "默认规则"); break; }
        foreach (var (pat, io) in rules.Io)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (WithIdentityHandle(pi.Identity, h => Native.SetIoPriority(h, Math.Clamp(io, 0, 3)))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_IO_PRIORITY, $"IO 优先级 → {IoName(io)}", pi.Path); break; }
        foreach (var (pat, mp) in rules.Mem)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (WithIdentityHandle(pi.Identity, h => Native.SetMemoryPriority(h, (uint)Math.Clamp(mp, 0, 7)))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_MEM_PRIORITY, $"内存优先级 → {mp}", pi.Path); break; }
        foreach (var (pat, mask) in rules.Aff)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path)) { SetAffinityInternal(pi.Identity, mask, "默认规则"); break; }
        foreach (var (pat, sets) in rules.Sets)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            {
                var cs = ParseCoreList(sets);
                if (cs.Count > 0 && WithIdentityHandle(pi.Identity, h => Native.SetCpuSets(h, cs))) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_CPUSETS, $"CPU 集 → {sets}", pi.Path);
                break;
            }
        foreach (var pat in rules.Eco)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            { if (EcoInternal(pi.Identity, true)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_ECO, "能效模式已启用", pi.Path); break; }
        foreach (var (pat, pct) in rules.Limit)
            if (LassoConfig.MatchPattern(pat, pi.Name, pi.Path))
            {
                lock (_throttleLock)
                {
                    if (!_throttled.TryGetValue(pi.Identity, out var state))
                    { state = new ThrottleState { CycleStart = DateTime.UtcNow }; _throttled[pi.Identity] = state; }
                    state.ActiveMs = pct * 10;
                    state.Name = pi.Name;
                }
                _log.Write(pi.Pid, pi.Name, ActionLog.ACT_THROTTLE, $"CPU 限额 → {pct}%", pi.Path);
                Emit(ActionLog.ACT_THROTTLE, $"{pi.Name} 限额 {pct}%", pi.Pid, pi.Name);
                break;
            }
        foreach (var pat in rules.Keep)
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
    void CheckDisallowed(RuleSnapshot rules)
    {
        if (rules.Disallow.Count == 0) return;
        foreach (var pi in Current)
        {
            if (pi.IsCurrent || !pi.CanManage) continue;
            if (rules.Allow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) continue;
            if (rules.Disallow.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path)))
                KillInternal(pi.Identity, ActionLog.ACT_DISALLOWED, "命中黑名单规则");
        }
    }

    void CheckInstanceLimits(RuleSnapshot rules)
    {
        foreach (var (pat, max) in rules.Instance)
        {
            var hits = Current.Where(x => x.CanManage && LassoConfig.MatchPattern(pat, x.Name, x.Path))
                .OrderBy(x => x.StartTime).ToList();
            while (hits.Count > max)
            {
                var victim = hits[^1];
                hits.RemoveAt(hits.Count - 1);
                KillInternal(victim.Identity, ActionLog.ACT_INSTANCE_KILL, $"实例数超过限制 {max}");
            }
        }
    }

    void CheckKeepRunning(RuleSnapshot rules)
    {
        if (rules.Keep.Count == 0) return;
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

    sealed class WatchRule
    {
        public string Pattern = "";
        public double CpuValue = -1, MemValueMb = -1, TimeValueSec = -1;
        public string CpuOperator = ">", MemOperator = ">", TimeOperator = ">";
        public bool UseAll;
        public string Action = "kill", ActionArg = "";
        public string TriggerText
        {
            get
            {
                var parts = new List<string>();
                if (CpuValue >= 0) parts.Add($"CPU{CpuOperator}{CpuValue:0.#}%");
                if (MemValueMb >= 0) parts.Add($"内存{MemOperator}{MemValueMb:0.#}MB");
                if (TimeValueSec >= 0) parts.Add($"运行{TimeOperator}{TimeValueSec:0.#}s");
                return string.Join((UseAll ? " 且 " : " 或 "), parts);
            }
        }

        public static WatchRule Parse(string s) => TryParse(s, out var rule, out _) ? rule : null;

        public static bool TryParse(string s, out WatchRule rule, out string error)
        {
            rule = null;
            error = "";
            if (string.IsNullOrWhiteSpace(s)) { error = "规则为空"; return false; }
            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) { error = "需要进程模式和至少一个条件"; return false; }
            var r = new WatchRule { Pattern = parts[0].Trim() };
            bool hasCondition = false, hasAction = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string seg in parts.Skip(1))
            {
                int opIndex = seg.IndexOfAny(new[] { '>', '<', '=' });
                if (opIndex <= 0) { error = $"无法解析条件“{seg}”"; return false; }
                string key = seg[..opIndex].Trim().ToLowerInvariant();
                int opLength = opIndex + 1 < seg.Length && seg[opIndex + 1] == '=' ? 2 : 1;
                string op = seg.Substring(opIndex, opLength);
                string value = seg[(opIndex + op.Length)..].Trim();
                if (!seen.Add(key)) { error = $"条件“{key}”重复"; return false; }
                if (key == "mode")
                {
                    if (op != "=") { error = "mode 必须使用等号"; return false; }
                    if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) r.UseAll = true;
                    else if (value.Equals("any", StringComparison.OrdinalIgnoreCase)) r.UseAll = false;
                    else { error = "mode 只能是 any 或 all"; return false; }
                    continue;
                }
                if (key == "action")
                {
                    if (op != "=") { error = "action 必须使用等号"; return false; }
                    var ax = value.Split(':', 2, StringSplitOptions.TrimEntries);
                    string action = ax[0].ToLowerInvariant();
                    if (action is not ("kill" or "trim" or "priority")) { error = "action 只能是 kill、trim 或 priority:<级别>"; return false; }
                    if (action == "priority")
                    {
                        if (ax.Length < 2 || !LassoConfig.TryParsePriority(ax[1], out _))
                        { error = "priority 动作缺少有效级别"; return false; }
                        r.ActionArg = ax[1];
                    }
                    r.Action = action;
                    hasAction = true;
                    continue;
                }
                if (key is not ("cpu" or "mem" or "time")) { error = $"未知条件“{key}”"; return false; }
                if (op is not (">" or ">=" or "<" or "<=")) { error = $"条件“{key}”使用了不支持的比较符"; return false; }
                string number = value.TrimEnd('%', 'M', 'B', 's').Trim();
                if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed) || parsed <= 0)
                { error = $"条件“{key}”需要正数"; return false; }
                if (key == "cpu") { r.CpuValue = parsed; r.CpuOperator = op; }
                else if (key == "mem") { r.MemValueMb = parsed; r.MemOperator = op; }
                else { r.TimeValueSec = parsed; r.TimeOperator = op; }
                hasCondition = true;
            }
            if (!hasCondition) { error = "未找到 CPU、内存或运行时间条件"; return false; }
            if (!hasAction) { error = "缺少 action"; return false; }
            rule = r;
            return true;
        }

        public bool Matches(ProcInfo pi)
        {
            var conditions = new List<bool>(3);
            if (CpuValue >= 0) conditions.Add(Compare(pi.CpuPercent, CpuValue, CpuOperator));
            if (MemValueMb >= 0) conditions.Add(Compare(pi.WorkingSetMB, MemValueMb, MemOperator));
            if (TimeValueSec >= 0) conditions.Add(Compare((DateTime.Now - pi.StartTime).TotalSeconds, TimeValueSec, TimeOperator));
            return UseAll ? conditions.All(x => x) : conditions.Any(x => x);
        }

        static bool Compare(double actual, double threshold, string op) => op switch
        {
            ">" => actual > threshold,
            ">=" => actual >= threshold,
            "<" => actual < threshold,
            "<=" => actual <= threshold,
            _ => false,
        };
    }

    public static bool ValidateWatchRule(string value, out string error)
        => WatchRule.TryParse(value, out _, out error);

    internal static bool MatchesWatchRule(string value, ProcInfo process) =>
        WatchRule.TryParse(value, out var rule, out _) && rule.Matches(process);

    void CheckWatchdogs(RuleSnapshot rules)
    {
        foreach (var r in rules.Watch)
            foreach (var pi in Current.Where(x => x.CanManage && LassoConfig.MatchPattern(r.Pattern, x.Name, x.Path)))
            {
                if (!r.Matches(pi)) continue;
                string why = $"看门狗触发（{r.TriggerText}）";
                switch (r.Action)
                {
                    case "kill": KillInternal(pi.Identity, ActionLog.ACT_WATCHDOG, why); break;
                    case "trim":
                        if (TrimInternal(pi.Identity)) _log.Write(pi.Pid, pi.Name, ActionLog.ACT_WATCHDOG, why + "：工作集整理", pi.Path);
                        break;
                    case "priority":
                        SetPriorityInternal(pi.Identity, LassoConfig.ParsePriority(r.ActionArg), "看门狗");
                        _log.Write(pi.Pid, pi.Name, ActionLog.ACT_WATCHDOG, why + $"：优先级 → {r.ActionArg}", pi.Path);
                        break;
                }
            }
    }

    // ---------------- ProBalance ----------------
    void ProBalanceTick(RuleSnapshot rules)
    {
        if (Paused) { RestoreAllRestraints(); return; }
        if (!_cfg.ProBalanceEnabled) { RestoreAllRestraints(); return; }
        if (_cfg.DisableWhenIdleMs > 0 && Native.IdleMilliseconds() > (uint)_cfg.DisableWhenIdleMs)
        { RestoreAllRestraints(); _overQuotaSince = null; return; }

        bool overQuota = TotalCpu >= _cfg.TotalCpuBeforeRestraint;
        if (overQuota)
        {
            _overQuotaSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _overQuotaSince.Value).TotalMilliseconds >= _cfg.TimeOverQuotaMs)
                foreach (var pi in Current)
                {
                    if (IsRestrained(pi.Identity)) continue;
                    if (pi.CpuPercent < _cfg.PerProcessCpuBeforeRestraint) continue;
                    if (!IsRestrainable(pi, rules)) continue;
                    Restrain(pi);
                }
        }
        else _overQuotaSince = null;

        List<(ProcessIdentity id, (ProcessPriorityClass pri, DateTime since, string name) state)> restrained;
        lock (_pbLock) restrained = _restrained.Select(x => (x.Key, x.Value)).ToList();
        foreach (var (id, st) in restrained)
        {
            var pi = Current.FirstOrDefault(x => x.Identity == id);
            bool restore = pi == null
                || (pi.CpuPercent <= _cfg.PerProcessCpuForRestore && (DateTime.UtcNow - st.since).TotalMilliseconds >= _cfg.MinRestraintMs)
                || (!overQuota && (DateTime.UtcNow - st.since).TotalMilliseconds >= _cfg.MinRestraintMs);
            if (restore) Unrestrain(id, pi);
        }

        bool nowActive;
        int restrainedCount;
        lock (_pbLock)
        {
            restrainedCount = _restrained.Count;
            nowActive = restrainedCount > 0;
            if (nowActive == _pbActive) return;
            _pbActive = nowActive;
            ProBalanceInfo = nowActive ? $"抑制 {restrainedCount} 个进程" : "";
            Emit(ActionLog.ACT_PB_BEGIN, nowActive ? ProBalanceInfo : "ProBalance：全部恢复", 0, "probalance");
        }
    }

    bool IsRestrainable(ProcInfo pi, RuleSnapshot rules)
    {
        if (pi.IsCurrent || !pi.CanManage) return false;
        if (_cfg.TameOnlyNormal && pi.Priority != ProcessPriorityClass.Normal) return false;
        if (_cfg.ExcludeServices && pi.SessionId == 0) return false;
        if (_cfg.ExcludeForeground && pi.Pid == ForegroundPid) return false;
        if (_cfg.ExcludeChildrenOfForeground && ForegroundPid != 0)
        {
            if (_procs.TryGetValue(pi.Identity, out var proc) && Native.ParentPidOf(proc) == ForegroundPid) return false;
        }
        if (rules.PbExclusions.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) return false;
        if (IsThrottled(pi.Identity)) return false;
        return true;
    }

    void Restrain(ProcInfo pi)
    {
        if (Paused) return;
        var target = _cfg.LowerToIdle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
        if (!SetPriorityInternal(pi.Identity, target, "ProBalance")) return;
        if (_cfg.LowerIoOnRestraint) WithIdentityHandle(pi.Identity, h => Native.SetIoPriority(h, 0));
        bool accepted = false;
        lock (_pbLock)
        {
            if (!Paused && !_restrained.ContainsKey(pi.Identity))
            {
                _restrained[pi.Identity] = (pi.Priority, DateTime.UtcNow, pi.Name);
                accepted = true;
            }
        }
        if (!accepted)
        {
            SetPriorityInternal(pi.Identity, pi.Priority, "ProBalance取消");
            if (_cfg.LowerIoOnRestraint) WithIdentityHandle(pi.Identity, h => Native.SetIoPriority(h, 2));
            return;
        }
        _log.Write(pi.Pid, pi.Name, ActionLog.ACT_PB_BEGIN, $"ProBalance 抑制：优先级 {pi.Priority}→{target}（CPU {pi.CpuPercent}%）", pi.Path);
        Emit(ActionLog.ACT_PB_BEGIN, $"{pi.Name} 被抑制（CPU {pi.CpuPercent}%）", pi.Pid, pi.Name);
    }

    void Unrestrain(ProcessIdentity id, ProcInfo pi)
    {
        (ProcessPriorityClass pri, DateTime since, string name) st;
        lock (_pbLock)
        {
            if (!_restrained.TryGetValue(id, out st)) return;
            _restrained.Remove(id);
        }
        if (pi != null)
        {
            SetPriorityInternal(id, st.pri, "ProBalance恢复");
            if (_cfg.LowerIoOnRestraint) WithIdentityHandle(id, h => Native.SetIoPriority(h, 2));
        }
        _log.Write(id.Pid, st.name, ActionLog.ACT_PB_END, "ProBalance 恢复原优先级 " + st.pri, pi?.Path ?? "");
    }

    void RestoreAllRestraints()
    {
        List<KeyValuePair<ProcessIdentity, (ProcessPriorityClass pri, DateTime since, string name)>> states;
        lock (_pbLock)
        {
            states = _restrained.ToList();
            _restrained.Clear();
            _pbActive = false;
            ProBalanceInfo = "";
        }
        foreach (var (id, st) in states)
        {
            var pi = Current.FirstOrDefault(x => x.Identity == id);
            if (pi == null) continue;
            SetPriorityInternal(id, st.pri, "ProBalance恢复");
            if (_cfg.LowerIoOnRestraint) WithIdentityHandle(id, h => Native.SetIoPriority(h, 2));
            _log.Write(id.Pid, st.name, ActionLog.ACT_PB_END, "ProBalance 恢复原优先级 " + st.pri, pi.Path);
        }
    }

    // ---------------- 前台提升 ----------------
    void ForegroundBoostTick()
    {
        if (!_cfg.FgBoostEnabled) { RestoreFgBoost(); return; }
        var fg = Current.FirstOrDefault(x => x.Pid == ForegroundPid);
        if (fg == null) { RestoreFgBoost(); return; }
        lock (_boostLock) if (_boosted == fg.Identity) return;
        RestoreFgBoost();
        if (Paused) return;
        if (fg.IsCurrent || !fg.CanManage) return;
        if (_cfg.FgBoostOnlyNormal && fg.Priority != ProcessPriorityClass.Normal) return;
        var target = LassoConfig.ParsePriority("0x" + _cfg.FgBoostPriority.ToString("X"));
        if (!SetPriorityInternal(fg.Identity, target, "前台提升")) return;
        bool accepted = false;
        lock (_boostLock)
        {
            if (!Paused)
            {
                _boosted = fg.Identity;
                _boostOrig = fg.Priority;
                accepted = true;
            }
        }
        if (!accepted) SetPriorityInternal(fg.Identity, fg.Priority, "前台提升取消");
    }

    void RestoreFgBoost()
    {
        ProcessIdentity id;
        ProcessPriorityClass original;
        lock (_boostLock)
        {
            if (_boosted is not ProcessIdentity boosted) return;
            id = boosted;
            original = _boostOrig;
            _boosted = null;
        }
        var pi = Current.FirstOrDefault(x => x.Identity == id);
        if (pi != null) SetPriorityInternal(id, original, "前台提升恢复");
    }

    // ---------------- SmartTrim ----------------
    public void RunSmartTrim()
    {
        Task[] tasks;
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !EngineRunning || _cts == null) return;
            lock (_trimTaskLock)
            {
                _trimTasks.RemoveWhere(x => x.IsCompleted);
                if (_trimTasks.Count == 0)
                    StartTrimTaskCore(waitForPrevious: true, Volatile.Read(ref _rules), _cts.Token);
                tasks = _trimTasks.ToArray();
            }
        }
        WaitForTrimTasks(tasks);
    }

    void QueueSmartTrim(RuleSnapshot rules, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        lock (_trimTaskLock)
        {
            _trimTasks.RemoveWhere(x => x.IsCompleted);
            if (_trimTasks.Count > 0) return;
            StartTrimTaskCore(waitForPrevious: false, rules, token);
        }
    }

    Task StartTrimTaskCore(bool waitForPrevious, RuleSnapshot rules, CancellationToken token)
    {
        var task = Task.Run(() => RunSmartTrimCore(waitForPrevious, rules, token));
        _trimTasks.Add(task);
        return task;
    }

    void WaitForTrimTasks()
    {
        Task[] tasks;
        lock (_trimTaskLock) tasks = _trimTasks.Where(x => !x.IsCompleted).ToArray();
        WaitForTrimTasks(tasks);
    }

    static void WaitForTrimTasks(IEnumerable<Task> tasks)
    {
        try { Task.WaitAll(tasks.ToArray()); }
        catch { }
    }

    void RunSmartTrimCore(bool waitForPrevious, RuleSnapshot rules, CancellationToken token)
    {
        if (token.IsCancellationRequested || !_cfg.SmartTrimEnabled) return;
        bool entered = false;
        if (waitForPrevious)
        {
            try { entered = _trimGate.Wait(0); if (!entered) { _trimGate.Wait(token); entered = true; } }
            catch (OperationCanceledException) { return; }
        }
        else if (!_trimGate.Wait(0))
        {
            _log.Write(0, "opengovernor", ActionLog.ACT_SKIP_STANDBY, "上一轮智能整理仍在运行，本轮跳过");
            return;
        }
        else entered = true;
        try
        {
            if (token.IsCancellationRequested) return;
            var snapshot = Snapshot;
            if (snapshot.RamLoad < _cfg.SmartTrimMinRamLoad)
            {
                _log.Write(0, "opengovernor", ActionLog.ACT_SKIP_STANDBY, $"内存负载 {snapshot.RamLoad:0}% 低于阈值 {_cfg.SmartTrimMinRamLoad}%，本轮跳过整理");
                return;
            }

            int trimmed = 0;
            double recovered = 0;
            var targets = snapshot.Processes
                .Where(x => !x.IsCurrent && x.CanManage && x.Pid != Environment.ProcessId)
                .OrderByDescending(x => x.WorkingSetMB)
                .ToList();
            foreach (var pi in targets)
            {
                if (token.IsCancellationRequested) break;
                if (pi.WorkingSetMB < _cfg.SmartTrimMinWssMb) break;
                if (rules.TrimExclusions.Any(x => LassoConfig.MatchPattern(x, pi.Name, pi.Path))) continue;
                double before = pi.WorkingSetMB;
                if (!TrimInternal(pi.Identity)) continue;
                double after = WithIdentityResult(pi.Identity, p => p.WorkingSet64 / 1048576.0, before);
                trimmed++;
                recovered += Math.Max(0, before - after);
                _log.Write(pi.Pid, pi.Name, ActionLog.ACT_TRIM, $"已恢复 {Math.Max(0, before - after):0} MB RAM。工作组原为 {before:0} MB - 现在为 {after:0} MB。", pi.Path);
            }
            if (token.IsCancellationRequested) return;
            _log.Write(0, "opengovernor", ActionLog.ACT_TRIM_DONE, $"已整理 {trimmed} 个进程，共有 {snapshot.Processes.Count} 个。");
            if (trimmed > 0) Emit(ActionLog.ACT_TRIM_DONE, $"智能整理 {trimmed} 个进程，释放约 {recovered:0} MB", 0, "smarttrim");
        }
        finally
        {
            if (entered) _trimGate.Release();
        }
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
                lock (_throttleLock)
                {
                    if (!Paused)
                    {
                        const int period = 1000;
                        var now = DateTime.UtcNow;
                        foreach (var (identity, state) in _throttled.ToList())
                        {
                            double elapsed = (now - state.CycleStart).TotalMilliseconds;
                            if (elapsed >= period)
                            {
                                if (state.Suspended) WithIdentityHandle(identity, h => Native.NtResumeProcess(h));
                                state.CycleStart = now;
                                state.Suspended = false;
                                continue;
                            }

                            bool shouldSuspend = elapsed >= state.ActiveMs;
                            if (shouldSuspend == state.Suspended) continue;
                            bool changed = shouldSuspend
                                ? WithIdentityHandle(identity, h => Native.NtSuspendProcess(h))
                                : WithIdentityHandle(identity, h => Native.NtResumeProcess(h));
                            if (changed) state.Suspended = shouldSuspend;
                        }
                    }
                }
            }
            catch { }
            try { await Task.Delay(50, ct); } catch (TaskCanceledException) { break; }
        }
        RestoreAllThrottles();
    }

    // ---------------- 手动操作（GUI 调用） ----------------
    public bool SetPriorityInternal(int pid, ProcessPriorityClass pri, string why)
    {
        if (!TryResolveIdentity(pid, out var identity)) return false;
        return SetPriorityInternal(identity, pri, why);
    }

    internal bool SetPriorityInternal(ProcessIdentity identity, ProcessPriorityClass pri, string why)
    {
        if (!WithIdentityProcess(identity, p => p.PriorityClass = pri)) return false;
        var pi = Current.FirstOrDefault(x => x.Identity == identity);
        _log.Write(identity.Pid, pi?.Name ?? identity.Pid.ToString(), ActionLog.ACT_PRIORITY, $"优先级 → {pri}（{why}）", pi?.Path ?? "");
        return true;
    }

    public bool SetAffinityInternal(int pid, long mask, string why)
    {
        if (!TryResolveIdentity(pid, out var identity)) return false;
        return SetAffinityInternal(identity, mask, why);
    }

    internal bool SetAffinityInternal(ProcessIdentity identity, long mask, string why)
    {
        if (!WithIdentityProcess(identity, p => p.ProcessorAffinity = (IntPtr)mask)) return false;
        var pi = Current.FirstOrDefault(x => x.Identity == identity);
        _log.Write(identity.Pid, pi?.Name ?? identity.Pid.ToString(), ActionLog.ACT_AFFINITY, $"亲和性掩码 → 0x{mask:X}（{why}）", pi?.Path ?? "");
        return true;
    }

    public bool KillInternal(int pid, int actId, string why)
    {
        if (!TryResolveIdentity(pid, out var identity)) return false;
        return KillInternal(identity, actId, why);
    }

    internal bool KillInternal(ProcessIdentity identity, int actId, string why)
    {
        if (!WithIdentityProcess(identity, p => { if (!p.HasExited) p.Kill(true); })) return false;
        var pi = Current.FirstOrDefault(x => x.Identity == identity);
        _log.Write(identity.Pid, pi?.Name ?? identity.Pid.ToString(), actId, why, pi?.Path ?? "");
        Emit(actId, $"已终止 {pi?.Name ?? identity.Pid.ToString()}（{why}）", identity.Pid, pi?.Name ?? "");
        return true;
    }

    public bool TrimInternal(int pid)
    {
        if (!TryResolveIdentity(pid, out var identity)) return false;
        return TrimInternal(identity);
    }

    internal bool TrimInternal(ProcessIdentity identity) => WithIdentityHandle(identity, h => Native.EmptyWorkingSet(h));

    public bool EcoInternal(int pid, bool on)
    {
        if (!TryResolveIdentity(pid, out var identity)) return false;
        return EcoInternal(identity, on);
    }

    internal bool EcoInternal(ProcessIdentity identity, bool on) => WithIdentityHandle(identity, h => Native.SetEcoMode(h, on));

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
