using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

// TEMPORARY diagnostic probe. Explicit command-line switches survive UAC elevation.
// No timer, buffers, disk access or per-sample allocation while disabled.
internal static class MusicOverlayDiagnostics
{
    internal enum Metric
    {
        RenderingCallback, RenderingIntervalMs, CallbackDisplayStepMs, RenderCoreMs, SnapshotDrawMs, NotesMs, HitMs, HoldMs,
        CaptureMs, CopyPixelsMs, BlendMs, WritePixelsMs, DecodeMs, StaticDecodeMs, HoldTextureDecodeMs, CacheHit, CacheMiss,
        VisibleNotes, ActiveHits, ActiveHolds, SnapshotAgeMs, DisplayStepMs,
        DisplayCorrectionMs, RepeatedRenderingTime, CoordinatorQueueMs, PresenterQueueMs,
        InputDownLateSongMs, InputUpLateSongMs, SubmitMs, CaptureFrameMs,
        CaptureDispatchMs, OcrCloneMs, OcrWorkMs, OcrQueueMs,
        OcrInferenceLockWaitMs, OcrPreprocessMs, OcrInferenceMs, OcrDecodeMs, OcrRecognitionMs, OcrDetectorLockWaitMs,
        OcrNotifyMs, OcrUiInvokeTotalMs, OcrUiInvokeQueueMs, OcrUiInvokeExecuteMs,
        DispatcherQueueMs, DispatcherExecuteMs, UiRenderProbeQueueMs,
        CoordinatorExecuteMs, PresenterExecuteMs, CoordinatorActionMs, PresenterActionMs, Count
    }

    private static readonly string[] Arguments = Environment.GetCommandLineArgs();
    internal static readonly bool Enabled = Array.IndexOf(Arguments, "--music-diagnostics=off") < 0 &&
        (Array.IndexOf(Arguments, "--music-diagnostics=on") >= 0 || Environment.GetEnvironmentVariable("BPP_MUSIC_DIAGNOSTICS") == "1");
    private static ConcurrentBag<Writer>? _writers;
    private static ConcurrentBag<ControlProbe>? _controls;
    private static Thread? _thread;
    private static readonly ManualResetEventSlim Stop = new(false);
    [ThreadStatic] private static Writer? s_writer;
    private static int _nextControl;
    private static readonly ConcurrentQueue<object> Events = new();
    private static int _eventCount;
    private static long _eventDropped;
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    internal static void Event(string name, object detail)
    {
        if (!Enabled || _writers == null) return;
        if (Interlocked.Increment(ref _eventCount) > 4096)
        {
            Interlocked.Decrement(ref _eventCount); Interlocked.Increment(ref _eventDropped); return;
        }
        Events.Enqueue(new { type = "event", name, timestamp = Stopwatch.GetTimestamp(), threadId = GetCurrentThreadId(), detail });
    }
    internal static long Timestamp() => Enabled ? Stopwatch.GetTimestamp() : 0;
    internal static void Elapsed(Metric metric, long start)
    {
        if (start != 0) Record(metric, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
    }

    internal readonly struct Measurement(Metric metric) : IDisposable
    {
        private readonly long _start = Enabled ? Stopwatch.GetTimestamp() : 0;
        public void Dispose()
        {
            if (_start != 0) Record(metric, (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency);
        }
    }

    internal static Measurement Measure(Metric metric) => new(metric);
    internal readonly struct UiOperation(Metric metric, string owner, string caller, long posted) : IDisposable
    {
        private readonly long _start = Timestamp();
        public void Dispose()
        {
            if (_start == 0) return;
            long end = Stopwatch.GetTimestamp();
            double executeMs = (end - _start) * 1000.0 / Stopwatch.Frequency;
            double queueMs = posted == 0 ? 0 : (_start - posted) * 1000.0 / Stopwatch.Frequency;
            Record(metric, executeMs);
            if (executeMs >= 8 || queueMs >= 16)
                Event("ui-known-operation", new { owner, caller, posted, started = _start, ended = end, queueMs, executeMs });
        }
    }

    internal static void Record(Metric metric, double value, double expectedSongMs = double.NaN)
    {
        if (!Enabled || _writers == null || !double.IsFinite(value)) return;
        if (s_writer == null) { s_writer = new Writer(); _writers.Add(s_writer); }
        s_writer.Write(new Sample(metric, value, Stopwatch.GetTimestamp(), expectedSongMs));
    }

    // SPSC bounded ring per producer thread: a slow diagnostic writer drops samples,
    // never blocks rendering/input, and reports those drops in the next summary.
    internal readonly record struct Sample(Metric Metric, double Value, long Timestamp, double ExpectedSongMs = double.NaN);
    internal sealed class Writer
    {
        internal readonly uint ThreadId = GetCurrentThreadId();
        private readonly Sample[] _buffer = new Sample[32768];
        private int _read, _write;
        internal long Dropped;
        internal void Write(Sample value)
        {
            int next = (_write + 1) & (_buffer.Length - 1);
            if (next == Volatile.Read(ref _read)) { Interlocked.Increment(ref Dropped); return; }
            _buffer[_write] = value;
            Volatile.Write(ref _write, next);
        }
        internal bool TryRead(out Sample sample)
        {
            if (_read == Volatile.Read(ref _write)) { sample = default; return false; }
            sample = _buffer[_read];
            Volatile.Write(ref _read, (_read + 1) & (_buffer.Length - 1));
            return true;
        }
    }

    internal sealed class ControlProbe
    {
        internal readonly int Id = Interlocked.Increment(ref _nextControl);
        internal volatile bool Loaded, Visible, Hooked, Playing, Preparing, Released, HitBursts, HoldGlow, Simple;
        internal long Hwnd;
        internal volatile string SkinId = "", Branch = "", SessionId = "", ChartFile = "";
        internal double Width, Height, Dpi, Speed, Intensity;
        internal int FallDurationMs, VisualOffsetMs;
        internal long Callbacks, CoreCalls, SnapshotDraws, Hooks, Unhooks, LastCallback;
        private double _lastDisplay, _lastRenderingTime;
        private double _lastCallbackDisplay;
        private string? _callbackSession;
        private long _callbackRevision;
        internal bool NextDisplayIsCallback;
        private string? _session;
        private long _revision;
        private bool _hasDisplay;

        internal void Rendering(double renderingTime)
        {
            Record(Metric.RenderingCallback, 1);
            NextDisplayIsCallback = true;
            long now = Stopwatch.GetTimestamp();
            if (LastCallback != 0) Record(Metric.RenderingIntervalMs, (now - LastCallback) * 1000.0 / Stopwatch.Frequency);
            LastCallback = now;
            if (Callbacks != 0 && renderingTime == _lastRenderingTime) Record(Metric.RepeatedRenderingTime, 1);
            _lastRenderingTime = renderingTime;
            Interlocked.Increment(ref Callbacks);
        }
        internal void Display(MusicRenderSnapshot snapshot, double now, double display)
        {
            if (NextDisplayIsCallback)
            {
                if (_callbackSession == snapshot.SessionId && _callbackRevision == snapshot.Revision)
                    Record(Metric.CallbackDisplayStepMs, display - _lastCallbackDisplay);
                _lastCallbackDisplay = display; _callbackSession = snapshot.SessionId; _callbackRevision = snapshot.Revision;
                NextDisplayIsCallback = false;
            }
            Record(Metric.SnapshotAgeMs, now - snapshot.HostMonotonicMs);
            if (_hasDisplay && _session == snapshot.SessionId && _revision == snapshot.Revision)
                Record(Metric.DisplayStepMs, display - _lastDisplay);
            _hasDisplay = true; _session = snapshot.SessionId; _revision = snapshot.Revision; _lastDisplay = display;
        }
        internal object Snapshot() => new
        {
            Id, Hwnd, Simple, Loaded, Visible, Hooked, Playing, Preparing, Released, SkinId, Branch, SessionId, ChartFile,
            HitBursts, HoldGlow, Width, Height, Dpi, Speed, Intensity, FallDurationMs, VisualOffsetMs,
            callbacks = Interlocked.Read(ref Callbacks), coreCalls = Interlocked.Read(ref CoreCalls),
            snapshotDraws = Interlocked.Read(ref SnapshotDraws), hooks = Interlocked.Read(ref Hooks),
            unhooks = Interlocked.Read(ref Unhooks), lastCallbackTimestamp = Interlocked.Read(ref LastCallback)
        };
    }

    internal static ControlProbe? RegisterControl()
    {
        if (!Enabled || _controls == null) return null;
        var probe = new ControlProbe(); _controls.Add(probe); return probe;
    }

    internal static void Start(int renderTier)
    {
        if (!Enabled || _thread != null) return;
        _writers = new(); _controls = new();
        int startupThread = Environment.CurrentManagedThreadId;
        uint startupOsThread = GetCurrentThreadId();
        _thread = new Thread(() => Collect(renderTier, startupThread, startupOsThread)) { IsBackground = true, Name = "Music diagnostic summary", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    internal static void Shutdown()
    {
        if (!Enabled || _thread == null) return;
        MusicDispatcherDiagnostics.Shutdown();
        Stop.Set(); _thread.Join(2000);
    }

    private static void Collect(int renderTier, int startupThread, uint startupOsThread)
    {
        try
        {
            string? argumentDirectory = null;
            foreach (string argument in Arguments)
                if (argument.StartsWith("--music-diagnostics-dir=", StringComparison.Ordinal))
                    argumentDirectory = argument["--music-diagnostics-dir=".Length..];
            string directory = argumentDirectory ?? Environment.GetEnvironmentVariable("BPP_MUSIC_DIAGNOSTICS_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "data", "music-diagnostics");
            Directory.CreateDirectory(directory);
            using var output = new StreamWriter(Path.Combine(directory, $"music-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl"));
            var assembly = typeof(MusicOverlayDiagnostics).Assembly;
            using (var stream = File.OpenRead(assembly.Location))
            using (var pe = new PEReader(stream))
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "identity", utc = DateTime.UtcNow, pid = Environment.ProcessId,
                    exe = Environment.ProcessPath, dll = assembly.Location, baseDirectory = AppContext.BaseDirectory,
                    workingDirectory = Environment.CurrentDirectory, mvid = assembly.ManifestModule.ModuleVersionId,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                    readyToRun = pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size > 0,
                    runtime = Environment.Version.ToString(), stopwatchFrequency = Stopwatch.Frequency, renderTier, startupThread, startupOsThread,
                    metricNames = Enum.GetNames<Metric>(),
                    qpcTimestamp = Stopwatch.GetTimestamp(), qpcUtc = DateTime.UtcNow,
                    activation = Array.IndexOf(Arguments, "--music-diagnostics=on") >= 0 ? "command-line" : "environment",
                    note = "CPU timings and raw QPC samples; actual GPU/DWM present requires matching ETL. Control fields sampled; counters cumulative. OCR inference is host wall time around session.Run, not GPU execution duration."
                }));
            using var process = Process.GetCurrentProcess();
            var values = new List<Sample>[(int)Metric.Count];
            for (int i = 0; i < values.Length; i++) values[i] = new();
            long last = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(false);
            long collectorAllocated = GC.GetAllocatedBytesForCurrentThread();
            long gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            TimeSpan pause = GC.GetTotalPauseDuration();
            output.Flush();
            while (true)
            {
                bool stopping = Stop.Wait(1000);
                long now = Stopwatch.GetTimestamp();
                double seconds = (now - last) / (double)Stopwatch.Frequency;
                long dropped = 0;
                var submissions = new List<object>();
                foreach (var writer in _writers!)
                {
                    // Bound collection even if a producer remains busy.
                    for (int i = 0; i < 32768 && writer.TryRead(out var sample); i++)
                    {
                        values[(int)sample.Metric].Add(sample);
                        if (double.IsFinite(sample.ExpectedSongMs)) submissions.Add(new
                        {
                            down = sample.Metric == Metric.InputDownLateSongMs, expectedSongMs = sample.ExpectedSongMs,
                            actualSongMs = sample.ExpectedSongMs + sample.Value, lateSongMs = sample.Value,
                            observedTimestamp = sample.Timestamp
                        });
                    }
                    dropped += Interlocked.Exchange(ref writer.Dropped, 0);
                }
                while (Events.TryDequeue(out var item)) { Interlocked.Decrement(ref _eventCount); output.WriteLine(JsonSerializer.Serialize(item)); }
                var metrics = new Dictionary<string, object>();
                for (int i = 0; i < values.Length; i++)
                {
                    var list = values[i]; if (list.Count == 0) continue;
                    list.Sort((a, b) => a.Value.CompareTo(b.Value));
                    double sum = 0; foreach (var s in list) sum += s.Value;
                    double Percentile(double p) => list[Math.Max(0, (int)Math.Ceiling(list.Count * p) - 1)].Value;
                    metrics[((Metric)i).ToString()] = new
                    {
                        count = list.Count, perSecond = list.Count / seconds, mean = sum / list.Count,
                        min = list[0].Value, p50 = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99),
                        max = list[^1].Value, maxTimestamp = list[^1].Timestamp
                    };
                    list.Clear();
                }
                var controlStates = new List<object>(); foreach (var control in _controls!) controlStates.Add(control.Snapshot());
                process.Refresh();
                long nextAllocated = GC.GetTotalAllocatedBytes(false);
                long next0 = GC.CollectionCount(0), next1 = GC.CollectionCount(1), next2 = GC.CollectionCount(2);
                double nextCpu = process.TotalProcessorTime.TotalMilliseconds;
                TimeSpan nextPause = GC.GetTotalPauseDuration();
                long nextCollectorAllocated = GC.GetAllocatedBytesForCurrentThread();
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "window", utc = DateTime.UtcNow, startTimestamp = last, endTimestamp = now, seconds,
                    dropped, eventDropped = Interlocked.Exchange(ref _eventDropped, 0), metrics, submissions, controls = controlStates,
                    allocatedBytesPerSecond = (nextAllocated - allocated) / seconds,
                    collectorAllocatedBytesPerSecond = (nextCollectorAllocated - collectorAllocated) / seconds,
                    gen0 = next0 - gen0, gen1 = next1 - gen1, gen2 = next2 - gen2,
                    gcPauseMs = (nextPause - pause).TotalMilliseconds,
                    cpuCores = (nextCpu - cpu) / (seconds * 1000),
                    cpuPercentOfMachine = (nextCpu - cpu) / (seconds * 10 * Environment.ProcessorCount),
                    workingSetBytes = process.WorkingSet64
                }));
                output.Flush();
                last = now; allocated = nextAllocated; cpu = nextCpu; pause = nextPause;
                collectorAllocated = nextCollectorAllocated;
                gen0 = next0; gen1 = next1; gen2 = next2;
                if (stopping) break;
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never turn a file/permission error into a playback failure.
            Serilog.Log.Warning(ex, "Music diagnostics stopped before completing collection");
        }
    }
}
