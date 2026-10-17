using System;
using System.Text.Json.Serialization;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>Compiled atlas metadata from the reference's active HOLD_PROFILES registry.</summary>
public sealed class ReferenceHoldProfile
{
    public int FrameSize { get; init; }
    public int Columns { get; init; }
    public string NormalUrl { get; init; } = string.Empty;
    public string AdditiveUrl { get; init; } = string.Empty;
    public ReferenceHoldLayer[] Layers { get; init; } = [];
}

public sealed class ReferenceHoldLayer
{
    public string Name { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public int FrameStart { get; init; }
    public int FrameCount { get; init; }
    public int MaxParticles { get; init; }
    public double LifeMs { get; init; }
    public double DisplaySize { get; init; }
    public bool Ambient { get; init; }
    public ReferenceHoldEmissionSpec Emission { get; init; } = new();
    /// <summary>The exact holdSlots capacity; ambient source layers collapse to one slot.</summary>
    [JsonIgnore]
    public int Slots
    {
        get
        {
            if (!Emission.Enabled) return 0;
            if (Ambient) return 1;
            var rateSlots = Math.Ceiling(LifeMs * Emission.RatePerSecond / 1000);
            double burstSlots = 0;
            foreach (var burst in Emission.Bursts)
                burstSlots += (double)burst.Count * burst.CycleCount *
                    (Emission.Looping ? Math.Ceiling(LifeMs / Emission.DurationMs) + 1 : 1);
            return (int)Math.Min(MaxParticles, rateSlots + burstSlots);
        }
    }
}

public sealed class ReferenceHoldEmissionSpec
{
    public bool Enabled { get; init; }
    public bool Looping { get; init; }
    public double RatePerSecond { get; init; }
    public double DurationMs { get; init; }
    public double StartDelayMs { get; init; }
    public ReferenceHoldBurst[] Bursts { get; init; } = [];
}

public sealed class ReferenceHoldBurst
{
    public double TimeMs { get; init; }
    public double RepeatIntervalMs { get; init; }
    public int Count { get; init; }
    public int CycleCount { get; init; }
}
