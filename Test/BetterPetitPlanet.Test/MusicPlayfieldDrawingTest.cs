using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPlayfieldDrawingTest
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    [Fact]
    public void MusicPlayfieldControl_HasThreeVisualLayersInOrder()
    {
        RunInSta(() =>
        {
            var control = new MusicPlayfieldControl();

            // Layer count must be exactly 3: Tracks, Notes, Feedback
            var visualChildrenCountProp = typeof(FrameworkElement)
                .GetProperty("VisualChildrenCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            var count = (int)visualChildrenCountProp!.GetValue(control)!;
            Assert.Equal(3, count);

            var getVisualChildMethod = typeof(FrameworkElement)
                .GetMethod("GetVisualChild", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            for (int i = 0; i < 3; i++)
            {
                var visual = getVisualChildMethod!.Invoke(control, [i]);
                Assert.NotNull(visual);
                Assert.IsType<DrawingVisual>(visual);
            }
        });
    }

    [Fact]
    public void RenderSnapshot_ExecutesWithoutExceptionAcrossAllLayers()
    {
        RunInSta(() =>
        {
            var control = new MusicPlayfieldControl
            {
                Width = 1920,
                Height = 1080
            };

            var timeline = new PerformanceTimeline
            {
                Events =
                [
                    new() { NoteId = "t1", Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
                    new() { NoteId = "t2", Key = "S", TimeMs = 1000, Kind = MusicNoteKind.Tap }, // Chord
                    new() { NoteId = "h1", Key = "D", TimeMs = 1200, DurationMs = 800, Kind = MusicNoteKind.Hold }
                ]
            };
            control.SetChart(timeline);

            var snapshot = new MusicRenderSnapshot(
                SessionId: "test-session",
                Revision: 1,
                PositionMs: 1000,
                HostMonotonicMs: 5000,
                MonotonicTimestamp: 5000,
                PlaybackSpeed: 1.0,
                IsPlaying: true,
                IsPreparing: false,
                NoteCount: 3,
                ActiveAutomaticKeys: ["A"],
                ActiveManualKeys: ["D"],
                ActiveKeys: ["A", "D"]
            );

            // Execute explicit render at 1920x1080
            control.RenderSnapshot(snapshot, 1920, 1080);

            // Verify repeated render handles dirty flag cache efficiently
            control.RenderSnapshot(snapshot, 1920, 1080);
        });
    }

    [Fact]
    public void MusicDefaultSkin_AllBrushesAndPensAreFrozen()
    {
        RunInSta(() =>
        {
            Assert.True(MusicDefaultSkin.TrackBackgroundBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.TrackLaneGuidePen.IsFrozen);
            Assert.True(MusicDefaultSkin.TrackCenterGuidePen.IsFrozen);
            Assert.True(MusicDefaultSkin.JudgementLinePen.IsFrozen);
            Assert.True(MusicDefaultSkin.JudgementLineGlowBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.KeyLabelBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.TapNoteFillBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.TapNoteStrokePen.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldBodyBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldBodyActiveBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldBodyStrokePen.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldTailCapBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.ChordSyncPen.IsFrozen);
            Assert.True(MusicDefaultSkin.ChordSyncFillBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.KeyManualGlowBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.KeyAutoGlowBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.KeyHitBurstBrush.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldEdgeHighlightPen.IsFrozen);
            Assert.True(MusicDefaultSkin.HoldEdgeGlowPen.IsFrozen);
            Assert.Equal(1.2, MusicDefaultSkin.HoldEdgeHighlightPen.Thickness, precision: 4);
            Assert.Equal(4.0, MusicDefaultSkin.HoldEdgeGlowPen.Thickness, precision: 4);
        });
    }

    [Fact]
    public void DrawHoldBeam_UsesReferenceTextureWidthAndEndpointExtensions()
    {
        RunInSta(() =>
        {
            const double width = 1920.0;
            const double height = 1080.0;
            var layout = new MusicOverlayLayoutConfig();
            var skin = MusicSkin.Default;

            var holdNote = new PerformanceEvent
            {
                NoteId = "h1",
                Key = "A",
                TimeMs = 1000,
                DurationMs = 2000,
                Kind = MusicNoteKind.Hold
            };

            double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);

            // Holding state: displayTime = 1500 (note has arrived at judge line)
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                MusicPlayfieldDrawing.DrawHoldBeam(
                    dc,
                    holdNote,
                    displayTimeMs: 1500,
                    fallDurationMs: 2000,
                    width,
                    height,
                    layout,
                    skin,
                    isHeld: true);
            }

            Assert.NotNull(visual.Drawing);
            var group = visual.Drawing;
            var beamImage = Assert.Single(CollectDrawings<ImageDrawing>(group));
            var rect = beamImage.Rect;

            // beam-texture.ts: full texture width 144px; body edges at x=10 and x=134.
            Assert.Equal(144.0, rect.Width, precision: 4);
            Assert.Equal(144.0, beamImage.ImageSource.Width, precision: 4);
            Assert.Equal(512.0, beamImage.ImageSource.Height, precision: 4);

            // notes.ts: the beam extends 6 source pixels beyond the pinned head center.
            double bottomY = rect.Y + rect.Height;
            Assert.Equal(judgeY + 6.0, bottomY, precision: 4);

            // Top of body rectangle must be bounds.TailY - 20px (20px tail geometric extension)
            double naturalTailY = MusicPlayfieldGeometry.TimeToY(3000, 1500, 2000, height, layout);
            double expectedTailY = naturalTailY - 20.0;
            Assert.Equal(expectedTailY, rect.Y, precision: 4);
        });
    }

    [Fact]
    public void DrawHoldBeam_WhenTailCrossesJudgementLine_SkipsDrawing()
    {
        RunInSta(() =>
        {
            const double width = 1920.0;
            const double height = 1080.0;
            var layout = new MusicOverlayLayoutConfig();
            var skin = MusicSkin.Default;

            var holdNote = new PerformanceEvent
            {
                NoteId = "h1",
                Key = "A",
                TimeMs = 1000,
                DurationMs = 2000,
                Kind = MusicNoteKind.Hold
            };

            // displayTime = 3000ms: tail has reached judgement line (3000 >= 1000 + 2000)
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                MusicPlayfieldDrawing.DrawHoldBeam(
                    dc,
                    holdNote,
                    displayTimeMs: 3000,
                    fallDurationMs: 2000,
                    width,
                    height,
                    layout,
                    skin,
                    isHeld: true);
            }

            if (visual.Drawing != null)
            {
                Assert.Empty(CollectDrawings<ImageDrawing>(visual.Drawing));
                Assert.Empty(CollectDrawings<GeometryDrawing>(visual.Drawing));
            }
        });
    }

    [Fact]
    public void DrawNotes_KeepsSourceHoldHeadPinnedAndTailAtMatchingSizeUntilCompletion()
    {
        RunInSta(() =>
        {
            const double width = 1920.0;
            const double height = 1080.0;
            var layout = new MusicOverlayLayoutConfig();
            var effects = new MusicOverlayEffectsConfig();
            var skin = MusicSkin.Default;

            var holdNote = new PerformanceEvent
            {
                NoteId = "h1",
                Key = "A",
                TimeMs = 1000,
                DurationMs = 2000,
                Kind = MusicNoteKind.Hold
            };

            double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);

            // Phase 1: Falling (displayTime = 500ms, naturalY < judgeY)
            // Head flower MUST be rendered
            var visualFalling = new DrawingVisual();
            using (var dc = visualFalling.RenderOpen())
            {
                MusicPlayfieldDrawing.DrawNotes(
                    dc,
                    [holdNote],
                    displayTimeMs: 500,
                    fallDurationMs: 2000,
                    width,
                    height,
                    layout,
                    effects,
                    activeKeys: ["A"],
                    skin);
            }
            Assert.NotNull(visualFalling.Drawing);
            var fallingImages = CollectDrawings<ImageDrawing>(visualFalling.Drawing);
            Assert.NotEmpty(fallingImages);

            // Phase 2: Active holding on judgement line (displayTime = 1500ms, naturalY > judgeY, naturalTailY < judgeY)
            // notes.ts preserves the hold head at progress=1 while the tail keeps falling.
            var visualHolding = new DrawingVisual();
            using (var dc = visualHolding.RenderOpen())
            {
                MusicPlayfieldDrawing.DrawNotes(
                    dc,
                    [holdNote],
                    displayTimeMs: 1500,
                    fallDurationMs: 2000,
                    width,
                    height,
                    layout,
                    effects,
                    activeKeys: ["A"],
                    skin);
            }
            Assert.NotNull(visualHolding.Drawing);
            var holdingImages = CollectDrawings<ImageDrawing>(visualHolding.Drawing);
            var flowers = holdingImages.Where(image => ReferenceEquals(image.ImageSource,
                skin.NoteHoldBitmap ?? MusicDefaultSkin.NoteHoldBitmap)).ToArray();
            Assert.Equal(2, flowers.Length);
            double tailY = MusicPlayfieldGeometry.TimeToY(3000, 1500, 2000, height, layout);
            var head = Assert.Single(flowers.Where(image => Math.Abs(image.Rect.Y + image.Rect.Height / 2 - judgeY) < .0001));
            var tail = Assert.Single(flowers.Where(image => Math.Abs(image.Rect.Y + image.Rect.Height / 2 - tailY) < .0001));
            Assert.Equal(120.0, head.Rect.Width);
            Assert.Equal(head.Rect.Size, tail.Rect.Size);
            Assert.Same(tail, holdingImages.Last()); // Source tail is above the head as the flowers merge.

            // Phase 3: Completely finished (displayTime = 3500ms, naturalTailY >= judgeY)
            // Note should be completely skipped
            var visualDone = new DrawingVisual();
            using (var dc = visualDone.RenderOpen())
            {
                MusicPlayfieldDrawing.DrawNotes(
                    dc,
                    [holdNote],
                    displayTimeMs: 3500,
                    fallDurationMs: 2000,
                    width,
                    height,
                    layout,
                    effects,
                    activeKeys: ["A"],
                    skin);
            }
            if (visualDone.Drawing != null)
            {
                var doneImages = CollectDrawings<ImageDrawing>(visualDone.Drawing);
                Assert.Empty(doneImages);
            }
        });
    }

    private static List<T> CollectDrawings<T>(Drawing drawing) where T : Drawing
    {
        var result = new List<T>();
        CollectRecursive(drawing, result);
        return result;

        static void CollectRecursive(Drawing current, List<T> list)
        {
            if (current is T match)
            {
                list.Add(match);
            }
            if (current is DrawingGroup group)
            {
                foreach (var child in group.Children)
                {
                    CollectRecursive(child, list);
                }
            }
        }
    }
}
