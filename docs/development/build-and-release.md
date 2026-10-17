# 编译与发布流程

本文档说明 BetterPetitPlanet 项目的纯原生构建环境要求、自动化测试验证、Release 打包脚本执行以及 GitHub Releases 发布规范。

---

## 1. 纯原生构建与运行环境要求

### 1.1 开发构建环境
- **.NET SDK**：.NET 8.0 SDK（目标框架：`net8.0-windows10.0.22621.0`）。
- **IDE / 构建工具**：Visual Studio 2022（需安装“.NET 桌面开发”工作负载）或 `dotnet` CLI。
- **原生 WPF 构建**：音乐谱面浮层由 WPF 原生图形管线承载。当前主项目文件和发布脚本没有声明 Node.js/Bun、WebView2 或前端 dist 构建步骤；常规构建使用 `dotnet build` 或 `dotnet publish`。

### 1.2 最终用户运行环境
- **操作系统**：Windows 10 / Windows 11 64 位（推荐 21H2 或更高版本）。
- **权限要求**：必须**以管理员身份运行**（用于 Windows 屏幕捕获与全局物理按键模拟）。
- **硬件要求**：支持 DirectX 12 的显卡（用于 DirectML GPU 硬件加速 OCR 推理）。
- **浮层运行依赖**：项目文件没有 WebView2 包引用，原生浮层由应用内 WPF 窗口或 DirectComposition 独立透明宿主承载，不创建额外的浏览器子进程。
- **.NET 运行时**：发布脚本使用自包含模式，会把 .NET 运行时带入产物；依赖框架的发布方式则要求目标机器安装相应 .NET Desktop Runtime。

---

## 2. 自动化测试验证

在执行打包或发布之前，请先运行单元测试套件：

```powershell
dotnet test Test/BetterPetitPlanet.Test/BetterPetitPlanet.Test.csproj -c Release -p:Platform=x64 --logger "console;verbosity=minimal"
```

确保包含捕获诊断、乐器检测、配置同步、OCR 黑白名单过滤以及音乐解析/原生渲染在内的全量测试用例均通过（Failed: 0）。

---

## 3. 使用发布脚本一键打包

仓库根目录提供了 PowerShell 发布脚本 `publish_release.ps1`，执行完整的自包含编译与资源组装：

```powershell
.\publish_release.ps1
```

该脚本会自动完成以下步骤：
1. **处理现有产物**：检测 `release/` 目录下少数被运行实例占用的程序文件并尝试更名。脚本不会清空整个 `release/`；`dotnet publish` 也不会保证移除输出目录里的任意旧文件。脚本总会删除并重建 `release/data/songs/recorder/`，即使没有歌曲源文件；仅当 `data/music-skins/` 源目录存在时才会删除并替换 `release/data/music-skins/`，源目录缺失时只告警并保留已有目标目录。
2. **自包含 ReadyToRun 发布**：执行 `dotnet publish`，生成面向 64 位 Windows 平台（`win-x64`）的自包含产物，并启用 ReadyToRun 编译（输出至 `release/` 目录）。ReadyToRun 不等于 Native AOT。
3. **曲谱装配**：若源文件存在，则复制发布版默认推荐的演示曲谱至 `release/data/songs/recorder/`：
   - `dao_xiang.json`
   - `one_last_kiss.json`
   - `see_you_again.json`
4. **皮肤与模型装配**：若 `data/music-skins/` 存在则复制皮肤目录；若 OCR 模型文件在脚本配置的候选位置存在，则复制至 `release/Assets/Models/Ocr/`。缺少皮肤源目录时脚本会告警但不会删除旧皮肤；缺少 OCR 模型候选文件时脚本会跳过复制且不会删除目标目录中已有模型。发布目录因此可能混有上一次构建留下的资源。

`publish_release.ps1` 固定写入仓库的 `release/`。要验证干净产物，应将发布到真正全新的空目录；若复用 `release/`，须在发布前后逐项盘点内容，不能根据“源资源缺失”或脚本告警推断目标文件不存在。

若需使用命令行仅编译核心程序（不含模型与曲谱复制），也可直接运行：

```powershell
dotnet publish BetterPetitPlanet/BetterPetitPlanet.csproj -c Release -r win-x64 --self-contained false -o release
```

---

## 4. Release 目录产物结构

资源齐全且发布后检查通过时，`release/` 可作为自包含产物使用。主程序集和构建配置不包含 WebView2 包依赖，原生 WPF 与 DirectComposition 浮层也不需要前端 dist 文件：

```text
release/
├── BetterPetitPlanet.exe                  # 应用程序主执行入口
├── BetterPetitPlanet.dll                  # 主程序程序集（内含原生悬浮窗与矢量默认皮肤）
├── BetterPetitPlanet.GameCapture.dll      # 屏幕画面捕获子系统程序集
├── BetterPetitPlanet.WindowsInput.dll     # 键盘与鼠标按键模拟子系统程序集
├── BetterPetitPlanet.HotkeyCapture.dll    # 全局 Win32 热键子系统程序集
├── DirectML.dll                           # DirectML 硬件加速库
├── onnxruntime.dll                        # ONNX Runtime 跨平台推理运行时
├── OpenCvSharpExtern.dll                  # OpenCV 原生机器视觉算法库
├── Assets/
│   ├── Instruments/Recorder/              # 竖笛特征图标与按键模板
│   └── Models/Ocr/
│       ├── ch_PP-OCRv3_rec_infer.onnx     # OCR 字符识别模型（约 10MB）
│       └── ch_PP-OCRv3_det_infer.onnx     # 物品名称辅助检测模型（约 2.3MB）
└── data/
    └── songs/
        └── recorder/
            ├── dao_xiang.json             # 默认测试曲谱 1
            ├── one_last_kiss.json         # 默认测试曲谱 2
            └── see_you_again.json         # 默认测试曲谱 3
```

---

## 5. GitHub Releases 发布附件规范

发布新版本到 GitHub Releases 时，附件（Assets）应包含以下两部分，兼顾普通用户与源码开发者：

1. **一体化压缩包**（如 `BetterPetitPlanet-win-x64.zip`）：
   - 将通过内容核对的 `release/` 目录整体压缩为 `.zip`。自包含产物包含主程序及其 .NET 运行时，解压即可运行。
2. **独立模型与曲谱资源文件**：
   - `ch_PP-OCRv3_rec_infer.onnx`（供源码克隆开发者放入 `BetterPetitPlanet/Assets/Models/Ocr/`）
   - `ch_PP-OCRv3_det_infer.onnx`（供源码克隆开发者放入 `BetterPetitPlanet/Assets/Models/Ocr/`）
   - `dao_xiang.json`（供放入 `data/songs/recorder/`）
   - `one_last_kiss.json`（供放入 `data/songs/recorder/`）
   - `see_you_again.json`（供放入 `data/songs/recorder/`）
