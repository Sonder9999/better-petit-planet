# 编译与发布流程

本文档定义 BetterPetitPlanet 项目的标准构建、自动化测试与 Release 分发包发布流程。

---

## 1. 自动化测试验证

在执行任何发布操作之前，必须运行全量单元测试套件：

```bash
dotnet test
```

确保包含 `InstrumentDetectorTest`、`LiveCaptureDiagnosticTest`、`OfficialCoverServiceTest` 在内的所有测试用例均通过，且无任何失败（Failed: 0）。

---

## 2. 标准 Release 构建发布

主程序通过 .NET CLI 编译并打包为针对 64 位 Windows 平台的发布产物：

```bash
dotnet publish BetterPetitPlanet\BetterPetitPlanet.csproj -c Release -r win-x64 --self-contained false -o release
```

### 构建参数解析
- `-c Release`：启用编译器完整优化，剥离调试符号与冗余断言。
- `-r win-x64`：明确目标平台为 Windows x64 架构。
- `--self-contained false`：依赖目标主机已安装的 .NET 8 运行时，显著减小分发包体积。
- `-o release`：将最终输出物规整归档至仓库根目录下的 `release/` 文件夹。

---

## 3. 发布目录产物结构说明

编译成功后，`release/` 目录将包含以下核心组件：

```text
release/
├── BetterPetitPlanet.exe                  # 应用程序主执行入口
├── BetterPetitPlanet.dll                  # 主程序程序集
├── BetterPetitPlanet.GameCapture.dll      # 捕获子系统程序集
├── BetterPetitPlanet.WindowsInput.dll      # 输入子系统程序集
├── BetterPetitPlanet.HotkeyCapture.dll    # 全局热键子系统程序集
├── DirectML.dll                           # DirectML 硬件加速库
├── onnxruntime.dll                        # ONNX Runtime 推理库
├── OpenCvSharpExtern.dll                  # OpenCV 原生运算库
├── Assets/                                # 静态资产（乐器资源、图标等）
└── config.json                            # 默认配置文件模版
```
