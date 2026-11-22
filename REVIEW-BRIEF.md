# GlassLauncher 审查交接简报（给评审 AI）

> 目的：请另一个 AI 审查本项目代码，找出潜在 bug、竞态、设计缺陷。
> 本文档提供必要的背景，避免评审走弯路。**请先读完本文再看代码。**
> 项目根目录：`C:/Users/fjx/WorkBuddy/2026-11-18-22-37-41/GlassLauncher`

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
     因此排除现在是**永久常开**（启动设一次），全代码严禁随手开关它。
  3. **托盘 Hide()/Show() 会把 DWM 表面留在脏状态**，脏表面+排除+模态拖动
     稳定触发故障 1。现有缓解：唤出淡入期间 RebuildDwmSurface()（开关一次
     排除洗表面）。
- 排除刚打开时 DWM 要 1–2 个合成帧才真正落实（ExclusionSettleMs = 60ms），
  不到时间就抓屏会把窗口自己抓进背景（鬼影帧）。

## 3. 核心文件与职责

```
src/GlassLauncher/
├─ MainWindow.xaml.cs        ★ 核心（~1400 行），重点审查
│   · WndProc：拦截 WM_NCLBUTTONDOWN/HTCAPTION → StartManualDrag（仅 Normal 态）
│   · StartManualDrag/ManualDragTick/EndManualDrag：非模态拖动，
│     15ms 轮询 GetCursorPos + SetWindowPos(SWP_NOACTIVATE)
│   · StartDragRide/WndProc_DragStarted/WndProc_DragEnded：live 开始/
│     模态路径收尾（缩放边缘才走模态）
│   · OnLiveFrame/OnLiveFrameReady：每帧几何提交（UI 线程）→ 后台抓帧 → 回 UI 上屏
│   · FreezeBackdrop(bool instant)：静止时抓屏锚定；instant=直接换帧（松手专用）
│   · ShowFrozenFrame/CommitFrozenFrame：交叉淡入 180ms（过时帧场景用）
│   · RebuildDwmSurface：开关一次排除强制 DWM 重建表面
├─ Chrome/LiveGrab.cs        ★ 抓帧泵：BitBlt 在工作线程，Submit()/FrameReady 事件
│                              （GDI DC 有线程亲和，DC 全部在工作线程创建/销毁）
├─ Chrome/ScreenGrab.cs      TrySetExclusion/GetExclusion/Capture（BitBlt 封装）
├─ Chrome/BackdropService.cs / GlassWindow.cs / GlassOptics.cs / OpticalGlassSurface.cs
│                             玻璃视觉栈（Acrylic/Mica/Optical 模式）
├─ Chrome/TrayIcon.cs        托盘（Hide/Show 循环与脏表面问题相关）
├─ Services/DragVisuals.cs   拖动期视觉（行车记录仪采样器等）
├─ Services/HotkeyHelper.cs / AutostartService.cs / SettingsStore.cs 等
MainWindow.xaml              FrozenSurface（底层）+ FrozenSurfaceAlt（淡入层）
                             + FrozenShift（TranslateTransform 位移预测）
```

## 4. 关键设计决策与不变量（评审时别当 bug 报）

1. **排除永久常开**。代价：用户自己的截图/录屏里没有本窗口（启动器可接受）。
   任何"抓完顺手关掉"的改动都会重新引入闪烁。
2. **标题拖动必须非模态**。改回 DoDragDrop/DefWndProc 会让消失 bug 复发
   （在这台 build 上）。双击标题最大化等行为依赖"未拦截时的默认处理"。
3. **松手（EndManualDrag）路径是"精确对齐 + 原子换帧"**：
   - 拖动中每帧设置 FrozenShift = (O_n − O_{n−1})/dpi（位移预测，抵消抓屏滞后）；
   - 松手时窗口已停，用真实终点重算最后一帧偏移（对齐条件 s = (O_capture − W_window)/dpi）；
   - 静态帧（shift 恒 0）抓好后直接换 Source，不走淡入——因为此刻底下 live 帧
     与新帧像素级重合，交叉淡入反而叠出错位重影（这正是上一版"松手失焦闪一下"的根因）；
   - 对应地，**底下是过时帧的场景**（托盘唤出、尺寸变化、模态缩放松手）仍走淡入。
4. **live 帧路径绕过淡入层**：OnLiveFrameReady 直接写 FrozenSurface.Source、
   清 alt 层。淡入层只服务静止锚定。
5. **_captureGeneration 作废机制**：在途抓帧（await/BitBlt 中）若拖动开始，
   结果作废，否则一帧过时画面会在拖动开始瞬间淡入。
6. **_exclusionOnTick 补足等待**：GetExclusion 只反映开关状态，不反映 DWM 是否
   已落实；刚开就抓 = 自身鬼影。
7. **Win32 P/Invoke 命名陷阱**：WPF 已有 ReleaseCapture，自声明必须改名
   （ReleaseWin32Capture）并配 EntryPoint，否则 EntryPointNotFoundException
   且进程 exit 0 假死。

## 5. 已知问题 / 请重点审查的方向

按怀疑度排序：

1. **竞态**：_freezeBusy/_freezeQueued/_freezeQueuedInstant、_liveGrabActive、
   _captureGeneration、FrameReady 回调（Dispatcher 排队）与拖动开始/结束交错。
   特别是：EndManualDrag 之后仍有在途 FrameReady 回调到达时的处理路径。
2. **位移预测数学**：FrozenShift 在帧上屏时刻与窗口当前位置的关系推导
   （见上"s = (O_capture − W)/dpi"）。拖动起步第一帧、停顿后起步、
   快速甩动再急停时的对齐是否成立？_livePrevOrigin 为 null 的首帧？
3. **模态缩放路径**：WndProc_DragStarted 关排除 → 出循环开排除 → FreezeBackdrop
   （淡入）。这条路上的闪烁是否还有更好的绕法？排除开关本身就会闪，
   进出循环各一次 = 两次闪烁，现状是否可接受/可改善？
4. **LiveGrab 线程安全**：DC 生命周期、AutoResetEvent 背压、Submit 在 ResetWarmup
   竞争时丢帧行为、LastGrabMs 的可见性（是否需要 volatile/lock）。
5. **WriteableBitmap 每帧新建**（FreezeBackdrop 与 LiveGrab 路径）——GC/显存压力？
   是否值得池化？
6. **托盘 Hide/Show + RebuildDwmSurface**：永久排除后 RebuildDwmSurface 还必要吗？
   HideToTray 的淡出动画与排除的交互？
7. **DPI 变化**（实测日志里拖动中会收到 DpiChanged）：拖动中 DPI 切换时
   PointToScreen/GetDpi/抓屏几何是否一致？
8. **EnsureOnScreen（60% 可见阈值）** 与多显示器/任务栏遮挡场景。
9. **托盘菜单、热键、自启、设置存储**（Services/）——常规 WPF 单例/资源释放问题。
10. **WndProc 拦截的边界**：WM_NCLBUTTONDOWN 拦截条件（WindowState == Normal），
    最大化态拖动、Aero Snap、Alt+Tab 期间的显示亲和行为。

## 6. 如何构建 / 运行 / 验证

```bash
cd GlassLauncher/src/GlassLauncher
dotnet build -c Debug        # 用户日常跑 Debug exe
dotnet build -c Release
```

- 运行日志：`%LOCALAPPDATA%/GlassLauncher/glass.log`
- 关键日志标记：`permanent exclusion held=`（启动，确认跑的是新代码——
  **历史教训：曾因源码编译不过而误判"修复无效"，先看这个标记再下结论**）、
  `dwm surface rebuilt`、`Live drag frames=… avg=…`、`drag end align …`
- 复现脚本：workspace 下 `.tmp/`（tray-test2.py / instant-drag.py /
  drag-repro2.py / run-shot.py / fg.py），可模拟拖动/截屏取证。

## 7. Git 状态

- 本地 main，5 个提交，remote = `git@github.com:aminoc4/GlassLauncher.git`
  （SSH 密钥，未推送）。HEAD = `01ada9a`。
- 历史教训：v0.1 无托盘 Hide/Show 时从不出"消失"问题，回归测试务必走一遍
  托盘循环 → 拖动 → 缩放 → 松手 → 再拖动 的完整链路。

## 8. 评审产出建议

- 按严重度分级：崩溃/数据丢失 > 视觉回归（消失/闪烁/鬼影） > 性能 > 代码质量。
- 对每条发现给出：文件+方法名、触发条件、复现步骤、建议修法。
- 不确定是否是"有意为之"的设计，请先对照第 4 节不变量，仍不确定就标注提问，
  不要直接改。
