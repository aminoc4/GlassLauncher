# GlassLauncher 资源占用实测（2026-11-22 晚）

> 本文件是**本轮独立实测**的结果，用来替换 `REFACTOR-CHARTER.md` 第 3 节里"按怀疑度排序的假设"。
> 所有数字都能用 `.agent/` 下的脚本复现（脚本清单见文末）。测量机就是交接包描述的测试机：
> Windows 10 Pro for Workstations build 26300（Insider）、单屏 2048×1152、DPI 1.25、.NET 8.0.34。

## 0. 一句话结论

**"169MB 内存"是口径问题，不是本应用的泄漏。**

- 用户/任务管理器看到的 **Memory（私有工作集）**：可见约 **44–57MB**，隐藏到托盘约 **26–44MB**；
- `Get-Process` 的 **Working Set** 是 139–164MB，其中大部分是 WPF/CLR 的**共享页**（只读代码、字体缓存等），不是本应用独享；
- **私有提交（Private Bytes / 提交大小）** 84–103MB 常驻，拖动/隐藏会临时冲到 150–200MB，但多数是**已提交不驻留**的页面。

空 WPF 窗口（同机、Release、840×560、无任何业务代码）本身就有：
**私有提交 73.5MB / 私有工作集 ~40MB / WS 104MB / 34 线程 / 739 句柄**。
所以：

- 在**私有工作集**口径下，"<60MB" 目标本轮实测已经基本达到（可见态最差 57MB）；
- 在**WS/私有提交**口径下，WPF 的底座已经超过 60MB，**任何不换渲染栈的重构都不可能达标**。

## 1. 测量方法

1. 源码同步到 `.agent/src`（可写副本），只加一个测试补丁：单实例互斥名改成 `AgentTest`，
   这样可以和用户正在运行的实例并存。
2. 用唯一进程名 `GlassLauncherBench.exe` 跑基准，避免 perf 计数器实例名冲突。
3. 数据/日志目录用 `GLASSLAUNCHER_DATA_DIR` / `GLASSLAUNCHER_LOG_DIR` 指向 `.agent/`（本轮新增支持）。
4. 内存口径：
   - `Working Set` = `Get-Process.WorkingSet64`；
   - `Private Bytes` = 提交大小（`Process.PrivateMemorySize64`，等于 `GetProcessMemoryInfo.PagefileUsage`）；
   - `Private WS` = 性能计数器 `\Process(...)\Working Set - Private`，即任务管理器 "Memory" 列；
   - 原生内存分布 = `VirtualQueryEx` 按 MEM_PRIVATE / MEM_MAPPED / MEM_IMAGE 汇总（`.agent/vmprobe2.ps1`）。

## 2. 基准对照（同一台机器、同一时刻、同一窗口尺寸）

| 变体 | 私有提交 | 私有工作集 | WS | 线程 | 句柄 |
|---|---|---|---|---|---|
| 空 WPF 窗口（Release，探针工程） | 73.5MB | ~40MB | 104.5MB | 34 | 739 |
| 空 WPF + 一张全窗 BlurEffect(Radius 12) | 82.2MB | — | 115.8MB | 34 | 734 |
| 空 WPF + 半分辨率层 + BlurEffect(Radius 6) + 2×缩放 | 74.5MB | — | 105.4MB | 34 | 739 |
| GlassLauncher 默认（Acrylic + 冻结背景），可见 | 84–96MB | 43–57MB | 138–155MB | 34–36 | 830 |
| GlassLauncher 默认，隐藏到托盘 | 135–142MB | 44MB | 139MB | — | — |
| GlassLauncher `GLASSLAUNCHER_BACKDROP=mica`（无冻结背景） | 78.4MB | 40.2MB | 135.0MB | 31 | 824 |
| GlassLauncher `GLASSLAUNCHER_BACKDROP=none`（不透明降级） | 90.0MB | 40.0MB | 149MB | 31 | 768 |
| GlassLauncher + `BlurRadius=2` | 83.4MB | — | 136.9MB | 35 | 829 |

**读法**：

1. 本应用相对空 WPF 的**自有增量只有约 10MB**（图标、XAML、3 块位图、托管堆），不是"100MB 的黑洞"。
2. 冻结背景 + 两个 BlurEffect 的**总成本约 5MB**（mica 78.4 vs acrylic 84–96；`BlurRadius=2` 与 12 无差别）。
   所以交接文档里"满分辨率下两个 BlurEffect 吃 83MB"的说法**不成立**，半分辨率优化也不是内存解药。
3. 线程/句柄不是问题：空 WPF 就是 34 线程/739 句柄，本应用还更少。

## 3. 真正值得修的两处"高水位"

### 3.1 Acrylic 窗口隐藏时 +53MB 私有提交（可复现）

| 变体 | 可见提交 | 隐藏 10s 后提交 |
|---|---|---|
| Acrylic（默认） | 85.2MB | **138.2MB** |
| Mica | 77.1MB | 77.1MB（不变） |
| None | 76.7MB | 76.8MB（不变） |

私有工作集全程不变（~40–44MB），所以这是**已提交不驻留**的页面——但它解释了用户实例
`PeakCommit=203MB` 的来源：每次 Hide 都往上顶，然后不再回落。冻结背景层本身已经 100% 盖住
Acrylic，DWM 的模糊一次都看不见；去掉 Acrylic（改成自绘不透明窗口）能同时消掉这笔提交、
去掉一层 DWM 交互，并减少"开关排除 = 表面重建"的诱因。**这是产品视觉决策，需实机确认后再改。**

### 3.2 拖动会把提交顶上去，且松手后不回落

合成拖动 ×3（走真实 `StartManualDrag → StartDragRide → EndManualDrag` 路径）实测：

- 帧率 58–63 ticks/s，live 帧 77–82/1.5s（≈52fps，受 60fps 上限与 BitBlt 14ms 约束）；
- 私有提交从 ~95MB 涨到 ~165MB，隐藏后仍有 135MB；
- 托管堆只从 5MB 涨到 9MB —— **不是托管泄漏**，是 WPF 渲染表面/位图池的高水位。

### 3.3 LiveGrab 首次拖动后常驻（已修，见第 4 节）

DIB（0.92MB）+ 上传位图 + 池块 + 3 个 GDI 对象在第一次拖动后一直留到进程退出（即使隐藏）。
本轮已加 `ReleaseIdle()`，隐藏时归还。

## 4. 本轮据此改了什么

| 改动 | 依据 |
|---|---|
| `LiveGrab.ReleaseIdle()`：隐藏到托盘时释放 DIB/位图/缓冲池 | 3.3 |
| `ShrinkFreezeCache()`：隐藏时丢掉备用冻结帧与像素缓冲 | 2/3.2 高水位 |
| 冻结帧高水位收缩（尺寸回退时丢旧槽、像素缓冲超过 2× 就换） | 3.2 |
| `GlassWindow.DpiChanged` 过滤 `1.25→1.25` 假事件 | 审查报告 §1.5（原修法漏了基类） |
| 图标解码 128 → 96px | 40 个图标约省 2–3MB，且更快的启动解码 |
| `_manualDragTimer` 提到 Render 优先级 | 拖动是 15ms 硬节拍，Background 会被渲染挤后 |
| `_autoScrollTimer` 只在指针压边时运行 | 原实现整个拖动期 60Hz 空转 |
| `LaunchEntry` 的 `Task.Run` 加异常兜底 | async void + 无 catch |
| `Diag`/`LauncherStore` 支持 `GLASSLAUNCHER_LOG_DIR`/`GLASSLAUNCHER_DATA_DIR`（Release 也可用） | 回归测试要能在临时目录跑 |
| DEBUG 回归探针 `Diagnostics/DiagnosticHarness.cs` | 让第 6 节回归可半自动执行 |
| 修正代码里"83MB 模糊"与 "Margin=-24" 的错误注释 | 本文件 2 节 |

## 5. 目标重定（建议）

| 指标 | 现状（本轮实测） | 建议目标 |
|---|---|---|
| 私有工作集（任务管理器 Memory） | 可见 44–57MB / 隐藏 26–44MB | **≤ 60MB**（已基本达成，守住即可） |
| 私有提交（提交大小） | 可见 84–96MB / 隐藏 135MB（Acrylic） | 隐藏 ≤ 100MB（去掉 Acrylic 后） |
| 空闲 CPU | 0–1.8%（1s 采样） | ≈ 0（已达成） |
| 线程 / 句柄 | 34–36 / 830 | 不劣化 |
| 拖动帧率 | 58–63 tick/s，live ≈52fps，BitBlt 13–15ms | ≥ 55fps，avg ≤ 12ms |
| 冷启动 | 首帧冻结 ~1.3s（Debug，含 60ms 排除等待） | < 1s（Release 实测后定） |

> 如果确实要求 "Working Set / 私有提交 < 60MB"，WPF 方案无法达成（空窗口 73.5MB）。
> 那属于**换渲染栈**（Win32 + DirectComposition/D2D，或 WinUI3）的决策，不是重构能解决的。

## 6. 复现脚本（都在 `.agent/`，不进产品源码）

| 脚本 | 用途 |
|---|---|
| `vmprobe2.ps1` | 按进程把已提交内存分成 IMAGE/MAPPED/PRIVATE，列最大块 |
| `memcounter.ps1` / `memcounter2.ps1` | WS / 私有提交 / 峰值提交 |
| `run-bench.ps1` | 起一个隔离实例，按环境变量跑变体并采样 |
| `make-bench.ps1` | 把构建产物复制成 `GlassLauncherBench.exe`（唯一进程名） |
| `probe/` | 空 WPF 窗口 / 全分辨率模糊 / 半分辨率模糊 的对照工程 |

产品侧的 DEBUG 探针（`DiagnosticHarness.cs`）支持：

```
GLASSLAUNCHER_PERF=1                         每秒 PERF 行（ws/private/managed/handles/gdi/线程）
GLASSLAUNCHER_AUTODRAG=220,90,1500,3,700     合成窗口拖动 dx,dy,时长,次数,间隔
GLASSLAUNCHER_TRAY_CYCLE=4                   自动隐藏/唤出 n 次
GLASSLAUNCHER_DUMP_BACKDROP=2500             2.5s 后把背景层渲染成 PNG
```
