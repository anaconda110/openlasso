using System.Diagnostics;
using System.Text;

namespace OpenLasso;

/// <summary>无头自检：验证引擎核心功能（快照、默认规则、优先级/亲和性/整理/终止、日志）</summary>
static class SelfTest
{
    public static int Run()
    {
        Native.AttachConsole(-1);
        var outp = new StringBuilder();
        void P(string s) { Console.WriteLine(s); outp.AppendLine(s); }
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        { if (ok) pass++; else fail++; P($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}"); }

        P($"OpenLasso 自检  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (管理员: {IsAdmin()})");

        // 1. 配置与引擎
        var cfg = new LassoConfig(AppPaths.ConfigPath);
        var log = new ActionLog(AppPaths.LogDir);
        var eng = new GovernorEngine(cfg, log);
        eng.Config.DefaultPriorities = "pingsleep.exe,idle";       // 注入测试规则
        eng.Config.InstanceLimits = "";
        eng.ReloadRules();
        eng.Start();
        P($"配置: {cfg.ConfigPath}{(cfg.ImportedFrom != null ? "（已从 Process Lasso 导入: " + cfg.ImportedFrom + "）" : "")}");

        // 2. 起一个受管测试进程
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c title pingsleep & ping -n 30 127.0.0.1 > nul",
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized,
        };
        Process test;
        try { test = Process.Start(psi); }
        catch (Exception ex) { P("[FAIL] 无法启动测试进程: " + ex.Message); WriteResult(outp, ref fail); return 1; }
        int pid = test.Id;
        P($"测试进程 cmd.exe pid={pid}（窗口标题 pingsleep）");

        // 3. 等引擎看到它（≤6 个轮询周期）
        ProcInfo seen = null;
        for (int i = 0; i < 12 && seen == null; i++)
        { Thread.Sleep(500); seen = eng.Current.FirstOrDefault(x => x.Pid == pid); }
        Check("引擎快照捕获新进程", seen != null);
        if (seen != null) Check("进程名解析", seen.Name == "cmd.exe", seen.Name);

        // 4. 手动优先级 + 读回验证
        bool setOk = eng.SetPriorityInternal(pid, System.Diagnostics.ProcessPriorityClass.BelowNormal, "自检");
        var readBack = TryPri(pid);
        Check("设置优先级→BelowNormal", setOk && readBack == System.Diagnostics.ProcessPriorityClass.BelowNormal, $"读回={readBack}");

        // 5. 亲和性 + 读回
        bool affOk = eng.SetAffinityInternal(pid, 0x3, "自检");
        var aff = TryAff(pid);
        Check("设置亲和性→0x3", affOk && aff == 0x3, $"读回=0x{aff:X}");

        // 6. 工作集整理
        Check("工作集整理(EmptyWorkingSet)", eng.TrimInternal(pid));

        // 7. IO 优先级（尽力而为，受保护进程可能失败）
        var io = eng.EcoInternal(pid, false); // Eco 关闭应成功或无害
        P($"[*] 能效模式关闭调用返回: {io}");

        // 8. 终止
        Check("终止测试进程", eng.KillInternal(pid, ActionLog.ACT_KILL, "自检"));
        Thread.Sleep(400);
        Check("进程已退出", !Process.GetProcesses().Any(p => p.Id == pid));

        // 9. 日志
        eng.Stop();
        var tail = log.Tail(8);
        P("--- 日志尾部 ---");
        foreach (var l in tail) P(l);
        Check("日志已写入动作", tail.Any(l => l.Contains("自检") || l.Contains("(0x10a)")), $"{tail.Count} 行");

        P($"\n结果: {pass} 通过, {fail} 失败");
        WriteResult(outp, ref fail);
        return fail == 0 ? 0 : 1;
    }

    static void WriteResult(StringBuilder sb, ref int fail)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.BaseDir(), "selftest-result.txt"), sb.ToString(), Encoding.UTF8); } catch { }
        _ = fail;
    }

    static bool IsAdmin()
    {
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    static System.Diagnostics.ProcessPriorityClass TryPri(int pid)
    { try { using var p = Process.GetProcessById(pid); return p.PriorityClass; } catch { return 0; } }
    static long TryAff(int pid)
    { try { using var p = Process.GetProcessById(pid); return (long)p.ProcessorAffinity; } catch { return -1; } }
}
