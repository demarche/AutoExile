using System.Collections.Concurrent;
using System.Diagnostics;

namespace AutoExile.Systems
{
    // Never call the overlay logger from a native input call or the watchdog.
    // A stalled host drains these bounded records on its next Tick, preserving the
    // actual event timestamp rather than the logger's later flush timestamp.
    public static class InputLatencyDiagnostics
    {
        private const int Capacity = 1024;
        private static readonly ConcurrentQueue<string> Pending = new();
        private static readonly ConcurrentDictionary<long, Operation> Active = new();
        private static readonly AsyncLocal<long> Correlation = new();
        private static readonly object Lifecycle = new();
        private static CancellationTokenSource? _stop;
        private static Thread? _watchdog;
        private static long _nextId, _dropped, _tickAt, _renderAt, _lastFrameStallAt;
        private static bool _monitorFrames;
        public static long SequenceId { get => Correlation.Value; set => Correlation.Value = value; }
        public static long NewId() => Interlocked.Increment(ref _nextId);

        public static void Start(Func<string> desktopSnapshot)
        {
            lock (Lifecycle)
            {
                if (_watchdog != null) return;
                var stop = _stop = new CancellationTokenSource();
                _watchdog = new Thread(() => Watch(stop.Token, desktopSnapshot))
                { IsBackground = true, Name = "AutoExile input watchdog" };
                _watchdog.Start();
            }
        }

        public static void Stop()
        {
            lock (Lifecycle)
            {
                if (_stop == null) return;
                _stop.Cancel();
                // Never block plugin shutdown on a native desktop query.
                if (_watchdog!.Join(250)) _stop.Dispose();
                _stop = null;
                _watchdog = null;
                _monitorFrames = false;
            }
        }

        public static Operation Begin(string stage, bool always = false, string detail = "") =>
            new(stage, always, detail);

        public static void Frame(string name, bool enabled)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = name == "Tick"
                ? Interlocked.Exchange(ref _tickAt, now)
                : Interlocked.Exchange(ref _renderAt, now);
            if (enabled && _monitorFrames && previous != 0 &&
                Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds >= 500)
                Record($"event=frame-gap stage={name} elapsedMs={Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds:F1}");
            if (name == "Tick") Volatile.Write(ref _monitorFrames, enabled);
        }

        public static void Record(string message) => Enqueue(
            $"[InputTrace] at={DateTimeOffset.Now:O} seq={SequenceId} thread={Environment.CurrentManagedThreadId} {message}");

        private static void Enqueue(string record)
        {
            Pending.Enqueue(record);
            while (Pending.Count > Capacity && Pending.TryDequeue(out _))
                Interlocked.Increment(ref _dropped);
        }

        public static void Drain(Action<string> logger, int maximum = 24)
        {
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped != 0) Enqueue($"[InputTrace] at={DateTimeOffset.Now:O} event=dropped count={dropped}");
            // Both a count limit and a time budget prevent log bursts monopolizing Tick.
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < maximum && Pending.TryDequeue(out var record); i++)
            {
                try { logger(record); }
                catch { Interlocked.Increment(ref _dropped); break; }
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
            }
        }

        private static void Watch(CancellationToken stop, Func<string> snapshot)
        {
            while (!stop.WaitHandle.WaitOne(100))
            {
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    foreach (var operation in Active.Values)
                        operation.ReportStall(now);
                    var tick = Volatile.Read(ref _tickAt);
                    var render = Volatile.Read(ref _renderAt);
                    var tickMs = tick == 0 ? 0 : Stopwatch.GetElapsedTime(tick, now).TotalMilliseconds;
                    var renderMs = render == 0 ? 0 : Stopwatch.GetElapsedTime(render, now).TotalMilliseconds;
                    if (Volatile.Read(ref _monitorFrames) && Math.Max(tickMs, renderMs) >= 500 &&
                        Stopwatch.GetElapsedTime(_lastFrameStallAt, now).TotalMilliseconds >= 1000)
                    {
                        _lastFrameStallAt = now;
                        Record($"event=host-stall tickAgeMs={tickMs:F1} renderAgeMs={renderMs:F1} " +
                            $"poolPending={ThreadPool.PendingWorkItemCount} poolThreads={ThreadPool.ThreadCount} " +
                            $"gc=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}] desktop=[{snapshot()}]");
                    }
                }
                catch (Exception ex) { Record($"event=watchdog-error type={ex.GetType().Name}"); }
            }
        }

        public sealed class Operation : IDisposable
        {
            private readonly long _id = NewId(), _sequence = SequenceId;
            private readonly long _started = Stopwatch.GetTimestamp();
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly string _stage, _detail;
            private readonly bool _always;
            private long _lastStallAt;
            private int _disposed;
            public string Result { get; set; } = "pending";
            public bool Failed { get; set; }

            internal Operation(string stage, bool always, string detail)
            {
                _stage = stage; _always = always; _detail = detail;
                Active[_id] = this;
                if (always) Emit("begin", 0);
            }

            private void Emit(string kind, double elapsed) => Enqueue(
                $"[InputTrace] at={DateTimeOffset.Now:O} seq={_sequence} op={_id} " +
                $"thread={_thread} event={kind} stage={_stage} elapsedMs={elapsed:F1} " +
                $"result={Result} {_detail}");

            internal void ReportStall(long now)
            {
                var elapsed = Stopwatch.GetElapsedTime(_started, now).TotalMilliseconds;
                if (Volatile.Read(ref _disposed) != 0 || elapsed < 500 ||
                    (_lastStallAt != 0 && Stopwatch.GetElapsedTime(_lastStallAt, now).TotalMilliseconds < 1000)) return;
                _lastStallAt = now;
                Emit("still-running", elapsed);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Active.TryRemove(_id, out _);
                var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                if (Result == "pending") Result = "returned";
                if (_always || Failed || elapsed >= 50) Emit("end", elapsed);
            }
        }
    }
}
