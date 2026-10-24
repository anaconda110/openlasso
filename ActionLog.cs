using System.Text;

namespace OpenLasso;

/// <summary>动作日志：与 Process Lasso 相同列结构的 CSV + 轮转（.log … .log.9）。
/// ActionId 沿用 PL 实测值（0x3c/0x3d/0x52/0x53/0x5a…），OpenLasso 专有动作用 0x1xx 段。</summary>
public class ActionLog
{
    public const int ACT_START = 0x3c;            // 进程管理已开始
    public const int ACT_CONFIG_LOADED = 0x3d;    // 配置文件已加载
    public const int ACT_SCOPE = 0x3e;            // 管理范围
    public const int ACT_TRIM = 0x52;             // 工作集整理
    public const int ACT_TRIM_DONE = 0x53;        // 智能内存整理完成
    public const int ACT_SKIP_STANDBY = 0x5a;     // 跳过清除待机列表
    public const int ACT_PB_BEGIN = 0x100;        // ProBalance 抑制开始
    public const int ACT_PB_END = 0x101;          // ProBalance 恢复
    public const int ACT_PRIORITY = 0x102;        // 应用默认/手动优先级
    public const int ACT_AFFINITY = 0x103;        // 应用亲和性
    public const int ACT_IO_PRIORITY = 0x104;     // 应用 IO 优先级
    public const int ACT_MEM_PRIORITY = 0x105;    // 应用内存优先级
    public const int ACT_CPUSETS = 0x106;         // 应用 CPU 集
    public const int ACT_ECO = 0x107;             // 能效模式
    public const int ACT_INSTANCE_KILL = 0x108;   // 超出实例数限制终止
    public const int ACT_DISALLOWED = 0x109;      // 黑名单进程终止
    public const int ACT_KILL = 0x10a;            // 手动终止
    public const int ACT_RESTART = 0x10b;         // KeepRunning 重启
    public const int ACT_THROTTLE = 0x10c;        // CPU 限额应用
    public const int ACT_POWER = 0x10d;           // 电源计划切换
    public const int ACT_FG_BOOST = 0x10e;        // 前台提升
    public const int ACT_WATCHDOG = 0x10f;        // 看门狗动作
    public const int ACT_TRIM_ONE = 0x110;        // 手动整理单进程
    public const int ACT_SUSPENDED_SKIP = 0x111;  // 跳过已挂起进程

    readonly string _dir;
    readonly string _file;
    readonly object _lock = new();
    const long MaxBytes = 512 * 1024;

    public ActionLog(string dir)
    {
        _dir = dir; _file = Path.Combine(dir, "openlasso.log");
        Directory.CreateDirectory(dir);
    }

    public void Write(int pid, string name, int actionId, string info, string procPath = "", string cmdLine = "")
    {
        var now = DateTime.Now;
        var csv = string.Join(",",
            Q(now.ToFileTimeUtc().ToString()), Q(now.ToString("yyyy-MM-dd HH:mm:ss")),
            Q(Environment.MachineName), Q(CurrentUserName(pid)), Q(pid.ToString()), Q(name ?? ""),
            Q(actionId.ToString()), Q($"(0x{actionId:x2}) {ActionName(actionId)}"), Q(info ?? ""),
            Q(procPath ?? ""), Q(cmdLine ?? ""));
        lock (_lock)
        {
            try
            {
                RotateIfNeeded();
                if (!File.Exists(_file))
                    File.WriteAllText(_file, "\"UTC\",\"LocalTime\",\"Machine\",\"User\",\"PID\",\"Basename\",\"ActionId\",\"Action\",\"Info\",\"Path\",\"CommandLine\"\n", new UTF8Encoding(true));
                File.AppendAllText(_file, csv + "\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    static string CurrentUserName(int pid) => pid == 0 ? "SYSTEM" : Environment.UserName;

    void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_file) || new FileInfo(_file).Length < MaxBytes) return;
            for (int i = 9; i > 0; i--)
            {
                string from = i == 1 ? _file : _file + "." + (i - 1), to = _file + "." + i;
                if (File.Exists(from)) File.Copy(from, to, true);
            }
            File.Delete(_file);
        }
        catch { }
    }

    public static string ActionName(int id) => id switch
    {
        ACT_START => "进程管理已开始", ACT_CONFIG_LOADED => "配置文件已加载", ACT_SCOPE => "管理范围",
        ACT_TRIM => "整理", ACT_TRIM_DONE => "智能内存整理完成", ACT_SKIP_STANDBY => "跳过清除待机列表",
        ACT_PB_BEGIN => "ProBalance 抑制", ACT_PB_END => "ProBalance 恢复",
        ACT_PRIORITY => "优先级调整", ACT_AFFINITY => "亲和性调整", ACT_IO_PRIORITY => "IO 优先级调整",
        ACT_MEM_PRIORITY => "内存优先级调整", ACT_CPUSETS => "CPU 集调整", ACT_ECO => "能效模式",
        ACT_INSTANCE_KILL => "实例数限制", ACT_DISALLOWED => "禁止进程", ACT_KILL => "终止进程",
        ACT_RESTART => "保持运行重启", ACT_THROTTLE => "CPU 限额", ACT_POWER => "电源计划切换",
        ACT_FG_BOOST => "前台提升", ACT_WATCHDOG => "看门狗", ACT_TRIM_ONE => "手动整理",
        _ => "事件 0x" + id.ToString("x2"),
    };

    /// <summary>读取日志尾部 N 行（GUI 用）</summary>
    public List<string> Tail(int lines)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_file)) return new();
                var all = File.ReadAllLines(_file, Encoding.UTF8);
                return all.Skip(Math.Max(0, all.Length - lines)).ToList();
            }
            catch { return new(); }
        }
    }
}
