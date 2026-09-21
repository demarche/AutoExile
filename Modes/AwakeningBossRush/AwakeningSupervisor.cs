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
    public bool Save()
    {
        if (StorageError.Length > 0) return false;
        var operation = "write temporary checkpoint";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, State, AwakeningJson.Options);
                fs.Flush(true);
            }
            operation = "replace checkpoint";
            // Cloud sync and file scanners can briefly deny replacement on Windows.
            // Only retry the already flushed replacement, never the command or its game action.
            // At most 85 ms of backoff; persistent failures still disarm before input starts.
            int[] delaysMs = [10, 25, 50];
            for (int retry = 0; ; retry++)
            {
                try { _replaceCheckpoint(temp, _file); break; }
                catch (Exception ex) when (retry < delaysMs.Length &&
                    (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33))
                {
                    PersistenceRetries++;
                    LastPersistenceWarning = $"{operation}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
                    Thread.Sleep(delaysMs[retry]);
                }
            }
            return true;
        }
        catch (Exception ex) { StorageError = $"{operation}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"; State.Armed = false; return false; }
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
