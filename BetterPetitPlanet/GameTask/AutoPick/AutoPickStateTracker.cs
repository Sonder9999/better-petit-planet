using System;
using System.Diagnostics;

namespace BetterPetitPlanet.GameTask.AutoPick;

/// <summary>
/// 密集花田连采状态追踪器：
/// 专门解决密集花田连续采集多个相同名称目标（如连续 5 朵野菊花）时的漏采与防抖问题。
/// 机制原理：
/// 1. 消除朴素帧差分死锁：花田中连续同名同位置的花朵，若直接比对文字或位置容易误判为“无变化未摘完”导致漏采；
/// 2. 气泡消失波形检测：前一朵花采摘后交互 UI 必然出现短暂消失或波形下坠谷底（Absent 帧），作为下一朵花无缝重新上膛的判据；
/// 3. 安全兜底超时：即使玩家站在紧密重合的作物簇中导致气泡未完全消失，在冷却完成后超过兜底窗口也将自动重新上膛。
/// </summary>
public sealed class AutoPickStateTracker
{
    private bool _isArmed = true;
    private int _consecutiveAbsenceFrames;
    private long _lastTriggerTimestamp;
    private readonly int _reArmSafetyTimeoutMs;

    public bool IsArmed => _isArmed;
    public int ConsecutiveAbsenceFrames => _consecutiveAbsenceFrames;

    public AutoPickStateTracker(int reArmSafetyTimeoutMs = 300)
    {
        _reArmSafetyTimeoutMs = reArmSafetyTimeoutMs;
    }

    /// <summary>
    /// 根据当前帧的视觉/OCR提示存在状态和按键冷却状态，评估是否允许触发拾取。
    /// </summary>
    /// <param name="hasPrompt">当前帧是否存在可交互气泡</param>
    /// <param name="cooldownReady">按键输入通道是否已完全冷却就绪</param>
    /// <returns>若可以立即触发拾取则返回 true</returns>
    public bool ShouldTrigger(bool hasPrompt, bool cooldownReady)
    {
        if (!hasPrompt)
        {
            _consecutiveAbsenceFrames++;
            if (_consecutiveAbsenceFrames >= 1)
            {
                // 观察到前一朵花采集后交互提示消失的下降沿波形，重新上膛
                _isArmed = true;
            }
            return false;
        }

        // 当前帧有提示，复位连续无提示帧计数器
        _consecutiveAbsenceFrames = 0;

        // 如果按键通道尚在物理按下或冷却中，暂不触发
        if (!cooldownReady)
        {
            return false;
        }

        // 若尚未上膛，检测安全兜底超时（防止极密集多花无缝重叠导致气泡未降至阈值下）
        if (!_isArmed)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedMs = (double)(now - _lastTriggerTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= _reArmSafetyTimeoutMs)
            {
                _isArmed = true;
            }
        }

        return _isArmed;
    }

    /// <summary>
    /// 当一次拾取被确认触发后记录状态，进入待退出或等待波形下降状态。
    /// </summary>
    public void RecordTrigger()
    {
        _isArmed = false;
        _consecutiveAbsenceFrames = 0;
        _lastTriggerTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// 重置追踪器状态（如失焦、功能禁用或手动切换时调用）。
    /// </summary>
    public void Reset()
    {
        _isArmed = true;
        _consecutiveAbsenceFrames = 0;
        _lastTriggerTimestamp = 0;
    }
}
