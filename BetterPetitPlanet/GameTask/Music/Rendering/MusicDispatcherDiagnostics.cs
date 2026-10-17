using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

// Passive timing of queued operations. Callback identity is optional private-runtime
// metadata, never used for control flow. CPU/wait stacks come from the paired ETL.
internal static class MusicDispatcherDiagnostics
{
    private sealed class Operation(long posted, uint producer)
    {
        internal readonly long Posted = posted;
        internal readonly uint Producer = producer;
        internal long Started;
        internal int Depth;
        internal string Callback = "unavailable";
    }
    private static readonly ConcurrentDictionary<DispatcherOperation, Operation> Operations = new();
    private static readonly ConcurrentDictionary<MethodInfo, string> Names = new();
    private static readonly FieldInfo? CallbackField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static Dispatcher? _dispatcher;
    private static Timer? _probe;
    private static int _probePending, _depth;
    private static long _overflow;

    internal static void Start(Dispatcher dispatcher)
    {
        if (_dispatcher != null) return;
        _dispatcher = dispatcher;
        dispatcher.Hooks.OperationPosted += Posted;
        dispatcher.Hooks.OperationStarted += Started;
        dispatcher.Hooks.OperationCompleted += Completed;
        dispatcher.Hooks.OperationAborted += Aborted;
        MusicOverlayDiagnostics.Event("dispatcher-probe", new { callbackMetadataAvailable = CallbackField != null,
            assembly = typeof(Dispatcher).Assembly.FullName, osThreadId = MusicOverlayDiagnostics.GetCurrentThreadId(), detailThresholdMs = 8, capacity = 4096 });
        _probe = new Timer(_ =>
        {
            if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _probePending, 1) != 0) return;
            long posted = Stopwatch.GetTimestamp();
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    MusicOverlayDiagnostics.Elapsed(MusicOverlayDiagnostics.Metric.UiRenderProbeQueueMs, posted);
                    Volatile.Write(ref _probePending, 0);
                }));
            }
            catch (InvalidOperationException) { Volatile.Write(ref _probePending, 0); }
        }, null, 250, 250);
    }

    private static void Posted(object? sender, DispatcherHookEventArgs e)
    {
        if (Operations.Count >= 4096) { Interlocked.Increment(ref _overflow); return; }
        Operations.TryAdd(e.Operation, new(Stopwatch.GetTimestamp(), MusicOverlayDiagnostics.GetCurrentThreadId()));
    }

    private static void Started(object? sender, DispatcherHookEventArgs e)
    {
        if (!Operations.TryGetValue(e.Operation, out var op)) return;
        op.Started = Stopwatch.GetTimestamp();
        op.Depth = ++_depth;
        try
        {
            if (CallbackField?.GetValue(e.Operation) is Delegate callback && Names.Count < 1024)
                op.Callback = Names.GetOrAdd(callback.Method, method => $"{method.DeclaringType?.FullName}.{method.Name}");
        }
        catch { /* Unsupported runtime metadata: preserve timing and use ETW stacks. */ }
        MusicOverlayDiagnostics.Elapsed(MusicOverlayDiagnostics.Metric.DispatcherQueueMs, op.Posted);
    }

    private static void Completed(object? sender, DispatcherHookEventArgs e)
    {
        if (!Operations.TryRemove(e.Operation, out var op) || op.Started == 0) return;
        long ended = Stopwatch.GetTimestamp();
        _depth = Math.Max(0, _depth - 1);
        double executionMs = (ended - op.Started) * 1000.0 / Stopwatch.Frequency;
        double queueMs = (op.Started - op.Posted) * 1000.0 / Stopwatch.Frequency;
        MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.DispatcherExecuteMs, executionMs);
        if (executionMs >= 8 || queueMs >= 16)
            MusicOverlayDiagnostics.Event("dispatcher-operation", new { callback = op.Callback, priority = e.Operation.Priority.ToString(),
                posted = op.Posted, started = op.Started, ended, queueMs, executionMs, depth = op.Depth, producerThread = op.Producer,
                overflow = Interlocked.Exchange(ref _overflow, 0), note = "inclusive wall time; use ETL sampled/CSwitch/ReadyThread stacks for CPU vs blocked wait" });
    }

    private static void Aborted(object? sender, DispatcherHookEventArgs e) => Operations.TryRemove(e.Operation, out _);

    internal static void Shutdown()
    {
        _probe?.Dispose();
        if (_dispatcher == null) return;
        _dispatcher.Hooks.OperationPosted -= Posted;
        _dispatcher.Hooks.OperationStarted -= Started;
        _dispatcher.Hooks.OperationCompleted -= Completed;
        _dispatcher.Hooks.OperationAborted -= Aborted;
        Operations.Clear();
        _dispatcher = null;
    }
}
