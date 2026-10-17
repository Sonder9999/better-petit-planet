using System;
using Xunit;

namespace BetterPetitPlanet.Test;

/// <summary>Real desktop input is never part of an unattended test run.</summary>
public sealed class MusicDesktopInputFactAttribute : FactAttribute
{
    public MusicDesktopInputFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PETIT_MUSIC_DESKTOP_TESTS") != "1")
        {
            Skip = "Set PETIT_MUSIC_DESKTOP_TESTS=1 and run only this test against an isolated game desktop.";
        }
    }
}
