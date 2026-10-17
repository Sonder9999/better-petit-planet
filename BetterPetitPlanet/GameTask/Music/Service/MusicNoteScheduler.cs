using System;
using BetterPetitPlanet.GameTask.Music.Rendering;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>Consumes note starts exactly once; owns only successfully submitted keys.</summary>
public sealed class MusicNoteScheduler
{
    private readonly IMusicInputTransport _transport;
    private readonly MusicPlaybackOptions _options;
    private readonly Func<double> _now;
    private sealed record OwnedNote(PerformanceEvent Note, IntPtr Target, MusicInputMode Mode);
    private sealed record ReleaseAttempt(int Count, double NextMs);
    private readonly Dictionary<string, OwnedNote> _held = [];
    private readonly Dictionary<string, ReleaseAttempt> _releases = [];
    private IReadOnlyList<PerformanceEvent> _notes = [];
    public int NextIndex { get; private set; }
    public string[] ActiveKeys => _held.Keys.ToArray();
    public string? LastError { get; private set; }
    public bool HasPendingReleases => _releases.Values.Any(attempt => attempt.Count < _options.ReleaseRetryAttempts);
    public event Action<MusicKeyCommand>? Submitted;
    public event Action<string>? Failed;
    public event Action<string>? ReleaseExhausted;
    public MusicNoteScheduler(IMusicInputTransport transport, MusicPlaybackOptions? options = null,
        Func<double>? monotonicMilliseconds = null)
    {
        _transport = transport;
        _options = options ?? new MusicPlaybackOptions();
        _options.Validate();
        _now = monotonicMilliseconds ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    public void Load(IReadOnlyList<PerformanceEvent> notes)
    {
        if (_held.Count != 0) throw new InvalidOperationException("Release keys before replacing a score.");
        _notes = notes;
        NextIndex = 0;
        LastError = null;
    }

    public void Seek(double positionMs, IntPtr target, MusicInputMode mode, bool restartExhausted = false)
    {
        ReleaseAll(target, mode, restartExhausted);
        NextIndex = 0;
        while (NextIndex < _notes.Count && _notes[NextIndex].TimeMs < positionMs) NextIndex++;
    }

    public void SkipThrough(double positionMs)
    {
        while (NextIndex < _notes.Count && _notes[NextIndex].TimeMs <= positionMs) NextIndex++;
    }

    public void Advance(double positionMs, bool automatic, IntPtr target, MusicInputMode mode)
    {
        var releases = _held.Values.Where(owned => _releases.ContainsKey(owned.Note.Key) ||
                !automatic || owned.Target != target || owned.Mode != mode ||
                owned.Note.TimeMs + owned.Note.DurationMs <= positionMs)
            .Select(owned => new MusicKeyCommand(owned.Note, false)).ToArray();
        Send(releases, target, mode);
        while (NextIndex < _notes.Count && _notes[NextIndex].TimeMs <= positionMs)
        {
            int start = NextIndex;
            int time = _notes[start].TimeMs;
            while (NextIndex < _notes.Count && _notes[NextIndex].TimeMs == time) NextIndex++;
            if (!automatic) continue;
            var notes = _notes.Skip(start).Take(NextIndex - start).ToArray();
            // A scheduler delayed past a complete note never replays that historical note.
            notes = notes.Where(note => note.TimeMs + note.DurationMs > positionMs).ToArray();
            var conflicts = notes.Where(note => _held.TryGetValue(note.Key, out var owned) &&
                (note.Kind == MusicNoteKind.Hold || owned.Note.Kind == MusicNoteKind.Hold)).ToArray();
            foreach (var note in conflicts) ReportFailure($"Lane {note.Key}: overlapping hold ownership.");
            notes = notes.Except(conflicts).ToArray();
            var repeated = notes.Where(note => _held.ContainsKey(note.Key))
                .Select(note => new MusicKeyCommand(_held[note.Key].Note, false)).ToArray();
            Send(repeated, target, mode);
            if (_held.Values.Any(owned => owned.Target != target || owned.Mode != mode)) continue;
            Send(notes.Where(note => !_held.ContainsKey(note.Key)).Select(note => new MusicKeyCommand(note, true)).ToArray(), target, mode);
        }
    }

    public void ReleaseAll(IntPtr target, MusicInputMode mode, bool restartExhausted = false)
    {
        if (restartExhausted)
            foreach (var key in _releases.Where(pair => pair.Value.Count >= _options.ReleaseRetryAttempts)
                         .Select(pair => pair.Key).ToArray())
                _releases[key] = _releases[key] with { Count = 0 };
        Send(_held.Values.Select(owned => new MusicKeyCommand(owned.Note, false)).ToArray(), target, mode);
    }

    public void RetryPendingReleases()
        => Send(_releases.Keys.Select(key => new MusicKeyCommand(_held[key].Note, false)).ToArray(),
            IntPtr.Zero, MusicInputMode.Keyboard);

    private bool BeginReleaseAttempt(MusicKeyCommand command)
    {
        if (command.IsDown) return true;
        double now = _now();
        if (_releases.TryGetValue(command.Note.Key, out var previous) &&
            (previous.Count >= _options.ReleaseRetryAttempts || now < previous.NextMs)) return false;
        _releases[command.Note.Key] = new((previous?.Count ?? 0) + 1, now + _options.ReleaseRetryIntervalMs);
        return true;
    }

    private void Send(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
    {
        if (commands.Count == 0) return;
        var routed = commands.Where(BeginReleaseAttempt).GroupBy(command => command.IsDown ? (target, mode) :
            (_held[command.Note.Key].Target, _held[command.Note.Key].Mode));
        foreach (var group in routed)
        foreach (var result in Submit(group.ToArray(), group.Key.Item1, group.Key.Item2))
        {
            if (!result.Submitted)
            {
                ReportFailure(result.Error ?? "Input submission failed.");
                if (!result.Command.IsDown && _releases[result.Command.Note.Key].Count == _options.ReleaseRetryAttempts)
                {
                    ReportFailure($"Release retries exhausted for lane {result.Command.Note.Key} after {_options.ReleaseRetryAttempts} attempts.");
                    ReleaseExhausted?.Invoke(result.Command.Note.Key);
                }
                continue;
            }
            var command = result.Command;
            if (command.IsDown) _held[command.Note.Key] = new(command.Note, group.Key.Item1, group.Key.Item2);
            else { _held.Remove(command.Note.Key); _releases.Remove(command.Note.Key); }
            Submitted?.Invoke(command);
        }
    }

    private IReadOnlyList<MusicInputResult> Submit(MusicKeyCommand[] commands, IntPtr target, MusicInputMode mode)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.SubmitMs);
        try { return _transport.Submit(commands, target, mode); }
        catch (Exception ex) { return commands.Select(command => new MusicInputResult(command, false, ex.Message)).ToArray(); }
    }

    private void ReportFailure(string message)
    {
        LastError = message;
        Failed?.Invoke(message);
    }
}
