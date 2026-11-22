# GlassLauncher 审查交接简报（给评审 AI）

> ⚠️ **可信度声明（最高优先级，务必先读）** ⚠️
> 本简报及配套的 DEVLOG.md / REFACTOR-CHARTER.md 均由**前一轮开发 AI** 撰写。
> 其对故障模型、根因分析、资源占用诊断的判断**未经独立验证，已被证实存在多处
> 错误**（见下方修订表与 `REVIEW-FINDINGS.md`——例如"live 帧像素级重合"的实际
> 真相是像素根本没写进位图）。请把这三份文档当**线索和参考**，不要当事实：
> - 所有"实测确认"的说法，请用代码 + 日志 + 复现自行验证后再采信；
> - "设计不变量"仅代表上一轮 AI 的自我辩护，可能存在把 bug 当设计、把猜测
>   当实测的情况；
> - 资源占用的数字与归因需要你自己重新测量与定位；
> - **与本文冲突时，一律以你自己的代码审查结论为准。**
>
> 目的：请另一个 AI 审查本项目代码，找出潜在 bug、竞态、设计缺陷。
> 项目根目录：`C:/Users/fjx/WorkBuddy/2026-11-18-22-37-41/GlassLauncher`

---

## 0. 修订说明（原版哪些说法是错的）

| 原版说法 | 修订 |
|---|---|
| §2/§4.1「排除永久常开，全代码严禁随手开关它」 | 代码里**仍有 5 处开关**（`RebuildDwmSurface` 双向切换、`StartDragRide` 补开、模态循环进出各一次、`FreezeBackdrop` 补开）。"零切换"只是目标，不是现状 |
| §4.3「静态帧与 live 帧像素级重合 → 松手零闪烁」 | 不成立。当时 live 帧有三处问题：① **像素根本没写进 WriteableBitmap**（拖动时背景是空位图 = 黑底 + 白纱 = 一片灰）；② 抓取尺寸按 32px 桶放大后被 `Stretch=Fill` 压缩，错位最多 14px；③ 位移预测符号与对齐公式相反 |
| §5.1「首要竞态是 EndManualDrag 之后在途的 FrameReady」 | 那条已有 `_liveGrabActive` 兜底。真正的坑是托盘唤出时 `RebuildDwmSurface` 刚切完排除就抓帧（`_exclusionOnTick` 未重置 → 跳过等待 → 抓到自己 = 鬼影帧） |
| §6「用 `permanent exclusion held=` 确认跑的是新代码」 | 这行正好打在 4cbd772 那版**启动崩溃前 214ms**，是误导性标记。现在真正的健康标记是 `freeze ok WxH`（冻结帧抓取成功） |
| §2「每次开关排除都会重建 DWM 表面」 | 成立。所以托盘唤出时那两次切换就是两次整窗闪（`dwm surface rebuilt` 日志出现过 8 次） |
| §7「5 个提交，HEAD=01ada9a」 | 修订时已 6 个提交，HEAD=`3a710e3` |

---

## 1. 项目是什么

WPF（.NET 8）仿 macOS 液态玻璃启动器，Windows 10/11。核心难点全部集中在
**背景视觉管线与拖动**：系统 Acrylic 有失焦变灰板、模糊浓度不可调等缺陷，所以
自建了"抓屏 → 高斯模糊 → 覆盖层"的背景方案，截屏排除（SetWindowDisplayAffinity
WDA_EXCLUDEFROMCAPTURE）保证抓到的是"被本窗口盖住的那片桌面"而不是窗口自己。

## 2. 测试机关键事实（评审时不能当巧合忽略）

- Windows Insider **build 26300**，DPI 1.25。以下故障是实测确认的，不是推测：
  1. **排除开启 + 模态移动循环（WM_ENTERSIZEMOVE）= DWM 停止呈现整个窗口**
     （一拖整个消失，松手才回来）。普通标题拖动已用非模态方案绕开；
     窗口边缘缩放仍走模态循环，那条路是"进循环临时关排除、出循环再开"。
  2. **每次开关排除都会强制 DWM 重建窗口表面**，视觉 = 整窗闪一下。
     所以排除**目标上**是常开（启动设一次）；但见 §4.1，代码里仍有几处例外。
  3. **托盘 Hide()/Show() 会把 DWM 表面留在脏状态**，脏表面+排除+模态拖动
     稳定触发故障 1。现有缓解：唤出期间 RebuildDwmSurface()（开关一次
     排除洗表面）——代价是两次闪，且会污染排除的生效计时（见 §5.1）。
  4. 排除刚打开时 DWM 要 1–2 个合成帧才真正落实（ExclusionSettleMs = 60ms），
     不到时间就抓屏会把窗口自己抓进背景（鬼影帧）。
- **教训（2026-11-22）**：本项目历史上多次把"程序看起来在跑"当成"代码生效"。
  确认方法必须看 `freeze ok` 这样的**功能完成**日志，而不是启动横幅。

## 3. 核心文件与职责

```
src/GlassLauncher/
├─ MainWindow.xaml.cs        ★ 核心（~2900 行，含 ~1300 行 #if DEBUG 探针），重点审查
│   · WndProc：拦截 WM_NCLBUTTONDOWN/HTCAPTION → StartManualDrag（仅 Normal 态）
│               WM_NCLBUTTONDBLCLK → 先收掉手动拖动再交给系统最大化
│   · StartManualDrag/ManualDragTick/EndManualDrag：非模态拖动，
│     15ms 轮询 GetCursorPos + SetWindowPos(SWP_NOACTIVATE)
│   · AbortWindowDrag：隐藏/关窗/状态变化/双击标题时收掉手动拖动
│   · StartDragRide/WndProc_DragStarted/WndProc_DragEnded：live 开始/
│     模态路径收尾（缩放边缘才走模态）
│   · OnLiveFrame/OnLiveFrameReady：每帧几何提交（UI 线程）→ 后台抓帧 → 回 UI 上屏
│   · FreezeBackdrop(bool instant)：静止时抓屏锚定；instant=直接换帧（松手专用）
│   · ShowFrozenFrame/CommitFrozenFrame：交叉淡入 180ms（过时帧场景用）
│   · EnsureExclusion：排除状态的唯一入口（实查 GetExclusion，不信缓存）
│   · RebuildDwmSurface：开关一次排除强制 DWM 重建表面（并重置生效计时）
├─ Chrome/LiveGrab.cs        ★ 抓帧泵：BitBlt 在工作线程，Submit()/FrameReady 事件
│                              （GDI DC 有线程亲和，DC 全部在工作线程创建/销毁；
│                               像素经缓冲池拷到 UI 线程 WritePixels）
├─ Chrome/ScreenGrab.cs      TrySetExclusion/GetExclusion/Capture（BitBlt 封装）
├─ Chrome/BackdropService.cs / GlassWindow.cs / GlassOptics.cs / OpticalGlassSurface.cs
│                             玻璃视觉栈（Acrylic/Mica/Optical 模式）
├─ Chrome/TrayIcon.cs        托盘（Hide/Show 循环与脏表面问题相关）
├─ Chrome/Diag.cs            诊断日志（发布版也写，1MB 轮转；GLASSLAUNCHER_LOG=0 关闭）
├─ Services/DragVisuals.cs   拖动期视觉（行车记录仪采样器等）
├─ Services/HotkeyHelper.cs / AutostartService.cs / SettingsStore.cs 等
MainWindow.xaml              FrozenSurface（底层）+ FrozenSurfaceAlt（淡入层）
                             + FrozenShift（TranslateTransform 位移对齐）
```

## 4. 关键设计决策与不变量（评审时别当 bug 报）

1. **排除目标上是常开**。代价：用户自己的截图/录屏里没有本窗口（启动器可接受）。
   **例外（截至本次修订仍存在）**：
   - `RebuildDwmSurface()`：托盘每次唤出开关一次（两次闪），目的是洗掉 Hide/Show 的脏表面；
   - 模态循环（缩放/最大化拖动）：进出各切一次；
   - 上面两处都可能唤醒"重新打开排除"，所以必须有生效等待（60ms）。
   任何新增的开关都要走 `EnsureExclusion()`，它会同步 `_exclusionHeld` / `_exclusionOnTick`。
2. **标题拖动必须非模态**。改回 DoDragDrop/DefWndProc 会让消失 bug 复发
   （在这台 build 上）。双击标题最大化依赖"未拦截时的默认处理"，但进入双击前要先
   `AbortWindowDrag()`，否则手动拖动带着最大化状态继续搬窗口。
3. **松手（EndManualDrag）路径是"精确对齐 + 原子换帧"**：
   - 拖动中每帧设置 `FrozenShift = (O_capture − W_now)/dpi`（**不是**上一帧位移量）；
   - 松手时窗口已停，用真实终点重算最后一帧偏移（同一个公式）；
   - 静态帧（shift 恒 0）抓好后直接换 Source，不走淡入——因为此刻底下 live 帧
     与新帧像素级重合，交叉淡入反而叠出错位重影；
   - 对应地，**底下是过时帧的场景**（托盘唤出、尺寸变化、模态缩放松手）仍走淡入。
4. **live 帧路径绕过淡入层**：OnLiveFrameReady 直接写 FrozenSurface.Source、
   清 alt 层。淡入层只服务静止锚定。
5. **_captureGeneration 作废机制**：在途抓帧（await/BitBlt 中）若拖动开始，
   结果作废，否则一帧过时画面会在拖动开始瞬间淡入。
6. **_exclusionOnTick 补足等待**：GetExclusion 只反映开关状态，不反映 DWM 是否
   已落实；刚开就抓 = 自身鬼影。**任何切换排除的地方都必须重置它。**
7. **Win32 P/Invoke 命名陷阱**：WPF 已有 ReleaseCapture，自声明必须改名
   （ReleaseWin32Capture）并配 `EntryPoint = "ReleaseCapture"`，否则
   EntryPointNotFoundException 且进程 exit 0 假死。
8. **`Dispatcher.BeginInvoke` 不能直接收带可选参数的方法组**：
   `BeginInvoke(Delegate, …)` 走 `Delegate.DynamicInvoke`，反射调用不认可选参数，
   会抛 `TargetParameterCountException`。必须写成 `(Action)(() => M())`。
   曾经因此让程序启动 0.3 秒后"无响应→消失"。

## 5. 已知问题 / 请重点审查的方向

按怀疑度排序（✅ = 2026-11-22 已修，🔶 = 仍待验证/待决策）：

1. ✅ **崩溃类**：`BeginInvoke(FreezeBackdrop)` 的可选参数导致启动即抛
   `TargetParameterCountException`；`App.DispatcherUnhandledException` 又不设
   `e.Handled`，把异常放大成"未响应 + 进程消失"。两处都已修。
2. ✅ **live 帧像素从未上传**（ab4e894 重构时漏掉 `WritePixels`）：
   拖动时背景是空位图，显示成一片灰。已修（缓冲池 + UI 线程 WritePixels）。
3. ✅ **几何**：抓取尺寸按 32px 桶放大 → 被 Stretch=Fill 压缩 → 与桌面错位；
   位移预测符号与对齐公式相反（多了 2 帧滞后，松手时再跳回来）。都已修。
4. ✅ **排除计时**：`RebuildDwmSurface` 不重置 `_exclusionOnTick` → 托盘唤出后
   第一帧可能抓到自己（鬼影）。已修。
5. 🔶 **模态缩放路径**：进出循环各切一次排除 = 两次闪。想彻底消除需要把边缘缩放
   也改成非模态（拦截 WM_NCHITTEST + 自己做 SetWindowPos 改尺寸），还没做。
6. 🔶 **托盘 Hide/Show + RebuildDwmSurface**：永久排除后它是否还必要？
   需要做 A/B（有/无 RebuildDwmSurface 各走 20 次托盘循环 + 模态缩放）。
7. 🔶 **WriteableBitmap 复用**：live 路径已复用；静态路径（FreezeBackdrop）仍每帧
   新建 `byte[]` + `WriteableBitmap`（4MB 量级），值得池化。
8. 🔶 **DPI 变化**：拖动中切到另一块不同缩放的显示器时，`PointToScreen`/抓屏几何
   是否一致还没实测（1.25 单屏机器上只观察到大量 "1.25→1.25" 的假事件，已过滤）。
9. 🔶 **EnsureOnScreen** 已改成用物理像素 `SetWindowPos`（避免跨屏 DPI 换算误差），
   多显示器场景仍需实机回归。
10. 🔶 **托盘菜单 / 热键 / 自启 / 设置存储**：常规 WPF 单例/资源释放问题。
    热键数字键往返、裸键校验已修，仍建议补一组 `Format↔TryParse` 单测。
11. 🔶 **WndProc 拦截的边界**：最大化态拖动、Aero Snap（手动拖动天然没有贴边吸附）、
    Alt+Tab 期间的显示亲和行为。

## 6. 如何构建 / 运行 / 验证

```bash
cd GlassLauncher/src/GlassLauncher
dotnet build -c Debug        # 用户日常跑 Debug exe
dotnet build -c Release
```

- 运行日志：`%LOCALAPPDATA%/GlassLauncher/glass.log`（发布版也写，1MB 轮转到 `glass.log.1`；
  `GLASSLAUNCHER_LOG=0` 可关闭）
- 关键日志标记（按可靠性排序，**以后者为准**）：
  - `permanent exclusion held=True` —— 启动横幅，**不代表后续流程活着**；
  - `freeze ok WxH instant=… settle=…` —— 冻结帧真的抓到了（健康检查看这个）；
  - `dwm surface rebuilt toggle=… restore=… held=…` —— 托盘唤出洗表面；
  - `Live drag frames=… avg=…` / `drag end align …` —— 拖动帧率与松手对齐；
  - `freeze aborted` / `freeze 失败` / `DispatcherUnhandledException` —— 异常路径。
- 复现脚本：workspace 下 `.tmp/`（tray-test2.py / instant-drag.py /
  drag-repro2.py / run-shot.py / fg.py），可模拟拖动/截屏取证。
- **注意**：验证拖动时不能只截屏——本窗口开着截屏排除，截图里看不到它。
  需要用日志（`freeze ok` / `live upload` 之类的探针）确认像素真的上了屏。

## 7. Git 状态

- 本地 main，修订时 6 个提交，HEAD = `3a710e3`；随后叠加了本次审查修复（未提交）。
- remote = `git@github.com:aminoc4/GlassLauncher.git`（SSH 密钥，未推送）。
- 回归测试务必走完整链路：托盘循环 → 拖动 → 缩放 → 松手 → 再拖动。

## 8. 评审产出建议

- 按严重度分级：崩溃/数据丢失 > 视觉回归（消失/闪烁/鬼影） > 性能 > 代码质量。
- 对每条发现给出：文件+方法名、触发条件、复现步骤、建议修法。
- 不确定是否是"有意为之"的设计，请先对照第 4 节不变量，仍不确定就标注提问，
  不要直接改。
- 不要把 REVIEW-BRIEF 当事实依据：先看 `REVIEW-FINDINGS.md` 的验证记录。
