# GlassLauncher 原生重写（C++ / Win32 + Direct2D）· 进展记录

> 决策背景：WPF 版即使空窗口也要 **私有工作集 ~40MB / 提交 73.5MB / WS 104MB**（本机实测），
> "功能就这么一点却 44MB"的根源是 UI 栈，不是业务代码。继续在 WPF 里重构最多省 10MB，
> 所以本轮改为原生重写。WPF 版保留在 `src/GlassLauncher/` 作为参考实现和回退版本。

## 1. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言 | C++17 | 目标机器已有 w64devkit（GCC 16.2，含 d2d1/dwrite/dcomp/dwmapi/wincodec 头与库），COM 原生可用；Rust 也验证过可编译，但 crates.io 不通、没有 windows crate，D2D/COM 全要手写 FFI，开发量 2–3 倍且内存不会更小 |
| 渲染 | Direct2D (HwndRenderTarget) + DirectWrite | 与 WPF 相同的 GPU 管线，但无托管/UI 框架底座 |
| 背景 | 抓屏 → 半分辨率 → 3 次盒式模糊 → D2D 位图 | 保留"液态玻璃=真实桌面模糊"的视觉 DNA；不依赖系统 Acrylic（浓度/失焦不可控） |
| 数据 | 与 WPF 版**同一份** `launcher-data.json` / `settings.json` | 零迁移；图标缓存目录也复用 |
| 构建 | `build.ps1` + g++（静态链接，无外部依赖） | 单命令出包；产物 ~350KB |

## 2. 已实测（本机：build 26300 / DPI 1.25 / 40 条目）

| 指标 | WPF 版（Debug） | 原生版（M1） |
|---|---|---|
| 私有工作集（任务管理器 Memory，可见） | 44–57MB | **25.1MB** |
| 私有工作集（隐藏到托盘） | 26–44MB | **25.1MB** |
| 私有提交 / 提交大小 | 84–96MB（隐藏态 135MB） | **83.4MB**（隐藏态不变） |
| Working Set | 138–155MB | **67–73MB** |
| 线程 / 句柄 | 34–36 / 830 | **25 / 522** |
| 空闲 CPU | 0–1.8% | **0.00%** |
| 启动到窗口就绪 | ~1.3s（Debug，WPF） | **328–359ms** |
| 可执行文件 | 自包含 ~70MB | **350KB**（+ .NET 运行时无） |

> 原生版的 25MB 里：D2D/D3D 设备与交换链约 10MB、40 个图标位图约 5MB、背景位图约 1MB、
> DirectWrite/字体约 2MB、剩余为 CRT/窗口/堆。下一步可以做到 ~20MB（图标按显示尺寸解码、
> 上传后释放 CPU 侧像素缓冲、按需创建背景）。

## 3. 代码结构

```
src/GlassLauncher.Native/
├─ build.ps1                    # g++ 一把梭（输出到 .agent/native）
└─ src/
   ├─ main.cpp                  # wWinMain：DPI、COM、单实例
   ├─ app.h / app.cpp           # 窗口、数据、交互、托盘/热键、绘制编排、日志
   ├─ renderer.h / renderer.cpp # D2D/DWrite/WIC 封装（图元、文字、位图）
   ├─ backdrop.h / backdrop.cpp # 抓屏 + 半分辨率 + 盒式模糊 + D2D 位图
   ├─ store.h / store.cpp       # AppEntry/Settings 与 JSON 读写（兼容 WPF 版）
   └─ json.h                    # 极简 JSON parser/writer（无第三方依赖）
```

### 关键实现注意（踩过的坑）

1. **不要给 `ID2D1HwndRenderTarget` 调 `SetDpi`**：本机（build 26300）上设置后 `EndDraw` 返回 S_OK
   但画面完全不呈现（黑窗）。改用默认 DPI + Identity 变换即正常；默认 DPI 就是桌面 DPI（120）。
2. `WS_POPUP | WS_THICKFRAME` + `WM_NCCALCSIZE` 里把 `rgrc[0]` 设为窗口矩形，客户区才能铺满
   整个窗口（只 `return 0` 会保留 8px 边框内缩）。圆角用 `SetWindowRgn`（16 DIP）。
3. HwndRenderTarget 在**窗口已显示**后创建最稳；背景抓屏要在此之前完成（此时桌面干净、不含自己）。
4. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 在 show 之后设置；设置后本进程的
   BitBlt 抓屏也不会包含自己（这正是抓背景需要的）。
5. 调试开关：`GL_NATIVE_NO_EXCLUSION=1`（截图取证）、`GL_NATIVE_PERF=1`、`GL_NATIVE_DUMP_BACKDROP=path.bmp`、
   `GL_NATIVE_DEBUG_RED=1`、`GL_NATIVE_DEBUG_GDI=1`。

## 4. M1 已实现

- 无边框窗口（840×560 DIP，Per-Monitor V2 DPI，圆角，居中），单实例互斥；
- 背景：干净桌面抓取 → 585×410 → 3×盒式模糊 → 全窗覆盖（窗口外扩 48 DIP）；
- 数据：读取 WPF 版 `launcher-data.json`（40 条实测）、`settings.json`（主题/模糊半径/置顶/热键）；
- 图标：PNG 缓存（WIC→D2D），缺失时 `ExtractIconEx` 提取；40/40 加载成功；
- 视觉：白纱 + 内容面板 + 搜索胶囊 + "全部"药丸 + 68×68 图标底板 + 56×56 图标 + 名字省略号 +
  窗口描边/顶部高光（与 WPF 版同一套色值/尺寸）；
- 交互：标题区**非模态拖动**（定时器 + SetWindowPos，无系统模态循环）、图标悬停、点击启动
  （ShellExecute）、滚轮滚动、Esc 隐藏、关闭=隐藏到托盘；
- 外壳：托盘图标 + 右键菜单（打开/隐藏、设置(占位)、退出）、全局热键（Alt+G）、置顶、启动日志；
- 截屏排除（用户截图里不出现本窗口）。

## 5. 已知缺口（下一阶段）

| 优先级 | 缺口 |
|---|---|
| P0 | 沙箱里 `Shell_NotifyIcon` 失败（低完整性进程），托盘/热键只能由用户正常启动验证；热键冲突（WPF 实例占着 Alt+G） |
| P0 | 拖动中实时背景目前在 UI 线程同步抓屏+模糊（~15–20ms/帧，约 50fps），窗口移动节拍仍只有 ~100Hz；下一步改成 worker 线程 + 双缓冲，UI 只做 CopyFromMemory |
| P1 | 窗口边缘缩放、Aero Snap、双击标题最大化 |
| P1 | 右键菜单的"重命名 / 重新指定目标"（目前灰色占位）；设置窗口是标准 Win32 控件（没有玻璃皮肤） |
| P2 | 入场/退场动画、图标悬停放大动画、托盘图标资源（当前用系统默认图标）、暗色主题下设置窗口仍是浅色 |

## 6. M2 进展（本轮完成）

- **搜索**：子窗口 EDIT（IME/剪贴板/选中都是系统行为）+ `EM_SETCUEBANNER` 占位符；
  输入即过滤（大小写不敏感）、无匹配时显示"没有匹配的应用"、回车启动第一个匹配、Esc 清空/隐藏；
- **右键菜单**：打开 / 以管理员身份运行（ShellExecuteEx runas）/ 在资源管理器中打开 / 移除；
  改名与重定向留了菜单项（灰色）待 M3；
- **拖拽排序**：按住图标移动超过 6 DIP 进入拖拽，浮起副本跟随光标，松手按落点重排并原子写回 JSON
  （过滤状态下禁用排序，避免容器下标与主列表错位）；已实测"moved entry 0 -> 2"且落盘顺序正确；
- **外部拖入**：`DragAcceptFiles` + `WM_DROPFILES`；.lnk 走 IShellLink 解析、.url 读
  `InternetShortcut/URL`、目录/exe 原样；自动去重、按落点插入、立即保存；实测 41 → 42 条；
- **失效检查**：启动时 `GetFileAttributesW` 全量检查（HTTP URL 跳过），失效条目画橙色
  "!" 角标；实测 `broken entries=1`；
- **设置窗口**：主题（跟随/浅/深）、模糊半径、置顶、拖动降模糊、开机自启（HKCU Run）、
  启动检查失效、全局热键、打开数据目录、清除图标缓存；模态 + 保存 + 立即应用（实测 OK）；
- **齿轮按钮**：右上角设置入口（`WM_NCHITTEST` 里先把齿轮判成客户区，否则永远点不到）；
- **实时背景**：拖动开始后每 ~16ms 抓一帧（`StretchBlt COLORONCOLOR` + 3×盒式模糊 +
  `CopyFromMemory` 复用位图），窗口移动时桌面是流动的；实测 liveFrames≈50fps、单帧 ~15–20ms；
- 窗口缩放/DPI 变化时重抓背景、重排版；隐藏→唤出时先抓干净桌面再显示。

M2 实测（41 条 + 1 个失效条目 + 1 个外部拖入）：私有工作集 **25.1MB**、提交 84.9MB、
WS 74.1MB、525 句柄、25 线程、空闲 CPU 0%、启动 ~330ms。

## 7. 构建 / 运行 / 验证

```powershell
pwsh -File src\GlassLauncher.Native\build.ps1           # 输出 .agent\native\GlassLauncherNative.exe
$env:GLASSLAUNCHER_LOG_DIR='D:\GlassLauncher-handoff\.agent\native\logs'
$env:GL_NATIVE_PERF='1'
.\ .agent\native\GlassLauncherNative.exe
# 日志：native.log（启动耗时、背景抓取、图标加载、PERF 每秒内存、热键/托盘状态）
```

验证截图（测试时加 `GL_NATIVE_NO_EXCLUSION=1`，否则窗口被截屏排除）：
`.agent/native/window2.png`（M1 窗口）、`.agent/native/backdrop.png`（抓屏+模糊结果）。

## 8. 阶段路线

1. **M2 交互闭环**：搜索/IME、右键菜单、设置窗口、拖拽排序、外部拖入、失效检查、窗口缩放。
2. **M3 视觉对齐**：入场/收场动画、悬停放大、拖动中实时背景、玻璃层次（渐变/内外描边）、
   托盘图标/应用图标资源、主题切换。
3. **M4 回归/发布**：自动化拖动/托盘循环脚本、长稳与内存曲线、Release 打包、与 WPF 版并排对比。

## 9. M3 进展（本轮）

### 9.1 实时背景搬到 worker 线程（`LiveCapture`）

同步抓帧（StretchBlt + 3×盒式模糊 ≈ 15–20ms）会卡住 8ms 的窗口移动计时器。现在拆成：

- UI：拖动/缩放计时器只 `Submit()`（微秒级），收到 `WM_APP_LIVE_CAPTURE` 后
  `CopyFromMemory` 上屏（≈0.2ms）；
- worker：独占 GDI DC/DIB + 2 块 0.96MB 像素缓冲，抓帧→模糊→发布最新帧；
  UI 没取走旧帧就覆盖，不排队、不限流；
- 对齐：每帧记录抓取时的窗口原点，绘制时按 `(抓取原点 − 当前原点)` 平移，
  窗口移动后背景仍钉在桌面对应位置。

实测（自动合成拖动 ×5，每次 1500ms）：
`windowTicks ≈ 94–96/1.5s`（≈63Hz 窗口移动）、`liveFrames ≈ 56–60/1.5s`（≈40fps 背景更新）、
`avgGrab 13–17ms`、整段 12s 平均 CPU 2.2%（8 核）。

### 9.2 内存

缓冲裁剪（静态背景上传后释放 CPU 像素、live 缓冲 3→2）后：

| 状态 | 私有工作集 | 提交 | WS | 线程/句柄 |
|---|---|---|---|---|
| 拖动中（5 次后立即） | ~27MB | 85MB | 80MB | 26 / 534 |
| 空闲（拖动后 12s） | **18.5MB** | 75MB | 55.5MB | 15 / 408 |

空闲态已达到 ≤20MB 目标；拖动中的峰值主要来自 D2D 表面/驱动池，下一步按需再压。

### 9.3 非模态边缘缩放 + 双击最大化

- `WM_NCHITTEST` 在四边/四角 6 DIP 内返回 HTLEFT/…/HTBOTTOMRIGHT；
- `WM_NCLBUTTONDOWN` 拦截这些命中，进入自己的 8ms 计时器 + `SetWindowPos` 改尺寸，
  **不进 `WM_ENTERSIZEMOVE`**（避开本机"排除 + 模态循环 = DWM 停画"的故障），
  缩放期间实时背景照常流动；最小尺寸 680×460 DIP；
- `WM_NCLBUTTONDBLCLK` 双击标题最大化/还原；最大化时清掉自绘圆角区域、交给 DWM；
- 实测：左边缘拖动 100px → 窗口 1050→1150、位置 755→655；双击标题 → `IsZoomed=True`、
  16 列布局、失效角标/拖入图标都正常（见 `docs/native-maximized.png`）。

### 9.4 托盘 / 热键（沙箱外实测通过）

沙箱内低完整性进程加不了托盘，按用户要求通过 **admin 审批网关**在沙箱外跑了一次
（`GL_NATIVE_EXIT_AFTER_MS=9000` 自动退出），日志确认：

```
tray added=1
hotkey registered=1 mods=0x4001 vk=0x4A     # Alt+J（测试用独立数据目录，避开 WPF 实例的 Alt+G）
startup window-ready 375 ms
exclusion set=1
auto exit / exit                            # 到期自动退出，没有残留进程
```

也就是说 `Shell_NotifyIcon` 和 `RegisterHotKey` 的真实路径是通的；托盘左键切换、右键菜单、
热键触发的代码路径与日志已在 M1/M2 覆盖。

### 9.5 未完成

- Aero Snap（贴边分屏）仍未实现（非模态拖动/缩放的自然代价）；
- 入场/退场动画、托盘/应用图标资源、暗色设置窗口仍待 M4；
- 拖动中的背景更新实测约 40fps（窗口移动 63Hz、抓帧 13–17ms/帧），下一步评估
  "两趟盒式模糊"或"隔帧更新"把 worker 成本再压一档。

### 9.6 "底部透明条" bug 修复（用户反馈）

现象：窗口部分区域（尤其最底部）不是模糊背景，而是直接透出桌面。

根因是 `WM_NCCALCSIZE` 用错了矩形：wParam=TRUE 时 `rgrc[0]` 已经是**新窗口矩形**，
之前却把它改成 `rgrc[1]`（旧窗口矩形）。于是缩放时客户区停在旧尺寸：
`WM_SIZE` 不触发、渲染目标/圆角区域/背景都不更新，多出来的底部/右侧不归 D2D 管，
DWM 就露出桌面。另有第二处：启动时 `UpdateWindowRegion()` 在客户区 1034×684 的早期
就设了圆角区域，之后客户区变成 1050×700 却没有再设区域，右下 16px 直接被裁掉。

修复：

1. `WM_NCCALCSIZE` 直接 `return 0`，不再改任何 rgrc；
2. `WM_SIZE` 里 `UpdateWindowRegion()` / `LayoutChildren()` 不再受"渲染器是否就绪"限制
   （启动早期也要跟上），渲染器就绪后再 Resize + 重抓背景；
3. `Paint` 先 `rt->Clear(不透明主题底色)`，`DrawBackdrop` 的目标尺寸按**当前窗口**算
   （不是位图尺寸），即使拿到偏小的旧帧也不会留缝；
4. 结束拖动/缩放后 `LiveCapture::DropReady()` 丢弃排队帧，`OnLiveCaptureReady`
   只在拖动/缩放进行中且帧尺寸与当前窗口一致时才 AdoptPixels，防止旧帧覆盖刚锚定的静态帧。

验证：外部 `SetWindowPos` 把窗口从 1050×700 改成 1050×850 →
`WM_SIZE` 触发、区域变 1050×850、背景重抓 585×485，截图整窗无透明缝
（`docs/native-resized.png`）。

### 9.7 M3c 其余进展

- **重命名 / 重新指定目标**：右键菜单项已启用，弹出单行输入框（PromptDialog），
  保存并刷新；重新指定目标会重新提取图标、重算失效状态；
- **应用/托盘图标**：`windres` 把 WPF 版 `app.ico` 编进 exe（资源 id 1），窗口类/托盘都用它；
- **悬停放大动画**：16ms 节拍把每格 0→1 的进度做成 smoothstep，图块放大 1.10×
  （与 WPF 版的弹性放大近似），移开后自动回落；
- 抓屏 GDI 资源改为跨调用复用（每轮 hide/show 不再 Create/Delete DC + DIB）。

### 9.8 打开/关闭动画 + 描边白线

- **动画**：之前没做，本轮补上。窗口改成 `WS_EX_LAYERED`，用
  `SetLayeredWindowAttributes` 做 180ms alpha 淡入/淡出（ease-out）：
  启动、托盘/热键唤出走淡入；关闭/隐藏到托盘走淡出，播完再 `ShowWindow(SW_HIDE)`；
  淡出中再次唤出会反向淡入。实测淡入中途截图能看到桌面透过来（分层窗口与 D2D 共存正常），
  截屏排除也仍然有效（截图里窗口区域显示的是桌面）。`GL_NATIVE_NO_ANIM=1` 可关。
- **白线**：原来窗框/面板描边/顶部高光都是纯白 1px，亮背景上就是扎眼的硬白线。
  现在改成与 WPF 版同款的渐变描边：
  - 窗框：左上白 80% → 中段 14% → 右下黑 28%（对角渐变）；
  - 面板棱：白 60% → 白 18% → 黑 22%；
  - 顶部高光：两端透明 → 12%/88% 白 50% → 中间白 82% 的横向渐变。
- **已知问题（待查）**：hide/show 循环里 `SetForegroundWindow` 每次会累积约 13–15 个内核句柄
  （最小 D2D 探针上不出现；与排除属性、搜索框、窗口圆角区域均无关，已分别 A/B 过）。
  当前 100 次循环约 +1.3k 句柄，功能不受影响；已记录，留待 M4 继续定位
  （候选：分层/激活/DWM 表面，下一步试"动画结束后移除 WS_EX_LAYERED"与
  `SwitchToThisWindow`/`SetActiveWindow` 组合）。

