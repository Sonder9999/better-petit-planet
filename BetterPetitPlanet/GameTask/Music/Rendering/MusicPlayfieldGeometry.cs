using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

public readonly record struct ChordLineSegment(double X1, double X2, double Y);

public readonly record struct NoteRenderBounds(double X, double Y, double Width, double Height);

public readonly record struct HoldRenderBounds(
    double X,
    double HeadY,
    double TailY,
    double Width,
    double Height,
    bool IsHolding);

/// <summary>
/// Pure coordinate geometry calculations for the 7-lane music playfield.
/// Transforms time, lanes, and configurations into WPF DIP (Device Independent Pixel) positions.
/// Driven by MusicOverlayLayoutConfig.
/// </summary>
public static class MusicPlayfieldGeometry
{
    public static readonly string[] LaneKeys = ["A", "S", "D", "F", "J", "K", "L"];

    public static readonly MusicLane[] Lanes =
    [
        MusicLane.A, MusicLane.S, MusicLane.D, MusicLane.F,
        MusicLane.J, MusicLane.K, MusicLane.L
    ];

    /// <summary>
    /// Normalized anchor X coordinates of the 7 tracks based on the 2560px design canvas.
    /// Perfectly centered at 1280 (0.50) and symmetric across tracks.
    /// </summary>
    public static readonly double[] DefaultAnchorX =
    [
        598.0 / 2560.0,
        826.0 / 2560.0,
        1052.0 / 2560.0,
        1280.0 / 2560.0,
        1508.0 / 2560.0,
        1734.0 / 2560.0,
        1960.0 / 2560.0
    ];

    public static int GetLaneIndex(MusicLane lane) => (int)lane;

    public static int GetLaneIndex(string laneKey)
    {
        int idx = Array.IndexOf(LaneKeys, laneKey.Trim().ToUpperInvariant());
        return idx >= 0 ? idx : 0;
    }

    public static double GetLaneAnchorX(int laneIndex)
    {
        int clamped = Math.Clamp(laneIndex, 0, DefaultAnchorX.Length - 1);
        return DefaultAnchorX[clamped];
    }

    /// <summary>
    /// Calculates the horizontal DIP coordinate of a given track center.
    /// </summary>
    public static double GetLaneX(int laneIndex, double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double anchor = GetLaneAnchorX(laneIndex);
        return (layout.X + anchor * layout.Width) * playfieldWidth;
    }

    public static double GetLaneX(MusicLane lane, double playfieldWidth, MusicOverlayLayoutConfig layout)
        => GetLaneX((int)lane, playfieldWidth, layout);

    public static double GetLaneX(string laneKey, double playfieldWidth, MusicOverlayLayoutConfig layout)
        => GetLaneX(GetLaneIndex(laneKey), playfieldWidth, layout);

    /// <summary>
    /// Returns the vertical spawn line DIP coordinate.
    /// </summary>
    public static double GetSpawnY(double playfieldHeight, MusicOverlayLayoutConfig layout)
        => layout.Y * playfieldHeight;

    /// <summary>
    /// Returns the vertical judgement line DIP coordinate.
    /// </summary>
    public static double GetJudgementY(double playfieldHeight, MusicOverlayLayoutConfig layout)
        => (layout.Y + layout.Height * layout.JudgementLine) * playfieldHeight;

    /// <summary>
    /// Returns the vertical distance between spawn and judgement line.
    /// </summary>
    public static double GetFallDistance(double playfieldHeight, MusicOverlayLayoutConfig layout)
        => layout.Height * layout.JudgementLine * playfieldHeight;

    /// <summary>
    /// Computes the visual lane width in DIPs.
    /// </summary>
    public static double GetLaneWidth(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        // Spacing between adjacent lanes (~226 / 2560) scaled by layout width and scale
        const double baseLaneSpacingRatio = 226.0 / 2560.0;
        return baseLaneSpacingRatio * layout.Width * playfieldWidth * layout.Scale;
    }

    /// <summary>
    /// Computes note flower / block width in DIPs.
    /// Strictly scales with canvas width based on the 1920x1080 design canvas in music-overlay-integration (120px note).
    /// </summary>
    public static double GetNoteWidth(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double baseSize = (playfieldWidth / 1920.0) * 120.0 * layout.Scale;
        return Math.Max(24.0 * layout.Scale, baseSize);
    }

    /// <summary>
    /// Computes note flower / block height in DIPs (1:1 square ratio for natural flower/circle geometry).
    /// </summary>
    public static double GetNoteHeight(double playfieldWidth, MusicOverlayLayoutConfig layout)
        => GetNoteWidth(playfieldWidth, layout);

    public static double GetNoteHeight(MusicOverlayLayoutConfig layout)
        => 120.0 * layout.Scale;

    /// <summary>
    /// Official reverse-engineered hold beam total width (144px @ 1920 design canvas).
    /// Note flower width is ~120px, so flower naturally extends beyond the 144px beam and covers the endpoints.
    /// </summary>
    public static double GetHoldBeamWidth(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double baseSize = (playfieldWidth / 1920.0) * 144.0 * layout.Scale;
        return Math.Max(18.0 * layout.Scale, baseSize);
    }

    /// <summary>
    /// Official reverse-engineered hold beam body width (124px @ 1920 design canvas, with 10px inset on each side).
    /// </summary>
    public static double GetHoldBeamBodyWidth(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double baseSize = (playfieldWidth / 1920.0) * 124.0 * layout.Scale;
        return Math.Max(16.0 * layout.Scale, baseSize);
    }

    /// <summary>
    /// Official reverse-engineered hold beam lateral edge inset (10px @ 1920 design canvas).
    /// </summary>
    public static double GetHoldBeamEdgeInset(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double baseSize = (playfieldWidth / 1920.0) * 10.0 * layout.Scale;
        return Math.Max(1.0 * layout.Scale, baseSize);
    }

    /// <summary>
    /// Official reverse-engineered hold beam tail geometric extension (20px @ 1920 design canvas).
    /// Extends behind tail flower center to eliminate rendering seams.
    /// </summary>
    public static double GetHoldBeamTailExtension(double playfieldWidth, MusicOverlayLayoutConfig layout)
    {
        double baseSize = (playfieldWidth / 1920.0) * 20.0 * layout.Scale;
        return Math.Max(3.0 * layout.Scale, baseSize);
    }

    /// <summary>
    /// Converts note start time to vertical DIP position.
    /// When displayTimeMs == noteTimeMs, the note is precisely on the judgement line.
    /// When noteTimeMs &gt; displayTimeMs, note is falling towards judgement line (y &lt; judgeY).
    /// Positive visualOffsetMs advances displayTimeMs, shifting notes closer to/past the judgement line.
    /// </summary>
    public static double TimeToY(
        double noteTimeMs,
        double displayTimeMs,
        int fallDurationMs,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout)
    {
        double judgeY = GetJudgementY(playfieldHeight, layout);
        double fallDistance = GetFallDistance(playfieldHeight, layout);
        double timeRemaining = noteTimeMs - displayTimeMs;
        int safeFall = Math.Max(1, fallDurationMs);

        return judgeY - (timeRemaining / safeFall) * fallDistance;
    }

    /// <summary>
    /// Overload that computes TimeToY taking positionMs and visualOffsetMs directly.
    /// </summary>
    public static double TimeToYWithOffset(
        double noteTimeMs,
        double positionMs,
        int visualOffsetMs,
        int fallDurationMs,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout)
    {
        return TimeToY(noteTimeMs, positionMs + visualOffsetMs, fallDurationMs, playfieldHeight, layout);
    }

    /// <summary>
    /// Calculates the vertical pixel height of a hold bar for a given duration in milliseconds.
    /// </summary>
    public static double HoldDurationToHeight(
        double durationMs,
        int fallDurationMs,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout)
    {
        double fallDistance = GetFallDistance(playfieldHeight, layout);
        int safeFall = Math.Max(1, fallDurationMs);
        return (durationMs / safeFall) * fallDistance;
    }

    /// <summary>
    /// Computes bounding box for a tap note centered on track lane.
    /// </summary>
    public static NoteRenderBounds ComputeNoteBounds(
        PerformanceEvent note,
        double displayTimeMs,
        int fallDurationMs,
        double playfieldWidth,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout)
    {
        double centerX = GetLaneX(note.Key, playfieldWidth, layout);
        double centerY = TimeToY(note.TimeMs, displayTimeMs, fallDurationMs, playfieldHeight, layout);
        double width = GetNoteWidth(playfieldWidth, layout);
        double height = GetNoteHeight(playfieldWidth, layout);

        return new NoteRenderBounds(centerX - width / 2.0, centerY - height / 2.0, width, height);
    }

    /// <summary>
    /// Computes bounds and coordinates for a hold note beam.
    /// If isHolding is true (key currently held down at or crossing judgement line),
    /// the head Y is pinned to the judgement line.
    /// </summary>
    public static HoldRenderBounds ComputeHoldBounds(
        PerformanceEvent note,
        double displayTimeMs,
        int fallDurationMs,
        double playfieldWidth,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout,
        bool isHolding)
    {
        double centerX = GetLaneX(note.Key, playfieldWidth, layout);
        double judgeY = GetJudgementY(playfieldHeight, layout);
        int duration = MusicChartIndex.GetNoteDuration(note);

        double naturalHeadY = TimeToY(note.TimeMs, displayTimeMs, fallDurationMs, playfieldHeight, layout);
        double tailY = TimeToY(note.TimeMs + duration, displayTimeMs, fallDurationMs, playfieldHeight, layout);

        double headY = isHolding && naturalHeadY >= judgeY ? judgeY : naturalHeadY;
        double width = GetHoldBeamWidth(playfieldWidth, layout);
        double height = Math.Max(0, headY - tailY);

        return new HoldRenderBounds(centerX - width / 2.0, headY, tailY, width, height, isHolding);
    }

    /// <summary>
    /// Computes chord connection line between simultaneous notes across tracks.
    /// Returns null if fewer than 2 notes provided.
    /// </summary>
    public static ChordLineSegment? ComputeChordLine(
        IReadOnlyList<PerformanceEvent> chordNotes,
        double displayTimeMs,
        int fallDurationMs,
        double playfieldWidth,
        double playfieldHeight,
        MusicOverlayLayoutConfig layout)
    {
        if (chordNotes == null || chordNotes.Count < 2)
            return null;

        double minX = double.MaxValue;
        double maxX = double.MinValue;

        foreach (var note in chordNotes)
        {
            double x = GetLaneX(note.Key, playfieldWidth, layout);
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
        }

        double y = TimeToY(chordNotes[0].TimeMs, displayTimeMs, fallDurationMs, playfieldHeight, layout);
        return new ChordLineSegment(minX, maxX, y);
    }
}
