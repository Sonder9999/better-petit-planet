# 简谱解析与演奏调度引擎

星布谷地内置乐器演奏支持玩家自由按键奏乐。BetterPetitPlanet 提供了完整的乐谱解析（Numbered Musical Notation / 简谱）与纳秒级时间精度的演奏调度系统。

---

## 1. 简谱语法树与曲谱解析

### 1.1 简谱语法支持
解析器（`PetitScoreParser`）能够将通用文本简谱或 MIDI 简化结构转化为顺序播放队列：
- **基本唱名**：`1`、`2`、`3`、`4`、`5`、`6`、`7`，对应自然音阶中的 Do、Re、Mi、Fa、Sol、La、Ti。
- **音高八度修饰**：
  - 低音修饰：数字下方加点或字符前缀（如 `-1`、`-5`）。
  - 高音修饰：数字上方加点或字符前缀（如 `+1`、`+3`）。
- **休止符**：`0` 代表休止，持续一个基准时值。
- **时值修饰**：
  - 增时线（`-`）：音符后追加 `-` 延长一拍（如 `1 - - -` 表示四拍长音）。
  - 减时线（下划线或 `/`）：缩短时值至二分之一、四分之一。
- **多音和弦**：使用括号包裹同一瞬间触发的组合键（如 `(135)` 代表三和弦同时按下）。

### 1.2 语法解析管线
1. **词法切分**：滤除歌词、注释与无效标点，提取时序 Token 流。
2. **拍速计算（BPM）**：根据乐谱元数据（如 `BPM: 120`）计算基准四分音符毫秒时值（$T_{quarter} = \frac{60000}{BPM}$）。
3. **时钟队列生成**：生成按时间戳升序排序的演奏动作列表 `List<NoteEvent>`，每个事件明确包含：
   - 目标绝对时间戳（以曲目开始为基准，单位微秒）。
   - 按键物理扫描码（Virtual Key Code）。
   - 动作类型（KeyDown / KeyUp）。

---

## 2. 微秒级高精度时间调度器

### 2.1 传统 Sleep 的局限
Windows 系统原生 `Thread.Sleep(1)` 的时钟中断精度通常为 15.6ms（未调用 timeBeginPeriod 时），即便调用多媒体时钟 `timeBeginPeriod(1)`，其抖动范围仍在 1ms - 2ms 左右。对于 120+ BPM 下包含大量十六分音符的高速曲目，时间抖动会导致明显的抢拍、拖拍现象。

### 2.2 混合自旋等待策略（Hybrid Spin-Wait）
为了兼顾 CPU 功耗与微秒级精准度，演奏调度器使用 `Stopwatch.GetTimestamp()` 配合两段式混合调度：

```csharp
long targetTicks = eventItem.TimestampTicks;
while (true)
{
    long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
    if (remainingTicks <= 0) break;
    
    double remainingMs = (double)remainingTicks / Stopwatch.Frequency * 1000.0;
    
    // 剩余时间大于 2ms 时，让出时间片休眠，降低 CPU 消耗
    if (remainingMs > 2.0)
    {
        Thread.Sleep(1);
    }
    // 剩余时间小于 2ms 时，进入纳秒级 CPU 自旋等待（SpinWait）
    else
    {
        Thread.SpinWait(10);
    }
}
```

通过此机制，按键触发的时间抖动控制在 $\pm 0.1\text{ms}$ 以内，完全满足严苛的节拍要求。

---

## 3. 按键传输模式

系统支持两种主要运行模式，分别应对不同场景需求：

### 3.1 前台物理模拟模式（Foreground / SendInput）
- **实现手段**：通过 Win32 `SendInput` API 向系统硬件输入队列直接投递按键事件。
- **优点**：能够绕过绝大多数应用层的按键过滤机制，响应极为可靠，与真实物理键盘输入完全一致。
- **限制**：要求游戏窗口必须处于前台激活状态并拥有输入焦点。在演奏过程中玩家无法在电脑上进行其他操作，否则可能导致按键打断或字符输入到其他窗口。

### 3.2 后台消息模式（Background / PostMessage）
- **实现手段**：直接向游戏窗口的顶级句柄或渲染子句柄发送 `WM_KEYDOWN` 和 `WM_KEYUP` 消息。
- **优点**：无需将游戏窗口置顶，理论上不抢占用户的物理键盘焦点，方便后台挂机演奏。
- **限制**：现代虚幻引擎游戏通常使用 DirectInput 或 Raw Input 绕过常规 Windows 消息队列。若游戏只在具备窗口焦点时拉取按键状态，后台消息可能被部分或全部丢弃。为解决此局限，建议配合项目提供的**桌面分身（Child Session）**模式使用。
