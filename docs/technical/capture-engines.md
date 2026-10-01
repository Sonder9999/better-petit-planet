# 捕获引擎技术实现与评估

BetterPetitPlanet 当前内置两种主要捕获引擎：`WindowsGraphicsCapture`（推荐主力引擎）与 `PrintWindow`（兼容回退引擎）。本文档深入剖析两者的底层机理、性能指标与环境适用边界。

---

## 1. WindowsGraphicsCapture (WGC) 引擎

### 1.1 技术原理
`WindowsGraphicsCaptureEngine` 基于 Windows 10（版本 1803+）引入的 WinRT 现代屏幕捕获 API（`Windows.Graphics.Capture`）构建。

- **Direct3D11 管道**：创建与显卡物理适配的 `ID3D11Device`，并建立 `Direct3D11CaptureFramePool` 帧缓冲池。
- **显存级共享**：DWM（桌面窗口管理器）直接将游戏交换链表面拷贝至帧缓冲池中的 Direct3D 纹理，不经过系统内存中转，具备极高的帧率（实测轻松跑满 60fps 以上）与极低的 CPU 占用。
- **无遮挡干扰**：通过 `GraphicsCaptureItem.CreateFromVisual(HWND)` 绑定目标游戏窗口句柄，即便游戏窗口被其他普通窗口（如浏览器、记事本）遮挡，捕获到的仍然是游戏画面的完整内容。

### 1.2 适用边界与限制
- **操作系统要求**：最低要求 Windows 10 1803（构建号 17134）以上，推荐 Windows 10 2004+ 或 Windows 11。
- **窗口状态限制**：当游戏窗口被最小化（Minimized）至任务栏时，DWM 会停止为其分配渲染表面，捕获会暂时挂起或保持最后一帧，直至窗口恢复显示。

---

## 2. PrintWindow 引擎

### 2.1 技术原理
`PrintWindowCapture` 基于 Win32 GDI / DWM 协作 API 构建，核心调用如下：

```csharp
[DllImport("user32.dll", SetLastError = true)]
private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
```

- **渲染标志位**：必须使用 `PW_RENDERFULLCONTENT = 0x02`（Windows 8.1+ 引入）。此标志位通知 DWM 协调窗口关联的 DirectX 硬件加速图层，重新绘制并投影到调用方提供的设备上下文（HDC）中。
- **双缓冲内存管理**：在宿主进程内存中分配兼容量化 DIBSection（兼容位图），利用 `BitBlt` 或 `GetDIBits` 完成像素提取。

### 2.2 局限性与可能不可用情况标注

> 重要的可用性风险说明：PrintWindow 引擎在不同软硬件环境下表现极不稳定，强烈建议仅作为辅助或备用选项。

经深度测试，PrintWindow 在以下典型场景中**可能完全不可用或呈现异常**：
1. **纯黑画面（Black Screen）**：
   - 虚幻引擎 5（UE5）等现代游戏普遍采用 DirectX 12 独占翻转交换链（DXGI_SWAP_EFFECT_FLIP_DISCARD）。部分显卡驱动在此模式下会拒绝响应 `PW_RENDERFULLCONTENT` 请求，导致抓取的 DC 内容完全为纯黑色（RGB 全部为 0）。
2. **性能开销较高与丢帧**：
   - PrintWindow 每次调用都会强迫 DWM 触发一次上下文刷新，并将 GPU 显存纹理逐行拷贝（Read-back）回系统内存 CPU 空间。在 2K 或更高分辨率下，频繁调用会显著增加 DWM 进程与本程序的 CPU 开销，难以稳定维持 30fps 以上捕获。
3. **窗口最小化失效**：
   - 游戏窗口最小化时，`PrintWindow` 返回 `false` 或截取到空白垃圾数据。
4. **系统版本差异**：
   - 在某些特定 Windows 10 版本或关闭了硬件加速计划（HAGS）的系统上，`PW_RENDERFULLCONTENT` 表现各异，无法保证普适可用。

---

## 3. 两款引擎横向对比

| 指标 / 特性 | WindowsGraphicsCapture (WGC) | PrintWindow (PW_RENDERFULLCONTENT) |
| :--- | :--- | :--- |
| **推荐等级** | 首选推荐（默认启用） | 备用（存在不可用风险） |
| **最低系统要求** | Windows 10 1803+ | Windows 8.1+ |
| **图形管线架构** | 纯 GPU 显存级拷贝（Direct3D 11） | DWM 触发重绘并拷贝至 CPU 内存 |
| **典型帧捕获延迟** | 1ms - 3ms | 15ms - 40ms |
| **被其他窗口遮挡** | 完美捕获（不受遮挡影响） | 受遮挡影响极小（部分驱动有黑块风险） |
| **全屏独占游戏支持** | 稳定支持无边框/窗口化 | 独占全屏下基本完全黑屏 |
| **DX12 翻转模型兼容性**| 极高 | 较低（部分硬件下黑屏） |
| **内存与 CPU 负载** | 极低 | 较高 |
