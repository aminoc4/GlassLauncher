# GlassLauncher 开发记录

WPF 仿 macOS 液态玻璃启动器。本文记录核心问题的迭代过程、已做改动与遗留问题。
更新时间：2026-11-22 20:10（含审查后修复，见「三·补」）

> ⚠️ **可信度声明**：本文由前一轮开发 AI 撰写，其中的根因分析与故障归因
> **未经独立验证，已被审查证实存在错误**（见 `REVIEW-FINDINGS.md`）。仅作
> 历史线索参考，一切以代码、日志实测与 `REVIEW-FINDINGS.md` 的结论为准。

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
  > ⚠ 事后修订：这条"像素级重合"当时并没有成立——live 帧的像素根本没上传、尺寸被桶放大、
  > 位移预测符号也反了（见「三·补」）。而且该提交让程序启动即崩，所谓"运行"只持续了 200ms。
- FreezeBackdrop 增加 instant 路径；忙时排队补跑透传 instant 标志；instant 分支先 CommitFrozenFade 定格在途淡入再换帧。

---

## 三、当前各问题状态

| 问题 | 状态 |
|---|---|
| 拖动整个窗口消失（DWM 停止呈现） | ✅ 根除——非模态拖动绕开模态循环 |
| 拖动卡顿 | ✅ 解决——BitBlt 线程化，avg ≈ 11ms/帧 |
| 松手背景硬切 | ✅ 解决——交叉淡入（模态路径）/ 原子换帧（非模态路径） |
| 松手瞬间失焦闪烁 | 🔶 4cbd772 的修复**从未被验证过**（那一版启动就崩，见下节），本轮修好崩溃后再看 |
| 托盘 Hide/Show 炸渲染 | 🔶 随永久排除方案理论上规避，仍待完整回归 |
| **启动 0.3s 后"未响应"→ 进程消失** | ✅ 已根因定位并修复（本轮） |
| **拖动时背景一片灰** | ✅ 已修复：live 帧像素根本没上传（本轮） |

**重要教训（2026-11-22 凌晨）**：上一轮"问题依旧"的结论不可靠——中断前的编辑把
`_exclusionHeld` 字段声明弄丢，源码编译不过，实际运行的是不含修复的旧 exe。
判断"修复无效"前必须先确认运行中的 exe 确实包含该修复。

**重要教训（2026-11-22 晚，修订）**：上面这条教训本身也踩了坑——`permanent exclusion held=`
只是启动横幅，它后面 214ms 就是崩溃。真正的健康标记是 `freeze ok`（冻结帧抓取成功）。
详见下一节和 `REVIEW-FINDINGS.md`。

---

## 三·补、2026-11-22 晚：审查后的修复

外部审查定位到 4cbd772 引入的一个必现崩溃，并顺带挖出 4 个长期隐藏的功能缺陷。
（完整证据链、复现方法在 `REVIEW-FINDINGS.md`；交接文档 `REVIEW-BRIEF.md` 已同步修订。）

### 根因：`Dispatcher.BeginInvoke` + 可选参数 = 启动即崩

4cbd772 把 `FreezeBackdrop()` 改成 `FreezeBackdrop(bool instant = false)`，
而两处老调用点仍写着 `Dispatcher.BeginInvoke(FreezeBackdrop, DispatcherPriority.Loaded)`。
`BeginInvoke(Delegate, …)` 内部走 `Delegate.DynamicInvoke`，**反射调用不认可选参数**，
于是抛 `TargetParameterCountException`；`App.DispatcherUnhandledException` 又只记日志、
不设 `e.Handled`，异常把调度线程打死 → 消息泵停摆 → 窗口"未响应"→ 进程消失。
症状就是"打开后卡一下然后没了"，而日志最后一行的 `permanent exclusion held=True`
恰好让人误以为新代码在跑。

### 顺带修掉的隐藏问题

1. **live 帧像素从未上传**：ab4e894 把抓帧线程化时漏掉了 `WritePixels`，
   `WriteableBitmap` 从头到尾是空的。拖动时看到的"背景"其实是空位图 + 白纱 = 一片灰。
2. **抓取尺寸按 32px 桶放大**，显示端 `Stretch=Fill` 又压回去 → 背景整体缩放 ~1.2%，
   与桌面/静态帧错位最多 14px，松手换帧时跳一下。
3. **位移预测符号反了**：用 `(O_n − O_{n−1})`（窗口位移的正方向）去补，而正确值是
   `(O_capture − W_now)`（反方向），等于把背景推成 2 帧滞后，松手对齐时再跳回来。
4. **托盘唤出后可能抓到自己**：`RebuildDwmSurface` 切完排除不重置 `_exclusionOnTick`，
   紧接着的 `FreezeBackdrop` 以为"早就生效"，跳过 60ms 等待 → 鬼影帧。
5. 热键数字键往返坏掉（`"1"` 被 `Enum.TryParse` 当枚举值 1 = `Key.Cancel`）；
   `TryParse` 还允许注册裸键全局热键。
6. 隐藏到托盘/关窗/双击标题时，正在进行的"拖窗口"没人收尾：隐藏的窗口继续跟着鼠标跑，
   `_inModalMove` 卡在 true。
7. 数据文件损坏时静默返回空列表，下一次保存就把用户数据覆盖掉（现在会留存损坏文件并尝试 `.bak`）。
8. `DpiChanged` 假事件（1.25→1.25）触发 `SWP_FRAMECHANGED` 全量重算；`EnsureOnScreen`
   用当前窗口 DPI 换算跨屏位置。

### 验证方式（可复现）

用一份改写了日志路径的副本跑完整链路（托盘隐藏/唤出 → 标题拖动 ×2 → 边缘缩放），
日志证据：

```
permanent exclusion held=True
freeze ok 1170x820 instant=False settle=140ms px=959400     ← 冻结帧真的抓到了
dwm surface rebuilt toggle=True restore=True held=True
freeze ok 1170x820 instant=False settle=0ms                 ← 唤出后补帧
manual drag start rect=(755,370)
live shift=-8.0,-3.2 origin=649,370 now=659,374             ← 窗口右移 → 负偏移（对齐公式）
drag end align dx=-8.0 dy=-3.2
Live drag frames=58 avg=10.8ms max=15.4ms
modal drag: exclusion released held=False
modal drag ended: exclusion restored
DispatcherUnhandledException: 0 次
```

> 注意：本窗口开着截屏排除，**截图里看不到它**，所以只能靠日志判断像素是否真的上屏
> （`live upload mean=… min=… max=…` 这类探针）。

---

## 四、遗留问题 / 待办

1. **实机验证松手闪烁**：崩溃修好后才有意义。请拖动几次确认松手瞬间是否还有失焦/重影感；
   若有，下一个怀疑对象是模态缩放路径（进/出循环各闪一次）。
2. **缩放边缘的模态路径**：窗口边缘缩放仍走 WM_ENTERSIZEMOVE 模态循环，排除在那条路上仍是"进循环关、出循环开"，松手走交叉淡入——如果还有闪，大概率出在这条残余路径，可考虑缩放也改自定义实现或接受现状。
3. **`RebuildDwmSurface` 去留**：每次托盘唤出都要切两次排除（两次整窗闪）。建议做 A/B：
   有/无各走 20 次托盘循环 + 模态缩放，确认"脏表面"是否仍然需要洗。
4. **静态路径的 WriteableBitmap 池化**：live 路径已复用位图，`FreezeBackdrop` 仍每帧新建
   `byte[]` + `WriteableBitmap`（4MB 级）。
5. **git push 未完成**：remote 已配置为 `git@github.com:aminoc4/GlassLauncher.git`（SSH 密钥），待推送。
6. **⚠ 安全提醒**：此前 GitHub PAT 曾以明文粘贴到对话中，建议尽快到 GitHub → Settings → Developer settings → Tokens 撤销并轮换（现已改用 SSH，不再需要 PAT）。
7. **性能上限**：BitBlt 单帧约 10–13ms，帧率上限约 75–100fps，受 GDI 路径限制；若将来要更顺滑可评估 DXGI 桌面复制 API。
8. **DWM 故障本身未根治**：只是全部绕开。Windows 更新后故障模型可能变化，值得在正式版系统上回归测试。
9. **发布物过期**：`dist/GlassLauncher-v0.1.0.zip` 是 10/21 的自包含单文件，不含 4cbd772 之后的任何改动，需要重新发布并升版本号。

---

## 五、测试脚本与日志

- 脚本：`.tmp/`（tray-test2.py / instant-drag.py / drag-repro2.py / run-shot.py / fg.py）
- 运行日志：`%LOCALAPPDATA%/GlassLauncher/glass.log`
  （关键标记：`permanent exclusion held=`、**`freeze ok`**、`dwm surface rebuilt`、
  `manual drag start`、`drag end align …`、`Live drag frames=…`、`DispatcherUnhandledException`）
- 发布版默认也写日志（1MB 轮转到 `glass.log.1`），`GLASSLAUNCHER_LOG=0` 可关闭。
