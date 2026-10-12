# 编译与发布流程

本文档说明 BetterPetitPlanet 项目的单元测试验证、Release 打包脚本执行以及 GitHub Releases 发布规范。

---

## 1. 自动化测试验证

在执行打包或发布之前，请先运行单元测试套件：

```bash
dotnet test
```

确保包含捕获诊断、乐器检测、配置同步与 OCR 黑白名单过滤在内的全部测试用例均通过（Failed: 0）。

---

## 2. 使用发布脚本一键打包

仓库根目录提供了 PowerShell 发布脚本 `publish_release.ps1`，可执行完整构建与资源装配：

```powershell
.\publish_release.ps1
```

该脚本会自动完成以下步骤：
1. 编译 64 位 Windows 平台 Release 产物（输出至 `release/` 目录）。
2. 将 `BetterPetitPlanet/Assets/Models/Ocr/` 下的识别模型 `ch_PP-OCRv3_rec_infer.onnx` 和检测模型 `ch_PP-OCRv3_det_infer.onnx` 复制至 `release/Assets/Models/Ocr/`（项目资源配置同时用于构建和发布）。
3. 清理 `release/data/songs/` 下的多余文件，仅将以下三首默认测试曲谱复制至 `release/data/songs/recorder/`：
   - `dao_xiang.json`
   - `one_last_kiss.json`
   - `see_you_again.json`

若需使用命令行仅编译核心程序（不含外部曲谱拷贝），也可直接运行：

```bash
dotnet publish BetterPetitPlanet\BetterPetitPlanet.csproj -c Release -r win-x64 --self-contained false -o release
```

---

## 3. Release 目录产物结构

打包完成后，`release/` 目录包含用户开箱即用的完整结构：

```text
release/
├── BetterPetitPlanet.exe                  # 应用程序主执行入口
├── BetterPetitPlanet.dll                  # 主程序程序集
├── BetterPetitPlanet.GameCapture.dll      # 捕获子系统程序集
├── BetterPetitPlanet.WindowsInput.dll     # 输入子系统程序集
├── BetterPetitPlanet.HotkeyCapture.dll    # 全局热键子系统程序集
├── DirectML.dll                           # DirectML 硬件加速库
├── onnxruntime.dll                        # ONNX Runtime 推理库
├── OpenCvSharpExtern.dll                  # OpenCV 原生运算库
├── Assets/
│   ├── Instruments/Recorder/              # 竖笛图标与按键识别模板
│   └── Models/Ocr/
│       ├── ch_PP-OCRv3_rec_infer.onnx     # OCR 识别模型（约 10MB）
│       └── ch_PP-OCRv3_det_infer.onnx     # 物品名辅助检测模型（约 2.3MB）
└── data/
    └── songs/
        └── recorder/
            ├── dao_xiang.json             # 默认测试曲谱 1
            ├── one_last_kiss.json         # 默认测试曲谱 2
            └── see_you_again.json         # 默认测试曲谱 3
```

---

## 4. GitHub Releases 发布附件规范

发布新版本到 GitHub Releases 时，附件（Assets）应包含以下两部分，兼顾普通用户与源码开发者：

1. **一体化压缩包**（如 `BetterPetitPlanet-win-x64.zip`）：
   - 将上述 `release/` 目录打包为 `.zip`，内部已包含可执行程序、双 OCR 模型以及三首默认曲谱，用户下载解压即可直接运行。
2. **独立资源文件**：
   - `ch_PP-OCRv3_rec_infer.onnx`（放入 `BetterPetitPlanet/Assets/Models/Ocr/`）
   - `ch_PP-OCRv3_det_infer.onnx`（放入 `BetterPetitPlanet/Assets/Models/Ocr/`）
   - `dao_xiang.json`（放入 `data/songs/recorder/`）
   - `one_last_kiss.json`（放入 `data/songs/recorder/`）
   - `see_you_again.json`（放入 `data/songs/recorder/`）
