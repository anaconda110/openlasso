# OpenLasso 迁移/交接说明

> 生成时间：2026-10-25（Asia/Shanghai）  
> 项目根目录：/home/oraw/projects/00.Program/openlasso  
> 技术栈：C# / .NET 8 / WinForms  
> 当前分支：`main`
>
> 续作更新：2026-10-25 23:34。第 1～6 节保留交接时的原始快照；最新完成状态和验证结果见第 7、8、9 节。

## 工作区快照

本次检查为只读检查；除新增本文件外，没有继续修改源码、构建或运行自检。

```text
 M GraphControl.cs
 M RulesForm.cs
 M SettingsForm.cs
?? UiTheme.cs
```

- `git diff --stat`：3 个已跟踪文件变更，合计 `532 insertions(+), 137 deletions(-)`。
- `git diff --check`：通过，未发现空白错误。
- 未提交、未推送。
- 本轮没有修改 `Config.cs`、`MainForm.cs`、`SelfTest.cs`。
- 禁止覆盖已安装版本 `D:\Program Files\tools\OpenLasso\OpenLasso.exe`。
- 禁止向真实 `C:\ProgramData\ProcessLasso\config\prolasso.ini` 写入或保存。

## 1. 已完成内容

### 1.1 统一浅色 UI 主题

新增 `UiTheme.cs`，提供：

- 背景、卡片、边框、正文、次要文字、主色、成功/警告/危险色。
- 标题、正文、小字、等宽字体等统一字体资源。
- 窗体、按钮、输入框、卡片、提示块、菜单、上下文菜单和表格样式。
- `CardPanel` 卡片容器和 `SmoothDataGridView` 平滑滚动表格控件。

### 1.2 图表页优化

已修改 `GraphControl.cs`：

- 图表视觉和绘制逻辑优化。
- 与浅色主题保持一致。

### 1.3 规则页优化

已修改 `RulesForm.cs`：

- 规则页面改为卡片化布局。
- 统一样式、间距、控件外观和可读性。
- 保留既有规则编辑功能。

### 1.4 设置页优化

已修改 `SettingsForm.cs`：

- 设置页改为可缩放、可滚动的卡片分区布局。
- 顶部标题/说明、固定底部操作栏。
- 默认客户区 `760×720`，最小尺寸 `700×580`。
- 新增/接通大部分 ProBalance 开关，包括只抑制 Normal、排除前台进程及子进程、空闲暂停等。
- 电源自动化区域已有 IdleSaver、低/高负载阈值和高性能计划输入框。
- 保存时会把当前设置页已展示的字段写回 `LassoConfig`。

### 1.5 已有历史验证记录

交接前记录显示曾完成一次 Release 构建并通过 8 项自检；详情见第 4 节。该结果不是本轮重新验证结果。

## 2. 未完成事项

### P0：修复配置导入可能覆盖 Process Lasso 配置

当前 `Config.cs` 构造函数：

```csharp
public LassoConfig(string path)
```

首次运行且 OpenLasso 自己的配置不存在时，会把 `Ini` 切换为真实 `prolasso.ini` 以读取配置。但 `Ini.Save()` 随后仍会写入该文件的路径，因此用户点击保存时可能覆盖真实 Process Lasso 配置。

后续必须改为安全导入模型，建议：

```csharp
public LassoConfig(string path, bool importFromProcessLasso = true)
```

要求：

- `Ini` 始终指向 OpenLasso 自有配置路径，不切换到 Process Lasso 文件。
- 首次运行只使用 `File.Copy(plIni, path, overwrite: false)` 复制导入。
- 复制成功后才记录 `ImportedFrom`。
- 导入失败时保留 OpenLasso 自有空配置，不影响后续保存。
- 自动化测试使用 `new LassoConfig(path, false)`，彻底隔离真实配置。

### P1：补齐配置保存/重载字段

`Reload()` 已读取许多详细字段，但 `Save()` 仍缺少以下写入：

```text
OutOfControlProcessRestraint/TameOnlyNormal
OutOfControlProcessRestraint/LowerIOPriorityDuringRestraint
OutOfControlProcessRestraint/LowerToIdleInsteadOfBelowNormal
OutOfControlProcessRestraint/ExcludeForegroundProcesses2
OutOfControlProcessRestraint/ExcludeChildrenOfForeground
OutOfControlProcessRestraint/ExcludeServices
OutOfControlProcessRestraint/DisableProBalanceIfSysIdleThisManyMS
ForegroundBoosting/ForegroundBoostPriorityClass
ForegroundBoosting/BoostOnlyNormal
Performance/ManageOnlyCurrentUser
MemoryManagement/SmartTrimExclusions
PowerSaver/PowerSaver_IdlePlan
LoadBasedPowerSwitcher/LoadBasedPowerSwitcherLowCPULoadThreshold
LoadBasedPowerSwitcher/LoadBasedPowerSwitcherHighCPULoadThreshold
LoadBasedPowerSwitcher/LoadBasedPowerSwitcherLowPlan
LoadBasedPowerSwitcher/LoadBasedPowerSwitcherHighPlan
```

另外：

- `Reload()` 当前没有读取 IdleSaver 计划名、低负载计划名和高负载计划名。
- 前景优先级保存时建议写成 `"0x" + FgBoostPriority.ToString("X")`。
- 能效模式统一写入 `ProcessAllowances/EfficiencyMode`；读取时兼容旧键 `EfficiencyModes`。
- 保存后应再次读取并比较，避免 UI 值看似保存但实际丢失。

### P1：补完设置页字段

`SettingsForm.cs` 当前还缺少：

- 低负载计划名输入框，并写入 `LoadLowPlan`。
- `LowerToIdle`、`ExcludeServices`、前台提升优先级等未完整暴露/保存。
- `SmartTrimExclusions` 尚无可编辑入口。
- 需要验证 `700×580` 最小尺寸下无横向滚动、标签不裁切、底部按钮始终可见。
- 需要验证 Tab 顺序、Enter 保存、Escape 取消及高 DPI 布局。

### P1：统一主窗口主题

`MainForm.cs` 仍保留深色硬编码、默认 `DataGridView`、硬编码字体和状态颜色。后续建议：

- `_grid` 改用 `SmoothDataGridView`，并调用 `UiTheme.StyleDataGridView`。
- 日志/信息区使用 `UiTheme.Surface`、`UiTheme.Text` 和 `UiTheme.MonoFont`。
- 窗体调用 `UiTheme.ApplyForm(this)`，菜单和上下文菜单使用主题方法。
- 状态颜色改用 `UiTheme.Warning`、`UiTheme.Danger`、`UiTheme.Text`。
- `PromptCombo` 与 `AffinityDialog` 统一主题，同时保持现有功能和快捷键不变。

### P1：增加隔离的配置往返自检

`SelfTest.cs` 尚未覆盖配置持久化和导入安全。至少新增：

- 主要配置字段保存后重新构造并读回一致。
- `EfficiencyMode` 新键写入，以及旧键 `EfficiencyModes` 兼容读取。
- OpenLasso 配置不存在且 `importFromProcessLasso:false` 时，`Ini` 仍指向传入的自有路径。
- 测试全程在临时目录执行，`finally` 清理，不访问真实 Process Lasso 配置。

## 3. 关键文件

| 文件 | 当前状态 | 作用/说明 |
|---|---|---|
| `UiTheme.cs` | 新增，未跟踪 | 统一主题、卡片、按钮、输入框、菜单和表格样式；含 `SmoothDataGridView` |
| `GraphControl.cs` | 已修改 | 图表绘制与视觉优化 |
| `RulesForm.cs` | 已修改 | 规则页卡片化和布局优化 |
| `SettingsForm.cs` | 已修改 | 设置页卡片化、滚动布局、固定操作栏及字段绑定 |
| `Config.cs` | 本轮未修改 | ini 读写核心；存在 P0 导入保存风险及字段保存缺口 |
| `MainForm.cs` | 本轮未修改 | 主窗口仍待统一浅色主题 |
| `SelfTest.cs` | 本轮未修改 | 现有 8 项引擎自检；待补配置隔离测试 |
| `Program.cs` | 本轮未修改 | `--selftest`、`--help`、`--minimized` 入口 |
| `AppPaths.cs` | 本轮未修改 | `%ProgramData%\OpenLasso`，无权限时回退 `%AppData%\OpenLasso` |
| `OpenLasso.csproj` | 未修改 | .NET 8 WinForms，版本 1.0.0 |

Release 产物：

```text
bin/Release/net8.0-windows/OpenLasso.exe  2026-10-25 19:30:46 +08:00
bin/Release/net8.0-windows/OpenLasso.dll  2026-10-25 19:30:46 +08:00
```

## 4. 测试结果

### 已记录的历史结果

- Release 构建：0 警告，0 错误。
- `--selftest`：8 项通过，0 失败。
- 覆盖项：引擎快照捕获、进程名解析、优先级设置/读回、亲和性设置/读回、工作集整理、进程终止、进程退出确认、动作日志写入。

### 当前可核验状态

- Release 产物晚于最后一次 `SettingsForm.cs` 修改，时间上说明该产物可能包含当前 UI 修改，但没有保存构建日志。
- 当前工作区及 `bin` 下均未找到 `selftest-result.txt`。
- 本轮按要求没有重新构建或运行 `--selftest`，因此以上历史结果不能视为对当前工作树的最终验证。
- `git diff --check` 已通过。

## 5. 已知问题

1. **P0 配置覆盖风险**：首次导入后保存可能写回真实 `prolasso.ini`。
2. **配置保存不完整**：多项 ProBalance、前台提升、SmartTrim、引擎和电源字段未写回。
3. **电源计划名丢失**：计划名没有可靠重载/保存，设置页缺少低负载计划输入。
4. **能效键不一致**：读取 `EfficiencyMode`，保存仍写旧键 `EfficiencyModes`。
5. **主窗口视觉不统一**：`MainForm.cs` 仍有深色/默认控件样式。
6. **设置页布局未做当前版本视觉回归**：最小尺寸、高 DPI、滚动和键盘操作尚未人工验证。
7. **缺少配置安全回归测试**：目前自检会使用真实 `AppPaths.ConfigPath` 读取配置，未验证导入隔离和保存往返。

## 6. 下一步

1. 先修 P0：让 `Ini` 永远绑定 OpenLasso 自有路径，只通过复制导入 Process Lasso 配置。
2. 补齐 `Config.Save()` / `Reload()` 字段和能效键兼容，并增加临时目录配置往返测试。
3. 补齐设置页低负载计划及其他缺失字段，检查最小窗口和高 DPI 布局。
4. 将 `MainForm`、`PromptCombo`、`AffinityDialog` 接入 `UiTheme`。
5. 运行隔离自检，再执行 Release 构建，确认 0 警告、0 错误并保存测试输出。
6. 进行手工 UI 回归：主窗口、图表、规则页、设置页的缩放、滚动、Tab/Enter/Escape、状态色和托盘行为。
7. 全流程验证不得写入真实 `prolasso.ini`，也不得替换已安装的 OpenLasso 二进制。

## 7. 续作进度（2026-10-25 22:30）

### 已完成

- 修复 P0 配置覆盖风险：`Ini` 始终绑定 OpenLasso 自有 `openlasso.ini`；首次运行只通过 `File.Copy` 复制 Process Lasso 配置，导入失败也不影响后续自有配置保存。
- 构造函数增加 `importFromProcessLasso` 开关及可注入的测试来源路径；自动化测试可完全避开真实 `prolasso.ini`。
- 补齐 `Config.Save()` 的 ProBalance、前台提升、SmartTrim 排除项、当前用户范围、IdleSaver 和负载切换字段。
- 统一写新键 `ProcessAllowances/EfficiencyMode`，读取时兼容旧键 `EfficiencyModes`。
- 补读/补写 IdleSaver、低负载和高负载电源计划名；阈值使用 invariant culture 写入。
- `Save()` 后自动 `Reload()`，调用方看到的是实际落盘并回读后的值。
- 设置页补上降到空闲级、排除服务、前台提升优先级、SmartTrim 排除项、低负载计划名，并加入 Tab 顺序、阈值校验和保存回读。
- 主窗口改用 `SmoothDataGridView`，菜单、上下文菜单、状态栏、日志/信息区、图表容器、托盘状态色和弹出对话框统一接入 `UiTheme`。
- 自检全部改到临时目录，默认关闭 ProBalance、SmartTrim、前台提升和电源自动化，只显式操作自检创建的 `cmd.exe`。
- 新增配置保存/重载往返、能效新旧键、成功导入不回写来源、导入失败后安全保存等回归检查。

### 当前验证

- `git diff --check`：通过。
- Release 构建：0 警告，0 错误。
- `OpenLasso.exe --selftest`：14 项通过，0 失败。
- 自检日志中只有临时引擎启动信息和自检测试进程动作，没有对其他进程执行 SmartTrim。
- 仍未提交、未推送。

### 仍需手工验证

- 以非隔离的真实运行方式检查主窗口、设置页、规则页在 100% / 125% / 150% DPI 下的布局。
- 检查设置页在 `700×580` 最小尺寸下无横向滚动、标签不裁切、底部按钮始终可见。
- 检查 Tab 顺序、Enter 保存、Escape 取消、托盘显示/退出及主题在 Windows 实际渲染下的观感。

### 安全边界

- 本轮验证未启动主窗口，未读取真实 Process Lasso 配置用于引擎运行，也未修改真实 `C:\ProgramData\ProcessLasso\config\prolasso.ini`。
- 未替换 `D:\Program Files\tools\OpenLasso\OpenLasso.exe` 或安装目录中的任何二进制。

## 8. 续作进度（2026-10-25 23:25）

### 本轮修复

- **主窗口右侧面板消失**：新增 `_split` 持久引用，移除构造阶段无效的 `SplitterDistance` 赋值；窗口 `Shown` 后按实际客户区宽度初始化右侧宽度（约 320～400px）和两侧最小宽度。已验证 100% DPI 下默认 `1250×780` 与最小 `900×560` 均能完整显示图表、信息和日志卡片。
- **设置页布局与行为**：隔离测试确认 `700×580`、96 DPI 下客户区为 `684×541`，无横向滚动，32 个标签无裁切，5 个卡片完整，底部按钮始终可见。31 个字段 Tab 顺序唯一且正确；Escape 返回 `Cancel`；Enter 保存正确落盘；低阈值不小于高阈值时拒绝保存并保持窗口打开。
- **规则页卡片塌缩**：`RulesForm` 的 `editorCard`、`listCard`、`hintCard` 原先只按首选宽度左上锚定；现改为 `Dock = DockStyle.Fill`，铺满根表格列。列宽算法为纵向滚动条预留空间，并在重算列宽后清除 WinForms `ListView` 残留的横向滚动条。

### 规则页验证

- 96 DPI 下验证默认 `900×620`、最小 `760×520`。
- 空规则列表与 30 条规则（含纵向滚动）两种场景均通过：三个卡片横向铺满、类型/匹配模式/值输入框完整可见、提示文字无裁切、三列宽度合理、底部三个按钮均在窗口内。
- 最小尺寸报告：卡片宽 `708px`，提示标签可用宽 `706px`（首选宽 `579px`），列宽 `132/206/326px`，文字输入框宽 `202/185px`。
- 截图：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\rules_probe\rules_default.png`、`rules_min.png`。
- 布局报告：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\rules_probe\rules_report.txt`。

### 托盘与最终回归

- 托盘测试通过：关闭主窗口后隐藏而不退出；托盘“显示主窗口”恢复窗口；托盘“退出”销毁窗口并结束消息循环；托盘图标和菜单均存在。
- 托盘测试期间临时将 OpenLasso 自有配置切换为无规则的禁用自动化配置，测试后已恢复；恢复前后 SHA-256 一致，未触碰 Process Lasso 配置。
- Release 构建：0 警告，0 错误。
- `OpenLasso.exe --selftest`：14 项通过，0 失败。验证覆盖配置导入隔离、字段保存/重载、能效新旧键、引擎快照、优先级、亲和性、工作集整理、终止和动作日志。
- `git diff --check`：通过。
- 当前仍未提交、未推送。

### 仍未完成

- 真实 125% / 150% DPI 显示环境仍需最终视觉回归；96 DPI 已实测，120 / 144 DPI 的隔离合成回归见第 9 节。高 DPI 下需重点复核主窗口分栏、设置页滚动区、规则页输入框和 ListView 列宽。

## 9. 续作进度（2026-10-25 23:34）

### 本轮完成

- **产品措辞统一**：窗口标题、帮助、关于对话框和程序集描述改为中性表述，使用“开源进程优化器”“兼容 Process Lasso 核心功能”等说法；源码与文档中不再保留与原软件身份绑定的受限措辞。
- **规则页高 DPI**：列宽常量按窗体 `DeviceDpi / 96f` 缩放；监听 `DpiChanged` 后异步重算列宽；继续为纵向滚动条预留空间并清除 `ListView` 残留横向滚动条。
- **主窗口分栏可测试化**：抽出 `ConfigureSplitLayout(SplitContainer split, int dpi)`，按 DPI 缩放右侧 `320～400px` 范围及左右最小宽度；窗口 `Shown` 后使用窗体 DPI 初始化。

### 高 DPI 回归

- 隔离测试程序：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\highdpi_probe\`。
- 测试在独立进程中合成 `WM_DPICHANGED`，配置、日志和规则均写入临时目录，不读取真实 Process Lasso 配置。
- 主窗口分栏在 96 / 120 / 144 DPI 的默认与最小客户区均通过：右侧宽度分别为 `370/463/555px`（默认）和 `320/400/480px`（最小）。
- 规则页在 96 / 120 / 144 DPI 的默认与最小尺寸均通过：卡片横向铺满、三列表头可容纳、列宽总和不超过 `ListView` 客户区、提示文字不裁切、底部按钮均在窗体内。
- 设置页在 96 / 120 / 144 DPI 的默认与最小尺寸均通过：无横向滚动，5 个卡片完整，32 个标签无裁切，31 个字段 Tab 顺序唯一，保存/取消按钮始终在窗体内。
- 报告：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\highdpi_probe\highdpi_report.txt`。
- 截图前缀：`rules_*`、`settings_*_top.png`、`settings_*_bottom.png`。
- 测试程序自身的高 DPI 卡片宽度断言已改为按根布局可用宽度计算；修正后全部项目通过。

### 构建与自检

- Release 构建：0 警告，0 错误。
- 隔离无头自检：14 项通过，0 失败；临时目录为 `%TEMP%\OpenLasso-SelfTest-<guid>`，未写入真实 Process Lasso 配置。
- `git diff --check`：通过。
- 受限措辞全仓库扫描：无结果。
- 当前仍未提交、未推送。

### 验证边界

- 本机实际桌面仍为 96 DPI（双屏均 `1920×1080`）；120 / 144 是进程内合成 DPI 消息测试，不等同于真实 125% / 150% 显示器会话。
- 仍需在真实 125% / 150% 显示环境中复核字体设备 DPI 渲染、窗口跨屏拖动、主分栏、设置页滚动区和规则页列宽。
- 本轮未修改系统显示设置，未替换安装版二进制，未写入真实 `C:\ProgramData\ProcessLasso\config\prolasso.ini`。

## 10. 续作进度（2026-10-25 23:46）

### 本轮完成

- **GPU 采集**：新增 `GpuMonitor.cs`，通过 PDH 英文计数器 `\GPU Engine(*)\Utilization Percentage` 采样；按显卡 LUID 与引擎类型聚合进程实例，同一引擎求和，整机取最繁忙引擎的占用。计数器不可用时返回不可用，不影响 CPU 和内存监控。
- **引擎接入**：`GovernorEngine` 在每次 Tick 中采样 GPU，新增 `GpuLoad`、`GpuAvailable`；释放引擎时同步关闭 PDH 查询。
- **多序列占用曲线**：`GraphControl` 由单 CPU 曲线扩展为 CPU、GPU、内存三条序列。图例同时显示三项实时百分比；GPU 缺失时显示 `GPU --`，采样缺口保持断线，不连接假数据。
- **主窗口接入**：图表标题改为“系统占用”，每次刷新同时推送 `TotalCpu`、可选 `GpuLoad` 和 `RamLoad`。曲线、图例、边距和线宽按 `DeviceDpi / 96f` 缩放。
- **颜色区分**：CPU 使用蓝色 `37,99,235`，GPU 使用紫色 `124,58,237`，内存使用琥珀色 `217,119,6`。

### GPU 实测

- 独立探针连续采样成功，最新四次数值依次为 `3.1% / 1.1% / 3.4% / 0.5%`，`Available=True`。
- 探针目录：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\gpu_probe\`。

### 高 DPI 曲线回归

- 96 / 120 / 144 DPI 的三序列场景与 GPU 不可用场景全部通过。
- 96 DPI 最小宽度下图例完整显示，三种颜色易区分；GPU 不可用时不绘制对应曲线。
- 报告：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\highdpi_probe\highdpi_report.txt`。
- 截图：`graph_96_all.png`、`graph_96_no-gpu.png`、`graph_120_all.png`、`graph_120_no-gpu.png`、`graph_144_all.png`、`graph_144_no-gpu.png`（同目录）。

### 最终验证

- Release 构建：0 警告，0 错误。
- 隔离无头自检：14 项通过，0 失败。
- `git diff --check`：通过。
- 受限字样扫描：源码、文档及 Release 产物均无命中。
- 当前仍未提交、未推送。
- 本轮未修改系统 DPI，未写入真实 `C:\ProgramData\ProcessLasso\config\prolasso.ini`，未替换安装目录中的二进制。

## 11. 续作进度（2026-10-26 02:52）

### 本轮完成

- **自检隔离加固**：不再启动 `cmd.exe` 或依赖窗口标题，而是把系统 `ping.exe` 复制为 `olselftest-<唯一标识>.exe`，隐藏窗口、无 shell 运行；规则只可能命中本次测试进程。
- **GPU 引擎展示**：GPU 采样现在随快照发布最繁忙引擎类型，主窗口状态栏、信息框和宽版曲线图例会显示“3D 引擎”“视频解码引擎”等友好名称；窄图仍简写为 GPU，避免挤压绘图区。
- **进程元数据缓存**：缓存进程映像路径和描述，仅在新进程首次出现时访问 `MainModule`；进程退出后同步清理缓存、CPU 增量状态、规则状态和权限结果。
- **权限检查退避**：当前用户 SID 查询失败后暂停重试 15 秒；成功匹配结果缓存到进程退出，避免对受保护进程每轮重复查询。
- **进程表刷新节流**：曲线和状态统计仍按配置轮询间隔更新，进程表最快每秒刷新一次；批量更新期间暂停表格布局，降低大进程表下的 UI 抖动。
- **隐藏窗口降载**：主窗口隐藏或最小化时停止进程表批更新和日志视图刷新，只保留引擎、曲线采样与托盘状态；恢复显示后立即刷新进程表。
- **并发 SmartTrim 回归**：新增并发调用检查，确认两个整理请求都能在限时内返回、互不破坏状态，且引擎继续运行。

### 新增自检覆盖

- 进程表刷新节流边界（250 / 1000 / 5000 ms）及隐藏 / 最小化状态判定。
- GPU 同一适配器、同一引擎的多进程聚合，以及不同引擎分离。
- GPU 引擎中文名称映射与曲线引擎展示字段。
- 实际 GPU PDH 采样日志；本次实测 `available=True`，示例引擎为“3D 引擎”。
- 并发 SmartTrim 调用安全完成。

### 高 DPI 回归

- 隔离探针在 96 / 120 / 144 DPI 下重新验证主窗口分栏、CPU / GPU / 内存曲线、规则页和设置页，全部通过。
- 曲线同时覆盖 GPU 可用与不可用场景；96 / 120 DPI 窄图使用简写图例，144 DPI 宽图完整显示“GPU（视频解码引擎）”，图例与绘图区无重叠。
- 报告：`C:\Users\Tom\AppData\Local\Temp\openlasso_ui\highdpi_probe\highdpi_report.txt`。
- 最新截图：`graph_96_all.png`、`graph_120_all.png`、`graph_144_all.png` 及对应 `*-no-gpu.png`（同目录）。

### 最终验证

- Release 构建：0 警告，0 错误。
- 隔离无头自检：48 项通过，0 失败。
- `git diff --check`：通过。
- 指定禁用字样扫描：源码、Markdown 和 Release 产物均无命中。
- 当前仍未提交、未推送。

### 安全边界与残余风险

- 本轮未写入真实 `C:\ProgramData\ProcessLasso\config\prolasso.ini`，未替换安装目录中的二进制。
- 96 / 120 / 144 DPI 为进程内合成回归；真实 125% / 150% 显示器会话、跨屏拖动和字体设备 DPI 渲染仍需手工复核。
- 当前 GPU 曲线展示整机最繁忙的“适配器 + 引擎类型”，尚未提供多适配器选择、设备名称和历史数据持久化。
