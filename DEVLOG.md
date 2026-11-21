# GlassLauncher 开发记录

WPF 仿 macOS 液态玻璃启动器。本文记录核心问题的迭代过程、已做改动与遗留问题。
更新时间：2026-11-22 02:15

---

## 一、测试环境关键事实

- Windows Insider build 26300，DPI 1.25。
- 本机 DWM 存在一个系统级故障模型，全部迭代都围绕它展开：
  1. **截屏排除开启 + 模态移动循环（WM_ENTERSIZEMOVE）= DWM 停止呈现窗口**——一拖整个消失，松手才回来；
  2. **每次开关截屏排除（SetWindowDisplayAffinity）都会强制 DWM 重建窗口表面**——视觉上就是整窗闪一下；
  3. **托盘 Hide()/Show() 循环会把 DWM 表面留在"脏"状态**——脏表面 + 排除开启进入模态拖动，稳定触发故障 1。

---

## 二、迭代时间线（按 commit）

### 1501980 — v0.2.0-dev 初始版
基础版：系统 Acrylic 背景 + 冻结帧（抓屏→高斯模糊→覆盖层），模态拖动，排除按需开关。

### ab4e894 — 交叉淡入 + DWM 表面重建防线 + 抓帧线程化
- **抓帧线程化**：BitBlt（约 10–13ms）移到 LiveGrab 工作线程，UI 线程只做几何提交（微秒级）+ Dispatcher 回 UI 上屏。拖动帧率实测 121 帧 avg 12.6ms。
- **交叉淡入**：松手后新冻结帧放 FrozenSurfaceAlt 层 180ms 淡入再换底，消除松手背景硬切。
- **RebuildDwmSurface 防线**：托盘唤出淡入动画期间连续开关一次排除，强制 DWM 重建表面洗掉脏状态（部分有效，后续被更好的方案取代其必要性）。

### 84255dc — 非模态自定义拖动（消失 bug 的根除）
- 拦截 WM_NCLBUTTONDOWN/HTCAPTION（仅 Normal 状态），不进 Windows 模态移动循环；
- 15ms DispatcherTimer 轮询 GetCursorPos + SetWindowPos(SWP_NOACTIVATE) 连续移动；
- 期间 live 抓屏全程可见：实测 116 帧全程无消失。
- **结论：排除开启 + 非模态移动不会触发 DWM 停止呈现，消失 bug 只咬模态循环。**

### 4cbd772 — 永久排除常开 + 松手原子对齐（根除松手闪烁）
- **截屏排除永久常开**：OnSourceInitialized 设一次，全生命周期不再切换（代价：用户自己的截图/录屏里没有本窗口，对启动器可接受）。
- **松手闪烁真因定位**（排除修好后闪烁仍在，说明另有元凶）：
  1. 拖动中每帧 live 帧带"预测偏移"（上一帧位移量，中等拖速可达十几像素）；
  2. 旧实现松手时启动 100ms 回落动画，把已对齐的背景推歪；
  3. 约 15ms 后静态帧到达，硬杀回落动画、偏移瞬间归零，再做 180ms 交叉淡入——新旧两帧错位十几像素，淡入期间整片背景重影发虚，即"松开瞬间失焦闪一下"。
- **修复**：松手时窗口已静止，用真实终点重算最后一帧偏移（对齐条件 s = (O_capture − W_window) / dpi），live 帧与桌面像素级重合；静态帧抓好后直接换 Source（不走淡入层），两个状态完全重合 → 视觉零变化。
- FreezeBackdrop 增加 instant 路径；忙时排队补跑透传 instant 标志；instant 分支先 CommitFrozenFade 定格在途淡入再换帧。

---

## 三、当前各问题状态

| 问题 | 状态 |
|---|---|
| 拖动整个窗口消失（DWM 停止呈现） | ✅ 根除——非模态拖动绕开模态循环 |
| 拖动卡顿 | ✅ 解决——BitBlt 线程化，avg ≈ 11ms/帧 |
| 松手背景硬切 | ✅ 解决——交叉淡入（模态路径）/ 原子换帧（非模态路径） |
| 松手瞬间失焦闪烁 | 🔶 修复已实现并编译运行（4cbd772），**待人工验证** |
| 托盘 Hide/Show 炸渲染 | 🔶 随永久排除方案理论上规避，待验证 |

**重要教训（2026-11-22 凌晨）**：上一轮"问题依旧"的结论不可靠——中断前的编辑把 `_exclusionHeld` 字段声明弄丢，源码编译不过，实际运行的是不含修复的旧 exe。判断"修复无效"前必须先确认运行中的 exe 确实包含该修复（看日志启动标记 `permanent exclusion held=`）。

---

## 四、遗留问题 / 待办

1. **验证松手闪烁修复**：应用已以新版运行（日志确认 `permanent exclusion held=True`），请实际拖动几次确认松手瞬间是否还有失焦/重影感。
2. **缩放边缘的模态路径**：窗口边缘缩放仍走 WM_ENTERSIZEMOVE 模态循环，排除在那条路上仍是"进循环关、出循环开"，松手走交叉淡入——如果还有闪，大概率出在这条残余路径，可考虑缩放也改自定义实现或接受现状。
3. **git push 未完成**：remote 已配置为 `git@github.com:aminoc4/GlassLauncher.git`（SSH 密钥），本地领先 4 个提交，待推送。
4. **⚠ 安全提醒**：此前 GitHub PAT 曾以明文粘贴到对话中，建议尽快到 GitHub → Settings → Developer settings → Tokens 撤销并轮换（现已改用 SSH，不再需要 PAT）。
5. **性能上限**：BitBlt 单帧约 10–13ms，帧率上限约 75–100fps，受 GDI 路径限制；若将来要更顺滑可评估 DXGI 桌面复制 API。
6. **DWM 故障本身未根治**：只是全部绕开。Windows 更新后故障模型可能变化，值得在正式版系统上回归测试。

---

## 五、测试脚本与日志

- 脚本：`.tmp/`（tray-test2.py / instant-drag.py / drag-repro2.py / run-shot.py / fg.py）
- 运行日志：`%LOCALAPPDATA%/GlassLauncher/glass.log`（关键标记：`permanent exclusion held=`、`dwm surface rebuilt`、`Live drag frames=…`、`drag end align …`）
