using System;
using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPlayfieldGeometryTest
{
    private readonly MusicOverlayLayoutConfig _defaultLayout = new()
    {
        X = 0.0,
        Y = 0.0,
        Width = 1.0,
        Height = 0.78,
        Scale = 1.0,
        JudgementLine = 0.95
    };

    [Fact]
    public void VisualOffset_PositiveAdvancesDisplayTime_NegativeDelays()
    {
        const double height = 1080.0;
        const double noteTimeMs = 3000.0;
        const double positionMs = 2000.0;
        const int fallDurationMs = 2000;

        // Baseline (visualOffset = 0)
        double yZero = MusicPlayfieldGeometry.TimeToYWithOffset(
            noteTimeMs, positionMs, 0, fallDurationMs, height, _defaultLayout);

        // Positive offset (+300ms): advances display time, note arrives closer to/past judgement line (y is larger)
        double yPositive = MusicPlayfieldGeometry.TimeToYWithOffset(
            noteTimeMs, positionMs, 300, fallDurationMs, height, _defaultLayout);

        // Negative offset (-300ms): delays display time, note is higher up (y is smaller)
        double yNegative = MusicPlayfieldGeometry.TimeToYWithOffset(
            noteTimeMs, positionMs, -300, fallDurationMs, height, _defaultLayout);

        Assert.True(yPositive > yZero, $"Positive offset must advance note position: {yPositive} > {yZero}");
        Assert.True(yZero > yNegative, $"Negative offset must delay note position: {yZero} > {yNegative}");

        // Mathematical linearity check: offset of 300ms produces proportional pixel shift
        double expectedDeltaY = (300.0 / fallDurationMs) * MusicPlayfieldGeometry.GetFallDistance(height, _defaultLayout);
        Assert.Equal(expectedDeltaY, yPositive - yZero, precision: 4);
        Assert.Equal(expectedDeltaY, yZero - yNegative, precision: 4);
    }

    [Fact]
    public void SevenLanes_HorizontalDistribution_IsStrictlyMonotonicAndSymmetric()
    {
        const double width = 1920.0;
        var layout = _defaultLayout;

        double[] xCoords = new double[7];
        for (int i = 0; i < 7; i++)
        {
            xCoords[i] = MusicPlayfieldGeometry.GetLaneX(i, width, layout);
        }

        // 1. Strictly monotonically increasing from lane A (0) to lane L (6)
        for (int i = 0; i < 6; i++)
        {
            Assert.True(xCoords[i] < xCoords[i + 1],
                $"Track {i} ({xCoords[i]}) must be to the left of Track {i + 1} ({xCoords[i + 1]})");
        }

        // 2. Center lane F (index 3) is at exactly 50% width
        double centerExpected = width * 0.50;
        Assert.Equal(centerExpected, xCoords[3], precision: 4);

        // 3. Mirror symmetry around center track F
        // Distances from F to (A, L), (S, K), (D, J) must be identical
        double distF_A = xCoords[3] - xCoords[0];
        double distF_L = xCoords[6] - xCoords[3];
        Assert.True(Math.Abs(distF_A - distF_L) <= 2.0, $"Outer tracks symmetry within 2px: {distF_A} vs {distF_L}");

        double distF_S = xCoords[3] - xCoords[1];
        double distF_K = xCoords[5] - xCoords[3];
        Assert.Equal(distF_S, distF_K, precision: 4);

        double distF_D = xCoords[3] - xCoords[2];
        double distF_J = xCoords[4] - xCoords[3];
        Assert.Equal(distF_D, distF_J, precision: 4);
    }

    [Fact]
    public void JudgementLineAndScale_CalculatesCorrectCoordinates()
    {
        const double height = 1080.0;
        const double width = 1920.0;

        var layout = new MusicOverlayLayoutConfig
        {
            X = 0.1,
            Y = 0.05,
            Width = 0.8,
            Height = 0.70,
            Scale = 1.5,
            JudgementLine = 0.90
        };

        // Expected judgement line: (0.05 + 0.70 * 0.90) * 1080 = (0.05 + 0.63) * 1080 = 0.68 * 1080 = 734.4
        double expectedJudgeY = (0.05 + 0.70 * 0.90) * height;
        double actualJudgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        Assert.Equal(expectedJudgeY, actualJudgeY, precision: 4);

        // Expected spawn line: 0.05 * 1080 = 54.0
        double expectedSpawnY = 0.05 * height;
        double actualSpawnY = MusicPlayfieldGeometry.GetSpawnY(height, layout);
        Assert.Equal(expectedSpawnY, actualSpawnY, precision: 4);

        // Scale impacts lane width and note dimensions
        double baseLaneWidth = MusicPlayfieldGeometry.GetLaneWidth(width, _defaultLayout);
        var scaledLayout = new MusicOverlayLayoutConfig { Scale = 2.0 };
        double scaledLaneWidth = MusicPlayfieldGeometry.GetLaneWidth(width, scaledLayout);
        Assert.Equal(baseLaneWidth * 2.0, scaledLaneWidth, precision: 4);
    }

    [Fact]
    public void ChordLine_SpanFromMinLaneToMaxLane()
    {
        const double width = 1920.0;
        const double height = 1080.0;

        var chordNotes = new List<PerformanceEvent>
        {
            new() { Key = "S", TimeMs = 1500, Kind = MusicNoteKind.Tap },
            new() { Key = "K", TimeMs = 1500, Kind = MusicNoteKind.Tap },
            new() { Key = "A", TimeMs = 1500, Kind = MusicNoteKind.Tap }
        };

        var segment = MusicPlayfieldGeometry.ComputeChordLine(
            chordNotes, displayTimeMs: 1000, fallDurationMs: 2000, width, height, _defaultLayout);

        Assert.NotNull(segment);
        // Min X is lane A, Max X is lane K
        double expectedX1 = MusicPlayfieldGeometry.GetLaneX("A", width, _defaultLayout);
        double expectedX2 = MusicPlayfieldGeometry.GetLaneX("K", width, _defaultLayout);
        double expectedY = MusicPlayfieldGeometry.TimeToY(1500, 1000, 2000, height, _defaultLayout);

        Assert.Equal(expectedX1, segment.Value.X1, precision: 4);
        Assert.Equal(expectedX2, segment.Value.X2, precision: 4);
        Assert.Equal(expectedY, segment.Value.Y, precision: 4);
    }

    [Fact]
    public void ComputeHoldBounds_PinsHeadYWhenHolding()
    {
        const double width = 1920.0;
        const double height = 1080.0;
        var holdNote = new PerformanceEvent
        {
            Key = "A",
            TimeMs = 1000,
            DurationMs = 2000,
            Kind = MusicNoteKind.Hold
        };

        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, _defaultLayout);

        // When displayTime is 1500ms (note head has reached and passed judgement line at 1000ms):
        // 1. If NOT holding: head moves past judgement line
        var freeBounds = MusicPlayfieldGeometry.ComputeHoldBounds(
            holdNote, displayTimeMs: 1500, fallDurationMs: 2000, width, height, _defaultLayout, isHolding: false);
        Assert.True(freeBounds.HeadY > judgeY);

        // 2. If HOLDING: head is pinned at judgement line
        var heldBounds = MusicPlayfieldGeometry.ComputeHoldBounds(
            holdNote, displayTimeMs: 1500, fallDurationMs: 2000, width, height, _defaultLayout, isHolding: true);
        Assert.Equal(judgeY, heldBounds.HeadY, precision: 4);

        // When displayTime is 500ms (head has not reached judgement line yet):
        // Even if holding flag was set prematurely, head does not jump ahead of its natural fall position
        var earlyHeldBounds = MusicPlayfieldGeometry.ComputeHoldBounds(
            holdNote, displayTimeMs: 500, fallDurationMs: 2000, width, height, _defaultLayout, isHolding: true);
        Assert.True(earlyHeldBounds.HeadY < judgeY);
    }

    [Fact]
    public void HoldBeam_MatchesReference144PixelTextureAnd124PixelBody()
    {
        const double width = 1920.0;
        const double height = 1080.0;
        var holdNote = new PerformanceEvent
        {
            Key = "A",
            TimeMs = 1000,
            DurationMs = 2000,
            Kind = MusicNoteKind.Hold
        };

        var bounds = MusicPlayfieldGeometry.ComputeHoldBounds(
            holdNote, displayTimeMs: 1500, fallDurationMs: 2000, width, height, _defaultLayout, isHolding: true);

        // beam-texture.ts uses a 144px texture with edge centers at x=10 and x=134.
        Assert.Equal(144.0, bounds.Width, precision: 4);

        // Body width: 124px, with a 10px outer margin on each side.
        double bodyWidth = MusicPlayfieldGeometry.GetHoldBeamBodyWidth(width, _defaultLayout);
        double edgeInset = MusicPlayfieldGeometry.GetHoldBeamEdgeInset(width, _defaultLayout);
        Assert.Equal(124.0, bodyWidth, precision: 4);
        Assert.Equal(10.0, edgeInset, precision: 4);
        Assert.Equal(bounds.Width, bodyWidth + edgeInset * 2.0, precision: 4);

        // Tail geometric extension: 20px behind tail flower center @ 1920
        double tailExtension = MusicPlayfieldGeometry.GetHoldBeamTailExtension(width, _defaultLayout);
        Assert.Equal(20.0, tailExtension, precision: 4);

        // Note size is independent from the beam texture's glow margins.
        double noteWidth = MusicPlayfieldGeometry.GetNoteWidth(width, _defaultLayout);
        Assert.Equal(120.0, noteWidth, precision: 4);
    }
}
