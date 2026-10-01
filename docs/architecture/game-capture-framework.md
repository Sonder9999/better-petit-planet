# 图像捕获框架架构设计

## 1. 架构目标

在针对现代 3D 游戏（尤其是基于虚幻引擎 5 开发的游戏）进行图像捕获时，不同玩家的操作系统版本、显卡驱动、多显示器配置以及窗口显示模式（全屏独占、无边框全屏、窗口化）存在巨大差异。

为了保证捕获系统的高可用性、易测试性以及未来对新捕获 API（如 NvFBC、DirectX 显存共享纹理直读）的低成本扩充，`BetterPetitPlanet.GameCapture` 采用了基于**微内核与插件引擎分离**的架构模型。

---

## 2. 模块划分与拓扑

捕获子系统划分为 `Core` 契约核心与 `Engines` 独立引擎实现两个层级：

```text
BetterPetitPlanet.GameCapture/
├── Core/
│   ├── IGameCapture.cs            # 统一捕获引擎接口
│   ├── GameCaptureFrame.cs        # 帧图像内存容器与元数据
│   ├── CaptureEngineInfo.cs       # 引擎元信息与环境可用性标志
│   └── GameCaptureRegistry.cs     # 全局引擎注册表与发现管理器
└── Engines/
    ├── PrintWindow/               # 基于 Win32 硬件加速重绘的捕获引擎
    └── WindowsGraphicsCapture/    # 基于 WinRT/D3D11 的现代捕获引擎
```

---

## 3. 核心契约抽象

### 3.1 IGameCapture 契约
所有捕获引擎均必须实现 `IGameCapture` 接口，定义标准生命周期：

```csharp
public interface IGameCapture : IDisposable
{
    CaptureEngineInfo Info { get; }
    bool IsCapturing { get; }

    event EventHandler<GameCaptureFrame>? FrameArrived;

    bool Start(IntPtr targetHwnd);
    void Stop();
    GameCaptureFrame? CaptureSingleFrame();
}
```

- **统一生命周期**：业务调用方无需感知底层使用的是 WinRT 回调事件还是 Win32 轮询抓取，统一通过 `Start()` / `Stop()` 控制生命周期。
- **双模获取**：
  - 流式捕获（`FrameArrived` 事件）：供自动拾取高频识别调度器使用，每帧通知。
  - 单帧捕获（`CaptureSingleFrame()`）：供配置界面测试或单次调试诊断使用。

### 3.2 帧容器与内存管理（GameCaptureFrame）
捕获到的游戏画面经过封装后由 `GameCaptureFrame` 承载：
- 封装 GDI `Bitmap` 或 DirectX 纹理内存。
- 携带帧到达时间戳、分辨率（Width / Height）以及来源引擎类型。
- 实现 `IDisposable`，并在生命周期结束时严格释放关联的非托管 GDI 句柄或 DirectX 暂存缓冲区，防止高帧率捕获导致的内存与 GDI 对象泄漏。

---

## 4. 注册表与动态发现机制（GameCaptureRegistry）

`GameCaptureRegistry` 作为静态/单例管理器，负责整个系统内捕获引擎的收集、注册与检索：

1. **引擎注册**：在模块加载期注册所有已知实现类及其元信息。
2. **环境探测**：各引擎实现类通过 `CheckEnvironmentSupported()` 自检当前操作系统版本（如 Windows 10 1803+ 才能支持 WGC）与运行环境。
3. **友好展示**：对于当前环境不可用的模式，在下拉列表中追加星号（`*`）作为标注，提示用户该模式可能受限或不可用。
4. **运行时热切换**：用户在 UI 更改捕获模式后，任务调度器先停止当前引擎，从 Registry 获取新引擎实例并立即初始化运行，无需重启主程序。
