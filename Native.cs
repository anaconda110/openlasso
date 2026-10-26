using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace OpenLasso;

/// <summary>Windows 原生 API 封装（进程优先级/IO/内存优先级/CPU 集/能效模式/工作集/电源/输入空闲）</summary>
internal static class Native
{
    // ---------- 结构 ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct FT { public uint Lo, Hi; public static ulong Val(FT f) => ((ulong)f.Hi << 32) | f.Lo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

    // ---------- 系统级 ----------
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FT idle, out FT kernel, out FT user);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] internal static extern uint GetTickCount();

    /// <summary>总 CPU 占用百分比（0..100），基于 idle/kernel/user 时间增量</summary>
    public static double QueryTotalCpuPercent(ref ulong lastIdle, ref ulong lastKn, ref ulong lastUs)
    {
        if (!GetSystemTimes(out var idle, out var kn, out var us)) return 0;
        ulong i = FT.Val(idle), k = FT.Val(kn), u = FT.Val(us);
        ulong dk = k - lastKn, du = u - lastUs, di = i - lastIdle;
        lastIdle = i; lastKn = k; lastUs = u;
        ulong total = dk + du;
        if (total == 0) return 0;
        return Math.Clamp(100.0 * (total - di) / total, 0, 100);
    }

    /// <summary>距上次键鼠输入的毫秒数</summary>
    public static uint IdleMilliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? (GetTickCount() - lii.dwTime) : 0;
    }

    // ---------- 前台窗口 ----------
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        return GetWindowThreadProcessId(h, out var pid) != 0 ? (int)pid : 0;
    }

    // ---------- 进程信息与控制 ----------
    [DllImport("psapi.dll")] internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_USER { public SID_AND_ATTRIBUTES User; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length, out uint returned);

    /// <summary>读取进程令牌的用户 SID；权限不足或进程退出时返回 null。</summary>
    public static string ProcessUserSid(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;
        const int TokenUser = 1;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        IntPtr process = IntPtr.Zero, token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero || !OpenProcessToken(process, TOKEN_QUERY, out token)) return null;
            if (GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint size) ||
                Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || size == 0) return null;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, TokenUser, buffer, size, out _)) return null;
            var user = Marshal.PtrToStructure<TOKEN_USER>(buffer);
            return user.User.Sid == IntPtr.Zero ? null : new SecurityIdentifier(user.User.Sid).Value;
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    [DllImport("ntdll.dll")] internal static extern int NtSetInformationProcess(IntPtr h, int cls, ref int info, int len);
    internal const int ProcessIoPriority = 0x21; // IO_PRIORITY_HINT: 0=VeryLow 1=Low 2=Normal 3=High

    [DllImport("ntdll.dll")] internal static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] internal static extern int NtResumeProcess(IntPtr h);

    // SetProcessInformation 公开类: 0=MemoryPriority 1=PowerThrottling(能效模式)
    [DllImport("kernel32.dll")] internal static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE si, int size);
    [DllImport("kernel32.dll")] internal static extern bool SetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION si, int size);

    [DllImport("kernel32.dll")] internal static extern bool SetProcessDefaultCpuSets(IntPtr hProcess, uint[] cpuSets, int cpuSetCount);

    /// <summary>设置 IO 优先级 0..3</summary>
    public static bool SetIoPriority(IntPtr h, int io) { var v = io; return NtSetInformationProcess(h, ProcessIoPriority, ref v, 4) == 0; }
    /// <summary>设置内存优先级 0..7（5=默认）</summary>
    public static bool SetMemoryPriority(IntPtr h, uint pri)
    { var m = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = pri }; return SetProcessInformation(h, 0, ref m, Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>()); }
    /// <summary>能效模式（Eco）on/off，Win10 1709+ / Win11</summary>
    public static bool SetEcoMode(IntPtr h, bool eco)
    {
        var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = eco ? 1u : 0u };
        return SetProcessInformation(h, 1, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
    /// <summary>设置默认 CPU 集（按逻辑核索引，如 0,2,4）</summary>
    public static bool SetCpuSets(IntPtr h, IEnumerable<int> cores)
    { var arr = cores.Select(c => (uint)c).ToArray(); return SetProcessDefaultCpuSets(h, arr, arr.Length); }

    // ---------- 父进程（用于排除前台进程的子进程） ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct PBI { public IntPtr _1, _2, _3; public IntPtr UniquePid; public IntPtr _5; public IntPtr InheritedFromUniquePid; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PBI pbi, int len, out int retLen);
    public static int ParentPidOf(System.Diagnostics.Process p)
    {
        try
        {
            var pbi = new PBI();
            if (p != null && !p.HasExited && NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf<PBI>(), out _) == 0)
                return (int)pbi.InheritedFromUniquePid;
        }
        catch { }
        return 0;
    }

    // ---------- 电源计划 ----------
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, int flags, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid scheme, out Guid dest);
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);

    internal const uint ERROR_NO_MORE_ITEMS = 259;

    public static List<(Guid guid, string name)> EnumPowerSchemes()
    {
        var list = new List<(Guid, string)>();
        for (uint i = 0; ; i++)
        {
            var buf = new byte[16]; uint sz = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, i, buf, ref sz) != 0) break;
            var g = new Guid(buf);
            var nb = new byte[512]; uint nsz = 512;
            string name = g.ToString();
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, nb, ref nsz) == 0 && nsz > 2)
                name = Encoding.Unicode.GetString(nb, 0, (int)nsz).TrimEnd('\0');
            list.Add((g, name));
        }
        return list;
    }

    public static bool SetActiveScheme(Guid g) => PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out Guid scheme);

    /// <summary>当前活动电源计划名称（失败返回 null）</summary>
    public static string ActivePlanName()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var g) != 0) return null;
            var hit = EnumPowerSchemes().FirstOrDefault(s => s.guid == g);
            return hit.name;
        }
        catch { return null; }
    }

    /// <summary>复制“高性能”计划并重命名（不存在时创建等效计划）</summary>
    public static Guid? EnsurePerformanceScheme(string friendlyName)
    {
        var schemes = EnumPowerSchemes();
        var hit = schemes.FirstOrDefault(s => s.name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase));
        if (hit.name != null) return hit.guid;
        var hiPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"); // Windows 内置“高性能”
        if (PowerDuplicateScheme(IntPtr.Zero, ref hiPerf, out var ng) != 0) return null;
        var nb = Encoding.Unicode.GetBytes(friendlyName + "\0");
        PowerWriteFriendlyName(IntPtr.Zero, ref ng, IntPtr.Zero, IntPtr.Zero, nb, (uint)nb.Length);
        return ng;
    }

    // ---------- 控制台输出（WinExe 自检模式用） ----------
    [DllImport("kernel32.dll")] internal static extern bool AttachConsole(int pid);
}
