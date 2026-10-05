# 开发环境配置与源码构建指南

本文档面向参与 BetterPetitPlanet 开发或从源码自行编译的开发者，说明所需环境、未入库外部资源（OCR 模型与测试曲谱）的获取方式以及本地构建步骤。

> 如果你是玩家，无需阅读本文档或配置任何开发环境，直接前往 [GitHub Releases](https://github.com/Sonder9999/better-petit-planet/releases) 下载打包好的 `.zip` 压缩包解压即可使用。

---

## 1. 基础环境要求

- **操作系统**：Windows 10（版本 1903+，推荐 Windows 10 2004+ 或 Windows 11 22H2+）。
- **SDK 版本**：.NET 8.0 SDK（目标框架为 `net8.0-windows10.0.22621.0`）。
- **开发工具**：
  - Visual Studio 2022（17.8 及以上版本）或 JetBrains Rider / VS Code。
  - 若使用 Visual Studio，请勾选工作负载：**.NET 桌面开发（.NET Desktop Development）**，并确保包含 **Windows 11 SDK (10.0.22621.0)**。

---

## 2. 下载未入库的外部资源（重要）

开发者克隆源码后，需要前往项目的 [GitHub Releases](https://github.com/Sonder9999/better-petit-planet/releases) 页面，下载单独发布的附件并放入以下相对路径中：

1. **OCR 识别模型**（约 10 MB）：
   - 下载文件：`ch_PP-OCRv3_rec_infer.onnx`
   - 存放路径：`BetterPetitPlanet/Assets/Models/Ocr/ch_PP-OCRv3_rec_infer.onnx`
   - 说明：编译时 MSBuild 会自动将该目录下的文件复制到构建输出目录；若缺失该文件，DirectML OCR 自动拾取功能将无法启用。

2. **默认测试曲谱**：
   - 下载文件：`one_last_kiss.json`、`dao_xiang.json`
   - 存放路径：`data/songs/recorder/one_last_kiss.json` 与 `data/songs/recorder/dao_xiang.json`
   - 说明：供自动演奏模块在本地调试和打包时加载使用。

放置完成后的本地目录结构示意如下：

```text
better_petit_planet/
├── BetterPetitPlanet/
│   └── Assets/
│       └── Models/
│           └── Ocr/
│               └── ch_PP-OCRv3_rec_infer.onnx   <-- 放置 OCR 模型
└── data/
    └── songs/
        └── recorder/
            ├── one_last_kiss.json               <-- 放置测试曲谱 1
            └── dao_xiang.json                   <-- 放置测试曲谱 2
```

---

## 3. 核心原生依赖说明

本项目集成了硬件屏幕捕获、计算机视觉与 DirectML 显卡推理能力，所有原生动态链接库均通过 NuGet 在构建阶段自动还原，无需手动安装第三方运行时：

| 依赖模块 | 文件名称 | 来源与作用 |
| :--- | :--- | :--- |
| **DirectML** | `DirectML.dll` | 微软 DirectX 12 机器学习硬件加速库，提供跨品牌显卡（NVIDIA / AMD / Intel）OCR 推理加速。 |
| **ONNX Runtime** | `onnxruntime.dll` | 机器学习模型推理运行时，配合 `Microsoft.ML.OnnxRuntime.DirectML` 使用。 |
| **OpenCVSharp 原生库** | `OpenCvSharpExtern.dll` | 用于模板匹配、ROI 裁剪与图像矩阵处理。 |
| **WinRT 捕获投影** | Windows SDK 内置 | 系统级 `Windows.Graphics.Capture` 接口，无需额外外部依赖。 |

---

## 4. 本地编译与测试步骤

1. 克隆代码仓库：
   ```bash
   git clone https://github.com/Sonder9999/better-petit-planet.git
   cd better-petit-planet
   ```

2. 按上文第 2 节说明，将 `ch_PP-OCRv3_rec_infer.onnx` 及两首曲谱 `.json` 放入对应目录。

3. 还原 NuGet 依赖并编译解决方案：
   ```bash
   dotnet restore
   dotnet build
   ```

4. 运行自动化单元测试套件：
   ```bash
   dotnet test
   ```
   确保全部 65 项单元测试通过。
