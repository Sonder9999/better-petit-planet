# 自动拾取物品名称辅助日志

## 使用

在“实时触发”的“自动拾取”卡片中开启“识别物品名称并记录日志”。默认关闭，旧配置缺少此字段时也保持关闭；该开关不会自动开启自动拾取。识别成功时记录：

```text
【自动拾取】拾取到物品: 红向日葵
```

日志表示触发拾取时画面中最近的物品名称，不是游戏背包到账确认。识别不参与黑白名单、冷却或拾取按键判断。无文字、名字不在搜索区、模型不可用及推理异常均静默跳过。

## 执行顺序与资源所有权

1. `AutoPickTrigger` 继续通过原固定 ROI（X 0.660–0.835、Y 0.570–0.660）识别“拾取”，执行原有黑白名单与冷却判断。
2. 首先调用原 `InjectKeyPress`，提交原按键任务；辅助代码不在此调用之前运行。
3. `ItemNameRecognitionService.TryQueue` 检查开关并通过 `Interlocked` 获取单任务槽位。忙时立即跳过，不排队、不等待推理。
4. 在 `CaptureContent` 释放前，仅复制中心搜索 ROI。后台任务拥有该副本并在 `finally` 释放。裁剪复制发生在按键任务提交后，检测、识别和首次模型加载都在 `Task.Run` 内。
5. 工作线程首次使用时创建 DBNet 和独立 `DirectMlOcrEngine`。识别代码与模型复用现有实现，但实例不与主拾取共用，避免争用其推理锁。关闭开关后不接受新任务，运行中结果在关闭后不记录。
6. DI 容器退出时释放服务；进行中的推理拥有释放责任，避免在推理期间销毁 ONNX 会话，也不阻塞退出等待 GPU。

异步辅助工作仍消耗 CPU、GPU 和内存。这里保证的是拾取代码不等待辅助推理；无法保证共享硬件上的游戏帧率或线程调度耗时完全不变。繁忙时跳过的拾取可能没有名称日志。

## 检测与识别

- 默认搜索范围：X 0.20–0.75，Y 0.25–0.75。角色锚点为整帧 `(0.50W, 0.55H)`，换算到裁剪坐标后比较距离。
- PP-OCRv3 DBNet 使用 BGR 通道、ImageNet 均值/标准差归一化；最长边限制为 960，尺寸对齐 32。
- DirectML 会话设置 `ORT_SEQUENTIAL`、关闭 memory pattern。可配置初始化失败时回退 CPU。
- 概率图二值阈值 0.3，旋转矩形内平均概率阈值 0.5。Unclip 距离为矩形面积乘 1.5 再除以周长，按此距离扩张旋转矩形，最终返回原图坐标的外接轴对齐框并裁到边界。
- 排除中心落在原固定拾取提示 ROI 内的框，再选择距离角色锚点最近的候选。只识别该候选，不把远处文字补作物品名。
- 原分辨率文字裁剪交给独立的现有 PP-OCR 识别引擎。默认至少 2 字符，抑制衣服纹理等单字符误检；空串和包含“拾取”的文本不记录。

这属于最近文字候选的启发式定位，不能识别物品语义，也不保证所有视角都能找到气泡。低视角导致气泡移出搜索区时返回空是预期行为。

## 模型与配置

检测模型位于 `BetterPetitPlanet/Assets/Models/Ocr/ch_PP-OCRv3_det_infer.onnx`，与识别模型同目录。其 SHA-256 为：

```text
3439588C030FAEA393A54515F51E983D8E155B19A2E8ABA7891934C1CF0DE526
```

模型相对路径基于程序运行目录；不依赖外部 Python 环境。项目构建与发布时会自动将模型复制到 `release/Assets/Models/Ocr/`。

开关位于现有 `config/config.json` 的 `AutoPick.ItemNameRecognition.Enabled`。高级参数见 [配置示例](item-name-config.example.json)，只合并对应配置节点，不替换其他用户设置。开关立即生效；修改检测模型、阈值或最短长度后重启程序重新创建推理实例。非法 ROI 跳过，非法检测配置不影响拾取。

## 验证与测试指引

### 1. 自动化单元测试
自动测试覆盖 DBNet 概率阈值、坐标映射、边界、BGR/BGRA/灰度输入、真实 DirectML 推理、损坏或缺失模型、默认关闭、旧配置/空节点兼容、设置保存、最近框选择、任务繁忙、截图副本、异常静默、关闭后不写日志及推理结束后的释放。日常开发直接运行常规测试命令即可：

```bash
dotnet test
```

### 2. 真实截图批量回归测试
开发者如需对端到端文字检测与识别进行批量回归校验，可按照以下流程操作：

#### 步骤一：采集游戏截图
1. **分辨率**：建议在基准 2K 分辨率（2560 × 1440，16:9）下采集。
2. **触发画面**：当角色靠近可拾取目标、画面出现交互选项时截取全屏游戏原图（保存为 `.png` 格式）。
3. **样本建议**：
   - 包含正常物品名称气泡（如不同花卉、掉落材料等）；
   - 包含无物品名的交互场景（如单纯按键提示或无文字气泡）；
   - 气泡位于边缘或视角极低导致位于搜索 ROI 之外的场景（预期返回空结果）。
4. **命名建议**：建议使用易读的语义文件名（如 `01_红向日葵.png`、`08_无名称_1.png`），便于人工核验与测试定位。

#### 步骤二：准备 JSON 标注文件
参考仓库提供的模板文件 `Test/BetterPetitPlanet.Test/Fixtures/item-name-samples.example.json`。标注格式为纯键值对映射：

```json
{
  "01_红向日葵.png": "红向日葵",
  "02_黄向日葵_1.png": "黄向日葵",
  "08_无名称_1.png": ""
}
```

- **Key**：必须与截图文件名保持完全一致。
- **Value**：该截图中心区域预期识别出的准确物品名；若无名称或预期不产生物品名，填空字符串 `""`。
- **存放路径（二选一）**：
  - **方式 A（推荐）**：直接放置在截图样本目录下，命名为 `item-name-samples.json`（测试程序会优先检测并读取样本目录内的标注文件）。
  - **方式 B**：放置在工程目录 `Test/BetterPetitPlanet.Test/Fixtures/item-name-samples.json`。

#### 步骤三：运行回归测试
通过环境变量指定样本目录（必须为相对路径）：

```powershell
$env:PETIT_ITEM_NAME_SAMPLES = '../../pick_images'
dotnet test Test/BetterPetitPlanet.Test/BetterPetitPlanet.Test.csproj -p:Platform=x64
```

- 测试程序将遍历该目录下所有 `.png` 文件并与标注对照，对不匹配项产生断言提示。
- 测试过程中绘制的检测候选框（`*-detected.jpg`）与文字裁剪切片（`*-text.png`）会自动输出至 `Test/BetterPetitPlanet.Test/Validation/item-names/`，方便可视化核对。

### 3. 设置页离屏渲染测试
设置页提供了独立进程的离屏 WPF 渲染测试，用于验证开关实际可见、默认关闭及双向绑定保存：

```powershell
$env:PETIT_RENDER_ITEM_SETTINGS = '1'
dotnet test Test/BetterPetitPlanet.Test/BetterPetitPlanet.Test.csproj -p:Platform=x64 --filter FullyQualifiedName~ItemNameSettingsRenderingTest
Remove-Item Env:PETIT_RENDER_ITEM_SETTINGS
```

由于 WPF 的 `Application` 是进程单例，该测试必须单独运行，默认显式跳过。渲染截图输出至 `Test/BetterPetitPlanet.Test/Validation/item-names/settings.png`。
