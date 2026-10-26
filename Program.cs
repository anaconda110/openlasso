namespace OpenLasso;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--help"))
        {
            Console.WriteLine("OpenLasso — 开源进程优化器（兼容 Process Lasso 核心功能）");
            Console.WriteLine("  OpenLasso.exe            启动 GUI（托盘 + 进程管理）");
            Console.WriteLine("  OpenLasso.exe --selftest 无头自检：规则引擎/优先级/亲和性/整理/终止");
            return 0;
        }
        // 单实例（对应 PL 的 Event_ProcessLassoGUI_Running 语义）
        using var single = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\OpenLassoGUI_Running", out var createdFirst);
        if (!createdFirst) { MessageBox.Show("OpenLasso 已在运行。", "OpenLasso", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }

        ApplicationConfiguration.Initialize();
        bool minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using var form = new MainForm(minimized);
        Application.Run(form);
        return 0;
    }
}
