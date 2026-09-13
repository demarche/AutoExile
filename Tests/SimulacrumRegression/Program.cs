using AutoExile.Systems;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine($"PASS: {name}");
}

var now = new DateTime(2026, 9, 12, 12, 0, 0);
var wave = new SimulacrumWaveObservation();
Check(!wave.IsFresh(now), "Initial unknown state cannot start/complete a wave");
Check(wave.Observe(1, 0, 15, now) && wave.IsActive, "Active final wave is observed");
Check(!wave.Observe(null, 0, 15, now.AddSeconds(5)) && wave.IsActive,
    "Missing active state cannot complete final wave");
Check(!wave.Observe(0, null, 15, now.AddSeconds(5)) && wave.IsActive,
    "Missing goodbye state cannot complete final wave");
Check(!wave.Observe(0, 0, null, now.AddSeconds(5)) && wave.Wave == 15,
    "Missing wave number cannot reset the run");
Check(!wave.Observe(0, 0, 999, now.AddSeconds(5)), "Invalid memory values are rejected");
Check(!wave.IsFresh(now.AddSeconds(11)) && wave.IsActive,
    "Stale memory stops combat eligibility without inventing completion");
Check(wave.Observe(0, 0, 15, now.AddSeconds(12)) && !wave.IsActive && wave.IsFresh(now.AddSeconds(12)),
    "Fresh inactive state confirms final wave completion");
Check(wave.Observe(1, 1, 15, now.AddSeconds(13)) && !wave.IsActive,
    "Goodbye state overrides active flag");

var gate = new InputSequenceGate();
var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
bool clicked = false;
var operation = gate.RunAsync(async token =>
{
    try
    {
        await settle.Task;
        token.ThrowIfCancellationRequested();
        clicked = true;
    }
    finally
    {
        cleanupStarted.SetResult();
        await cleanupFinished.Task;
    }
});
Check(gate.IsRunning, "Unfinished cursor movement retains input ownership regardless of timer");
var overlappingActionRan = false;
try { await gate.RunAsync(_ => { overlappingActionRan = true; return Task.CompletedTask; }); }
catch (InvalidOperationException) { }
Check(!overlappingActionRan && gate.IsRunning, "Second input cannot steal the cursor");
gate.Cancel();
Check(gate.IsRunning, "Cancellation does not open gate before cleanup");
settle.SetResult();
await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(!clicked && gate.IsRunning, "Cancelled cursor sequence never clicks and retains ownership during cleanup");
cleanupFinished.SetResult();
try { await operation; }
catch (OperationCanceledException) { }
Check(!gate.IsRunning, "Cancellation releases gate after cleanup");
try { await gate.RunAsync(_ => throw new InvalidOperationException("simulated memory read failure")); }
catch (InvalidOperationException) { }
Check(!gate.IsRunning, "Exception cannot leave the input gate stuck");
await gate.RunAsync(_ => { clicked = true; return Task.CompletedTask; });
Check(clicked && !gate.IsRunning, "New action works after recovery");
var progress = new SparkProgressTracker();
progress.Reset(now);
progress.Observe([new(1, 100), new(2, 100)], now);
progress.Observe([new(1, 100), new(2, 0), new(3, 100)], now.AddSeconds(1));
Check(progress.TotalKills == 1, "New spawns do not cancel an observed kill");
progress.Observe([new(1, 50), new(2, 0)], now.AddSeconds(3));
Check(progress.TotalKills == 1 && progress.NoProgressSeconds(now.AddSeconds(3)) == 0,
    "Boss damage is progress; despawns and repeated corpses are not kills");
progress.Observe([new(1, 50), new(4, 100)], now.AddSeconds(6));
Check(progress.NoProgressSeconds(now.AddSeconds(6)) == 3,
    "No-damage duration survives new spawns and expires independently of rate windows");
progress.Observe([new(1, 0), new(1, 0)], now.AddSeconds(7));
Check(progress.TotalKills == 2, "Duplicate entities cannot double-count kills");
progress.Reset(now.AddSeconds(8));
Check(progress.TotalKills == 0 && progress.NoProgressSeconds(now.AddSeconds(8)) == 0,
    "New position starts a fresh damage observation window");

var deadlineGate = new InputSequenceGate();
var deadlineCleanup = false;
try
{
    await deadlineGate.RunAsync(async token =>
    {
        try { await Task.Delay(Timeout.Infinite, token); }
        finally { deadlineCleanup = true; }
    }, TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(5));
}
catch (OperationCanceledException) { }
Check(deadlineCleanup && !deadlineGate.IsRunning, "Overdue input is cancelled and cleaned up without waiting for another action");

var overlayContext = new UnpumpedOverlayContext();
var oldContext = SynchronizationContext.Current;
var workerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Task overlayOperation;
var overlayGate = new InputSequenceGate();
try
{
    SynchronizationContext.SetSynchronizationContext(overlayContext);
    overlayOperation = overlayGate.RunAsync(_ => workerCompleted.Task);
}
finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
workerCompleted.SetResult();
await overlayOperation.WaitAsync(TimeSpan.FromSeconds(5));
Check(!overlayGate.IsRunning && overlayContext.Posts == 0,
    "Input cleanup does not depend on the overlay pumping its synchronization context");

// A native call can block before the first await. The UI caller must still return
// immediately, even when every preceding delay would have completed synchronously.
var nativeRelease = new ManualResetEventSlim();
var prefixEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var uiReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
var dispatchGate = new InputSequenceGate();
var callerThreadId = 0;
var inputThreadId = 0;
var uiThread = new Thread(() =>
{
    callerThreadId = Environment.CurrentManagedThreadId;
    SynchronizationContext.SetSynchronizationContext(overlayContext);
    uiReturned.SetResult(dispatchGate.RunAsync(_ =>
    {
        inputThreadId = Environment.CurrentManagedThreadId;
        prefixEntered.SetResult();
        nativeRelease.Wait(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }));
}) { IsBackground = true };
uiThread.Start();
Task dispatched;
try
{
    dispatched = await uiReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await prefixEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(callerThreadId != inputThreadId && dispatchGate.IsRunning && overlayContext.Posts == 0,
        "Blocking native prefix runs off the UI thread while retaining input ownership");
}
finally { nativeRelease.Set(); }
await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
Check(!dispatchGate.IsRunning, "Dispatch releases ownership after native prefix returns");

var traces = new List<string>();
InputLatencyDiagnostics.SequenceId = 12345;
using (var submission = InputLatencyDiagnostics.Begin("test-native", true, "flags=0x2"))
{
    await Task.Yield();
    submission.Result = "inserted=0/1 win32=5";
    submission.Failed = true;
}
InputLatencyDiagnostics.SequenceId = 0;
InputLatencyDiagnostics.Drain(traces.Add, 100);
Check(traces.Any(x => x.Contains("seq=12345") && x.Contains("event=begin")) &&
    traces.Any(x => x.Contains("seq=12345") && x.Contains("event=end") && x.Contains("inserted=0/1 win32=5")),
    "Buffered native traces correlate begin/end and preserve failure details across await");

InputLatencyDiagnostics.Start(() => "test-desktop");
InputLatencyDiagnostics.Frame("Tick", true);
InputLatencyDiagnostics.Frame("Render", true);
try
{
    using var blockedCall = InputLatencyDiagnostics.Begin("test-blocked-native");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline &&
        !(traces.Any(x => x.Contains("event=still-running") && x.Contains("test-blocked-native")) &&
          traces.Any(x => x.Contains("event=host-stall"))))
    {
        await Task.Delay(100);
        InputLatencyDiagnostics.Drain(traces.Add, 100);
    }
    Check(traces.Any(x => x.Contains("event=still-running") && x.Contains("test-blocked-native")),
        "Watchdog records an unfinished native operation without needing another frame");
    Check(traces.Any(x => x.Contains("event=host-stall") && x.Contains("test-desktop")),
        "Watchdog records absent Tick/Render callbacks with a desktop snapshot");
}
finally { InputLatencyDiagnostics.Stop(); }
for (var i = 0; i < 1100; i++) InputLatencyDiagnostics.Record("test-overflow");
for (var i = 0; i < 20; i++) InputLatencyDiagnostics.Drain(traces.Add, 100);
Check(traces.Any(x => x.Contains("event=dropped")), "Trace backlog is bounded and reports lost records");
InputLatencyDiagnostics.Record("test-logger-failure");
InputLatencyDiagnostics.Drain(_ => throw new InvalidOperationException("logger unavailable"));
Check(true, "Logger failure cannot escape into Tick or input cleanup");
Console.WriteLine($"All {checks} regression checks passed.");

sealed class UnpumpedOverlayContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
}
