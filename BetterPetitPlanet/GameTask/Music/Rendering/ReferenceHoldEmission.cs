using System;
using System.Collections.Generic;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Direct port of render/hold-emission.ts. Each source layer has an emission cursor and
/// a fixed pool; no emission history or per-update allocations accumulate with song length.
/// </summary>
public sealed class ReferenceHoldEmission
{
    private readonly ReferenceHoldLayer _layer;
    private readonly double[] _births;
    private readonly double[] _burstIndices;
    // JS counters are doubles. Retain their arithmetic, including multiplication before division.
    private double _rateIndex = 1;
    private double _age = double.NegativeInfinity;

    public ReferenceHoldEmission(ReferenceHoldLayer layer)
    {
        _layer = layer;
        _births = new double[layer.Slots];
        Array.Fill(_births, double.NegativeInfinity);
        Births = Array.AsReadOnly(_births);
        _burstIndices = new double[layer.Emission.Bursts.Length];
    }

    public IReadOnlyList<double> Births { get; }

    public void Update(double ageMs)
    {
        if (!double.IsFinite(ageMs))
            throw new ArgumentOutOfRangeException(nameof(ageMs), "Hold age must be finite.");
        if (ageMs < _age) Reset();
        if (ageMs == _age) return;
        _age = ageMs;
        var emission = _layer.Emission;
        if (!emission.Enabled) return;
        var time = ageMs - emission.StartDelayMs;
        if (time < 0) return;

        if (_layer.Ambient)
        {
            var firstBurst = double.PositiveInfinity;
            foreach (var burst in emission.Bursts)
                if (burst.Count > 0 && burst.CycleCount > 0)
                    firstBurst = Math.Min(firstBurst, burst.TimeMs);
            var firstRate = emission.RatePerSecond > 0 ? 1000 / emission.RatePerSecond : double.PositiveInfinity;
            var first = Math.Min(firstBurst, firstRate);
            _births[0] = time >= first && (emission.Looping || time < first + _layer.LifeMs)
                ? emission.StartDelayMs + first
                : double.NegativeInfinity;
            return;
        }

        while (true)
        {
            var rate = emission.RatePerSecond > 0 ? _rateIndex * 1000 / emission.RatePerSecond : double.PositiveInfinity;
            var next = emission.Looping || rate < emission.DurationMs ? rate : double.PositiveInfinity;
            var burstIndex = -1;
            for (var index = 0; index < emission.Bursts.Length; index++)
            {
                var burst = emission.Bursts[index];
                if (burst.Count == 0 || burst.CycleCount == 0) continue;
                var cursor = _burstIndices[index];
                var loop = Math.Floor(cursor / burst.CycleCount);
                if (!emission.Looping && loop > 0) continue;
                var when = loop * emission.DurationMs + burst.TimeMs +
                    cursor % burst.CycleCount * burst.RepeatIntervalMs;
                // Bursts precede rate emissions on ties; equal-time bursts keep source order.
                if (when < next || when == next && burstIndex == -1)
                {
                    next = when;
                    burstIndex = index;
                }
            }

            if (next > time || !double.IsFinite(next)) break;
            var birth = emission.StartDelayMs + next;
            var remaining = burstIndex < 0 ? 1 : emission.Bursts[burstIndex].Count;
            for (var slot = 0; slot < _births.Length && remaining > 0; slot++)
            {
                if (birth - _births[slot] < _layer.LifeMs) continue;
                _births[slot] = birth;
                remaining--;
            }
            if (burstIndex < 0) _rateIndex++;
            else _burstIndices[burstIndex]++;
        }
    }

    public int? Frame(int slot)
    {
        if (slot < 0 || slot >= _births.Length || !double.IsFinite(_births[slot])) return null;
        var local = _age - _births[slot];
        if (local < 0 || !_layer.Ambient && local >= _layer.LifeMs) return null;
        return _layer.FrameStart + (_layer.Ambient ? 0 : (int)Math.Min(_layer.FrameCount - 1,
            Math.Floor(local / _layer.LifeMs * _layer.FrameCount)));
    }

    private void Reset()
    {
        Array.Fill(_births, double.NegativeInfinity);
        _rateIndex = 1;
        Array.Clear(_burstIndices);
        _age = double.NegativeInfinity;
    }
}
