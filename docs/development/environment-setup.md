# 开发环境配置

本文档为参与 BetterPetitPlanet 项目的开发人员提供基础环境安装与工程依赖配置指引。

---

## 1. 基础环境要求

- **操作系统**：Windows 10（版本 1803 / 构建号 17134 及以上，推荐 Windows 10 2004+ 或 Windows 11 22H2+）。
- **SDK 版本**：.NET 8.0 SDK（`net8.0-windows10.0.22621.0` 目标平台支持）。
- **开发工具**：
  - Visual Studio 2022（推荐 17.8 及以上版本）
  - 勾选工作负载：**.NET 桌面开发（.NET Desktop Development）**
  - 单个组件确保包含：**C# 12 编译器**、**Windows 11 SDK (10.0.22621.0)**

---

## 2. 核心原生依赖说明

本项目集成了高性能计算、计算机视觉与硬件屏幕捕获能力，运行时依赖以下原生动态链接库（DLL）：

| 依赖模块 | 文件名称 | 来源与作用 |
| :--- | :--- | :--- |
| **DirectML** | `DirectML.dll` | 微软官方 DirectX 12 机器学习硬件加速库，提供跨品牌显卡（NVIDIA / AMD / Intel）OCR 推理加速。 |
| **ONNX Runtime** | `onnxruntime.dll` | 跨平台机器学习模型加载与执行运行时，配合 DirectML 执行 Provider。 |
| **OpenCVSharp 原生库** | `OpenCvSharpExtern.dll` | 针对模板匹配、颜色直方图比对与图像矩阵操作的原生 C++ 封装。 |
| **WinRT 捕获投影** | Windows SDK 内置 | 系统级 `Windows.Graphics.Capture` 接口，无需第三方外部二进制。 |

所有原生依赖通过 NuGet 语义化包（如 `Microsoft.ML.OnnxRuntime.DirectML`、`OpenCvSharp4.runtime.win`）自动拉取并在构建时输出至运行目录。

---

## 3. 本地工程初始化

1. 克隆代码仓库：
   ```bash
   git clone <repository-url>
   cd better_petit_planet
   ```

2. 还原 NuGet 包并编译解决方案：
   ```bash
   dotnet restore
   dotnet build
   ```

3. 运行自动化单元测试套件：
   ```bash
   dotnet test
   ```
   确保 55 项基准测试全部通过。
