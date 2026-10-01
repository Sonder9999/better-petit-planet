# 竖笛检测与多乐器兼容扩展架构

星布谷地包含乐器演奏玩法。当前工具已实现首个乐器——**竖笛（Recorder）**的自动识别、按键映射与曲谱驱动。本文档介绍竖笛识别实现机制与面向未来乐器接入的兼容扩展设计。

---

## 1. 乐器资产分类组织规范

为了避免不同乐器及其资源文件无序散落在项目中，乐器资源统一归档在 `BetterPetitPlanet/Assets/Instruments/` 下，按独立乐器子目录严格进行物理隔离：

```text
BetterPetitPlanet/Assets/Instruments/
└── Recorder/                          # 竖笛专属目录
    ├── Icons/                         # 乐器 UI 图标与匹配模板
    │   └── recorder_active_template.png
    ├── Config/                        # 音域范围与键位映射表
    │   └── layout.json
    └── Samples/                       # 试听音源或校准音频
```

### 规范约定
- 每个独立乐器均拥有独立的子目录（如 `Recorder/`）。
- 乐器特征模板图、特定键位规则文件均存放在对应乐器目录下，严禁在外部全局目录杂糅混放。

---

## 2. 竖笛状态实时检测算法

### 2.1 检测目标
在自动演奏或热键触发前，系统需要快速、准确判定玩家在游戏中是否正处于竖笛持握/演奏状态。

### 2.2 ROI 与图像特征检测实现
在 `InstrumentDetector.cs` 中实现竖笛特征比对：
1. **ROI 视口定位**：
   - 提取屏幕右下侧技能与操作栏固定比例区域作为感兴趣区域（ROI）。
2. **多尺度模板匹配**：
   - 使用 OpenCV 的标准化相关系数算法（`TemplateMatchingModes.CCoeffNormed`）将游戏当前帧的 ROI 与 `recorder_active_template.png` 进行比对。
3. **颜色特征双重校验**：
   - 提取竖笛高亮指示区域的主色调（如特定黄绿色光晕），防止其他圆形技能图标产生的假阳性误报。
4. **节流与置信度阈值**：
   - 置信度阈值设定为 0.82。单次检测控制在 5ms 内存计算开销内，支持每秒多频次检测而不会造成游戏掉帧。

---

## 3. 面向未来乐器的通用扩展设计

星布谷地在后续版本中可能会引入其他新类型的演奏乐器。鉴于未来乐器的界面形式、音域广度与交互逻辑尚未完全确定，系统在底层构建了一套面向未知乐器的通用抽象层：

### 3.1 乐器抽象契约（IInstrument）
```csharp
public interface IInstrument
{
    string InstrumentId { get; }
    string DisplayName { get; }
    
    // 音域定义（最低音至最高音 MIDI 编码）
    int MinMidiNote { get; }
    int MaxMidiNote { get; }
    
    // 将标准音符映射为键盘物理按键
    ConsoleKey? MapNoteToKey(int noteIndex);
    
    // 检测画面中是否正处于该乐器界面
    bool DetectActive(Bitmap frameRoi);
}
```

### 3.2 动态乐器注册与选择
- **多乐器统一接入**：后续若需要支持新乐器，只需在 `Assets/Instruments/<NewInstrument>/` 增加专属目录，并向系统注入实现了 `IInstrument` 的类。
- **自动适配曲谱**：演奏引擎仅与 `IInstrument` 的通用音域和键位映射接口交互，曲谱解析器会根据当前激活乐器的音域范围自动完成跨八度升降调或越界提示，实现业务逻辑与乐器特性的完全解耦。
