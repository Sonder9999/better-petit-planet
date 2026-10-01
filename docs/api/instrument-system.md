# 乐器与演奏系统接口契约

命名空间：`BetterPetitPlanet.Core.Instruments` 与 `BetterPetitPlanet.GameTask`

本文档规范乐器抽象、状态检测器以及曲谱演奏执行器之间的调用契约。

---

## 1. 乐器抽象接口 (IInstrument)

```csharp
namespace BetterPetitPlanet.Core.Instruments;

public interface IInstrument
{
    // 乐器唯一标识符 (例如 "Recorder")
    string InstrumentId { get; }

    // 乐器用户可见名称 (例如 "竖笛")
    string DisplayName { get; }

    // 乐器支持的音域范围 (MIDI Note 编码，如 60 代表中央 C)
    int MinMidiNote { get; }
    int MaxMidiNote { get; }

    // 将标准音符索引映射为键盘按键
    ConsoleKey? MapNoteToKey(int noteIndex);

    // 基于当前帧的 ROI 判定该乐器是否处于持握激活状态
    bool DetectActive(Bitmap frameRoi);
}
```

---

## 2. 乐器状态检测器契约 (InstrumentDetector)

```csharp
namespace BetterPetitPlanet.GameTask;

public class InstrumentDetector
{
    // 载入特定乐器的特征模板
    public void LoadInstrumentTemplate(string instrumentId, string templateImagePath);

    // 对整幅游戏截图执行乐器持握状态检测
    public bool IsHoldingInstrument(Bitmap fullFrame, string instrumentId);

    // 释放 OpenCV 模板特征矩阵
    public void Dispose();
}
```

---

## 3. 曲谱播放控制器契约 (MusicPlayer)

```csharp
namespace BetterPetitPlanet.GameTask;

public class MusicPlayer
{
    public bool IsPlaying { get; }
    public double Progress { get; } // 播放进度 [0.0, 1.0]

    // 载入简谱文本内容
    public bool LoadScore(string scoreContent);

    // 开始演奏 (支持指定输入模式与目标乐器)
    public void Play(IInstrument targetInstrument, MusicInputMode inputMode);

    // 暂停/继续/停止
    public void Pause();
    public void Resume();
    public void Stop();
}
```
