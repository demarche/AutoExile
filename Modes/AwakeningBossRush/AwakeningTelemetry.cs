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
        // Rotation (2026-09-22): the single ~190 MB events.jsonl inside the OneDrive folder was re-synced on every
        // append; the active file is now rotated at 24 MB (events-<utc>.jsonl), so sync only touches a small file.
        _writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "events.jsonl");
                while (true)
                {
                    long size;
                    await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1 << 16))
                    {
                        size = stream.Length;
                        var rotate = false;
                        while (!rotate && await _disk.Reader.WaitToReadAsync())
                        {
                            while (_disk.Reader.TryRead(out var line)) { await writer.WriteLineAsync(line); size += line.Length + 2; }
                            await writer.FlushAsync();
                            rotate = size > 24L * 1024 * 1024;
                        }
                        if (!rotate) return; // channel completed
                    }
                    try { File.Move(path, Path.Combine(directory, $"events-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl")); }
                    catch (Exception ex) { Error = "rotate: " + ex.Message; }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
        });
    }
    public void Event(AwakeningRun run, string name, object? data = null)
    {
        var line = AwakeningJson.Serialize(new { schema = 1, @event = name, utc = DateTime.UtcNow, generation = _generation, run.RunId, run.AttemptId,
            run.BuildMvid, run.Instance, run.Area, run.Phase, elapsedMs = run.ElapsedSeconds * 1000, data });
        // 2026-09-22 (user: busy hourglass during the Awakening verbose log): every record was also pushed through the
        // ExileCore overlay/Verbose logger on the Tick thread (120-sample ring flushes came in bursts). The identical
        // records stay in events.jsonl, written off-thread; the overlay sink is no longer used.
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
