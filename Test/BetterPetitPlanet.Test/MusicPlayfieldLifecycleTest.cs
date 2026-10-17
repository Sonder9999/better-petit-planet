using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.View.Controls.Music;

namespace BetterPetitPlanet.Test;

public sealed class MusicPlayfieldLifecycleTest
{
    [Fact]
    public void DiagnosticSimpleSceneUsesExistingSnapshotClockAndRenderingSubscription() => MusicRenderingTestHost.Run(() =>
    {
        double now = 1000;
        EventHandler? rendering = null;
        bool simple = true;
        var control = new MusicPlayfieldControl(() => now, h => rendering += h, h => rendering -= h, _ => simple);
        using var host = MusicRenderingTestHost.Mount(control);
        var snapshot = Snapshot() with { IsPlaying = true, PositionMs = 10000, HostMonotonicMs = now };
        control.UpdateSnapshot(snapshot);
        Assert.NotNull(rendering);
        var first = Layer(control, 1).Bounds;
        now += 100;
        rendering!(null, EventArgs.Empty);
        var second = Layer(control, 1).Bounds;
        Assert.True(second.X > first.X);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(snapshot, control.CurrentSnapshot);
        simple = false;
        control.UpdateSnapshot(snapshot with { IsPlaying = false });
        Assert.Null(rendering);
        Assert.NotEqual(second, Layer(control, 1).Bounds);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Discontinuity_DiscardsOldFeedbackAndKeepsNewHeldKeys(bool useUpdate, bool newSession) => MusicRenderingTestHost.Run(() =>
    {
        var control = new Frames().CreateControl();
        control.Width = 800;
        control.Height = 600;
        var first = Snapshot("A", "L");
        var next = Snapshot("L") with { SessionId = newSession ? "next" : "test", Revision = newSession ? 1 : 2 };
        if (useUpdate)
        {
            control.UpdateSnapshot(first);
            control.UpdateSnapshot(next);
        }
        else
        {
            control.RenderSnapshot(first, 800, 600);
            control.RenderSnapshot(next, 800, 600);
        }
        var reference = new MusicPlayfieldControl();
        reference.RenderSnapshot(next, 800, 600);
        Assert.Equal(Layer(reference, 2).Bounds, Layer(control, 2).Bounds);
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(next, control.CurrentSnapshot);
    });

    [Fact]
    public void ExplicitResize_RebuildsStaticTracks() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicPlayfieldControl();
        control.RenderSnapshot(Snapshot(), 800, 600);
        control.RenderSnapshot(Snapshot(), 1200, 900);
        var reference = new MusicPlayfieldControl();
        reference.RenderSnapshot(Snapshot(), 1200, 900);
        Assert.Equal(Layer(reference, 0).Bounds, Layer(control, 0).Bounds);
    });

    [Fact]
    public void DpiChange_RebuildsLabelsUsingActualPixelsPerDip() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicPlayfieldControl();
        control.RenderSnapshot(Snapshot(), 800, 600);
        VisualTreeHelper.SetRootDpi(control, new DpiScale(1.5, 1.5));
        control.RenderSnapshot(Snapshot(), 800, 600);
        var labels = Drawings(Layer(control, 0)).OfType<GlyphRunDrawing>().ToArray();
        Assert.Equal(7, labels.Length);
        Assert.All(labels, label => Assert.Equal(1.5, label.GlyphRun.PixelsPerDip));
    });

    internal static MusicRenderSnapshot Snapshot(params string[] keys) => MusicRenderSnapshot.Empty with
    {
        SessionId = "test", Revision = 1, ActiveKeys = keys, ActiveAutomaticKeys = keys
    };

    [Fact]
    public void PausedHeldKey_IsDrawnWithoutRequestingFrames_AndOwnershipCanChange() => MusicRenderingTestHost.Run(() =>
    {
        var frames = new Frames();
        var control = frames.CreateControl();
        using var host = MusicRenderingTestHost.Mount(control);
        var both = Snapshot("A") with { ActiveManualKeys = ["A"] };
        control.UpdateSnapshot(both);
        Assert.Equal(0, frames.SubscriberCount);
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        var manualBrush = Drawings(Layer(control, 2)).OfType<GeometryDrawing>().First().Brush;
        Assert.Equal(control.Skin.KeyManualGlowBrush, manualBrush);
        frames.Now = 100;
        control.UpdateSnapshot(Snapshot("A"));
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(control.Skin.KeyAutoGlowBrush, Drawings(Layer(control, 2)).OfType<GeometryDrawing>().First().Brush);
        control.UpdateSnapshot(Snapshot("A")); // Repeated press/ownership updates must not restart feedback.
        frames.Now = 300;
        control.UpdateSnapshot(Snapshot());
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(1, frames.SubscriberCount);
        // Ownership changes and repeated snapshots preserve the original 64/60s burst.
        frames.Now = 1067;
        frames.Fire();
        Assert.True(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(0, frames.SubscriberCount);
    });

    [Fact]
    public void ReleasedFeedback_ExpiresOnControlledFrame_ThenUnsubscribes() => MusicRenderingTestHost.Run(() =>
    {
        var frames = new Frames();
        var control = frames.CreateControl();
        using var host = MusicRenderingTestHost.Mount(control);
        control.UpdateSnapshot(Snapshot("A"));
        frames.Fire();
        frames.Now = 100;
        control.UpdateSnapshot(Snapshot());
        Assert.Equal(1, frames.SubscriberCount);
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        frames.Now = 400;
        frames.Fire();
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(1, frames.SubscriberCount);
        frames.Now = 1066;
        frames.Fire();
        Assert.False(Layer(control, 2).Bounds.IsEmpty);
        frames.Now = 1067;
        frames.Fire();
        Assert.True(Layer(control, 2).Bounds.IsEmpty);
        Assert.Equal(0, frames.SubscriberCount);
    });

    [Fact]
    public void HiddenOrUnloaded_LateUpdatesCannotSubscribe_AndReloadResumesPlayback() => MusicRenderingTestHost.Run(() =>
    {
        var frames = new Frames();
        var control = frames.CreateControl();
        control.UpdateSnapshot(Snapshot() with { IsPlaying = true });
        Assert.Equal(0, frames.SubscriberCount);
        using var host = MusicRenderingTestHost.Mount(control);
        Assert.Equal(1, frames.SubscriberCount);
        control.Visibility = Visibility.Collapsed;
        control.UpdateSnapshot(Snapshot() with { IsPlaying = true });
        Assert.Equal(0, frames.SubscriberCount);
        control.Visibility = Visibility.Visible;
        Assert.Equal(1, frames.SubscriberCount);
        // WPF can deliver Unloaded before a queued state update even while IsVisible remains true.
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        control.UpdateSnapshot(Snapshot() with { IsPlaying = true });
        Assert.Equal(0, frames.SubscriberCount);
        host.RootVisual = null;
        MusicRenderingTestHost.Drain();
        control.UpdateSnapshot(Snapshot() with { IsPlaying = true });
        Assert.Equal(0, frames.SubscriberCount);
        host.RootVisual = control;
        MusicRenderingTestHost.Drain();
        Assert.Equal(1, frames.SubscriberCount);
        control.UpdateSnapshot(Snapshot());
        Assert.Equal(0, frames.SubscriberCount);
    });

    private sealed class Frames
    {
        private event EventHandler? Rendering;
        public double Now { get; set; }
        public int SubscriberCount => Rendering?.GetInvocationList().Length ?? 0;
        public MusicPlayfieldControl CreateControl() => new(() => Now, h => Rendering += h, h => Rendering -= h);
        public void Fire() => Rendering?.Invoke(this, EventArgs.Empty);
    }

    internal static DrawingGroup Layer(MusicPlayfieldControl control, int layer) =>
        ((DrawingVisual)VisualTreeHelper.GetChild(control, layer)).Drawing ?? new DrawingGroup();

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child)) yield return descendant;
    }
}

internal static class MusicRenderingTestHost
{
    internal static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test timed out");
        if (error != null) throw new AggregateException(error);
    }

    internal static HwndSource Mount(FrameworkElement element)
    {
        var source = new HwndSource(new HwndSourceParameters("Music lifecycle regression")
        {
            Width = 800, Height = 600, WindowStyle = 0 // No visible or activated window.
        });
        source.RootVisual = element;
        element.Measure(new Size(800, 600));
        element.Arrange(new Rect(0, 0, 800, 600));
        element.UpdateLayout();
        Drain();
        Assert.True(element.IsLoaded);
        return source;
    }

    internal static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
