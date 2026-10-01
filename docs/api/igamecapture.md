# IGameCapture 接口契约

命名空间：`BetterPetitPlanet.GameCapture`  
程序集：`BetterPetitPlanet.GameCapture.dll`

`IGameCapture` 是图像捕获子系统的核心契约接口。所有具体的捕获引擎（如 `WindowsGraphicsCaptureEngine`、`PrintWindowCapture`）均必须实现该接口。

---

## 1. 接口定义

```csharp
namespace BetterPetitPlanet.GameCapture;

public interface IGameCapture : IDisposable
{
    // 获取当前捕获引擎的静态元数据与可用性状态
    CaptureEngineInfo Info { get; }

    // 获取当前引擎是否正处于捕获循环运行中
    bool IsCapturing { get; }

    // 新帧到达事件通知（流式捕获）
    event EventHandler<GameCaptureFrame>? FrameArrived;

    // 绑定目标游戏窗口句柄并启动捕获
    bool Start(IntPtr targetHwnd);

    // 停止捕获并释放相关临时资源
    void Stop();

    // 单次抓取单一帧（用于测试与诊断）
    GameCaptureFrame? CaptureSingleFrame();
}
```

---

## 2. 核心数据结构

### 2.1 CaptureEngineInfo
```csharp
public class CaptureEngineInfo
{
    public string Id { get; set; }           // 引擎内部唯一标识 (如 "WindowsGraphicsCapture")
    public string DisplayName { get; set; }  // UI 显示名称
    public string Description { get; set; }  // 引擎特性描述
    public bool IsSupported { get; set; }    // 当前操作系统及硬件环境下是否可用
}
```

### 2.2 GameCaptureFrame
```csharp
public sealed class GameCaptureFrame : IDisposable
{
    public Bitmap Image { get; }             // GDI 位图对象
    public int Width => Image.Width;         // 画面宽度 (像素)
    public int Height => Image.Height;       // 画面高度 (像素)
    public DateTime Timestamp { get; }       // 帧到达时间戳
    public string EngineId { get; }          // 来源引擎标识

    public void Dispose();                   // 释放底层非托管位图与显存句柄
}
```

---

## 3. 实现准则与生命周期约束

1. **线程安全性**：
   - `FrameArrived` 事件通常在引擎内部的高速工作线程上触发。事件订阅方如果需要更新 UI 元素，必须通过 `Dispatcher.Invoke` 或 `SynchronizationContext` 调度至主 UI 线程。
2. **所有权与释放责任**：
   - 传递给 `FrameArrived` 的 `GameCaptureFrame` 实例通常在帧处理流水线完成或下一帧到来时被释放。
   - 订阅方如果需要持久化保留该帧图像，必须使用 `(Bitmap)frame.Image.Clone()` 创建独立副本。
