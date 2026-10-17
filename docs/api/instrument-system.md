# 多乐器识别系统 API 接口规范与契约

命名空间：
- `BetterPetitPlanet.GameTask.Music.Instrument`
- `BetterPetitPlanet.GameTask.Music.Service`

本文档规范多乐器识别配置档案、注册中心、检测器以及播放就绪门控的公共 API 契约。

---

## 1. 乐器配置与注册中心 (Instrument Profile & Registry)

### 1.1 `IInstrumentRegistry` 接口
```csharp
namespace BetterPetitPlanet.GameTask.Music.Instrument;

public interface IInstrumentRegistry
{
    // 获取指定乐器配置（未找到抛出 KeyNotFoundException）
    InstrumentProfile GetProfile(string id);

    // 尝试获取指定乐器配置（支持大小写不敏感与别名匹配）
    bool TryGetProfile(string id, out InstrumentProfile? profile);

    // 获取所有已注册的乐器配置档案
    IReadOnlyCollection<InstrumentProfile> GetAllProfiles();

    // 注册自定义乐器档案
    bool RegisterProfile(InstrumentProfile profile);

    // 校验特定乐器档案的模板图完整性与有效性
    InstrumentValidationResult ValidateProfile(string id, string? baseDirectory = null);

    // 校验所有乐器档案
    IReadOnlyList<InstrumentValidationResult> ValidateAllProfiles(string? baseDirectory = null);

    // 获取指定乐器基准分辨率 (2560x1440) 下的点击中心坐标
    bool TryGetBaseCoordinate(string instrumentId, string keyOrNote, out double x, out double y);

    // 根据实际客户端窗口尺寸换算缩放点击中心坐标
    bool TryGetScaledCoordinate(string instrumentId, string keyOrNote, int clientWidth, int clientHeight, out int clientX, out int clientY);

    // 解析目标小图的物理文件路径（支持专属目录、common 通用目录与历史回退探测）
    string? ResolveTargetFilePath(string instrumentId, string targetFileName, string? baseDirectory = null);

    // 解析整块底模大图的物理文件路径
    string? ResolveTemplatePath(string instrumentId, string? baseDirectory = null);
}
```

### 1.2 `InstrumentProfile` 配置模型
```csharp
public sealed class InstrumentProfile
{
    // 乐器唯一标识符（如 "Recorder", "Guitar"）
    public string Id { get; init; }

    // 乐器可见名称（如 "竖笛", "吉他"）
    public string DisplayName { get; init; }

    // 别名列表（用于解析曲谱与筛选匹配，如 ["guitar", "吉他"]）
    public IReadOnlyList<string> Aliases { get; init; }

    // 基准设计分辨率，默认 (2560.0, 1440.0)
    public (double Width, double Height) BaseResolution { get; init; }

    // 大底模板相对路径
    public string TemplateRelativePath { get; init; }

    // 专属小图目录相对路径
    public string TargetsDirectoryRelativePath { get; init; }

    // 公共按键锚点（如 key_A ~ key_L）
    public IReadOnlyList<AnchorTarget> KeyAnchors { get; init; }

    // 乐器专属目标（如竖笛 1~7/do~ti，吉他和弦 C~G7）
    public IReadOnlyList<AnchorTarget> ExclusiveTargets { get; init; }

    // Stage 2 用于快速外缘比对的验证锚点（缺省自动取首尾）
    public IReadOnlyList<AnchorTarget> VerificationAnchors { get; init; }

    // 7 键点击基准中心点映射表
    public IReadOnlyDictionary<string, (double X, double Y)> PlayCoordinates { get; init; }

    // 7 键视觉选框（X, Y, W, H, Label）
    public IReadOnlyList<(int X, int Y, int W, int H, string Label)> ButtonBounds { get; init; }
}
```

---

## 2. 识别结果模型 (InstrumentDetectionResult)

```csharp
namespace BetterPetitPlanet.GameTask.Music.Service;

public enum InstrumentDetectionState
{
    None = 0,               // 未检测到演奏界面
    KeyCandidateFound = 1,  // Stage 1 按键初筛命中
    Confirmed = 2,          // 乐器身份已明确确认
    Ambiguous = 3,          // 存在候选但无法区分（存疑保护）
    Error = 4               // 画面捕获或推理异常
}

public class InstrumentDetectionResult
{
    // 是否检测到处于可用演奏状态
    public bool IsDetected { get; set; }

    // 识别出的乐器 Id（如 "Recorder", "Guitar"，未确定时为 null）
    public string? InstrumentId { get; set; }

    // 识别出的乐器展示名称（如 "竖笛", "吉他"）
    public string? DisplayName { get; set; }

    // 检测状态
    public InstrumentDetectionState State { get; set; }

    // 决策阶段 ("KeyAnchor", "FeatureVerification", "OcrFallback")
    public string Stage { get; set; }

    // 本次检测是否调用了 OCR 引擎
    public bool OcrInvoked { get; set; }

    // 模板匹配置信度
    public double TemplateConfidence { get; set; }

    // OCR 置信度
    public double OcrConfidence { get; set; }

    // 综合置信度
    public double CombinedConfidence { get; set; }

    // 检测完成时间戳 (UTC)
    public DateTime Timestamp { get; set; }

    // 面向 UI 的状态摘要文本（如 "吉他: 已就绪 (模板: 96%)"）
    public string StatusSummary { get; set; }

    // 诊断与调试明细
    public string Details { get; set; }
}
```

---

## 3. 乐器识别引擎 (InstrumentDetector)

```csharp
namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class InstrumentDetector : IDisposable
{
    // 最新的全局检测快照
    public InstrumentDetectionResult? LatestResult { get; }

    // 全局检测事件（保证在锁外安全异步/同步发布）
    public event Action<InstrumentDetectionResult>? DetectionUpdated;

    // 构造函数注入注册表与可选 OCR 引擎
    public InstrumentDetector(
        IConfigService? configService = null,
        IOcrEngine? ocrEngine = null,
        ILogger<InstrumentDetector>? logger = null,
        IInstrumentRegistry? registry = null);

    // 执行多乐器分阶段全量检测（支持传入完整截图或 1550x250 ROI 子图）
    public InstrumentDetectionResult DetectFull(
        Mat frame,
        int roiOffsetX = 0,
        int roiOffsetY = 0,
        int clientWidth = 0,
        int clientHeight = 0);

    // 轻量兼容布尔检测
    public bool Detect(Mat frame, out double confidence);

    // 检查指定窗口句柄
    public bool CheckGameWindow(IntPtr hWnd, out double confidence);
}
```

---

## 4. 演奏就绪安全门控 (MusicReadinessGate)

```csharp
namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicReadiness(bool Ready, string Reason);

public sealed class MusicReadinessGate
{
    // 期望演奏的目标乐器（来自当前曲谱，如 "guitar", "recorder", "吉他"）
    public string? TargetInstrument { get; set; }

    // 画面当前识别出的乐器 Id
    public string? DetectedInstrumentId { get; }

    // 画面当前识别出的乐器展示名称
    public string? DetectedDisplayName { get; }

    // 记录乐器检测结果
    public void ObserveDetection(InstrumentDetectionResult result, double now);
    public void ObserveDetection(bool detected, double now, string? instrumentId, string? displayName, bool error = false);

    // 记录捕获帧时间戳
    public void ObserveCapture(double now);

    // 综合安全评估（比对进程、焦点、时效、乐器一致性）
    public MusicReadiness Evaluate(double now, bool exists, bool foreground, bool captureRunning);

    // 跨语言与别名比对工具方法
    public static bool MatchesInstrument(string target, string? detectedId, string? detectedDisplayName);
}
```
