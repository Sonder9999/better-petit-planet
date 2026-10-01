# 基于 DirectML 的 GPU 加速 OCR 与自动拾取

在星布谷地中，交互选项、掉落物品拾取以及 NPC 对话确认通常以动态弹出的文字气泡和按键选项形式展现。BetterPetitPlanet 采用了基于 DirectML GPU 硬件加速的轻量化 OCR 引擎，实现了低延迟、高并发的文字识别与自动拾取判定。

---

## 1. DirectML 推理引擎设计

### 1.1 为什么选择 DirectML
在 Windows 生态下，传统深度学习推理引擎往往重度依赖 NVIDIA CUDA，对 AMD Radeon 或 Intel 集显/核显支持较弱或部署体积庞大（需打包数百 MB 的 Torch/CUDA 运行时）。

项目选用微软官方的 **ONNX Runtime + DirectML Execution Provider**：
- **全显卡兼容**：借助 DirectX 12 运算图层，全面支持 NVIDIA、AMD、Intel 各主流显卡硬件加速。
- **免安装驱动运行时**：直接调用 Windows 系统自带的 `DirectML.dll` 与 DirectX 12 驱动，分发包体积小巧且绿色纯净。
- **低 CPU 消耗**：图像张量预处理、卷积计算与后处理矩阵操作全量在 GPU 显存内执行，单次文字识别平均耗时仅 12ms - 20ms。

### 1.2 线程安全与上下文锁机制
DirectML 的命令队列（DirectX 12 Command Queue）在并发调用时存在上下文非线程安全的局限。
为了防止高频截图线程与界面测试线程同时调用 OCR 引起 GPU 驱动超时重置（TDR）或崩溃，引擎内置了双重保护：
- **互斥同步锁**：在执行 `InferenceSession.Run()` 时引入严格的非阻塞排队与重入锁保护。
- **显存张量生命周期隔离**：每次推理显式复用固定大小的 DirectML 绑定缓冲区，避免高频拾取时的显存碎片化。

---

## 2. 视口 ROI 变换与分辨率兼容状态

### 2.1 交互气泡 ROI 动态计算（PetitRoiCalculator）
游戏在不同分辨率和长宽比下的 UI 布局通常遵循等比缩放或锚点靠齐逻辑。`PetitRoiCalculator` 根据截取帧的实际分辨率动态推导识别候选框：

```csharp
public static Rectangle CalculateDialogOptionRoi(int frameWidth, int frameHeight)
{
    // 以中心基准线动态偏移定位交互选择肢区域
    int roiWidth = (int)(frameWidth * 0.22);
    int roiHeight = (int)(frameHeight * 0.35);
    int roiX = (int)(frameWidth * 0.58);
    int roiY = (int)(frameHeight * 0.42);
    
    return new Rectangle(roiX, roiY, roiWidth, roiHeight);
}
```

### 2.2 当前分辨率测试状态说明

> 重要的测试环境与范围说明：
> - **目前已完成实测与标定的分辨率**：**仅有 2K（2560 × 1440，16:9 纵横比）**。在此分辨率下，候选框位置、文字识别字号尺寸与触发判定已得到全面验证，运行稳定。
> - **待测试与标定的分辨率**：
>   - 1080P（1920 × 1080）
>   - 4K（3840 × 2160）
>   - 16:10 比例（如 2560 × 1600、1920 × 1200）
>   - 21:9 带宽比例（带黑边或视口延展）
>   对于上述待测试分辨率，当前算法虽然包含比例推导，但由于缺乏充足的真实游戏截帧校验，可能存在 ROI 框轻微偏移或小字号置信度降低的情况，后续将逐步补充测试用例完成全分辨率标定。

---

## 3. 拾取触发与防抖控制

自动拾取如果无节制高频触发，容易引起游戏按键队列拥塞甚至交互异常。调度器在 `TaskTriggerDispatcher` 中内置了流控管线：
1. **冷却时间窗口（CooldownMs）**：默认设置 50ms - 150ms 冷却间隔，只有在上次触发完成并冷却后才执行下一次检测。
2. **关键词白名单与黑名单过滤**：
   - 白名单：支持自定义需要优先拾取的珍贵道具名称。
   - 黑名单：过滤危险交互（如误点击离开场景、消耗关键道具的交互确认框）。
3. **连击防抖机制**：针对长按或连续拾取的场景，通过单次按下并在收到游戏画面反馈后再释放的闭环状态机，避免空发 `F` 键。
