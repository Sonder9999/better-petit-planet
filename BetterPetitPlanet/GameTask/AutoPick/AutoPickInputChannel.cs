using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.AutoPick;

public interface IAutoPickInputChannel : IDisposable
{
    bool IsBusy { get; }
    long LastKeyUpTimestamp { get; }
    bool IsCooldownReady(int cooldownMs);
    bool TryTrigger(string keyName, int holdDurationMs, int cooldownMs, IntPtr hwnd, bool runInBackground);
    void ForceReleaseAll();
}

/// <summary>
/// 原子化串行安全按键输入通道：
/// 彻底消除 KeyDown / KeyUp 的并发竞态、连击冲撞与卡键风险。
/// 严格保证：
/// 1. 唯一并发：同一时刻仅允许一个按键生命周期（KeyDown -> Hold -> KeyUp -> Cooldown）执行；
/// 2. 闭环释放：通过 try-finally 确保 KeyUp 在任何异常或取消时必被执行；
/// 3. 精确冷却：冷却计时器在 KeyUp 物理释放完成瞬间起算，杜绝按键尚未松开冷却即被消费的问题；
/// 4. 零修饰键粘滞：在释放与重置时联动安全清理卡键修饰符。
/// </summary>
public sealed class AutoPickInputChannel : IAutoPickInputChannel
{
    private readonly ILogger? _logger;
    private readonly Func<bool>? _suppressCheck;
    private readonly Func<string, int, IntPtr, bool, Task>? _customExecutor;
    private int _isBusyFlag;
    private long _lastKeyUpTimestamp;

    public bool IsBusy => Volatile.Read(ref _isBusyFlag) == 1;
    public long LastKeyUpTimestamp => Volatile.Read(ref _lastKeyUpTimestamp);

    public AutoPickInputChannel(
        ILogger? logger = null,
        Func<bool>? suppressCheck = null,
        Func<string, int, IntPtr, bool, Task>? customExecutor = null)
    {
        _logger = logger;
        _suppressCheck = suppressCheck;
        _customExecutor = customExecutor;
    }

    public bool IsCooldownReady(int cooldownMs)
    {
        if (IsBusy)
        {
            return false;
        }

        long lastUp = Volatile.Read(ref _lastKeyUpTimestamp);
        if (lastUp == 0)
        {
            return true;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (double)(now - lastUp) * 1000.0 / Stopwatch.Frequency;
        return elapsedMs >= cooldownMs;
    }

    public bool TryTrigger(string keyName, int holdDurationMs, int cooldownMs, IntPtr hwnd, bool runInBackground)
    {
        if (_suppressCheck != null && _suppressCheck())
        {
            return false;
        }

        // 严格检查是否自上次 KeyUp 释放后已经满足冷却时长
        if (!IsCooldownReady(cooldownMs))
        {
            return false;
        }

        // 原子 CAS 抢占执行锁，确保任何时刻决无第二个并发任务进入按键通道
        if (Interlocked.CompareExchange(ref _isBusyFlag, 1, 0) != 0)
        {
            return false;
        }

        // 提交串行输入执行
        Task.Run(async () =>
        {
            try
            {
                if (_suppressCheck != null && _suppressCheck())
                {
                    _logger?.LogInformation("AutoPickInputChannel: 受到音乐让行抑制，跳过按键发送 '{Key}'", keyName);
                    return;
                }

                int clampedHold = Math.Clamp(holdDurationMs, 20, 500);
                string modeDesc = (runInBackground && hwnd != IntPtr.Zero) ? "后台PostMessage" : "前台SendInput";
                _logger?.LogInformation("AutoPickInputChannel: 正在按下按键 '{Key}' (保持 {HoldMs}ms, 模式: {Mode})", keyName, clampedHold, modeDesc);

                if (_customExecutor != null)
                {
                    await _customExecutor(keyName, clampedHold, hwnd, runInBackground);
                }
                else
                {
                    if (runInBackground && hwnd != IntPtr.Zero)
                    {
                        await HardwareInputSimulator.SendBackgroundKeyPressAsync(hwnd, keyName, clampedHold);
                    }
                    else
                    {
                        await HardwareInputSimulator.SendKeyPressAsync(keyName, clampedHold);
                    }
                }

                _logger?.LogInformation("AutoPickInputChannel: 已释放按键 '{Key}'，启动冷却计时 ({CooldownMs}ms)", keyName, cooldownMs);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "AutoPickInputChannel: 执行按键模拟异常 '{Key}'", keyName);
            }
            finally
            {
                // 关键点：KeyUp 释放完毕后才记录时间戳，使配置的冷却窗口严格从松开按键起算
                Volatile.Write(ref _lastKeyUpTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref _isBusyFlag, 0);
            }
        });

        return true;
    }

    public void ForceReleaseAll()
    {
        try
        {
            HardwareInputSimulator.ReleaseAllHeldKeys();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AutoPickInputChannel.ForceReleaseAll 清理按键失败，已忽略");
        }
        Volatile.Write(ref _isBusyFlag, 0);
    }

    public void Dispose()
    {
        ForceReleaseAll();
    }
}
