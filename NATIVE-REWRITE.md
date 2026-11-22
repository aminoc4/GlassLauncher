# GlassLauncher 原生重写（C++ / Win32 + Direct2D）· 阶段 1

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
| P0 | 拖动结束/显示时的背景重抓时机（M1 只在启动抓一次；移动后重抓已在 `StopWindowDrag`，但隐藏→显示没重抓） |
| P1 | 搜索框输入（IME）——计划用子类化 `EDIT` 或 IMM32 自绘文本框 |
| P1 | 右键菜单（打开/管理员/重命名/重定向/打开目录/移除）、设置对话框、拖拽排序、外部文件拖入、失效条目检查 |
| P1 | 窗口缩放（M1 不响应边缘）、Aero Snap、双击标题最大化 |
| P2 | 入场/退场动画、图标悬停放大动画、实时（拖动中）背景帧、托盘图标资源（当前用系统默认图标） |

## 6. 构建 / 运行 / 验证

```powershell
pwsh -File src\GlassLauncher.Native\build.ps1           # 输出 .agent\native\GlassLauncherNative.exe
$env:GLASSLAUNCHER_LOG_DIR='D:\GlassLauncher-handoff\.agent\native\logs'
$env:GL_NATIVE_PERF='1'
.\ .agent\native\GlassLauncherNative.exe
# 日志：native.log（启动耗时、背景抓取、图标加载、PERF 每秒内存、热键/托盘状态）
```

验证截图（测试时加 `GL_NATIVE_NO_EXCLUSION=1`，否则窗口被截屏排除）：
`.agent/native/window2.png`（M1 窗口）、`.agent/native/backdrop.png`（抓屏+模糊结果）。

## 7. 阶段路线

1. **M2 交互闭环**：搜索/IME、右键菜单、设置窗口、拖拽排序、外部拖入、失效检查、窗口缩放。
2. **M3 视觉对齐**：入场/收场动画、悬停放大、拖动中实时背景、玻璃层次（渐变/内外描边）、
   托盘图标/应用图标资源、主题切换。
3. **M4 回归/发布**：自动化拖动/托盘循环脚本、长稳与内存曲线、Release 打包、与 WPF 版并排对比。
