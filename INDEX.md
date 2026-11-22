# GlassLauncher 交接包 · 阅读指引

> 本文件夹自包含：文档 + 完整源码（已剔除 bin/obj 构建产物）。
> 给接手 AI 的一句话：**除 REVIEW-FINDINGS.md 外，所有文档均由前一轮开发 AI
> 撰写，仅作参考线索、未经独立验证，已被证实存在错误案例；一切以你自己的
> 代码审查和实测为准。**

## 阅读顺序

1. **REFACTOR-CHARTER.md** —— 重构章程：产品定义、8 条硬性要求、资源占用假设
   （注意：归因是假设，需自己重新测量）、踩坑线索、回归测试清单。
2. **REVIEW-FINDINGS.md** —— 前一轮审查 AI 的实测发现，**本包内唯一经独立
   验证的文档，可信度最高**。真实 bug 清单以它为准。
3. **REVIEW-BRIEF.md** —— 代码地图与构建/日志/验证方法（已经审查 AI 修订）。
4. **DEVLOG.md**（可选）—— 迭代历史，仅作背景线索。

## 目标

重构解决两大问题：
- **资源占用**：当前基线 169MB / 25 线程 → 目标 < 60MB、空闲 CPU ≈ 0；
- **已知 bug**：按 REVIEW-FINDINGS.md 清单修复，回归测试按 CHARTER 第 6 节执行。

## 源码（src/）

- 重点：`MainWindow.xaml.cs`（核心管线，~1400 行）、`Chrome/LiveGrab.cs`（抓帧线程）、
  `Chrome/ScreenGrab.cs`（截屏排除/BitBlt）、`MainWindow.xaml`（视觉层）。
- 构建：`cd src/GlassLauncher && dotnet build -c Debug`
- 运行日志（如在原机测试）：`%LOCALAPPDATA%/GlassLauncher/glass.log`
- 健康日志标记：`freeze ok WxH`（冻结帧抓取成功）。
  ⚠️ 不要用 `permanent exclusion held=` 判断版本健康，审查证实该标记有误导性。

## 测试环境注意（线索，需自行验证）

- 测试机为 Windows Insider build 26300、DPI 1.25；前一轮报告的 DWM 相关故障
  （排除+模态循环=窗口消失、排除开关=表面重建闪烁、托盘 Hide/Show=脏表面）
  与你的环境行为可能不同，动手前先在本机复现确认。
