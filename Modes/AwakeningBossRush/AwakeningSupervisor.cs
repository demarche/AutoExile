using System.Text.Json;

namespace AutoExile.Modes.AwakeningBossRush;

/// <summary>Durable one-attempt gate. Codex performs review/edit/build/reload outside the game process.</summary>
public sealed class AwakeningSupervisor
{
    public AwakeningCheckpoint State { get; private set; } = new();
    public string StorageError { get; private set; } = "";
    public string Generation { get; } = Guid.NewGuid().ToString("N");
    public string Mvid { get; }
    public string LastCommand { get; private set; } = "";
    public int PersistenceRetries { get; private set; }
    public string LastPersistenceWarning { get; private set; } = "";
    private readonly string _file;
    private readonly Action<string, string> _replaceCheckpoint;
    public AwakeningRun Run => State.Run;
    public AwakeningSupervisor(string directory, string mvid, Action<string, string>? replaceCheckpoint = null)
    {
        Mvid = mvid;
        _file = Path.Combine(directory, "checkpoint.json");
        _replaceCheckpoint = replaceCheckpoint ?? ((source, destination) => File.Move(source, destination, true));
        try
        {
            if (File.Exists(_file)) State = JsonSerializer.Deserialize<AwakeningCheckpoint>(File.ReadAllText(_file), AwakeningJson.Options) ?? throw new InvalidDataException("Empty checkpoint");
            if (State.Schema != 1 || State.History == null || State.Requests == null || !ValidRun(State.Run) || State.History.Any(r => !ValidRun(r)))
                throw new InvalidDataException("Invalid checkpoint schema or missing collections");
            // Loading a DLL never arms input or resumes a previous attempt by itself.
            State.Armed = false;
        }
        catch (Exception ex) { StorageError = ex.Message; State = new(); }
    }
    private static bool ValidRun(AwakeningRun? run) => run != null && !string.IsNullOrWhiteSpace(run.RunId)
        && run.Bosses != null && run.Bosses.Values.All(x => x != null) && run.Recipe != null && run.PortalIds != null && run.PriorPortalIds != null
        && run.PhaseSeconds != null && run.LootReceipts != null && run.Visited != null && run.Visited.All(x => x is { Length: 2 })
        && run.UnresolvedLoot != null && run.MapBossIds != null && run.DeadMapBossIds != null && run.LootSweptBosses != null
        && (run.Map == null || (run.Map.Mods != null && run.Map.Mods.All(m => m != null && m.Stats != null && m.Values != null)))
        && double.IsFinite(run.ElapsedSeconds) && double.IsFinite(run.OperatingSeconds) && run.AttemptNumber >= 0;
    // 2026-09-22 (user: busy hourglass while the Awakening verbose log runs): Save() used to serialize the ~1.3 MB
    // checkpoint, fsync it and retry the OneDrive-locked replace with up to 1.5 s of Thread.Sleep — all on the game
    // Tick thread. Now the snapshot is serialized on the caller (consistent state) and written by a background thread
    // (latest snapshot wins). A persistent write failure still sets StorageError, which disarms the loop.
    private readonly object _saveGate = new();
    private byte[]? _pendingSnapshot;
    private readonly AutoResetEvent _saveSignal = new(false);
    private readonly ManualResetEventSlim _saveIdle = new(true);
    private Thread? _saver;
    public bool Save()
    {
        if (StorageError.Length > 0) return false;
        byte[] snapshot;
        // Finished runs keep their outcome/mod evidence; their scout breadcrumbs (Visited) made the checkpoint ~1.3 MB.
        foreach (var h in State.History) if (h.Visited.Count > 0) h.Visited = new();
        try { snapshot = JsonSerializer.SerializeToUtf8Bytes(State, AwakeningJson.Options); }
        catch (Exception ex) { StorageError = $"serialize checkpoint: {ex.GetType().Name}: {ex.Message}"; State.Armed = false; return false; }
        lock (_saveGate)
        {
            _pendingSnapshot = snapshot; _saveIdle.Reset();
            if (_saver == null) { _saver = new Thread(SaveLoop) { IsBackground = true, Name = "Awakening checkpoint writer", Priority = ThreadPriority.BelowNormal }; _saver.Start(); }
        }
        _saveSignal.Set();
        return true;
    }
    /// <summary>Waits (bounded) until the latest snapshot is on disk; used on unload.</summary>
    public void Flush(int timeoutMs = 2000) => _saveIdle.Wait(timeoutMs);
    private void SaveLoop()
    {
        while (true)
        {
            _saveSignal.WaitOne(1000);
            byte[]? snapshot;
            lock (_saveGate) { snapshot = _pendingSnapshot; _pendingSnapshot = null; if (snapshot == null) { _saveIdle.Set(); continue; } }
            WriteSnapshot(snapshot);
            lock (_saveGate) { if (_pendingSnapshot == null) _saveIdle.Set(); }
        }
    }
    private void WriteSnapshot(byte[] snapshot)
    {
        var operation = "write temporary checkpoint";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(snapshot, 0, snapshot.Length);
                fs.Flush(true);
            }
            operation = "replace checkpoint";
            // Cloud sync and file scanners can briefly deny replacement on Windows (OneDrive): back off, then overwrite in place.
            int[] delaysMs = [10, 25, 50, 100, 200, 400, 700];
            for (int retry = 0; ; retry++)
            {
                try { _replaceCheckpoint(temp, _file); break; }
                catch (Exception ex) when (retry >= delaysMs.Length &&
                    (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33))
                {
                    operation = "overwrite checkpoint";
                    File.Copy(temp, _file, true);
                    try { File.Delete(temp); } catch { }
                    PersistenceRetries++;
                    LastPersistenceWarning = "replace checkpoint failed; overwrote in place: " + ex.Message;
                    break;
                }
                catch (Exception ex) when (retry < delaysMs.Length &&
                    (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33))
                {
                    PersistenceRetries++;
                    LastPersistenceWarning = $"{operation}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
                    Thread.Sleep(delaysMs[retry]);
                }
            }
        }
        catch (Exception ex) { StorageError = $"{operation}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"; State.Armed = false; }
    }
    public string Command(string action, string requestId, string expectedGeneration, string review = "", string expectedMvid = "")
    {
        var rejection = ValidateRequest(requestId, expectedGeneration);
        if (rejection != null) return LastCommand = rejection;
        string result;
        switch (action)
        {
            case "arm": State.Armed = true; result = "armed"; break;
            case "stop": State.Armed = false; result = "stopped"; break;
            case "review":
                if (Run.Outcome == AttemptOutcome.None || Run.RecoveryRequired || !ValidReview(review)) return LastCommand = "rejected: recovered terminal attempt and structured evidence review required";
                Run.Reviewed = true; Run.Review = review; result = "reviewed"; break;
            case "begin":
                if (!State.Armed || expectedMvid != Mvid) return LastCommand = "rejected: arm and verify loaded MVID first";
                if (Run.RecoveryRequired) return LastCommand = "rejected: complete recovery first";
                if (Run.Outcome == AttemptOutcome.None && Run.AttemptNumber > 0) return LastCommand = "rejected: unfinished attempt; recover/evaluate it first";
                if (Run.Outcome != AttemptOutcome.None && !Run.Reviewed) return LastCommand = "rejected: Codex review required";
                if (Run.Outcome != AttemptOutcome.None)
                {
                    State.History.Add(JsonSerializer.Deserialize<AwakeningRun>(AwakeningJson.Serialize(Run), AwakeningJson.Options)!);
                    // A reviewed instance mismatch cannot reuse old bosses, loot or portal ownership.
                    // Mode captures the current Hideout portals as the pre-activation baseline.
                    if (Run.Outcome == AttemptOutcome.Success || Run.Reason is "unexpected_area_change" or "wrong_instance_on_reentry")
                        State.Run = new() { PriorPortalIds = Run.PortalIds.ToList() };
                }
                Run.AttemptNumber++; Run.AttemptId = Guid.NewGuid().ToString("N");
                Run.Outcome = AttemptOutcome.None; Run.Reason = ""; Run.Reviewed = false; Run.Review = "";
                Run.EnteredUtc = null; Run.EndedUtc = null; Run.ElapsedSeconds = 0;
                Run.ReturnedToHideout = false; Run.StashCompleted = false; Run.BuildMvid = Mvid;
                Run.RecoveryRequired = false;
                Run.Phase = AwakeningPhase.Prepare;
                result = "begun"; break;
            default: return LastCommand = "rejected: unknown action";
        }
        return RecordCommand(requestId, result);
    }
    public string? ValidateRequest(string requestId, string generation)
    {
        if (StorageError.Length > 0) return "rejected: checkpoint " + StorageError;
        if (generation != Generation || string.IsNullOrWhiteSpace(requestId)) return "rejected: generation/requestId";
        return State.Requests.Contains(requestId) ? "duplicate:" + requestId : null;
    }
    public string RecordCommand(string requestId, string result)
    {
        State.Requests.Add(requestId);
        if (State.Requests.Count > 256) State.Requests.RemoveAt(0);
        LastCommand = requestId + ":" + result;
        return Save() ? LastCommand : "rejected: persistence failed: " + StorageError;
    }
    public static bool ValidReview(string review)
    {
        try
        {
            using var doc = JsonDocument.Parse(review);
            return new[] { "observations", "diagnosis", "changes", "validation", "nextAction" }.All(key =>
                doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    public bool Finish(AttemptOutcome outcome, string reason, DateTime now)
    {
        if (Run.Outcome != AttemptOutcome.None) return false;
        Run.Outcome = outcome; Run.Reason = reason; Run.EndedUtc = now;
        Run.Phase = AwakeningPhase.AwaitingReview; Run.Reviewed = false;
        return Save();
    }
    public static AttemptOutcome Watchdog(AwakeningRun run, bool alive, bool inMap, double timeout = 300)
    {
        if (run.Outcome != AttemptOutcome.None || run.AttemptNumber == 0) return AttemptOutcome.None;
        if (!alive && inMap) return AttemptOutcome.Death;
        return inMap && run.EnteredUtc.HasValue && run.ElapsedSeconds >= timeout ? AttemptOutcome.Timeout : AttemptOutcome.None;
    }
}
