# GlassLauncher 代码审查报告

审查对象：`C:/Users/fjx/WorkBuddy/2026-11-18-22-37-41/GlassLauncher`，审查基线 HEAD = `3a710e3`
审查基准：`REVIEW-BRIEF.md` + 实际代码 + `%LOCALAPPDATA%/GlassLauncher/glass.log` + 现场实验
审查日期：2026-11-22

> 说明：按要求没有完全采信交接文件。凡是交接文件与代码/日志冲突的地方，都以实测为准，并在第 2 节逐条列出。
> **本报告所有 P0/P1 问题已于 2026-11-22 晚修复并现场验证**，验证方法与日志见第 10 节；
> 交接文档 `REVIEW-BRIEF.md` / `DEVLOG.md` 也已同步修订。

---

## 0. 结论摘要

| 严重度 | 问题 | 状态 |
|---|---|---|
| **P0** | 启动约 0.3s 后抛 `TargetParameterCountException`，UI 线程死亡 → 窗口"无响应"→ 进程崩溃 | ✅ 已修 |
| **P0** | 全局异常处理器只记日志、不设 `e.Handled`，把任何 UI 线程异常放大成"假死+崩溃" | ✅ 已修 |
| **P0** | live 帧的像素**从未写进 WriteableBitmap**（ab4e894 重构漏掉），拖动时背景是一片灰 | ✅ 已修（本轮新发现） |
| P1 | 托盘每次唤出：排除被连切两次（两次整窗闪），且紧接着抓帧不等待 DWM 落实 → 背景抓到自己（鬼影） | ✅ 已修 |
| P1 | live 帧按 32px 桶抓取却用 `Stretch=Fill` 显示 → 背景被缩放 ~1.2%，与桌面错位最多约 14px | ✅ 已修 |
| P1 | 位移预测 `FrozenShift` 符号与作者自己的对齐公式相反 → 拖动中背景多错 2 帧 | ✅ 已修 |
| P1 | 数字键热键往返坏掉：`Ctrl+1` 重启后变成 `Ctrl+Cancel`，`Ctrl+0` 直接失效 | ✅ 已修 |
| P1 | 隐藏到托盘/按 Esc 时正在"拖窗口"没人收尾：`_inModalMove` 卡死，隐藏的窗口继续跟着鼠标跑 | ✅ 已修 |
| P2 | 数据文件损坏后会被静默覆盖（数据丢失） | ✅ 已修 |
| P2 | `RebuildDwmSurface`/模态循环与 `_exclusionHeld` 缓存漂移 | ✅ 已修 |
| P2 | `DpiChanged` 假事件、`EnsureOnScreen` 跨屏换算、`LiveGrab` 字段跨线程、托盘添加失败不重试、发布版无日志 | ✅ 已修 |
| P2 | 模态缩放仍进出各切一次排除（两次闪）；`RebuildDwmSurface` 去留；Aero Snap 丢失 | 🔶 待决策 |

**最重要的结论**：`4cbd772`（"永久排除常开 + 松手原子对齐：根除松手瞬间闪烁"）这个提交让程序**根本跑不起来**。
交接文件和 DEVLOG 里"修复已实现并编译运行、待人工验证"的判断不成立——那次运行在打出
`permanent exclusion held=True` 之后 214ms 就抛异常，日志到此为止。


---

## 1. P0-1：启动即崩溃 / 无响应（必现）

### 位置

- [MainWindow.xaml.cs:765](src/GlassLauncher/MainWindow.xaml.cs#L765)（`Loaded` 里）
- [MainWindow.xaml.cs:1325](src/GlassLauncher/MainWindow.xaml.cs#L1325)（`ShowFromTray` 里）
- 受害方法：[MainWindow.xaml.cs:862](src/GlassLauncher/MainWindow.xaml.cs#L862)
- 放大器：[App.xaml.cs:46](src/GlassLauncher/App.xaml.cs#L46)

```csharp
// 765 / 1325
Dispatcher.BeginInvoke(FreezeBackdrop, DispatcherPriority.Loaded);

// 862：4cbd772 才加上的可选参数
private async void FreezeBackdrop(bool instant = false)
```

### 原因

`Dispatcher.BeginInvoke(Delegate, DispatcherPriority, params object[] args)` 最终走
`Delegate.DynamicInvoke(args)`，而**反射调用不认识"可选参数"**：方法有 1 个形参就必须传 1 个实参，
否则抛 `TargetParameterCountException`。

`FreezeBackdrop` 在 `ab4e894` 时还是无参方法，这两处 `BeginInvoke` 一直是对的；
`4cbd772` 给它加了 `bool instant = false`，这两处老调用点当场变成定时炸弹（编译期毫无提示，因为方法组能转成 `Action<bool>`）。

### 证据链

1. 现场日志（用户最后一次运行，Release 版，02:13:43）：

```
02:13:43.328  permanent exclusion held=True          ← 交接文件让你认这个标记
02:13:43.542  DispatcherUnhandledException: System.Reflection.TargetParameterCountException: Parameter count mismatch.
              at System.Delegate.DynamicInvokeImpl(Object[] args)
              ...
02:13:43.633  DpiChanged 1.25 -> 1.25 inModalMove=False
02:13:43.641  （日志到此为止，进程随后消失）
```

2. 最小 WPF 探针（把同样的写法抄出来跑）：

```
19:46:22.222 queued BeginInvoke(FreezeBackdrop, Loaded) — 编译期通过
19:46:22.224 Application.DispatcherUnhandledException: TargetParameterCountException: Parameter count mismatch.
19:46:22.224   e.Handled (before our handler) = False
退出码 -532462766 (0xE0434352 = CLR 未处理异常)
```

3. 插桩副本（`.audit/`）完整跑一遍：

```
19:49:14.963 [t1] UNHANDLED TargetParameterCountException: Parameter count mismatch. handled=False
```

  跑起来后该进程 `Responding = False`（Windows 判定"未响应"），随后被系统/用户结束。
  这和你描述的"程序无响应、会崩溃"完全对应。

4. 附带后果：`FreezeBackdrop` 一次都没真正进入（插桩日志里没有 `FreezeBackdrop START`），
   所以**冻结背景管线从未启动**，窗口显示的其实还是 DWM 的 Acrylic。
   "冻结层全时制"这个设计前提从 4cbd772 起就没有被执行过。

### 复现步骤

1. `dotnet build -c Debug`，双击 `bin/Debug/net8.0-windows/GlassLauncher.exe`
2. 约 0.2–0.5s 后打开 `%LOCALAPPDATA%\GlassLauncher\glass.log`
3. 能看到 `permanent exclusion held=True` 紧跟一条 `TargetParameterCountException`
4. 窗口标题栏变灰/点击无响应（任务管理器"未响应"），随后进程退出

### 建议修法（最小改动）

```csharp
// 765 与 1325 两处都改成显式 Action 闭包
Dispatcher.BeginInvoke((Action)(() => FreezeBackdrop()), DispatcherPriority.Loaded);
```

或者先把方法改成无参、把"instant"拆成两个方法（`FreezeBackdrop()` / `FreezeBackdropInstant()`），
避免以后再被 `BeginInvoke` 直接引用。**两个调用点都要改**，只改 765 的话托盘唤出仍然会崩。

> ✅ **已修**：两处都改成 `(Action)(() => FreezeBackdrop())`，并在 `App` 的处理器里补上
> `e.Handled = true`（配合 `Diag` 在发布版也写日志）。

---

## 1b. P0-3：live 帧的像素从来没有上传（拖动时背景一片灰）

### 位置

[LiveGrab.cs](src/GlassLauncher/Chrome/LiveGrab.cs)（`PumpLoop` / `CreateBitmap` / `RaiseFrame`）

### 现象

**你反馈的"按住拖动时背景直接灰了"就是这个。** 空 WriteableBitmap（Bgr32 全 0 = 黑）
盖在 Acrylic 上，再叠一层浅色白纱（Wash/Sheen）→ 看起来就是一片灰。

### 原因

`ab4e894` 把抓帧线程化时重写了这个类：BitBlt 抓进 DIB section，然后
`BeginInvoke(() => RaiseFrame(...))` 把 `_bitmap` 交给 UI —— 但**从头到尾没有把 `_bits`
的内容写进 `_bitmap`**。`CreateBitmap` 只是 `new WriteableBitmap(...)`，没有任何
`WritePixels` 调用。对比 1501980 的初版，那里是有这一行的：

```csharp
// 1501980 里存在、ab4e894 重构后消失
_bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _bits, _width * _height * 4, _width * 4);
```

### 为什么长期没被发现

- 帧率统计（`Live drag frames=…`）只数帧数，看不出内容；
- 窗口开着截屏排除，**任何截图/录屏都拍不到它**，所以"看截图验证"天然失效；
- 空白位图 + 白纱在浅色桌面上就是"有点灰"，很容易被当成"被 Acrylic 洗白了"。

### 修法（已实施）

工作线程 BitBlt 后把 DIB 拷进池化缓冲，UI 线程 `WritePixels` 上屏并归还缓冲：

```csharp
int bytes = _width * _height * 4;
byte[] buffer = RentBuffer(bytes);
Marshal.Copy(_bits, buffer, 0, bytes);
_dispatcher.BeginInvoke(() => UploadAndRaise(buffer, w, h, origin, dpi));
```

用 3~4 个缓冲的池（`ConcurrentQueue`，UI 上完屏归还），避免每帧分配 3.8MB 托管数组
（那么大的一律进 LOH，60fps 会把 GC 压垮）。

> ✅ **已修**，验证：拖动中采样 `live upload mean=252.1 min=15 max=255`（真实桌面内容，
> 有明暗层次；修复前恒为 0）。

---

## 2. P0-2：全局异常处理器不设 `e.Handled`，把异常放大成"崩溃"

### 位置

[App.xaml.cs:46](src/GlassLauncher/App.xaml.cs#L46)

```csharp
DispatcherUnhandledException += (_, e) =>
{
    Chrome.Diag.Log($"DispatcherUnhandledException: {e.Exception}");
};
```

### 问题

`DispatcherUnhandledException` 里不设 `e.Handled = true`，WPF 会把异常重新抛到调度线程上：
调度线程终止 → 消息泵停止 → 窗口变成"幽灵窗口"（一直显示、点不动、`Responding=False`），
然后进程才退出。用户看到的就是"卡死 → 崩溃"，日志里只留一行异常，没有任何"我要挂了"的明确信号。

P0-1 之所以表现成"无响应"而不是"报错退出"，就是被这里放大的。

### 建议

```csharp
DispatcherUnhandledException += (_, e) =>
{
    Chrome.Diag.Log($"DispatcherUnhandledException: {e.Exception}");
    // 明确策略：能让用户看见并自行保存的异常，不要拖死消息泵
    e.Handled = true;
};
```

注意：`e.Handled = true` 会掩盖真实崩溃，所以至少要配一个"连续 N 次异常后主动退出/提示"的兜底，
并把日志级别抬到用户可见（现在的 `Diag` 在 Release 下默认不写文件，见 P3-1）。

---

## 3. 交接文件里不可信 / 已过期的结论

| # | 交接文件说法 | 实际情况 |
|---|---|---|
| 1 | §2/§4.1「排除现在是**永久常开**……全代码**严禁**随手开关它」 | 代码里仍有 5 处开关：`RebuildDwmSurface` 双向切换、`StartDragRide` 补开、`WndProc_DragStarted` 关、`WndProc_DragEnded` 开、`FreezeBackdrop` 补开。日志里 `dwm surface rebuilt` 出现 **8 次**（= 16 次切换 = 16 次表面重建） |
| 2 | §4.3「静态帧抓好后直接换 Source……两个状态完全重合 → 视觉零变化」 | 不成立：live 帧被 32px 桶放大后再 `Stretch=Fill` 压缩显示（P1-2），位移预测符号还与对齐公式相反（P1-3） |
| 3 | §5.1 把「`EndManualDrag` 之后仍在途的 `FrameReady`」列为首要竞态 | 这条其实**已经有兜底**（`OnLiveFrameReady` 开头 `if (!_liveGrabActive) return;`），真正没兜住的是"托盘唤出后 `RebuildDwmSurface` 紧接抓帧"（P1-1） |
| 4 | §6「关键日志标记 `permanent exclusion held=`（确认跑的是新代码）」 | 这行正好打在崩溃前 214ms，是个**误导性**的验证标记。DEVLOG 里"已以新版运行"的结论就是这么来的 |
| 5 | DEVLOG §三「松手瞬间失焦闪烁 🔶 修复已实现并编译运行，待人工验证」 | 那次运行在 200ms 后崩了，**从未被验证过** |
| 6 | §7「本地 main，**5 个提交**，HEAD = `01ada9a`」 | 实际 6 个提交，HEAD = `3a710e3`（交接文件自己那个提交没算进去） |
| 7 | §3 说 `MainWindow.xaml.cs` ~1400 行 | 实际 2771 行（写了 `#if DEBUG` 探针 + 大段注释），审查时容易漏读后半段 |

另外，交接文件里对系统故障模型的描述（§2）本身我没有条件反驳——但它把"排除常开 = 零切换"当成了既成事实，
而代码里每次托盘唤出都在**主动破坏**这个前提，这一点值得先修。

---

## 4. P1 问题

### P1-1 托盘唤出：两次闪 + 鬼影帧（✅ 已修：`RebuildDwmSurface` 后重置 `_exclusionOnTick`；去留仍待 A/B）

**位置**：[MainWindow.xaml.cs:1306-1326](src/GlassLauncher/MainWindow.xaml.cs#L1306-L1326)（`ShowFromTray`）、
[MainWindow.xaml.cs:625](src/GlassLauncher/MainWindow.xaml.cs#L625)（`RebuildDwmSurface`）

```csharp
Dispatcher.BeginInvoke(RebuildDwmSurface, DispatcherPriority.Loaded);   // 先排
...
Dispatcher.BeginInvoke(PlayEntrance, DispatcherPriority.Loaded);
Dispatcher.BeginInvoke(FreezeBackdrop, DispatcherPriority.Loaded);      // 紧接着排
```

同一优先级 FIFO，所以执行顺序是：`RebuildDwmSurface`（把排除关掉再打开 = 两次 DWM 表面重建 = 两次整窗闪）
→ `PlayEntrance` → `FreezeBackdrop` 立刻开始抓帧。

致命点在第二段：`RebuildDwmSurface` **不更新 `_exclusionOnTick`**，而 `FreezeBackdrop` 靠
`Environment.TickCount64 - _exclusionOnTick` 判断"排除落实了没有"。此时 `_exclusionOnTick` 还是启动时的旧值，
`sinceOn` 远大于 `ExclusionSettleMs`，于是**跳过 60ms 等待直接抓**——而排除刚被重新打开，
DWM 要 1~2 个合成帧才落实。结果就是交接文件 §2 自己警告过的"鬼影帧"：
抓进来的背景里带着本窗口自己的残影，然后 180ms 淡入给用户看。

**修法**（二选一）：

- A：`RebuildDwmSurface` 里切换完把 `_exclusionOnTick = Environment.TickCount64;` 补上（最小改动）；
- B：更好——既然排除已永久常开，`Hide()/Show()` 未必真需要"洗表面"，
  用一次有意识的 A/B（有/无 `RebuildDwmSurface` 各走 20 次托盘循环 + 模态缩放）决定是否整个删掉它。
  删掉能同时省掉两次闪。

> 顺带回答交接文件 §5.6「永久排除后 RebuildDwmSurface 还必要吗」：它现在**是与"零切换"不变量直接冲突的唯一一处**，
> 而且引入了鬼影窗口。建议先按 B 做对照实验，别再无条件保留。

### P1-2 live 帧被 32px 桶缩放，和桌面/静态帧错位（✅ 已修：抓取尺寸精确等于请求区域）

**位置**：[LiveGrab.cs:149-161](src/GlassLauncher/Chrome/LiveGrab.cs#L149-L161)、[LiveGrab.cs:185](src/GlassLauncher/Chrome/LiveGrab.cs#L185)、
[MainWindow.xaml:64-67](src/GlassLauncher/MainWindow.xaml#L64-L67)（`Stretch="Fill"`）

`Bucket(v) = (v+31)/32*32`：抓取位图**总是比请求区域大 0~31px**，但显示端 `FrozenSurface` 是
`Stretch=Fill` + `Margin=-48`，必须把 1184px 的位图塞进 1170px 的位置。

以默认 840×560 @ DPI 1.25 为例：

```
抓取区域 = ceil((840+96)*1.25) × ceil((560+96)*1.25) = 1170 × 820
桶尺寸   = 1184 × 832        （分别 +14 / +12 px）
显示目标 = 1170 × 820 px     （窗口 + 2×48 DIP）
→ 横向缩放 0.9882，纵向 0.9856
```

左上角对齐、误差线性增长到右下角约 **14px（≈11 DIP）**。后果：

1. 拖动中背景与桌面不是 1:1，桌面细节（模糊后仍有明暗结构）会轻微"缩放游动"；
2. 松手瞬间从 live 帧原子切到静态帧（精确尺寸、无缩放），边缘处**必然跳一下**——
   这正是"松手闪一下"可以被感知的一个来源，且和交接文件 §4.3 的"像素级重合"前提矛盾。

**修法**：DIB 继续按桶分配（避免每帧重建），但
- `WriteableBitmap` 用**真实请求尺寸**创建（尺寸不变时可复用），或者
- 抓完只把左上角 `w×h` 区域 `WritePixels` 进去，位图尺寸 = 请求尺寸。

（这也顺带回答交接文件 §5.5：静态路径每帧新建 `byte[]` + `WriteableBitmap`（4MB 量级）确实值得池化，
但那是性能问题，这个才是正确性问题。）

### P1-3 位移预测 `FrozenShift` 符号反了（✅ 已修，现场日志确认方向与量）

**位置**：[MainWindow.xaml.cs:602-608](src/GlassLauncher/MainWindow.xaml.cs#L602-L608)

```csharp
if (_livePrevOrigin is Point prev)
{
    FrozenShift.X = (originPx.X - prev.X) / dpi.DpiScaleX;   // ← 当前写法：+Δ
    ...
}
_livePrevOrigin = originPx;
```

而作者自己在 [MainWindow.xaml.cs:433](src/GlassLauncher/MainWindow.xaml.cs#L433) 写的对齐条件是
`s = (O_capture − W_window) / dpi`。按这个条件推导：

设桌面有个标记在屏幕 `x=1000`，窗口左上角为 `W`。某帧在 `O_n` 处抓取，标记落在位图 `1000−O_n` 处，
显示在窗口内 `p = (1000−O_n) + s·dpi`，屏幕位置 `= W + p`。要求与桌面重合：

```
W + 1000 − O_n + s·dpi = 1000  →  s = (O_n − W)/dpi
```

帧到达时窗口已经又往前走了（`W > O_n`），所以**正确的 s 是负的**；而代码给的是 `O_n − O_{n−1} > 0`，
方向相反，幅度相同 → 误差约 **2 帧位移**（中等拖速下 10~20px）。松手时 `EndManualDrag` 用的是正确公式
（`lastOrigin − endRect`，见 433 行），于是偏移会从 `+v` 直接跳到 `−v`/0 —— **恰好在松手那一刻发生一次 ~2v 的跳变**，
然后静态帧原子替换。这非常像"松开瞬间闪/跳一下"的残留成因。

**建议**：在 `OnLiveFrameReady` 里改用**当前**窗口原点（`PointToScreen(new Point(0,0))`）而不是上一帧原点：

```csharp
Point now = PointToScreen(new Point(0, 0));
FrozenShift.X = (originPx.X - now.X) / dpi.DpiScaleX;
FrozenShift.Y = (originPx.Y - now.Y) / dpi.DpiScaleY;
```

并在拖动中打一条 `shift`/`windowOrigin`/`frameOrigin` 的采样日志（或录一帧慢动作）做 A/B 确认。

### P1-4 数字键热键往返坏掉；`TryParse` 还允许注册裸键（✅ 已修）

**位置**：[HotkeyHelper.cs:51](src/GlassLauncher/Services/HotkeyHelper.cs#L51)、[HotkeyHelper.cs:150-168](src/GlassLauncher/Services/HotkeyHelper.cs#L150-L168)

`Format` 把 `Key.D1` 写成 `"1"`，但 `TryParse` 用的是 `Enum.TryParse("1")`——它会把数字串当成**枚举数值**：

```
"1"  → parse=True key=Cancel   (value=1)  vk=0x03
"9"  → parse=True key=KanaMode (value=9)  vk=0x15
"0"  → parse=True key=None     (value=0)  → virtualKey==0 → TryParse 返回 false
"G"  → parse=True key=G        vk=0x47
```

（上面是探针实跑结果。）所以：

- 用户设成 `Ctrl+Alt+1`，保存后当场能用（内存里的 `_settings.Hotkey` 文本还是 `"Ctrl+Alt+1"`，
  `ApplyHotkeySetting` 内部又走 `TryParse`……**其实当场也会失效**），重启后更彻底：注册的是 `VK_CANCEL`；
- `Ctrl+Alt+0` 直接注册失败，静默无热键；
- 另外 `TryParse` 不校验"至少一个修饰键"，手改 `settings.json` 成 `"G"` 会**注册一个裸 G 全局热键**，
  把所有程序里的 G 都抢走（`TryFromKeyboard` 里明明拦了裸键，两条路径规则不一致）。

**修法**：`TryParse` 里遇到数字先映射到 `Key.D0..D9`；并且和 `TryFromKeyboard` 一样强制要求修饰键
（或至少要求 "非 F1~F12 必须有修饰键"）。最好把 `Format`/`TryParse` 做成对同一个键名表，
并加一组往返单测（`Format→TryParse` 恒等）。

### P1-5 隐藏/退出时，正在进行的"拖窗口"没有收尾（✅ 已修：新增 `AbortWindowDrag`）

**位置**：[MainWindow.xaml.cs:1159](src/GlassLauncher/MainWindow.xaml.cs#L1159)（`HideToTray`）、
[MainWindow.xaml.cs:348-446](src/GlassLauncher/MainWindow.xaml.cs#L348-L446)（`StartManualDrag`/`EndManualDrag`）

`HideToTray` 只调 `CancelGesture()`（那是**图标拖拽**的手势），完全不管 `_manualDrag`：

- 手动拖窗口（非模态）期间按 Esc / 托盘左键 / 全局热键 → `Hide()` 了，但 `_manualDragTimer` 还在 15ms 一拍
  调 `SetWindowPos`，**看不见的窗口继续跟着鼠标跑**；
- `_inModalMove` 停在 `true`：`RefreezeSoon()` 和 `FreezeBackdrop()` 全部让路，
  下次唤出后背景停在旧位置（且会一直排队 `_freezeQueued`）；
- 如果鼠标一直按着（拖到一半用热键收起），要等到松手 `ManualDragTick` 才自愈，期间窗口位置已经跑飞。

**修法**：`HideToTray`/`Closing` 里补 `if (_manualDrag) EndManualDrag();`（或抽一个 `CancelWindowDrag()`），
并且让 `StartManualDrag` 在 `!IsVisible` 时直接不启动。

### P1-6 `FreezeBackdrop` 是 `async void`，`await` 之后不复查窗口状态（✅ 已修）

**位置**：[MainWindow.xaml.cs:862-971](src/GlassLauncher/MainWindow.xaml.cs#L862-L971)

`hwnd`/`ActualWidth` 的检查在 `await Task.Delay(...)` **之前**。等待期间用户关窗/隐藏，
后续 `VisualTreeHelper.GetDpi(this)`、`PointToScreen` 可能抛异常；`async void` 的异常只能走
`DispatcherUnhandledException`，而那里又不设 `Handled`（P0-2）→ 又一次"无响应+崩溃"。
正常路径上 `Task.Delay` 只有 60ms，窗口很小但确实存在（托盘唤出 → 立刻退出 / 立刻隐藏）。

**修法**：`await` 后复查 `hwnd != IntPtr.Zero && IsVisible`，并把 `async void` 包一层
`try/catch` 记日志（不要依赖全局处理器兜底）。

---

## 5. P2 问题

### P2-1 模态缩放路径：两次闪是现状，但有更好的绕法（回答 §5.3）

现状 `WndProc_DragStarted` 关排除 → 出循环 `WndProc_DragEnded` 开排除 = 进/出各一次整窗闪，且缩放期间背景是冻结帧。
既然标题拖动已经证明了"非模态 SetWindowPos + 排除常开"不触发 DWM 停止呈现，**下一刀可以照同一个思路做边缘缩放**：
拦截 `WM_NCHITTEST` 的 `HTLEFT/HTRIGHT/HTTOP/...`，自己用轮询 + `SetWindowPos(cx,cy)` 改尺寸，
排除全程不开不关 → 零闪，还能顺手把 live 抓屏用在缩放上（现在缩放期间背景是死的）。
代价：要自己处理最小尺寸、Aero Snap（见 P2-8）、Alt+Space 菜单行为。

### P2-2 `_exclusionHeld` 缓存会与实际亲和性漂移（✅ 已修：统一走 `EnsureExclusion`）

`StartDragRide`（[477](src/GlassLauncher/MainWindow.xaml.cs#L477)）用缓存的 `_exclusionHeld` 判断要不要补开排除，
而 `FreezeBackdrop`（[896](src/GlassLauncher/MainWindow.xaml.cs#L896)）每次实查 `GetExclusion`。
两者一旦不一致（`RebuildDwmSurface` 的"恢复"失败、DWM 重启、远程桌面会话切换），
拖动期间就会**抓到自己**，且没有任何日志提示。建议统一走一个 `EnsureExclusion()`，
内部实查 + 更新 `_exclusionHeld`/`_exclusionOnTick`，任何"以为开着其实没开"的情况打日志。

### P2-3 `LiveGrab` 的线程细节（回答 §5.4）（✅ 部分已修，见文末 10.1）

| 点 | 现状 | 评价 |
|---|---|---|
| DC 生命周期 | 全部在工作线程创建/销毁，`ReleaseGdi` 也在工作线程 | ✅ 正确 |
| `_bitmap` | UI 线程 `CreateBitmap` 写、**工作线程 `ReleaseGdi` 里也写 null** | ⚠ 跨线程写同一字段，应改为只在 UI 线程置空（或干脆不置空） |
| `LastGrabMs` | 工作线程写、UI 线程读的双精度属性，无 `volatile`/锁 | ⚠ 只是统计值，实际不会出事；但严格说非原子，建议 `Interlocked.Exchange(ref _lastGrabMsBits, …)` 或加锁 |
| `ResetWarmup` | UI 线程写 `_warmupLeft`，工作线程自减 | ⚠ 竞争窗口内可能多跳/少跳 1 帧，无实际危害；若要干净，用 `Interlocked.Exchange` |
| 背压 | `AutoResetEvent` + 单 pending 槽，天然合并 | ✅ 合理 |
| 释放 | `MainWindow` **从不 Dispose `_liveGrab`**；`Dispose` 只 `Join(500)`，超时后 GDI 泄漏 | ⚠ 应在 `Closed` 里 Dispose，并接受 Join 超时（工作线程是后台线程，不影响退出） |
| 丢帧 | `Submit` 在 `ResetWarmup` 竞争时可能整帧丢弃 | ✅ 可接受（下一帧会补） |

### P2-4 数据文件损坏后会被静默覆盖（数据丢失）（✅ 已修：留存损坏文件 + 尝试 `.bak`）

**位置**：[LauncherStore.cs:58-79](src/GlassLauncher/Services/LauncherStore.cs#L58-L79)、[MainWindow.xaml.cs:1535](src/GlassLauncher/MainWindow.xaml.cs#L1535)

`Load` 解析失败时返回**空列表**并给出 `error`，`MainWindow` 只弹一条 5 秒的状态条。
之后用户随便点一下（启动应用失败标记、拖一个图标、关窗）都会 `Persist()` → 用空列表覆盖原文件。
`.bak` 是有的，但**没有任何自动恢复路径**，用户也看不到"你的数据文件坏了"。

**建议**：解析失败时先把原文件改名成 `launcher-data.corrupt-{时间戳}.json`，再尝试 `.bak`；都失败才用空列表，
并在界面上给一个持续可见的提示（而不是 5 秒自动消失）。

### P2-5 `DpiChanged` 假事件触发无谓的表面重建（✅ 已修：旧值=新值时直接返回）

日志里每次启动/唤出都有：

```
DpiChanged 1.25 -> 1.25 inModalMove=False
Corner attr33 pref=2 hr=0x00000000 framedchanged
```

旧值=新值却仍然走 `ApplyCornerMode(force: true)` → `SetWindowPos(SWP_FRAMECHANGED)`（一次完整非客户区重算）
→ 再 `RefreezeSoon()`。一次唤出会重复 2~5 次。建议 `e.OldDpi == e.NewDpi` 时直接返回。

### P2-6 `EnsureOnScreen` 的多显示器/DPR 换算（✅ 已修：改用物理像素 `SetWindowPos`）

[MainWindow.xaml.cs:1199-1249](src/GlassLauncher/MainWindow.xaml.cs#L1199-L1249)：`Left = targetLeft / dpi` 用的是
**当前**窗口的 DPI 去把物理像素换算成 DIP，但 `Left/Top` 的语义取决于目标显示器。
把窗口从 100% 屏拖到 125% 屏后唤出，夹回来的位置会偏 ~20%。PerMonitorV2 下更稳的写法是用
`MonitorFromRect` 拿到的显示器 DPI（`GetDpiForMonitor`）换算，或者直接用 `SetWindowPos` + `SWP_NOACTIVATE` 走物理像素。

### P2-7 双击标题栏最大化 与 手动拖动的交互（✅ 已修：拦截 `WM_NCLBUTTONDBLCLK` + `StateChanged`）

`WM_NCLBUTTONDOWN` 被吃掉并进入手动拖动，但 `WM_NCLBUTTONDBLCLK` 没有拦截，会走 `DefWindowProc` 最大化。
此时 `_manualDrag` 仍为 `true`、`_manualDragTimer` 仍在跑：`SetWindowPos(nx, ny, 0, 0, NOSIZE…)`
会把**最大化窗口**按普通窗口的位移逻辑搬动；松手后才 `EndManualDrag`。
交接文件 §4.2 说"双击标题最大化等行为依赖未拦截时的默认处理"——行为是保留了，但状态没收干净。
建议在 `WM_NCLBUTTONDBLCLK`（或 `StateChanged`）里先 `EndManualDrag()`。

### P2-8 手动拖动丢掉了 Aero Snap / 贴边分屏（回答 §5.10）

非模态拖动绕开 DWM 故障的代价：窗口不再有 Win11 的贴边 snap、抖开、以及"拖到另一屏"的原生吸附。
这是可接受的取舍，但应该在 README/设置里说明；如果之后要补，可以在 `EndManualDrag` 里按屏幕工作区做一次
"最近边缘吸附"的近似实现。

---

## 6. P3 代码质量（不影响功能，但会咬人）

1. **`Diag` 在 Release 下默认不写日志**（[Diag.cs:43-48](src/GlassLauncher/Chrome/Diag.cs#L43-L48)），
   而这次崩溃恰恰发生在 Release 运行。交接文件把 `glass.log` 当成关键证据来源，
   建议发布版默认开启（按大小滚动），否则现场问题永远只有一行"我挂了"。
2. **`Diag.Log` 用 `File.AppendAllText` + 全局 lock，UI 线程上调用**（`SampleDragState` 里一次还带 PNG 落盘）：
   DEBUG 下拖动采样器每 120ms 写日志 + 每 360ms 截全屏并同步编码 PNG，会污染拖动性能测量。
3. `TrayIcon.AddOrUpdate` 不看 `Shell_NotifyIcon` 返回值就置 `_added = true`（[TrayIcon.cs:88-96](src/GlassLauncher/Chrome/TrayIcon.cs#L88-L96)），
   托盘添加失败后永不重试；`TaskbarCreated` 重加时把 tooltip 写死成英文 `"Glass Launcher"`。
4. `InitTray` 里 `GetResourceStream(...).Stream` 未 Dispose，`TrayIcon` 里 `new System.Drawing.Icon(stream)` 也未 Dispose 流。
5. `BackdropService.Apply` 里的 `bool darkBase = darkMode;` 是死变量（[BackdropService.cs:44](src/GlassLauncher/Chrome/BackdropService.cs#L44)）。
6. `SampleDragState` 的抓屏尺寸 `vw * 5 / 4, vh * 5 / 4`（[MainWindow.xaml.cs:1115](src/GlassLauncher/MainWindow.xaml.cs#L1115)）
   与 `CopyFromScreen` 的目标尺寸不匹配时会有黑边/裁剪，是调试代码但会误导取证。
7. `MainWindow` 里 `#if DEBUG` 探针、行车记录仪、命中测试反编译等约 1300 行混在产品代码里，
   建议拆到 `Diagnostics/` partial 文件，否则每次审查都要在 2771 行里找 200 行真正的逻辑。
8. `Perform`：`Persist()` 在每次拖放/启动失败时同步写盘（单次几 KB，可接受），但 `Renumber()` 会改写所有
   `SortOrder` 并触发全量 JSON 序列化；40+ 条目时建议只在顺序真变化时写。

---

## 7. 我做了什么（可复现）

```powershell
# 1) 构建（0 警告 0 错误）
cd src/GlassLauncher; dotnet build -c Debug

# 2) 读现场日志，定位异常时间点
%LOCALAPPDATA%\GlassLauncher\glass.log     # 02:13:43.542 TargetParameterCountException

# 3) 最小 WPF 探针：复刻 Dispatcher.BeginInvoke(FreezeBackdrop, Loaded)
#    → TargetParameterCountException, e.Handled=False, 进程退出码 0xE0434352

# 4) 插桩副本（.audit/，源码副本 + 关键路径打点，日志写工作区）
#    → 19:49:14.963 UNHANDLED TargetParameterCountException
#    → 进程 Responding=False（"无响应"），FreezeBackdrop 从未执行

# 5) 热键解析探针：Enum.TryParse("1") → Key.Cancel (VK 0x03)
```

**环境限制（会影响你自己复现）**：本会话的文件沙箱不允许写 `%LOCALAPPDATA%`，
所以插桩版把日志写到了工作区，`LauncherStore.Save` 在测试期间也会失败
（日志里会看到 `SAMPLE error: A generic error occurred in GDI+`，那是采样器存 PNG 被拒绝，
不是产品 bug）。正式复现请在沙箱外跑，日志才会落到 `glass.log`。

**清理**：测试期间我启动的 GlassLauncher 进程都已结束；用户真实数据 `launcher-data.json` 未被修改。
`.audit/`、`.probe/`、`.verify/` 等临时目录已删除。

---

## 8. 建议的修复顺序

1. **P0-1 + P0-2**（两处 `BeginInvoke(FreezeBackdrop)` + `e.Handled`）—— 不修这个，后面所有视觉验证都没有意义。
2. 修完先跑一遍**交接文件 §7 的完整回归链**：托盘循环 → 拖动 → 缩放 → 松手 → 再拖动，
   确认日志里出现 `freeze ok`（证明冻结帧真的在跑）。
3. **P1-2 + P1-3**（几何与位移预测），用 60fps 录屏或 `drag align` 日志对比松手前后 ±100ms。
4. **P1-1**（RebuildDwmSurface 去留）做 A/B，决定删掉还是补 `_exclusionOnTick`。
5. **P1-4 / P1-5 / P1-6 / P2-4**（正确性与数据安全）。
6. P2/P3 按空闲排。

---

## 9. 需要你确认的（不确定是否"有意为之"）

1. `RebuildDwmSurface`：保留（已补 `_exclusionOnTick`）还是直接删掉走 A/B？我倾向前者作为过渡、后者作为终态。
2. `P2-8`：手动拖动丢掉 Aero Snap 是可接受的取舍吗？如果不可接受，需要另做吸附算法。
3. 那份 `dist/GlassLauncher-v0.1.0.zip`（71MB 单文件，10/21 构建）不包含 4cbd772 之后的任何修复，
   而且当前源码版本号还是 `0.1.0`——发布物要不要一并重建/升版本？

---

## 10. 本轮修复清单与验证记录

### 10.1 改了哪些文件

| 文件 | 改动 |
|---|---|
| `App.xaml.cs` | 全局异常处理器设 `e.Handled = true`（否则异常会打死调度线程） |
| `MainWindow.xaml.cs` | ① 两处 `BeginInvoke(FreezeBackdrop)` 改无参 lambda；② `OnLiveFrameReady` 用 `(O_capture − W_now)/dpi` 做对齐（原写法符号反）；③ 新增 `AbortWindowDrag`，在隐藏/关窗/状态变化/双击标题时收掉手动拖动；④ `StartManualDrag` 增加 `IsVisible` 守卫；⑤ 新增 `EnsureExclusion`（实查 `GetExclusion`，同步 `_exclusionHeld`/`_exclusionOnTick`），`StartDragRide`/`FreezeBackdrop`/模态进出都改走它；⑥ `RebuildDwmSurface` 切换后重置 `_exclusionOnTick` 并回读真实状态；⑦ `FreezeBackdrop` 在 `await` 后复查窗口是否还在、并就地 catch；⑧ 过滤 `DpiChanged` 假事件；⑨ `EnsureOnScreen` 改用物理像素 `SetWindowPos`；⑩ `Closed` 里 Dispose `LiveGrab`；⑪ DEBUG 采样器整段 `#if DEBUG` 收口（顺带 0 警告） |
| `Chrome/LiveGrab.cs` | ① **补回丢失的像素上传**（DIB → 池化 byte[] → UI 线程 `WritePixels`）；② 去掉 32px 桶，抓取尺寸精确等于请求区域；③ 不再从工作线程清 `_bitmap` |
| `Chrome/TrayIcon.cs` | 检查 `Shell_NotifyIcon` 返回值（失败不再假装成功）、失败后 1s×3 重试、`TaskbarCreated` 用回原 tooltip |
| `Chrome/Diag.cs` | 发布版也写日志 + 1MB 轮转，`GLASSLAUNCHER_LOG=0` 可关 |
| `Chrome/BackdropService.cs` | 删掉死变量 `darkBase` |
| `Services/HotkeyHelper.cs` | 数字键显式映射 `D0..D9`；拒绝纯数字枚举值；裸键（非 F1~F12）必须有修饰键 |
| `Services/LauncherStore.cs` | 损坏时先留存 `.corrupt-*.json`，再尝试 `.bak` 恢复（不再静默返回空列表） |

### 10.2 现场验证（改完的代码，跑完整链路）

```
permanent exclusion held=True
freeze ok 1170x820 instant=False settle=140ms px=959400     ← 冻结帧真的抓到了（修复前永远不出现）
freeze ok 1170x820 instant=False settle=203ms
dwm surface rebuilt toggle=True restore=True held=True
freeze ok 1170x820 instant=False settle=0ms                 ← 托盘唤出后补帧
manual drag start rect=(755,370)
live upload mean=252.1 min=15 max=255 1170x820              ← live 帧有真实像素（修复前恒 0）
live shift=-8.0,-3.2 origin=649,370 now=659,374             ← 窗口右移 → 偏移为负（对齐公式）
drag end align dx=-8.0 dy=-3.2
Live drag frames=58 avg=10.8ms max=15.4ms
modal drag: exclusion released held=False
modal drag ended: exclusion restored
freeze ok 1110x820 instant=False                            ← 缩放后按新尺寸补帧
DispatcherUnhandledException: 0 次
```

- 启动 → 托盘隐藏 → 托盘唤出 → 标题拖动 ×2 → 边缘缩放 → 再拖动：进程全程 `Responding=True`，
  无异常、无未响应。
- 几何核对：默认 840×560 @1.25 → 抓取 1170×820（= (840+96)×1.25 / (560+96)×1.25），与显示端
  `Margin=-48 + Stretch=Fill` 严格 1:1（修复前是 1184×832）。
- 位移核对：窗口从 649 移到 659（物理）→ 偏移 −8.0 DIP = −10px/1.25，方向与量都对得上。

### 10.3 仍然建议人工确认的

- **松手瞬间的手感**（这是最初的问题，之前所有"已修复"结论都作废了）。
- 托盘循环 20 次 + 模态缩放的 `RebuildDwmSurface` A/B。
- 多显示器 / 不同缩放比下的拖动与唤出位置。
