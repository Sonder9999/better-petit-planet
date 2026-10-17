using System;
using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicKeyCommand(PerformanceEvent Note, bool IsDown);
public sealed record MusicInputResult(MusicKeyCommand Command, bool Submitted, string? Error = null);

public interface IMusicInputTransport
{
    IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode);
}
