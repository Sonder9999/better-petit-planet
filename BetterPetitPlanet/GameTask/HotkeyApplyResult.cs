using System;
using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;

namespace BetterPetitPlanet.GameTask;

public sealed record HotkeyBinding(string Name, string Key);

public sealed record HotkeyApplyResult(
    bool Success,
    string ErrorMessage,
    string? FailedKeyName,
    HotkeyConfig ActiveConfig)
{
    public IReadOnlyList<HotkeyBinding> EffectiveBindings { get; init; } = Array.Empty<HotkeyBinding>();
    public IReadOnlyList<HotkeyBinding> UnconfirmedBindings { get; init; } = Array.Empty<HotkeyBinding>();
}
