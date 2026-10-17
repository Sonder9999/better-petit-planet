using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicLayoutAdjustmentTest
{
    private sealed class Fixture
    {
        public readonly TaskCompletionSource Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly MusicOverlayWindowCreation Creation;
        public readonly MusicLayoutAdjustmentControl Host;
        public readonly List<bool> Interactive = [];
        public bool WindowExists;
        public int Creates, Activations, Pauses, Saves;

        public Fixture()
        {
            Creation = new(async () => { Creates++; await Created.Task; WindowExists = true; });
            Host = new(() => Pauses++, () => Activations++,
                () => Creation.IsPending || !WindowExists ? Creation.EnsureAsync() : Task.CompletedTask,
                value => { if (WindowExists) Interactive.Add(value); }, () => { Saves++; return Task.CompletedTask; });
        }
    }

    [Fact]
    public async Task EnterWaitsForCreationAlreadyStartedByRendering()
    {
        var f = new Fixture();
        var render = f.Creation.EnsureAsync();
        var enter = f.Host.SetAsync(true);
        Assert.False(enter.IsCompleted);
        Assert.Equal(1, f.Creates);
        f.Created.SetResult(); await Task.WhenAll(render, enter);
        Assert.True(f.Host.Adjusting);
        Assert.Equal([true], f.Interactive);
        Assert.Equal(1, f.Pauses); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task ExitDuringCreationPreventsOldTrueFromRestoringInteraction()
    {
        var f = new Fixture();
        var enter = f.Host.SetAsync(true);
        await f.Host.SetAsync(false);
        f.Created.SetResult(); await enter;
        Assert.False(f.Host.Adjusting);
        Assert.DoesNotContain(true, f.Interactive);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task DisposeDuringCreationCannotApplyInteractionOrSaveAfterward()
    {
        var f = new Fixture();
        var enter = f.Host.SetAsync(true);
        f.Host.Dispose();
        f.Created.SetResult(); await enter;
        Assert.False(f.Host.Adjusting);
        Assert.Empty(f.Interactive); Assert.Equal(0, f.Saves);
    }

    [Fact]
    public async Task ClosingSettingsDuringSaveNeverActivatesOrEntersAdjustment()
    {
        var f = new Fixture();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MusicLayoutAdjustmentRequest(() => saved.Task, f.Host.SetAsync, () => f.Host.Adjusting);
        var entering = window.ToggleAsync();
        await window.CloseAsync();
        f.Created.SetResult(); saved.SetResult();
        bool updateClosedUi = await entering;
        Assert.Equal(0, f.Activations); Assert.False(f.Host.Adjusting);
        Assert.False(updateClosedUi); Assert.Empty(f.Interactive);
    }

    [Fact]
    public async Task ClosingSettingsDuringCreationEndsAdjustmentAndSkipsClosedUiUpdate()
    {
        var f = new Fixture();
        var window = new MusicLayoutAdjustmentRequest(() => Task.CompletedTask, f.Host.SetAsync, () => f.Host.Adjusting);
        var entering = window.ToggleAsync();
        Assert.True(f.Host.Adjusting);
        // Window exists, creation completion pending
        f.WindowExists = true;
        await window.CloseAsync();
        f.Created.SetResult();
        Assert.False(await entering);
        Assert.False(f.Host.Adjusting); Assert.Equal([false], f.Interactive);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task NewEntryAfterExitOwnsTheSharedCreationResult()
    {
        var f = new Fixture();
        var old = f.Host.SetAsync(true);
        await f.Host.SetAsync(false);
        var current = f.Host.SetAsync(true);
        f.Created.SetResult(); await Task.WhenAll(old, current);
        Assert.True(f.Host.Adjusting);
        Assert.Equal([true], f.Interactive);
        Assert.Equal(1, f.Creates);
        Assert.Equal(2, f.Saves);
    }

    [Fact]
    public async Task ConstructedWindowStillWaitsForItsPendingInitialization()
    {
        var f = new Fixture();
        var render = f.Creation.EnsureAsync(); f.WindowExists = true;
        var enter = f.Host.SetAsync(true);
        Assert.False(enter.IsCompleted); Assert.Empty(f.Interactive);
        f.Created.SetResult(); await Task.WhenAll(render, enter);
        Assert.Equal([true], f.Interactive);
    }

    [Fact]
    public async Task SecondClickCancelsEntryWhileSaving()
    {
        var f = new Fixture();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MusicLayoutAdjustmentRequest(() => saved.Task, f.Host.SetAsync, () => f.Host.Adjusting);
        var old = window.ToggleAsync();
        await window.ToggleAsync();
        saved.SetResult();
        Assert.False(await old);
        Assert.Equal(0, f.Activations); Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task EnterReleasesOwnedHoldAndExitRestoresPassThroughAndSavesNormalizedLayout()
    {
        var config = new TestConfig(); config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(new() { Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }], TotalDurationMs = 2500 });
        playback.Play(); Assert.Contains(input.Commands, c => c.IsDown);
        var modes = new List<bool>(); var savedX = new List<double>();
        using var host = new MusicLayoutAdjustmentControl(() => { playback.SetReadiness(false, "调整布局"); playback.Pause(); },
            () => { }, () => Task.CompletedTask, modes.Add, () =>
            { config.Config.MusicOverlay.Validate(); savedX.Add(config.Config.MusicOverlay.Layout.X); return Task.CompletedTask; });
        await host.SetAsync(true);
        Assert.Equal(PlayerState.Paused, playback.State); Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
        Assert.Contains(input.Commands, c => !c.IsDown);
        config.Config.MusicOverlay.Layout.Width = .8; config.Config.MusicOverlay.Layout.X = .2;
        await host.SetAsync(false);
        Assert.Equal([true, false], modes);
        Assert.Equal([0, .2], savedX);
        Assert.False(host.Adjusting); Assert.Equal(PlayerState.Paused, playback.State);
    }

    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public Task SaveAsync() => Task.CompletedTask;
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        public List<MusicKeyCommand> Commands { get; } = [];
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            Commands.AddRange(commands);
            return commands.Select(c => new MusicInputResult(c, true)).ToArray();
        }
    }
}
