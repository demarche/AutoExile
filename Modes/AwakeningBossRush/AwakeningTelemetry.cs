using System.Threading.Channels;

namespace AutoExile.Modes.AwakeningBossRush;

public sealed class AwakeningTelemetry : IDisposable
{
    private readonly Action<string> _log;
    private readonly string _generation;
    private readonly Channel<string> _disk = Channel.CreateBounded<string>(2048);
    private readonly Queue<string> _ring = new();
    private readonly Task _writer;
    public long Dropped { get; private set; }
    public string Error { get; private set; } = "";
    public AwakeningTelemetry(string directory, Action<string> log, string generation = "offline")
    {
        _log = log;
        _generation = generation;
        _writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                await using var stream = new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                await foreach (var line in _disk.Reader.ReadAllAsync()) await writer.WriteLineAsync(line);
            }
            catch (Exception ex) { Error = ex.Message; }
        });
    }
    public void Event(AwakeningRun run, string name, object? data = null)
    {
        var line = AwakeningJson.Serialize(new { schema = 1, @event = name, utc = DateTime.UtcNow, generation = _generation, run.RunId, run.AttemptId,
            run.BuildMvid, run.Instance, run.Area, run.Phase, elapsedMs = run.ElapsedSeconds * 1000, data });
        // Existing ExileCore log sink includes these records in Logs/VerboseYYYYMMDD.log.
        try { _log("[Awakening] " + line); }
        catch (Exception ex) { Error = "Verbose sink: " + ex.Message; }
        if (!_disk.Writer.TryWrite(line)) Dropped++;
    }
    public void Sample(object snapshot)
    {
        _ring.Enqueue(AwakeningJson.Serialize(snapshot));
        while (_ring.Count > 120) _ring.Dequeue();
    }
    public void FlushRing(AwakeningRun run, string reason)
    {
        Event(run, "diagnostic.ring", new { reason, count = _ring.Count, Dropped, Error });
        foreach (var sample in _ring)
        {
            using var document = System.Text.Json.JsonDocument.Parse(sample);
            Event(run, "diagnostic.sample", document.RootElement);
        }
    }
    public void Dispose()
    {
        _disk.Writer.TryComplete();
        // Bounded cleanup; no game objects or input on the writer thread.
        try { _writer.Wait(1500); } catch { }
    }
}
