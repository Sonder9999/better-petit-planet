# BetterPetitPlanet 文档

BetterPetitPlanet 是针对游戏《星布谷地》（Petit Planet）开发的自动化与辅助工具箱，基于 .NET 8、WPF（Wpf.Ui Fluent Design）以及原生图形与计算接口（WinRT Windows Graphics Capture、DirectML、OpenCV）构建。

本目录（`docs/`）为项目的完整工程与技术文档中心，涵盖系统架构设计、核心技术实现、视觉规范、历史方案对比评估以及开发者指南。

---

## 目录索引

### 1. 系统架构 (`architecture/`)
- [图像捕获抽象框架](architecture/game-capture-framework.md)：`IGameCapture` 统一接口、`GameCaptureRegistry` 引擎发现与运行时热切换、帧数据跨线程生命周期管理。

### 2. 技术实现 (`technical/`)
- [捕获引擎技术实现与评估](technical/capture-engines.md)：WindowsGraphicsCapture 与 PrintWindow 硬件加速方案深入剖析，明确标注其适用边界与可用性限制。
- [竖笛检测与多乐器兼容扩展](technical/recorder-and-instruments.md)：竖笛特征 ROI 识别算法、乐器资源分类存储规范，以及面向未来乐器接入的通用扩展设计。
- [简谱解析与演奏调度引擎](technical/music-parser-and-playback.md)：简谱语法树解析、高精度微秒级时间调度器、前后台按键模拟实现。
- [DirectML GPU 加速 OCR 与自动拾取](technical/directml-ocr-autopick.md)：基于 DirectML 的 ONNX Runtime OCR 推理流控与视口自适应变换（明确标注目前仅测试 2K 分辨率）。
- [官网主视觉动态逆向解析](technical/dynamic-official-cover.md)：星布谷地官网 Nuxt 分块动态哈希解析、内存流式三层视差呈现（说明未来动态动画支持规划）。

### 3. 视觉与交互设计 (`design/`)
- [封面 Banner 几何裁剪工程实践](design/banner-geometry-clipping.md)：WPF 容器圆角裁剪机理剖析、规避 OpacityMask 负边距拉伸失真与 ClearType 次像素保护实战。
- [Fluent UI 视觉规范与暗色主题](design/fluent-ui-and-theme.md)：WPF-UI 现代 Fluent 控件体系与主题层次规范。

### 4. 技术验证与失败方案复盘 (`archive/`)
- [前后台捕获与输入尝试全景复盘](archive/capture-and-input-attempts.md)：系统梳理前台与后台图像捕获（BitBlt、DwmSharedSurface、PrintWindow、WGC）与输入模拟（SendInput、PostMessage、桌面分身）的尝试结果，提供失败机理分析与可行性对比总览表。

### 5. 开发者指南 (`development/`)
- [开发环境配置](development/environment-setup.md)：.NET 8 SDK、Visual Studio 配置、原生依赖库（DirectML、ONNX Runtime、OpenCV）配置指南。
- [代码与工程规范](development/coding-standards.md)：项目命名约定、去历史化约束、资源释放规范与文档标准。
- [编译与发布流程](development/build-and-release.md)：Release 产物发布、自包含与依赖型打包指令规范。

### 6. 核心接口契约 (`api/`)
- [IGameCapture 接口契约](api/igamecapture.md)：捕获引擎标准化契约与帧数据结构说明。
- [乐器与演奏系统接口](api/instrument-system.md)：乐器状态检测器与音符触发契约。

---

## 总体系统架构

BetterPetitPlanet 采用分层解耦与依赖倒置架构设计，各核心子系统独立打包，边界明确：

```text
+-----------------------------------------------------------------------+
|                       BetterPetitPlanet (主程序)                       |
|   +-------------------+  +--------------------+  +----------------+   |
|   |    Views / UI     |  |     ViewModels     |  |   Services     |   |
|   |   (Wpf.Ui Fluent) |  | (CommunityToolkit) |  | (DI Container) |   |
|   +-------------------+  +--------------------+  +----------------+   |
+-----------------------------------+-----------------------------------+
                                    |
     +------------------------------+------------------------------+
     |                              |                              |
     v                              v                              v
+-------------------------+ +-----------------------+ +---------------------+
| BetterPetitPlanet.      | | BetterPetitPlanet.    | | BetterPetitPlanet.  |
| GameCapture             | | WindowsInput          | | HotkeyCapture       |
| - Core (契约与注册表)    | | - 前台 SendInput 模拟 | | - 全局 Win32 热键   |
| - Engines/WGC (WinRT)   | | - 后台 Windows 消息   | | - 快捷键调度器      |
| - Engines/PrintWindow   | | - 虚拟键位转换映射    | |                     |
+-------------------------+ +-----------------------+ +---------------------+
```

### 核心分层设计原则
1. **主程序（BetterPetitPlanet）**：负责 UI 展示、ViewModel 数据绑定、页面路由、应用配置持久化（`IConfigService`）以及通过 `Microsoft.Extensions.DependencyInjection` 进行全局服务装配。
2. **捕获子系统（GameCapture）**：提供硬件与系统级帧捕获，Core 模块不依赖具体引擎实现，业务层仅通过 `IGameCapture` 接口消费画面数据。
3. **输入子系统（WindowsInput）**：隔离操作系统底层 API，提供统一的前台物理模拟与后台窗口消息调用接口。
4. **热键子系统（HotkeyCapture）**：独立捕获全局系统级按键事件，向任务派发器广播触发信号。
