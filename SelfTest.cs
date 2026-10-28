using System.Diagnostics;
using System.Drawing;
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
        string tempRoot = Path.Combine(Path.GetTempPath(), "OpenLasso-SelfTest-" + Guid.NewGuid().ToString("N"));
        GovernorEngine eng = null;
        Process test = null;
        try
        {
            Directory.CreateDirectory(tempRoot);
            RunParsingChecks(Check);
            RunIconChecks(Check);
            RunIniChecks(Check, Path.Combine(tempRoot, "ini"));
            RunTelemetryChecks(P, Check, Path.Combine(tempRoot, "telemetry"));
            RunConfigChecks(P, Check, Path.Combine(tempRoot, "config"));

            // 1. 配置与引擎（全部使用临时目录，不读取真实 Process Lasso 配置）
            var cfg = new LassoConfig(Path.Combine(tempRoot, "engine", "openlasso.ini"), false);
            var log = new ActionLog(Path.Combine(tempRoot, "logs"));
            // 自检只允许操作显式指定的测试进程，关闭所有后台自动化。
            cfg.ProBalanceEnabled = false;
            cfg.FgBoostEnabled = false;
            cfg.SmartTrimEnabled = false;
            cfg.IdleSaverEnabled = false;
            cfg.LoadPowerEnabled = false;
            cfg.ManageOnlyCurrentUser = true;
            P($"[*] 配置规则解析: 优先级 {IniFile.ParsePairs(cfg.DefaultPriorities).Count} IO {IniFile.ParsePairs(cfg.DefaultIoPriorities).Count} 亲和 {IniFile.ParsePairs(cfg.DefaultAffinities).Count} 黑名单 {IniFile.SplitList(cfg.Disallowed).Count} ProBalance排除 {cfg.ProBalanceExclusions.Count} SmartTrim={(cfg.SmartTrimEnabled ? "开" : "关")} ProBalance={(cfg.ProBalanceEnabled ? "开" : "关")}");
            string testExeName = "olselftest-" + Guid.NewGuid().ToString("N")[..10] + ".exe";
            eng = new GovernorEngine(cfg, log);
            eng.Config.DefaultPriorities = testExeName + ",idle";       // 注入测试规则
            eng.Config.InstanceLimits = "";
            eng.ReloadRules();
            eng.Start();
            P($"配置: {cfg.ConfigPath}");

            // 2. 复制系统 ping 为唯一名称，确保规则只可能命中本次自检进程。
            string testExePath = Path.Combine(tempRoot, testExeName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), testExePath);
            var psi = new ProcessStartInfo
            {
                FileName = testExePath,
                Arguments = "-n 30 -w 1000 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try { test = Process.Start(psi); }
            catch (Exception ex) { Check("启动测试进程", false, ex.Message); }
            if (test != null)
            {
                int pid = test.Id;
                P($"测试进程 {testExeName} pid={pid}");

                // 3. 等引擎看到它（≤6 个轮询周期）
                ProcInfo seen = null;
                for (int i = 0; i < 12 && seen == null; i++)
                { Thread.Sleep(500); seen = eng.Current.FirstOrDefault(x => x.Pid == pid); }
                Check("引擎快照捕获新进程", seen != null);
                if (seen != null) Check("进程名解析", seen.Name == testExeName, seen.Name);

                long sequenceBefore = eng.Snapshot.Sequence;
                Check("引擎快照序号持续递增", WaitUntil(() => eng.Snapshot.Sequence > sequenceBefore, 3000),
                    $"起始={sequenceBefore}，当前={eng.Snapshot.Sequence}");
                var own = eng.Snapshot.Processes.FirstOrDefault(x => x.Pid == Environment.ProcessId);
                Check("当前用户范围：本进程可管理", own?.CanManage == true, own?.Note ?? "未找到");

                // 4. 手动优先级 + 读回验证
                bool setOk = eng.SetPriorityInternal(pid, System.Diagnostics.ProcessPriorityClass.BelowNormal, "自检");
                var readBack = TryPri(pid);
                Check("设置优先级→BelowNormal", setOk && readBack == System.Diagnostics.ProcessPriorityClass.BelowNormal, $"读回={readBack}");
                var wrongIdentity = new ProcessIdentity(pid, (seen?.StartTimeUtcTicks ?? 0) + 1);
                bool wrongIdentityRejected = !eng.SetPriorityInternal(wrongIdentity,
                    System.Diagnostics.ProcessPriorityClass.High, "自检错误身份");
                Check("启动时间不匹配的身份被拒绝",
                    wrongIdentityRejected && TryPri(pid) == System.Diagnostics.ProcessPriorityClass.BelowNormal,
                    $"读回={TryPri(pid)}");

                // 5. 亲和性 + 读回
                bool affOk = eng.SetAffinityInternal(pid, 0x3, "自检");
                var aff = TryAff(pid);
                Check("设置亲和性→0x3", affOk && aff == 0x3, $"读回=0x{aff:X}");

                // 6. 工作集整理
                Check("工作集整理(EmptyWorkingSet)", eng.TrimInternal(pid));

                // 7. IO 优先级（尽力而为，受保护进程可能失败）
                var io = eng.EcoInternal(pid, false); // Eco 关闭应成功或无害
                P($"[*] 能效模式关闭调用返回: {io}");

                // 7b. 并发手动整理不应死锁，也不得误碰测试进程
                cfg.SmartTrimEnabled = true;
                cfg.SmartTrimMinRamLoad = 101;
                bool trimCallsCompleted = Task.WaitAll(new[]
                {
                    Task.Run(() => eng.RunSmartTrim()),
                    Task.Run(() => eng.RunSmartTrim()),
                }, 5000);
                cfg.SmartTrimEnabled = false;
                Check("智能整理：并发调用安全完成", trimCallsCompleted && eng.EngineRunning,
                    trimCallsCompleted ? "两个调用均已返回" : "等待超时");

                // 8. 暂停后清除临时限速状态，快照立即反映
                eng.Config.CpuLimits = testExeName + ",99";
                eng.ReloadRules();
                bool throttled = WaitUntil(() => eng.Snapshot.Processes.Any(x => x.Pid == pid && x.Throttled), 4500);
                Check("注入限额规则后进程标记为限速", throttled, "最多等待 4.5 秒");
                long sequenceBeforePause = eng.Snapshot.Sequence;
                eng.SetPaused(true);
                var paused = eng.Snapshot;
                bool pausedProcessClear = paused.Processes.All(x => !x.Throttled && !x.ProBalanced);
                Check("暂停后临时限速状态清空",
                    paused.Paused && pausedProcessClear && paused.Sequence > sequenceBeforePause,
                    $"序号={paused.Sequence}");
                eng.SetPaused(false);

                // 9. 删除限速规则后，运行中的引擎恢复并移除状态
                bool throttledAgain = WaitUntil(() => eng.Snapshot.Processes.Any(x => x.Pid == pid && x.Throttled), 4500);
                eng.Config.CpuLimits = "";
                eng.ReloadRules();
                bool throttleRemoved = WaitUntil(() => eng.Snapshot.Processes.Any(x => x.Pid == pid) &&
                    eng.Snapshot.Processes.All(x => x.Pid != pid || !x.Throttled), 3000);
                Check("删除限额规则后恢复并清除限速状态", throttledAgain && throttleRemoved, "");

                // 10. 停止引擎必须恢复临时状态，快照同步退出暂停状态
                eng.Config.CpuLimits = testExeName + ",99";
                eng.ReloadRules();
                bool stopThrottleReady = WaitUntil(() => eng.Snapshot.Processes.Any(x => x.Pid == pid && x.Throttled), 4500);
                eng.Stop();
                var stopped = eng.Snapshot;
                Check("停止后恢复临时限速并清理暂停状态",
                    stopThrottleReady && !stopped.Paused && stopped.Processes.All(x => !x.Throttled && !x.ProBalanced) &&
                    Process.GetProcesses().Any(p => p.Id == pid), "");

                // 11. 终止
                Check("终止测试进程", eng.KillInternal(pid, ActionLog.ACT_KILL, "自检"));
                Thread.Sleep(400);
                Check("进程已退出", !Process.GetProcesses().Any(p => p.Id == pid));

                // 12. 日志
                var tail = log.Tail(8);
                P("--- 日志尾部 ---");
                foreach (var l in tail) P(l);
                Check("日志已写入动作", tail.Any(l => l.Contains("自检") || l.Contains("(0x10a)")), $"{tail.Count} 行");
            }
        }
        catch (Exception ex)
        {
            Check("自检未异常退出", false, ex.Message);
        }
        finally
        {
            try { eng?.Dispose(); } catch { }
            try { if (test is { HasExited: false }) test.Kill(); } catch { }
            test?.Dispose();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        P($"\n结果: {pass} 通过, {fail} 失败");
        WriteResult(outp, ref fail);
        return fail == 0 ? 0 : 1;
    }

    static void RunParsingChecks(Action<string, bool, string> Check)
    {
        Check("进程匹配：无通配名称", LassoConfig.MatchPattern("chrome.exe", "chrome.exe", @"C:\Apps\chrome.exe"), "");
        Check("进程匹配：省略 .exe", LassoConfig.MatchPattern("chrome", "chrome.exe", @"C:\Apps\chrome.exe"), "");
        Check("进程匹配：完整路径", LassoConfig.MatchPattern(@"C:\Apps\chrome.exe", "chrome.exe", @"C:\Apps\chrome.exe"), "");
        Check("进程匹配：通配路径", LassoConfig.MatchPattern(@"C:\Apps\*", "chrome.exe", @"C:\Apps\chrome.exe"), "");
        Check("进程匹配：大小写不敏感", LassoConfig.MatchPattern("CHROME.EXE", "chrome.exe", @"C:\Apps\chrome.exe"), "");
        Check("进程匹配：空模式不命中", !LassoConfig.MatchPattern("", "chrome.exe", @"C:\Apps\chrome.exe"), "");

        bool priorityOk =
            LassoConfig.TryParsePriority("high", out var p1) && p1 == ProcessPriorityClass.High &&
            LassoConfig.TryParsePriority("4", out var p2) && p2 == ProcessPriorityClass.RealTime &&
            LassoConfig.TryParsePriority("0x4000", out var p3) && p3 == ProcessPriorityClass.BelowNormal &&
            LassoConfig.TryParsePriority("above normal", out var p4) && p4 == ProcessPriorityClass.AboveNormal;
        Check("优先级：文本、序号、hex", priorityOk, "");
        Check("优先级：非法值被拒绝",
            !LassoConfig.TryParsePriority("urgent", out var invalidPriority) && invalidPriority == ProcessPriorityClass.Normal, "");

        Check("进程表：高频采样时刷新节流",
            MainForm.ProcessTableRefreshInterval(250) == 1000 &&
            MainForm.ProcessTableRefreshInterval(1000) == 1000 &&
            MainForm.ProcessTableRefreshInterval(5000) == 5000, "");
        Check("进程表：隐藏或最小化时暂停明细刷新",
            !MainForm.ShouldRefreshProcessDetails(false, FormWindowState.Normal) &&
            !MainForm.ShouldRefreshProcessDetails(true, FormWindowState.Minimized) &&
            MainForm.ShouldRefreshProcessDetails(true, FormWindowState.Normal), "");

        bool watchOk =
            GovernorEngine.ValidateWatchRule("watch.exe;cpu>50;action=trim", out _) &&
            GovernorEngine.ValidateWatchRule("watch.exe;mode=all;cpu>=50;mem>800;time>60;action=kill", out _) &&
            GovernorEngine.ValidateWatchRule("watch.exe;mode=any;cpu>50;mem>800;time>60;action=priority:low", out _) &&
            GovernorEngine.ValidateWatchRule("watch.exe;cpu<50;mem<=800;time<60;action=trim", out _);
        Check("看门狗：比较符、mode 与动作解析", watchOk, "");
        Check("看门狗：非法规则被拒绝",
            !GovernorEngine.ValidateWatchRule("watch.exe;mode=some;cpu>50;action=kill", out _) &&
            !GovernorEngine.ValidateWatchRule("watch.exe;cpu>50", out _) &&
            !GovernorEngine.ValidateWatchRule("watch.exe;action=kill", out _) &&
            !GovernorEngine.ValidateWatchRule("watch.exe;cpu=50;action=kill", out _) &&
            !GovernorEngine.ValidateWatchRule("watch.exe;cpu>50;cpu<80;action=kill", out _), "");

        var watchProcess = new ProcInfo
        {
            CpuPercent = 50,
            WorkingSetMB = 800,
            StartTime = DateTime.Now.AddSeconds(-90),
        };
        Check("看门狗：大小比较符边界与实际匹配",
            GovernorEngine.MatchesWatchRule("watch.exe;cpu>=50;action=trim", watchProcess) &&
            !GovernorEngine.MatchesWatchRule("watch.exe;cpu>50;action=trim", watchProcess) &&
            GovernorEngine.MatchesWatchRule("watch.exe;cpu<=50;action=trim", watchProcess) &&
            !GovernorEngine.MatchesWatchRule("watch.exe;cpu<50;action=trim", watchProcess) &&
            GovernorEngine.MatchesWatchRule("watch.exe;mem<=800;action=trim", watchProcess) &&
            GovernorEngine.MatchesWatchRule("watch.exe;time<120;action=trim", watchProcess), "");
        Check("看门狗：all/any 只组合已配置条件",
            GovernorEngine.MatchesWatchRule("watch.exe;mode=all;cpu>=50;action=trim", watchProcess) &&
            !GovernorEngine.MatchesWatchRule("watch.exe;mode=all;cpu>=50;mem>800;action=trim", watchProcess) &&
            GovernorEngine.MatchesWatchRule("watch.exe;mode=any;cpu>50;mem>=800;action=trim", watchProcess), "");
    }

    static void RunIconChecks(Action<string, bool, string> Check)
    {
        try
        {
            var sizes = new List<int>();
            using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(AppIcon.ResourceName)
                ?? throw new InvalidOperationException("未找到应用图标资源。"))
            {
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                reader.ReadUInt16();
                ushort type = reader.ReadUInt16();
                int count = reader.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    stream.Position = 6 + i * 16;
                    int width = stream.ReadByte();
                    stream.ReadByte();
                    sizes.Add(width == 0 ? 256 : width);
                }
                Check("应用图标：多尺寸 ICO 已嵌入",
                    type == 1 && sizes.Contains(16) && sizes.Contains(32) && sizes.Contains(256),
                    $"{count} 个尺寸: {string.Join('/', sizes)}");
            }

            using var icon = AppIcon.Load();
            Check("应用图标：窗口图标可加载", icon.Width >= 16 && icon.Height >= 16,
                $"{icon.Width}x{icon.Height}");
        }
        catch (Exception ex)
        {
            Check("应用图标：资源与加载检查", false, ex.Message);
        }
    }

    static void RunIniChecks(Action<string, bool, string> Check, string dir)
    {
        Directory.CreateDirectory(dir);
        static byte[] WithBom(Encoding encoding, string text) =>
            encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        void CheckBytes(string name, byte[] actual, byte[] expected)
            => Check(name, actual.SequenceEqual(expected), $"实际 {actual.Length} 字节，预期 {expected.Length} 字节");
        void CheckRoundTrip(string name, byte[] original, bool clone)
        {
            string path = Path.Combine(dir, name + ".ini");
            File.WriteAllBytes(path, original);
            var ini = new IniFile(path);
            if (clone) ini = ini.Clone();
            ini.Save();
            CheckBytes(name, File.ReadAllBytes(path), original);
        }

        string utf8Path = Path.Combine(dir, "utf8-bom.ini");
        File.WriteAllText(utf8Path, "; 保留注释\r\n# 另一条注释\r\n\r\n[Sample]\r\nKey=one\r\n", new UTF8Encoding(true));
        var utf8 = new IniFile(utf8Path);
        utf8.Set("Sample", "Key", "two");
        utf8.Set("Sample", "Key", "three");
        utf8.Save();
        string text = File.ReadAllText(utf8Path);
        int keyLines = text.Split('\n').Count(line => line.TrimStart('\uFEFF').StartsWith("Key=", StringComparison.Ordinal));
        Check("INI：UTF-8 BOM、注释、空行与连续 Set",
            utf8.Get("Sample", "Key") == "three" && text.Contains("; 保留注释") && keyLines == 1,
            $"Key 行={keyLines}");

        string utf16Path = Path.Combine(dir, "utf16.ini");
        File.WriteAllText(utf16Path, "[示例]\r\n键=原值\r\n", Encoding.Unicode);
        var utf16 = new IniFile(utf16Path);
        utf16.Set("示例", "键", "新值");
        utf16.Save();
        byte[] utf16Bytes = File.ReadAllBytes(utf16Path);
        var utf16Reloaded = new IniFile(utf16Path);
        Check("INI：UTF-16 保留编码与内容",
            utf16Bytes.Length >= 2 && utf16Bytes[0] == 0xFF && utf16Bytes[1] == 0xFE &&
            utf16Reloaded.Get("示例", "键") == "新值", "");

        var utf8Bom = new UTF8Encoding(true);
        var utf8NoBom = new UTF8Encoding(false);
        CheckRoundTrip("INI：UTF-8 BOM + CRLF + 无末尾换行字节往返",
            WithBom(utf8Bom, "; 未知内容\r\n[Section]\r\nKey=Value"), clone: false);
        CheckRoundTrip("INI：UTF-8 无 BOM + LF + 末尾换行克隆往返",
            utf8NoBom.GetBytes("[Section]\nKey=Value\n\n"), clone: true);
        CheckRoundTrip("INI：UTF-16BE + 单独 CR + 无末尾换行字节往返",
            WithBom(Encoding.BigEndianUnicode, "[示例]\r键=值"), clone: false);

        string duplicatePath = Path.Combine(dir, "duplicate.ini");
        byte[] duplicateOriginal = WithBom(utf8Bom,
            "; 保留\r\n[Rules]\r\nA=1\r\n[Rules]\r\nA=2\r\n[Other]\r\nA=9");
        File.WriteAllBytes(duplicatePath, duplicateOriginal);
        var duplicate = new IniFile(duplicatePath);
        bool duplicateRead = duplicate.Get("Rules", "A") == "2" && duplicate.Get("Other", "A") == "9";
        duplicate.Set("Rules", "A", "4");
        duplicate.Set("Rules", "B", "3");
        duplicate.Save();
        byte[] duplicateSaved = File.ReadAllBytes(duplicatePath);
        string duplicateText = File.ReadAllText(duplicatePath);
        int firstRules = duplicateText.IndexOf("[Rules]", StringComparison.Ordinal);
        int secondRules = duplicateText.IndexOf("[Rules]", firstRules + 1, StringComparison.Ordinal);
        int insertedB = duplicateText.IndexOf("B=3", StringComparison.Ordinal);
        int otherSection = duplicateText.IndexOf("[Other]", StringComparison.Ordinal);
        string noCrlf = duplicateText.Replace("\r\n", "");
        var duplicateReloaded = new IniFile(duplicatePath);
        Check("INI：重复节与重复键均以最后一处为准",
            duplicateRead && duplicateReloaded.Get("Rules", "A") == "4" && duplicateReloaded.Get("Other", "A") == "9" &&
            duplicateText.Contains("A=1") && secondRules > firstRules && insertedB > secondRules && insertedB < otherSection,
            $"最后一节={secondRules}，新键={insertedB}，下一节={otherSection}");
        Check("INI：修改后保留 CRLF、BOM 与无末尾换行",
            duplicateSaved.Length >= 3 && duplicateSaved[0] == 0xEF && duplicateSaved[1] == 0xBB && duplicateSaved[2] == 0xBF &&
            !noCrlf.Contains('\r') && !noCrlf.Contains('\n') &&
            duplicateSaved[^1] != (byte)'\r' && duplicateSaved[^1] != (byte)'\n', "");
    }

    static void RunTelemetryChecks(Action<string> P, Action<string, bool, string> Check, string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new ActionLog(dir);
        log.Write(100, "alpha.exe", ActionLog.ACT_KILL, "第一次记录");
        var first = log.ReadNewLines(40, out bool firstReset);
        Check("动作日志：首次读取获得完整尾部",
            firstReset && first.Any(x => x.Contains("第一次记录")), $"{first.Count} 行");

        log.Write(101, "beta.exe", ActionLog.ACT_PRIORITY, "第二次记录");
        var second = log.ReadNewLines(40, out bool secondReset);
        Check("动作日志：后续读取只返回增量",
            !secondReset && second.Count == 1 && second[0].Contains("第二次记录"), $"{second.Count} 行");

        File.AppendAllText(log.FilePath, "\"未完成", Encoding.UTF8);
        var incomplete = log.ReadNewLines(40, out bool incompleteReset);
        File.AppendAllText(log.FilePath, "行\"\n", Encoding.UTF8);
        var completed = log.ReadNewLines(40, out bool completedReset);
        Check("动作日志：只消费完整行",
            !incompleteReset && incomplete.Count == 0 && !completedReset &&
            completed.Count == 1 && completed[0].Contains("未完成行"), "");

        File.WriteAllText(log.FilePath, "\"截断后\"\n", Encoding.UTF8);
        var truncated = log.ReadNewLines(40, out bool truncatedReset);
        Check("动作日志：截断后重建读取位置",
            truncatedReset && truncated.Count == 1 && truncated[0].Contains("截断后"), "");

        using var graph = new GraphControl { Capacity = 3, GpuStatus = "测试 GPU 状态", GpuEngine = "3D 引擎" };
        graph.Push(10, 20, 30);
        graph.Push(20, null, 40);
        graph.Push(30, null, 50);
        Check("曲线：容量限制与缺失 GPU 点",
            graph.SampleCount == 3 && graph.CurrentCpu == 30 && graph.CurrentGpu == null &&
            graph.CurrentMemory == 50 && graph.GpuStatus == "测试 GPU 状态" && graph.GpuEngine == "3D 引擎", "");

        graph.SetPaused(true);
        graph.Push(99, 99, 99);
        Check("曲线：暂停后不再采样", graph.Paused && graph.SampleCount == 3 && graph.CurrentCpu == 30, "");
        graph.Clear();
        Check("曲线：清空与继续", graph.SampleCount == 0 && graph.CurrentCpu == null, "");
        graph.SetPaused(false);
        graph.Push(5, 10, 15);
        Check("曲线：继续后恢复采样", !graph.Paused && graph.SampleCount == 1 && graph.CurrentMemory == 15, "");

        using var windowGraph = new GraphControl { SamplingInterval = TimeSpan.FromSeconds(2) };
        windowGraph.TimeWindow = TimeSpan.FromSeconds(30);
        Check("曲线：容量跟随时间窗口与轮询间隔", windowGraph.Capacity == 17,
            $"容量={windowGraph.Capacity}");

        string firstEngine = GpuMonitor.EngineKey(
            "pid_100_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D");
        string secondEngine = GpuMonitor.EngineKey(
            "pid_200_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D");
        string decodeEngine = GpuMonitor.EngineKey(
            "pid_100_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_VideoDecode");
        Check("GPU：同一适配器引擎聚合与类型识别",
            firstEngine == secondEngine && firstEngine != decodeEngine &&
            GpuMonitor.FriendlyEngineName("3d") == "3D 引擎" &&
            GpuMonitor.FriendlyEngineName("videodecode") == "视频解码引擎", "");

        using var gpu = new GpuMonitor();
        bool gpuSampled = gpu.TrySample(out double gpuLoad);
        P($"[*] GPU 实测: available={gpuSampled} load={(gpuSampled ? gpuLoad.ToString("0.0") + "%" : "--")} engine={gpu.EngineName} status={gpu.StatusText}");
    }

    static void RunConfigChecks(Action<string> P, Action<string, bool, string> Check, string dir)
    {
        Directory.CreateDirectory(dir);
        string ownPath = Path.Combine(dir, "openlasso.ini");
        var cfg = new LassoConfig(ownPath, false);
        Check("首次运行绑定自有配置路径", cfg.Ini.Path == ownPath && cfg.ImportedFrom == null, cfg.Ini.Path);

        cfg.ProBalanceEnabled = true;
        cfg.TotalCpuBeforeRestraint = 71;
        cfg.PerProcessCpuBeforeRestraint = 7;
        cfg.TimeOverQuotaMs = 1300;
        cfg.PerProcessCpuForRestore = 3;
        cfg.MinRestraintMs = 5300;
        cfg.TameOnlyNormal = false;
        cfg.LowerIoOnRestraint = false;
        cfg.LowerToIdle = true;
        cfg.ExcludeForeground = false;
        cfg.ExcludeChildrenOfForeground = false;
        cfg.ExcludeServices = true;
        cfg.DisableWhenIdleMs = 45000;
        cfg.ProBalanceExclusions = new List<string> { "a.exe", "b*.exe" };
        cfg.FgBoostEnabled = true;
        cfg.FgBoostPriority = 0x80;
        cfg.FgBoostOnlyNormal = false;
        cfg.SmartTrimEnabled = true;
        cfg.SmartTrimMinRamLoad = 72;
        cfg.SmartTrimMinWssMb = 256;
        cfg.SmartTrimIntervalMin = 20;
        cfg.SmartTrimExclusions = new List<string> { "keep.exe", "cache*.exe" };
        cfg.UpdateSpeedMs = 1500;
        cfg.ManageOnlyCurrentUser = true;
        cfg.DefaultPriorities = "alpha.exe,high";
        cfg.DefaultIoPriorities = "beta.exe,low";
        cfg.DefaultMemPriorities = "gamma.exe,normal";
        cfg.DefaultAffinities = "delta.exe,0x3";
        cfg.DefaultCpuSets = "epsilon.exe,0,1";
        cfg.EfficiencyModes = "notepad.exe,eco";
        cfg.InstanceLimits = "single.exe,1";
        cfg.CpuLimits = "busy.exe,30";
        cfg.Disallowed = "bad.exe";
        cfg.Allowed = "good.exe";
        cfg.KeepRunning = "keeper.exe";
        cfg.WatchdogRules = "watch.exe,1";
        cfg.IdleSaverEnabled = true;
        cfg.IdleSaverIdleSec = 600;
        cfg.IdleSaverPlan = "平衡测试";
        cfg.LoadPowerEnabled = true;
        cfg.LoadLowPct = 12.5;
        cfg.LoadHighPct = 61.5;
        cfg.LoadLowPlan = "节能测试";
        cfg.LoadHighPlan = "高性能测试";
        cfg.Save();

        var r = new LassoConfig(ownPath, false);
        var problems = new List<string>();
        void Eq<T>(string name, T actual, T expected)
        { if (!EqualityComparer<T>.Default.Equals(actual, expected)) problems.Add($"{name}={actual} (期望 {expected})"); }
        void ListEq(string name, List<string> actual, params string[] expected)
        { if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) problems.Add($"{name}={string.Join("|", actual)}"); }

        Eq("ProBalanceEnabled", r.ProBalanceEnabled, true);
        Eq("TotalCpuBeforeRestraint", r.TotalCpuBeforeRestraint, 71);
        Eq("PerProcessCpuBeforeRestraint", r.PerProcessCpuBeforeRestraint, 7);
        Eq("TimeOverQuotaMs", r.TimeOverQuotaMs, 1300);
        Eq("PerProcessCpuForRestore", r.PerProcessCpuForRestore, 3);
        Eq("MinRestraintMs", r.MinRestraintMs, 5300);
        Eq("TameOnlyNormal", r.TameOnlyNormal, false);
        Eq("LowerIoOnRestraint", r.LowerIoOnRestraint, false);
        Eq("LowerToIdle", r.LowerToIdle, true);
        Eq("ExcludeForeground", r.ExcludeForeground, false);
        Eq("ExcludeChildrenOfForeground", r.ExcludeChildrenOfForeground, false);
        Eq("ExcludeServices", r.ExcludeServices, true);
        Eq("DisableWhenIdleMs", r.DisableWhenIdleMs, 45000);
        ListEq("ProBalanceExclusions", r.ProBalanceExclusions, "a.exe", "b*.exe");
        Eq("FgBoostEnabled", r.FgBoostEnabled, true);
        Eq("FgBoostPriority", r.FgBoostPriority, 0x80);
        Eq("FgBoostOnlyNormal", r.FgBoostOnlyNormal, false);
        Eq("SmartTrimEnabled", r.SmartTrimEnabled, true);
        Eq("SmartTrimMinRamLoad", r.SmartTrimMinRamLoad, 72);
        Eq("SmartTrimMinWssMb", r.SmartTrimMinWssMb, 256);
        Eq("SmartTrimIntervalMin", r.SmartTrimIntervalMin, 20);
        ListEq("SmartTrimExclusions", r.SmartTrimExclusions, "keep.exe", "cache*.exe");
        Eq("UpdateSpeedMs", r.UpdateSpeedMs, 1500);
        Eq("ManageOnlyCurrentUser", r.ManageOnlyCurrentUser, true);
        Eq("DefaultPriorities", r.DefaultPriorities, "alpha.exe,high");
        Eq("DefaultIoPriorities", r.DefaultIoPriorities, "beta.exe,low");
        Eq("DefaultMemPriorities", r.DefaultMemPriorities, "gamma.exe,normal");
        Eq("DefaultAffinities", r.DefaultAffinities, "delta.exe,0x3");
        Eq("DefaultCpuSets", r.DefaultCpuSets, "epsilon.exe,0,1");
        Eq("EfficiencyModes", r.EfficiencyModes, "notepad.exe,eco");
        Eq("InstanceLimits", r.InstanceLimits, "single.exe,1");
        Eq("CpuLimits", r.CpuLimits, "busy.exe,30");
        Eq("Disallowed", r.Disallowed, "bad.exe");
        Eq("Allowed", r.Allowed, "good.exe");
        Eq("KeepRunning", r.KeepRunning, "keeper.exe");
        Eq("WatchdogRules", r.WatchdogRules, "watch.exe,1");
        Eq("IdleSaverEnabled", r.IdleSaverEnabled, true);
        Eq("IdleSaverIdleSec", r.IdleSaverIdleSec, 600);
        Eq("IdleSaverPlan", r.IdleSaverPlan, "平衡测试");
        Eq("LoadPowerEnabled", r.LoadPowerEnabled, true);
        Eq("LoadLowPct", r.LoadLowPct, 12.5);
        Eq("LoadHighPct", r.LoadHighPct, 61.5);
        Eq("LoadLowPlan", r.LoadLowPlan, "节能测试");
        Eq("LoadHighPlan", r.LoadHighPlan, "高性能测试");
        Check("配置字段保存/重载往返", File.Exists(ownPath) && problems.Count == 0, string.Join("; ", problems));
        Check("能效模式写入新键 EfficiencyMode",
            r.Ini.Get("ProcessAllowances", "EfficiencyMode") == "notepad.exe,eco", "");

        string legacyPath = Path.Combine(dir, "legacy.ini");
        var legacyWriter = new IniFile(legacyPath);
        legacyWriter.Set("ProcessAllowances", "EfficiencyModes", "legacy.exe,eco");
        legacyWriter.Save();
        var legacy = new LassoConfig(legacyPath, false);
        Check("兼容读取旧键 EfficiencyModes", legacy.EfficiencyModes == "legacy.exe,eco", legacy.EfficiencyModes);

        string sourcePath = Path.Combine(dir, "source-prolasso.ini");
        var source = new IniFile(sourcePath);
        source.Set("OutOfControlProcessRestraint", "OocOn", "False");
        source.Set("Performance", "ManageOnlyCurrentUser", "True");
        source.Save();
        byte[] sourceBefore = File.ReadAllBytes(sourcePath);
        string importedPath = Path.Combine(dir, "imported", "openlasso.ini");
        var imported = new LassoConfig(importedPath, true, sourcePath);
        bool importSafe = imported.Ini.Path == importedPath && imported.ConfigPath == importedPath &&
                          imported.ImportedFrom == sourcePath && File.Exists(importedPath) &&
                          !imported.ProBalanceEnabled;
        imported.ManageOnlyCurrentUser = false;
        imported.Save();
        Check("首次导入复制到自有路径且不回写来源", importSafe && File.ReadAllBytes(sourcePath).SequenceEqual(sourceBefore),
            $"Ini={imported.Ini.Path}, 来源={imported.ImportedFrom}");

        string fallbackPath = Path.Combine(dir, "fallback", "openlasso.ini");
        var fallback = new LassoConfig(fallbackPath, true, Path.Combine(dir, "missing-prolasso.ini"));
        fallback.ManageOnlyCurrentUser = true;
        fallback.Save();
        Check("导入失败后仍可安全保存自有配置",
            fallback.Ini.Path == fallbackPath && fallback.ImportedFrom == null && File.Exists(fallbackPath) &&
            new LassoConfig(fallbackPath, false).ManageOnlyCurrentUser, fallback.Ini.Path);

        P($"[*] 配置回归临时目录: {dir}");
    }

    static void WriteResult(StringBuilder sb, ref int fail)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"), sb.ToString(), Encoding.UTF8); } catch { }
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

    static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(100);
        }
        try { return condition(); } catch { return false; }
    }
}
