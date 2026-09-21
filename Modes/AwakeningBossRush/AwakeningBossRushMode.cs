using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Modes.Shared;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

/// <summary>One supervised map attempt. Only Codex's reviewed begin command crosses the next-attempt boundary.</summary>
public sealed class AwakeningBossRushMode : IBotMode, IDisposable
{
    public const string ModeName = "Awakening Boss Rush";
    public string Name => ModeName;
    public string Status { get; private set; } = "Dormant — arm explicitly to start";
    public string Decision { get; private set; } = "";
    public AwakeningSupervisor Supervisor { get; }
    private AwakeningRun Run => Supervisor.Run;
    private readonly AwakeningTelemetry _log;
    private readonly SparkProgressTracker _damage = new();
    private readonly string _directory;
    private BotContext? _ctx;
    private long _lastClock = Stopwatch.GetTimestamp();
    private DateTime _phaseAt = DateTime.UtcNow, _sampleAt, _saveAt, _quietAt, _lastProgress, _moveAt, _lastKeyAt, _errorAt;
    private Vector2? _destination;
    private Vector2 _lastPosition;
    private string _lastBossSignature = "";
    private bool _lastRunning, _externalIssued, _relocating, _indexStarted, _withdrawing, _priorForceCtrlClick;
    private int _materialIndex;
    private int _stableEmpty;
    private long _lootId;
    private string _lootPath = "", _lootName = "";
    private int _lootBefore, _lootQuantity;
    private double _lootPrice;
    private DateTime _lootStarted;
    private readonly Dictionary<long, int> _lootAttempts = new();
    private readonly HashSet<string> _mapChecks = new();
    private readonly Dictionary<long, DateTime> _unreadableLoot = new();
    private readonly Dictionary<long, DateTime> _missingVisits = new();
    private readonly HashSet<string> _lootDecisions = new();
    private readonly HashSet<long> _uniqueEvidence = new();
    private IReadOnlyList<ModRisk> _risks = [];
    private string _commandRequestId = "", _commandResult = "";
    private DateTime _observedUtc;
    private bool _inspecting;
    private DateTime _inspectionStarted;
    private string _inspectionStatus = "";
    private int _reportedPersistenceRetries;
    private bool _shutdownRequested;
    private bool _deviceStockChecked;
    private bool _defensiveClear;
    private readonly SparkProgressTracker _scoutDamage = new();
    private DateTime _scoutRepositionUntil;
    private DateTime _scoutPositionSince;
    private DateTime _bossPositionSince;
    private DateTime _stashClickAt;
    private Vector2 _stashLastPosition;
    private int _stashStationaryClicks, _stashRecoveryIndex;
    private readonly Dictionary<string, string> _deviceMaterialPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] MaterialNames = ["Sacrifice at Dawn", "Sacrifice at Noon", "Sacrifice at Dusk", "Sacrifice at Midnight", "Horned Scarab of Awakening"];
    public bool ExternalInputOwned => Run.Phase is AwakeningPhase.Return or AwakeningPhase.ExternalStash;
    public bool HasActiveAttempt => Supervisor.State.Armed && Run.AttemptNumber > 0 && Run.Outcome == AttemptOutcome.None;

    public AwakeningBossRushMode(string directory, Action<string> log)
    {
        _directory = Path.Combine(directory, "AwakeningData");
        Supervisor = new(_directory, typeof(AwakeningBossRushMode).Assembly.ManifestModule.ModuleVersionId.ToString());
        _log = new(_directory, log, Supervisor.Generation);
        RefreshAnalysis();
        _log.Event(Run, "plugin.loaded", new { Supervisor.Generation, Supervisor.Mvid, Supervisor.StorageError });
    }
    public void OnEnter(BotContext ctx)
    {
        _ctx = ctx;
        _priorForceCtrlClick = ctx.MapDevice.ForceCtrlClick;
        _inspecting = false;
        ctx.Settings.Running.Value = false;
        CancelInput(ctx);
        ModeHelpers.EnableDefaultCombat(ctx);
        Status = "Dormant — Codex arm/begin required";
    }
    public void OnExit()
    {
        _inspecting = false;
        if (_ctx != null)
        {
            if (HasActiveAttempt) Finish(_ctx, AttemptOutcome.ManualIntervention, "mode_changed");
            CancelInput(_ctx);
            _ctx.Combat.SuppressPositioning = false;
            _ctx.Combat.SuppressTargetedSkills = false;
            _ctx.MapDevice.ForceCtrlClick = _priorForceCtrlClick;
        }
        Supervisor.State.Armed = false; Supervisor.Save();
    }
    // Called on the frame thread: background HTTP serialization must never enumerate live collections.
    public JsonElement Snapshot() => JsonSerializer.SerializeToElement(new { hostProcessId = Environment.ProcessId, hostExecutable = Environment.ProcessPath,
        shutdownRequested = _shutdownRequested, generation = Supervisor.Generation, loadedMvid = Supervisor.Mvid, armed = Supervisor.State.Armed,
        phase = Run.Phase.ToString(), Status, Decision, manualContinuous = _manualContinuous, run = Run, lastCommand = Supervisor.LastCommand,
        phaseStartedUtc = _phaseAt,
        inspecting = _inspecting, inspectionStatus = _inspectionStatus,
        commandRequestId = _commandRequestId, commandResult = _commandResult, observedUtc = _observedUtc,
        checkpointError = Supervisor.StorageError, telemetryError = _log.Error, telemetryDropped = _log.Dropped,
        Supervisor.PersistenceRetries, Supervisor.LastPersistenceWarning,
        netChaosPerHour = Run.CostKnown && Run.RevenueKnown && Run.OperatingSeconds > 0 ? (double?)((Run.RevenueChaos - Run.CostChaos) * 3600 / Run.OperatingSeconds) : null,
        modRisk = _risks }, AwakeningJson.Options);
    private void RefreshAnalysis() => _risks = AwakeningModRiskAnalyzer.Analyze(Supervisor.State.History.Append(Run)).Take(30).ToArray();

    private bool _manualContinuous;

    public void ManualStart(BotContext ctx, string source = "user_insert")
    {
        ctx.Settings.Running.Value = false;
        if (!ctx.Settings.Awakening.AllowManualStart.Value)
        { Status = "Insert start disabled: enable Awakening / Allow manual Insert start"; return; }
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true || ctx.Game.IsLoading || Run.RecoveryRequired || _shutdownRequested ||
            (Run.AttemptNumber > 0 && Run.Outcome == AttemptOutcome.None))
        { Status = "Manual start requires Hideout and a finished, recovered attempt"; return; }
        string Send(string action, string review = "") => Command(ctx, action, Guid.NewGuid().ToString("N"), Supervisor.Generation, review, Supervisor.Mvid);
        if (Run.Outcome != AttemptOutcome.None && !Run.Reviewed)
        {
            var ack = Send("review", AwakeningJson.Serialize(new {
                observations = "Human pressed Insert to retry after " + Run.Outcome + ": " + Run.Reason,
                diagnosis = "Human retry acknowledgement; no Codex diagnosis or code review performed",
                changes = "No automatic code changes", validation = "Manual start from Hideout; live result pending",
                nextAction = "Human-started continuous farming; stop on failure or supplies exhausted" }));
            if (ack.StartsWith("rejected:")) { Status = ack; return; }
        }
        var armed = Send("arm");
        if (armed.StartsWith("rejected:")) { Status = armed; return; }
        var begun = Send("begin");
        if (begun.StartsWith("rejected:")) { Status = begun; return; }
        _manualContinuous = true;
        _log.Event(Run, "attempt.manual_started", new { source, oneAttempt = false });
    }
    public string Command(BotContext ctx, string action, string id, string generation, string review, string mvid)
    {
        string result;
        try { result = CommandCore(ctx, action, id, generation, review, mvid); }
        catch (Exception ex) { result = "rejected: " + ex.Message; }
        _commandRequestId = id; _commandResult = result;
        _log.Event(Run, "supervisor.command", new { action, id, result });
        return result;
    }
    private string CommandCore(BotContext ctx, string action, string id, string generation, string review, string mvid)
    {
        _ctx = ctx;
        var invalid = Supervisor.ValidateRequest(id, generation);
        if (invalid != null) return invalid;
        if (_shutdownRequested) return "rejected: host shutdown pending";
        if (action == "fresh")
        {
            if (ctx.Settings.Running.Value || !Run.Reviewed || Run.RecoveryRequired || Run.Outcome == AttemptOutcome.None ||
                ctx.Game.Area?.CurrentArea?.IsHideout != true || mvid != Supervisor.Mvid)
                return "rejected: reviewed stopped recovered Hideout attempt required";
            Supervisor.State.History.Add(JsonSerializer.Deserialize<AwakeningRun>(AwakeningJson.Serialize(Run), AwakeningJson.Options)!);
            _log.Event(Run, "run.codex_retired", new { Run.Instance, reason = "explicit_fresh_map_request" });
            Supervisor.State.Run = new() { PriorPortalIds = StrictMapRecipe.Portals(ctx.Game).Select(p => (long)p.Id).ToList() };
            return Supervisor.RecordCommand(id, "fresh_map_ready");
        }
        if (action == "continuous")
        {
            if (mvid != Supervisor.Mvid || ctx.Settings.Running.Value) return "rejected: stopped verified build required";
            ManualStart(ctx, "codex_continuous_test");
            return Supervisor.RecordCommand(id, _manualContinuous && ctx.Settings.Running.Value ? "continuous_started" : "rejected: " + Status);
        }
        if (action == "shutdown")
        {
            if (ctx.Settings.Running.Value || _inspecting || Run.RecoveryRequired ||
                (Run.AttemptNumber > 0 && (Run.Outcome == AttemptOutcome.None || !Run.Reviewed)) || mvid != Supervisor.Mvid)
                return "rejected: shutdown requires stopped, reviewed, verified build";
            CancelInput(ctx); Supervisor.State.Armed = false;
            var ack = Supervisor.RecordCommand(id, "host_shutdown_scheduled");
            if (ack.StartsWith("rejected:")) return ack;
            _shutdownRequested = true;
            _ = Task.Run(async () => { await Task.Delay(2000); _log.Dispose(); Process.GetCurrentProcess().Kill(); });
            return ack;
        }
        if ((action is "begin" or "return" or "timeout" or "inspect") && ctx.Settings.ActiveMode.Value != ModeName)
            return "rejected: arm/select Awakening first";
        if (action == "evidence") { return WriteEvidence(ctx) ? Supervisor.RecordCommand(id, "evidence_written") : "rejected: evidence write failed"; }
        if (action == "rebind")
        {
            if (ctx.Settings.Running.Value || !Run.Reviewed || !Run.ActivationConfirmed ||
                (Run.Instance == 0 && Run.Reason != "map_device:Portal disappeared") ||
                Run.RecoveryRequired || ctx.Game.Area?.CurrentArea?.IsHideout != true || mvid != Supervisor.Mvid)
                return "rejected: rebind requires reviewed stopped owned-map retry";
            var portals = StrictMapRecipe.Portals(ctx.Game);
            if (portals.Count == 0 || portals.Any(p => (p.GetComponent<Portal>()?.Area?.Name ?? p.RenderName) != "Dunes"))
                return "rejected: rebind requires only Dunes portals verified by Codex";
            var previous = Run.PortalIds.ToArray();
            Run.PortalIds = portals.Select(p => (long)p.Id).ToList();
            _log.Event(Run, "portals.codex_rebound", new { previous, current = Run.PortalIds, Run.Instance });
            return Supervisor.RecordCommand(id, "portals_rebound");
        }
        if (action == "inspect")
        {
            if (ctx.Settings.Running.Value || ctx.Game.Area?.CurrentArea?.IsHideout != true || HasActiveAttempt)
                return "rejected: inspect requires stopped hideout without active attempt";
            var ack = Supervisor.RecordCommand(id, "inspect_started");
            if (ack.StartsWith("rejected:")) return ack;
            CancelInput(ctx); _inspecting = true; _inspectionStarted = DateTime.UtcNow; _inspectionStatus = "opening_atlas"; _inspectionClickedUiIndex = null;
            ctx.Settings.Running.Value = true; _lastRunning = true; return ack;
        }
        if (action == "recover")
        {
            if (ctx.Settings.Running.Value) return "rejected: recover requires stopped current generation";
            if (Run.Outcome == AttemptOutcome.None && Run.AttemptNumber > 0) Finish(ctx, AttemptOutcome.OperationalFailure, "host_reloaded_mid_attempt");
            return Supervisor.RecordCommand(id, "recovered_for_review");
        }
        if (action == "timeout")
        {
            if (!HasActiveAttempt || !Run.EnteredUtc.HasValue || (DateTime.UtcNow - Run.EnteredUtc.Value).TotalSeconds < ctx.Settings.Awakening.TimeoutSeconds.Value)
                return "rejected: no expired active attempt";
            Finish(ctx, AttemptOutcome.Timeout, "external_300_second_deadline");
            return Supervisor.RecordCommand(id, "timeout_recorded");
        }
        if (action == "return")
        {
            if (Run.Outcome == AttemptOutcome.None || ctx.Settings.Running.Value) return "rejected: return requires stopped terminal attempt";
            CancelInput(ctx); Run.RecoveryRequired = true; SetPhase(AwakeningPhase.Return, "codex_requested_recovery");
            var ack = Supervisor.RecordCommand(id, "return_started");
            if (!ack.StartsWith("rejected:")) { ctx.Settings.Running.Value = true; _lastRunning = true; }
            return ack;
        }
        if (action == "begin" && ctx.Game.Area?.CurrentArea?.IsHideout != true) return "rejected: begin requires hideout";
        var result = Supervisor.Command(action, id, generation, review, mvid);
        if (result.EndsWith(":begun", StringComparison.Ordinal))
        {
            _inspectionStatus = "";
            _deviceStockChecked = false;
            _mapRestockAttempted = false; _mapRestockStarted = false;
            _defensiveClear = false;
            _uniqueEvidence.Clear();
            _scoutRepositionUntil = DateTime.MinValue;
            _deviceMaterialPaths.Clear();
            _materialIndex = 0; _withdrawing = false; _indexStarted = false; _lootId = 0;
            _lootAttempts.Clear(); _mapChecks.Clear(); _lootDecisions.Clear(); _unreadableLoot.Clear(); _missingVisits.Clear(); _destination = null; _relocating = false;
            _lastClock = Stopwatch.GetTimestamp(); _phaseAt = DateTime.UtcNow; _lastPosition = ctx.Game.Player.GridPosNum;
            _lastProgress = DateTime.UtcNow; _damage.Reset(DateTime.UtcNow);
            Run.BuildConfiguration = AwakeningJson.Serialize(AutoExile.WebServer.SettingsApi.SerializeFlat(ctx.Settings)
                .Where(x => !x.Value.ReadOnly && (x.Key.StartsWith("build.") || x.Key.StartsWith("awakening.") || x.Key.StartsWith("threat.")))
                .OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value.Value));
            Run.BuildFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Run.BuildConfiguration)));
            // A fresh opening must distinguish portals already present at Begin from
            // portals appearing during preparation. Preserve ownership on retries.
            if (!Run.ActivationRequested && !Run.ActivationConfirmed)
            {
                Run.PriorPortalIds = StrictMapRecipe.Portals(ctx.Game).Select(p => (long)p.Id).ToList();
                _log.Event(Run, "prepare.portal_baseline", new { Run.PriorPortalIds });
            }
            if (!Supervisor.Save()) return "rejected: persistence failed before input start";
            ctx.Settings.Running.Value = true; _lastRunning = true;
            if (!ctx.LootTracker.IsActive) ctx.LootTracker.StartSession();
        }
        if (action == "stop" && !result.StartsWith("rejected"))
        {
            _manualContinuous = false;
            _inspecting = false;
            if (Run.Outcome == AttemptOutcome.None && Run.AttemptNumber > 0) Finish(ctx, AttemptOutcome.ManualIntervention, "codex_loop_stopped");
            ctx.Settings.Running.Value = false; CancelInput(ctx);
        }
        return result;
    }

    // Runs before focus/input/Prune early returns. No navigation or keypresses except stopping owned input.
    public void Observe(BotContext ctx)
    {
        _ctx = ctx;
        _observedUtc = DateTime.UtcNow;
        if (_shutdownRequested) { ctx.Settings.Running.Value = false; return; }
        if (_inspecting && (DateTime.UtcNow - _inspectionStarted).TotalSeconds > 30)
        {
            _inspecting = false; _inspectionStatus = "failed:inspection_wall_deadline";
            ctx.Settings.Running.Value = false; CancelInput(ctx); _lastRunning = false;
            Status = "Atlas inspection exceeded 30 seconds";
            _log.Event(Run, "inspection.ended", new { _inspectionStatus, foreground = ctx.Game.IsForeGroundCache });
            WriteEvidence(ctx);
        }
        var stamp = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(0, (stamp - _lastClock) / (double)Stopwatch.Frequency); _lastClock = stamp;
        var running = ctx.Settings.Running.Value;
        if (_inspecting && !running) { _inspecting = false; _inspectionStatus = "failed:intervention"; }
        if (_lastRunning && !running && HasActiveAttempt) Finish(ctx, AttemptOutcome.ManualIntervention, "user_or_runtime_stop");
        _lastRunning = running;
        if ((!HasActiveAttempt && !Run.RecoveryRequired) || !running) return;
        try
        {
            var now = DateTime.UtcNow;
            Run.OperatingSeconds += elapsed;
            Run.PhaseSeconds[Run.Phase.ToString()] = Run.PhaseSeconds.GetValueOrDefault(Run.Phase.ToString()) + elapsed;
            if (Run.EnteredUtc.HasValue) Run.ElapsedSeconds += elapsed;
            var gc = ctx.Game;
            if (!gc.InGame || gc.IsLoading || gc.Player == null) return;
            bool inMap = gc.Area?.CurrentArea != null && !gc.Area.CurrentArea.IsTown && !gc.Area.CurrentArea.IsHideout;
            if (inMap && Run.Outcome == AttemptOutcome.None && !Run.ActivationRequested && !Run.ActivationConfirmed)
            { Finish(ctx, AttemptOutcome.OperationalFailure, "unexpected_map_before_activation"); return; }
            if (!inMap && Run.EnteredUtc.HasValue && Run.Outcome == AttemptOutcome.None && Run.Phase is AwakeningPhase.Scout or AwakeningPhase.Fight or AwakeningPhase.Loot or AwakeningPhase.MapBoss)
            { Finish(ctx, AttemptOutcome.OperationalFailure, "left_map_before_objective_complete"); return; }
            if (inMap && Run.Instance != 0 && Run.Instance != (long)gc.IngameState.Data.CurrentAreaHash && Run.Outcome == AttemptOutcome.None)
            { Finish(ctx, AttemptOutcome.OperationalFailure, "unexpected_area_change"); return; }
            if (inMap && Run.Outcome == AttemptOutcome.None && !Run.EnteredUtc.HasValue && Run.ActivationConfirmed)
            {
                var hash = (long)(gc.IngameState.Data.CurrentAreaHash);
                if (Run.Instance != 0 && Run.Instance != hash) { Finish(ctx, AttemptOutcome.OperationalFailure, "wrong_instance_on_reentry"); return; }
                Run.Instance = hash; Run.Area = gc.Area!.CurrentArea.Name; Run.EnteredUtc = now;
                var raw = gc.IngameState.Data.RawPathfindingData;
                if (raw != null) ctx.Exploration.Initialize(raw, gc.IngameState.Data.RawTerrainTargetingData, gc.Player.GridPosNum, ctx.Settings.Build.BlinkRange.Value);
                ctx.MapDevice.Cancel(gc, ctx.Navigation); _damage.Reset(now);
                SetPhase(AwakeningPhase.Scout, "entered_expected_map");
                _log.Event(Run, "attempt.entered", new { Run.AttemptNumber, Run.Instance });
            }
            if (inMap && Run.EnteredUtc.HasValue && ((now - _sampleAt).TotalMilliseconds >= 250 || !gc.Player.IsAlive))
            {
                _sampleAt = now;
                foreach (var unique in gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster &&
                    e.Rarity == MonsterRarity.Unique && e.IsHostile && !_uniqueEvidence.Contains(e.Id)))
                {
                    _uniqueEvidence.Add(unique.Id);
                    _log.Event(Run, "monster.unique_observed", new { unique.Id, unique.Path, unique.RenderName,
                        unique.IsAlive, unique.IsTargetable, hp = unique.GetComponent<Life>()?.CurHP,
                        x = unique.GridPosNum.X, y = unique.GridPosNum.Y,
                        classification = AwakeningBossTracker.Classify(unique.Path, unique.RenderName ?? "")?.Member });
                }
                var samples = AwakeningGameReader.Bosses(ctx, Run);
                AwakeningBossTracker.Observe(Run, samples, now);
                if (Run.Phase != AwakeningPhase.MapBoss) _damage.Observe(samples.Select(s => new MonsterHealthSample(s.Id, s.Health)), now);
                ObserveMapBoss(ctx);
                if (Run.BossesCompleted && !AwakeningBossTracker.Complete(Run))
                {
                    Run.BossesCompleted = false;
                    if (Run.Outcome == AttemptOutcome.None && Run.Phase is AwakeningPhase.Loot or AwakeningPhase.MapBoss)
                    { ctx.Interaction.Cancel(gc); _lootId = 0; SetPhase(AwakeningPhase.Scout, "new_living_boss_observation"); }
                }
                var signature = string.Join(";", Run.Bosses.Values.Select(x => $"{x.Id}:{x.Life}"));
                if (signature != _lastBossSignature) { _lastBossSignature = signature; _log.Event(Run, "boss.observations", Run.Bosses.Values); Supervisor.Save(); }
                var pos = gc.Player.GridPosNum;
                if (Run.Visited.Count == 0 || Vector2.Distance(pos, new(Run.Visited[^1][0], Run.Visited[^1][1])) > 30) Run.Visited.Add([pos.X, pos.Y]);
                var life = gc.Player.GetComponent<Life>();
                _log.Sample(new { utc = now, Run.Phase, pos.X, pos.Y, hp = life?.CurHP, es = life?.CurES,
                    bosses = samples, ctx.Combat.IsChanneling, ctx.Combat.ChannelInputDiagnostics, ctx.Combat.NearbyMonsterCount,
                    input = BotInput.InputDiagnostics,
                    threats = ctx.Threat.TrackedMonsters.Values.Take(32).Select(t => new { t.EntityId, t.AnimationName, t.SkillName, t.AnimationProgress,
                        t.TimeRemainingMs, destination = new[] { t.CastDestination.X, t.CastDestination.Y }, t.DodgeSignaled }).ToArray(),
                    ctx.Navigation.PathfindingStatus, ctx.Navigation.LastRecoveryAction, ctx.Interaction.Status,
                    foreground = gc.IsForeGroundCache, latency = gc.IngameState.ServerData.Latency,
                    buffs = gc.Player.GetComponent<Buffs>()?.BuffsList?.Select(b => new { b.Name, b.Charges }).ToArray() });
                if (Vector2.Distance(pos, _lastPosition) > 8) { _lastPosition = pos; _lastProgress = now; }
                else if (Run.Phase is AwakeningPhase.Scout or AwakeningPhase.MapBoss or AwakeningPhase.Loot && (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) && (now - _lastProgress).TotalSeconds > 8)
                {
                    _log.Event(Run, "navigation.stalled", new { pos.X, pos.Y, destination = _destination, ctx.Navigation.LastRecoveryAction });
                    Run.InputFault = true;
                    ctx.Navigation.Stop(gc);
                    if (_destination.HasValue) ctx.Exploration.MarkRegionFailed(_destination.Value);
                    _lastProgress = now; _destination = null;
                }
            }
            var outcome = AwakeningSupervisor.Watchdog(Run, gc.Player.IsAlive, inMap, ctx.Settings.Awakening.TimeoutSeconds.Value);
            if (outcome != AttemptOutcome.None)
            {
                if (outcome == AttemptOutcome.Death) Run.Deaths++;
                Finish(ctx, outcome, outcome == AttemptOutcome.Death ? "player_dead" : "300_second_deadline"); return;
            }
            if ((now - _saveAt).TotalSeconds >= 1)
            {
                _saveAt = now; Supervisor.Save();
                _log.Event(Run, "attempt.pulse", new { Status, Decision, bossCount = Run.Bosses.Count, ctx.Combat.IsChanneling,
                    ctx.Navigation.IsNavigating, ctx.Navigation.IsPathfinding, ctx.Combat.ChannelInputDiagnostics,
                    navigation = new { ctx.Navigation.IsPaused, ctx.Navigation.MoveKey, ctx.Navigation.CurrentWaypointIndex,
                        ctx.Navigation.LastRecoveryAction, ctx.Navigation.StuckRecoveries,
                        waypoint = ctx.Navigation.CurrentNavPath.ElementAtOrDefault(ctx.Navigation.CurrentWaypointIndex) },
                    interaction = ctx.Interaction.Status, input = BotInput.InputDiagnostics,
                    position = new[] { gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y }, hp = gc.Player.GetComponent<Life>()?.CurHP,
                    es = gc.Player.GetComponent<Life>()?.CurES, bosses = Run.Bosses.Values.ToArray(), Run.RevenueChaos,
                    Run.ExarchCounter, Run.Invitation, groundItems = ctx.Entities.WorldItems.Count, stashieBusy = ExternalInputOwned ? AwakeningGameReader.StashieBusy() : null });
                if (Supervisor.PersistenceRetries != _reportedPersistenceRetries)
                {
                    _reportedPersistenceRetries = Supervisor.PersistenceRetries;
                    _log.Event(Run, "checkpoint.retry_status", new { Supervisor.PersistenceRetries, Supervisor.LastPersistenceWarning });
                }
            }
            if (Supervisor.StorageError.Length > 0) { ctx.Settings.Running.Value = false; CancelInput(ctx); Status = Supervisor.StorageError; }
        }
        catch (Exception ex)
        {
            Status = "Observation unavailable: " + ex.Message;
            if ((DateTime.UtcNow - _errorAt).TotalSeconds >= 2) { _errorAt = DateTime.UtcNow; _log.Event(Run, "observation.error", new { ex.Message }); }
        }
    }

    public void Tick(BotContext ctx)
    {
        if (_inspecting) { InspectAtlas(ctx); return; }
        if (!HasActiveAttempt && !Run.RecoveryRequired) { ctx.Settings.Running.Value = false; return; }
        try
        {
            if ((DateTime.UtcNow - _phaseAt).TotalSeconds > (Run.Phase == AwakeningPhase.IndexStash ? 150 : 60)
                && Run.Phase is AwakeningPhase.Prepare or AwakeningPhase.Withdraw or AwakeningPhase.OpenMap or AwakeningPhase.EnterPortal or AwakeningPhase.OpenStash or AwakeningPhase.ExternalStash or AwakeningPhase.Return or AwakeningPhase.IndexStash)
            {
                if (Run.RecoveryRequired)
                { Run.RecoveryRequired = false; ctx.Settings.Running.Value = false; CancelInput(ctx); SetPhase(AwakeningPhase.AwaitingReview, "return_failed_after_terminal_attempt"); }
                else Finish(ctx, AttemptOutcome.OperationalFailure, "phase_timeout:" + Run.Phase + ":" + Status);
                return;
            }
            switch (Run.Phase)
            {
                case AwakeningPhase.Prepare: Prepare(ctx); break;
                case AwakeningPhase.IndexStash: Index(ctx); break;
                case AwakeningPhase.Withdraw: Withdraw(ctx); break;
                case AwakeningPhase.OpenMap: OpenMap(ctx); break;
                case AwakeningPhase.RestockMap: RestockMap(ctx); break;
                case AwakeningPhase.EnterPortal: EnterPortal(ctx); break;
                case AwakeningPhase.Scout: Scout(ctx); break;
                case AwakeningPhase.Fight: Fight(ctx, false); break;
                case AwakeningPhase.Loot: Loot(ctx); break;
                case AwakeningPhase.MapBoss: Fight(ctx, true); break;
                case AwakeningPhase.Return: Return(ctx); break;
                case AwakeningPhase.OpenStash: OpenStash(ctx); break;
                case AwakeningPhase.ExternalStash: ExternalStash(ctx); break;
            }
        }
        catch (Exception ex)
        {
            if (Run.RecoveryRequired)
            { Run.RecoveryRequired = false; ctx.Settings.Running.Value = false; CancelInput(ctx); SetPhase(AwakeningPhase.AwaitingReview, "recovery_exception:" + ex.Message); }
            else Finish(ctx, AttemptOutcome.OperationalFailure, "exception:" + ex);
        }
    }
    private DateTime _inspectionNodeClick;
    private int? _inspectionClickedUiIndex;
    private void InspectAtlas(BotContext ctx)
    {
        try
        {
            if (ctx.Game.IngameState.IngameUi.Atlas?.IsVisible == true)
            {
                var atlas = ctx.Game.IngameState.IngameUi.Atlas;
                var nodes = ctx.Game.Files.AtlasNodes.EntriesList;
                var index = nodes.FindIndex(n => n.Area != null && Regex.IsMatch(n.Area.Id + " " + n.Area.RawName, @"\bDunes?\b|MapWorldsDunes", RegexOptions.IgnoreCase));
                var selected = atlas.GetChildAtIndex(7)?.IsVisible == true ? atlas.GetChildFromIndices(7, 0, 1, 0, 0)?.Text : null;
                var expected = index >= 0 ? nodes[index].Area.Name : null;
                if (selected != expected || selected == null)
                {
                    if ((DateTime.UtcNow - _inspectionStarted).TotalSeconds > 30)
                        throw new InvalidOperationException("Dunes device panel unavailable during inspection");
                    if ((DateTime.UtcNow - _inspectionNodeClick).TotalSeconds >= 1 && BotInput.CanAct)
                    {
                        if (_inspectionClickedUiIndex.HasValue && selected != null)
                        {
                            var actualIndex = nodes.FindIndex(n => n.Area?.Name == selected);
                            if (actualIndex >= 0)
                            {
                                var offset = _inspectionClickedUiIndex.Value - actualIndex;
                                if (Math.Abs(offset) > 10) throw new InvalidOperationException("Atlas node calibration mismatch");
                                ctx.Settings.Awakening.AtlasNodeOffset.Value = offset;
                                _log.Event(Run, "inspection.node_offset", new { expected, selected, index, actualIndex, offset });
                            }
                        }
                        var uiIndex = index + ctx.Settings.Awakening.AtlasNodeOffset.Value;
                        var element = index >= 0 && uiIndex >= 0 ? atlas.GetChildAtIndex(0)?.GetChildAtIndex(uiIndex) : null;
                        if (element?.IsVisible != true) throw new InvalidOperationException("Dunes atlas node unavailable");
                        var rect = element.GetClientRect();
                        var window = ctx.Game.Window.GetWindowRectangle();
                        if (rect.Center.X < 0 || rect.Center.Y < 0 || rect.Center.X > window.Width || rect.Center.Y > window.Height)
                            throw new InvalidOperationException("Dunes node is off screen; center Atlas before inspection");
                        if (BotInput.Click(new(window.X + rect.Center.X, window.Y + rect.Center.Y)))
                        {
                            _inspectionNodeClick = DateTime.UtcNow; _inspectionClickedUiIndex = uiIndex;
                            _log.Event(Run, "inspection.node_click", new { index, uiIndex, expected, selected, x = rect.Center.X, y = rect.Center.Y, rect.Width, rect.Height });
                        }
                    }
                    Status = "Inspecting Dunes device panel; no insertion or activation";
                    return;
                }
                _inspectionStatus = WriteEvidence(ctx) ? "complete" : "failed:evidence";
            }
            else if ((DateTime.UtcNow - _inspectionStarted).TotalSeconds > 30 || ctx.Game.Area?.CurrentArea?.IsHideout != true)
                _inspectionStatus = "failed:atlas_unavailable";
            else
            {
                OpenAtlasFromDevice(ctx);
                Status = "Inspecting Atlas and map mods; no activation";
                return;
            }
        }
        catch (Exception ex) { _inspectionStatus = "failed:" + ex.Message; }
        _inspecting = false; ctx.Settings.Running.Value = false; _lastRunning = false; CancelInput(ctx);
        Status = "Atlas inspection: " + _inspectionStatus;
        _log.Event(Run, "inspection.ended", new { _inspectionStatus });
    }
    private void SetPhase(AwakeningPhase phase, string reason)
    {
        var previous = Run.Phase; Run.Phase = phase; _phaseAt = DateTime.UtcNow;
        _quietAt = DateTime.MinValue; _externalIssued = false; _stableEmpty = 0;
        Status = reason; Decision = phase.ToString();
        _log.Event(Run, "phase.changed", new { previous, phase, reason }); Supervisor.Save();
        if (Supervisor.StorageError.Length > 0 && _ctx != null) { _ctx.Settings.Running.Value = false; BotInput.Cancel(); }
    }
    private static void CancelInput(BotContext ctx)
    {
        ctx.Combat.Suspend(); ModeHelpers.CancelAllSystems(ctx); BotInput.Cancel();
        ctx.Combat.SuppressPositioning = false; ctx.Combat.SuppressTargetedSkills = false;
    }
    private void Finish(BotContext ctx, AttemptOutcome outcome, string reason)
    {
        if (Run.Outcome != AttemptOutcome.None) return;
        _log.FlushRing(Run, reason); Supervisor.Finish(outcome, reason, DateTime.UtcNow);
        RefreshAnalysis();
        CancelInput(ctx); ctx.Settings.Running.Value = false; _lastRunning = false;
        if (_risks.Count > 0) _log.Event(Run, "modRisk.analysis", _risks);
        Status = outcome + " — awaiting Codex review: " + reason;
        _log.Event(Run, "attempt.ended", new { outcome, reason, Run.ReturnedToHideout, Run.StashCompleted, Run.BossesCompleted,
            Run.RevenueChaos, Run.RevenueKnown, Run.CostChaos, Run.CostKnown, Run.OperatingSeconds, Run.PhaseSeconds, Run.Deaths, Run.UnresolvedLoot });
        WriteEvidence(ctx);
        if (_manualContinuous && outcome == AttemptOutcome.Success && Run.ReturnedToHideout && Run.StashCompleted &&
            Supervisor.StorageError.Length == 0 && ctx.Game.Area?.CurrentArea?.IsHideout == true)
        {
            var ack = Command(ctx, "review", Guid.NewGuid().ToString("N"), Supervisor.Generation,
                AwakeningJson.Serialize(new {
                    observations = "Completed boss loot, hideout return and stash verified by state machine",
                    diagnosis = "User-authorized continuous farming; no Codex code review performed",
                    changes = "None", validation = "Success terminal with return and stash confirmed",
                    nextAction = "Next map until supplies exhausted, failure, or Insert stop" }), Supervisor.Mvid);
            if (!ack.StartsWith("rejected:"))
                ack = Command(ctx, "begin", Guid.NewGuid().ToString("N"), Supervisor.Generation, "", Supervisor.Mvid);
            if (!ack.StartsWith("rejected:"))
            {
                _log.Event(Run, "manual_loop.continued", new { reason = "previous_map_success", Run.AttemptId });
                return;
            }
            Status = ack;
        }
        if (_manualContinuous) _log.Event(Run, "manual_loop.stopped", new { outcome, reason, Status });
        _manualContinuous = false;
        if (Supervisor.StorageError.Length == 0 && ((outcome is AttemptOutcome.Death or AttemptOutcome.Timeout) || reason is "unexpected_map_before_activation" or "wrong_instance_on_reentry") && ctx.Game.Area?.CurrentArea?.IsHideout != true)
        {
            Run.RecoveryRequired = true; SetPhase(AwakeningPhase.Return, "recover_after_" + outcome);
            ctx.Settings.Running.Value = true; _lastRunning = true;
        }
    }
    private void Prepare(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true) { Status = "Waiting for hideout"; return; }
        var moveConflict = ctx.Combat.MovementBindingConflict(ctx.Game, ctx.Navigation.MoveKey);
        if (moveConflict != null)
        {
            Finish(ctx, AttemptOutcome.OperationalFailure, $"preflight:movement_key_{ctx.Navigation.MoveKey}_bound_to_{moveConflict}");
            return;
        }
        var inv = AwakeningGameReader.Inventory(ctx.Game);
        if (!inv.Valid) { Status = "Waiting for inventory"; return; }
        if (inv.OutsideReservedColumn > 0 && (Run.ActivationConfirmed || Run.Recipe.Count == 0))
        { SetPhase(AwakeningPhase.OpenStash, "stash_before_attempt"); return; }
        if (Run.ActivationConfirmed)
        {
            var portals = StrictMapRecipe.Portals(ctx.Game).Where(x => Run.PortalIds.Contains(x.Id)).ToList();
            if (portals.Count > 0) { SetPhase(AwakeningPhase.EnterPortal, "retry_existing_map"); return; }
            if ((DateTime.UtcNow - _phaseAt).TotalSeconds < 5) { Status = "Confirming exhausted portals"; return; }
            // Unrelated existing portals are never overwritten or entered.
            if (StrictMapRecipe.Portals(ctx.Game).Count > 0) { Finish(ctx, AttemptOutcome.OperationalFailure, "unrecognized_portal_set"); return; }
            _log.Event(Run, "run.portals_exhausted");
            var attempt = Run.AttemptId;
            Supervisor.State.Run = new() { AttemptId = attempt, AttemptNumber = 1, Phase = AwakeningPhase.Prepare,
                BuildMvid = Supervisor.Mvid, BuildFingerprint = Run.BuildFingerprint, BuildConfiguration = Run.BuildConfiguration };
            Supervisor.Save();
        }
        if (Run.ActivationRequested) { Finish(ctx, AttemptOutcome.OperationalFailure, "activation_indeterminate_do_not_reactivate"); return; }
        if (StrictMapRecipe.Portals(ctx.Game).Any(p => !Run.PriorPortalIds.Contains(p.Id)))
        { Finish(ctx, AttemptOutcome.OperationalFailure, "unrecognized_portal_set_before_activation"); return; }
        if (!ctx.Settings.Awakening.ModCatalogValidated.Value) { WriteEvidence(ctx); Finish(ctx, AttemptOutcome.OperationalFailure, "preflight: validate 17 NG rules using map evidence"); return; }
        if (ctx.Settings.Awakening.ExarchReadyCounter.Value < 0) { WriteEvidence(ctx); Finish(ctx, AttemptOutcome.OperationalFailure, "preflight: calibrate Exarch ready counter"); return; }
        if (!ctx.NinjaPrice.IsLoaded || (DateTime.Now - ctx.NinjaPrice.LastRefreshTime).TotalMinutes > ctx.Settings.Awakening.MaxPriceAgeMinutes.Value)
        { Status = "Waiting for fresh Allflame prices"; return; }
        if (ctx.Game.IngameState.ServerData.League != "Allflame") { Finish(ctx, AttemptOutcome.OperationalFailure, "expected_Allflame_price_league"); return; }
        if (!_deviceStockChecked)
        {
            var atlas = ctx.Game.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible != true)
            {
                if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible == true || ctx.Game.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
                { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
                OpenAtlasFromDevice(ctx);
                Status = "Opening Atlas to check device materials"; return;
            }
            if (atlas.GetChildAtIndex(7)?.IsVisible != true)
            {
                var nodes = ctx.Game.Files.AtlasNodes.EntriesList;
                var index = nodes.FindIndex(n => n.Area?.Id == "MapWorldsDunes");
                var node = index >= 0 ? atlas.GetChildAtIndex(0)?.GetChildAtIndex(index + ctx.Settings.Awakening.AtlasNodeOffset.Value) : null;
                if (node != null && BotInput.CanAct) BotInput.ClickLabel(ctx.Game, node.GetClientRect());
                Status = "Opening Dunes material panel"; return;
            }
            var slots = StrictMapRecipe.ReadSlots(ctx.Game);
            if (slots == null) { Status = "Waiting for device stock"; return; }
            _deviceStockChecked = true;
            var materials = MaterialNames.Select(name => slots.FirstOrDefault(item => AwakeningGameReader.Name(ctx.Game, item) == name)).ToArray();
            for (var i = 0; i < materials.Length; i++)
                if (materials[i] != null) _deviceMaterialPaths[MaterialNames[i]] = materials[i]!.Path;
            _log.Event(Run, "recipe.device_stock", new { found = _deviceMaterialPaths, missing = MaterialNames.Where(n => !_deviceMaterialPaths.ContainsKey(n)).ToArray() });
            if (materials.All(item => item != null))
            {
                Run.Recipe = materials.Select((item, i) => new RecipeItem(MaterialNames[i], item!.Path)).ToList();
                CancelInput(ctx); _log.Event(Run, "recipe.reuse_device_stock", Run.Recipe);
                SetPhase(AwakeningPhase.OpenMap, "verify_preloaded_recipe_before_activation"); return;
            }
        }
        if (ctx.Game.IngameState.IngameUi.Atlas?.IsVisible == true)
        { Status = "Closing Atlas before stash access"; if (BotInput.CanAct) { CancelInput(ctx); BotInput.PressKey(Keys.Escape); } return; }
        if (!ctx.NinjaPrice.IsLoaded || (DateTime.Now - ctx.NinjaPrice.LastRefreshTime).TotalMinutes > ctx.Settings.Awakening.MaxPriceAgeMinutes.Value)
        { Status = "Waiting for fresh Allflame prices"; return; }
        if (ctx.Game.IngameState.ServerData.League != "Allflame") { Finish(ctx, AttemptOutcome.OperationalFailure, "expected_Allflame_price_league"); return; }
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true)
        {
            if (TryClickStashLabel(ctx)) return;
            ctx.Interaction.Tick(ctx.Game);
            if (!ctx.Interaction.IsBusy)
            {
                var stash = ctx.Game.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Type == EntityType.Stash && e.IsTargetable);
                if (stash != null) ctx.Interaction.InteractWithEntity(stash, ctx.Navigation, false, requireVerified: true);
            }
            Status = "Opening supply stash: " + ctx.Interaction.Status;
            return;
        }
        CancelInput(ctx);
        SetPhase(AwakeningPhase.IndexStash, "discover_material_tabs");
    }
    private void OpenAtlasFromDevice(BotContext ctx)
    {
        ctx.Interaction.Tick(ctx.Game);
        if (ctx.Interaction.IsBusy) return;
        var device = ctx.Game.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.IsTargetable &&
            e.Path.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase));
        if (device == null) return;
        ctx.Interaction.InteractWithEntity(device, ctx.Navigation, false, requireVerified: true);
        var render = device.GetComponent<Render>();
        var point = ctx.Game.IngameState.Camera.WorldToScreen(render?.InteractCenterNum ?? device.BoundsCenterPosNum);
        _log.Event(Run, "prepare.device_interaction", new { device.Id, device.Path, point.X, point.Y, anchor = "interaction" });
    }
    private void Index(BotContext ctx)
    {
        if (!_indexStarted) { ctx.StashIndex.Start(ctx.Settings.Awakening.SupplyTab.Value, includeFragmentSections: true); _indexStarted = true; }
        ctx.StashIndex.Tick(ctx.Game); Status = ctx.StashIndex.Status;
        if (!ctx.StashIndex.IsComplete)
        { if (!ctx.StashIndex.IsRunning) Finish(ctx, AttemptOutcome.OperationalFailure, "stash_index_failed:" + Status); return; }
        var entries = ctx.StashIndex.Tabs.SelectMany(t => t.Items).ToList();
        var wanted = MaterialNames;
        var materials = new List<RecipeItem>();
        foreach (var name in wanted)
        {
            if (_deviceMaterialPaths.TryGetValue(name, out var loadedPath))
            { materials.Add(new(name, loadedPath)); continue; }
            var matches = entries.Where(e => e.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (ctx.Game.Files.BaseItemTypes.Translate(e.ItemPath)?.BaseName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)).ToList();
            if (matches.Count == 0) { _log.Event(Run, "materials.unresolved", entries.Select(x => new { x.ItemPath, x.BaseName, x.TabName })); Finish(ctx, AttemptOutcome.OperationalFailure, "material_not_found:" + name); return; }
            materials.Add(new(name, matches[0].ItemPath));
        }
        Run.Recipe = materials; _materialIndex = 0;
        _log.Event(Run, "recipe.resolved", materials); SetPhase(AwakeningPhase.Withdraw, "withdraw_exact_recipe");
    }
    private void Withdraw(BotContext ctx)
    {
        ctx.Stash.ApplyIncubators = false;
        var inv = AwakeningGameReader.Inventory(ctx.Game);
        if (!inv.Valid) return;
        if (_withdrawing)
        {
            var result = ctx.Stash.Tick(ctx.Game, ctx.Navigation); Status = ctx.Stash.Status;
            if (result == StashResult.Failed) { Finish(ctx, AttemptOutcome.OperationalFailure, "withdraw_failed:" + Status); return; }
            if (result != StashResult.Succeeded) return;
            ctx.Stash.Cancel(ctx.Game, ctx.Navigation); _withdrawing = false;
            inv = AwakeningGameReader.Inventory(ctx.Game);
            if (!inv.Valid || inv.Counts.GetValueOrDefault(Run.Recipe[_materialIndex].Path) < 1)
            { Finish(ctx, AttemptOutcome.OperationalFailure, "withdraw_missing_material"); return; }
        }
        while (_materialIndex < Run.Recipe.Count && (inv.Counts.GetValueOrDefault(Run.Recipe[_materialIndex].Path) >= 1
            || _deviceMaterialPaths.ContainsKey(Run.Recipe[_materialIndex].Name))) _materialIndex++;
        if (_materialIndex == Run.Recipe.Count) { SetPhase(AwakeningPhase.OpenMap, "materials_ready"); return; }
        var material = Run.Recipe[_materialIndex];
        var tab = ctx.Settings.Awakening.SupplyTab.Value;
        if (string.IsNullOrWhiteSpace(tab)) tab = ctx.StashIndex.BestTabForPath(material.Path)?.Name;
        if (string.IsNullOrWhiteSpace(tab)) { Finish(ctx, AttemptOutcome.OperationalFailure, "missing_material_tab"); return; }
        _withdrawing = ctx.Stash.Start(withdrawTabName: tab, withdrawFragmentPath: material.Path, withdrawCount: 1, itemFilter: _ => false);
    }
    private void OpenMap(BotContext ctx)
    {
        if (!ctx.MapDevice.IsBusy)
        {
            if (Run.ActivationConfirmed) { SetPhase(AwakeningPhase.EnterPortal, "enter_activated_map"); return; }
            if (Run.ActivationRequested) { Finish(ctx, AttemptOutcome.OperationalFailure, "activation_indeterminate_do_not_reactivate"); return; }
            var node = ctx.Game.Files.AtlasNodes.EntriesList.FirstOrDefault(n => n.Area != null && Regex.IsMatch(n.Area.Id + " " + n.Area.RawName, @"\bDunes?\b|MapWorldsDunes", RegexOptions.IgnoreCase));
            if (node?.Area == null) { Finish(ctx, AttemptOutcome.OperationalFailure, "Dunes_atlas_node_not_found"); return; }
            bool Allowed(Entity entity)
            {
                var map = AwakeningGameReader.ReadMap(ctx.Game, entity, "Dunes");
                var reasons = AwakeningMapPolicy.Rejections(map);
                if (_mapChecks.Add(entity.Id + ":" + string.Join(',', reasons))) _log.Event(Run, reasons.Count == 0 ? "map.accepted" : "map.rejected", new { map, reasons });
                if (reasons.Count == 0) Run.Map = map;
                return reasons.Count == 0;
            }
            var strict = new StrictMapRecipe
            {
                Materials = Run.Recipe, MapAllowed = Allowed,
                PrepareActivation = _ =>
                {
                    var actual = ctx.Game.IngameState.IngameUi.Atlas.GetChildFromIndices(7, 0, 1, 0, 0)?.Text;
                    if (actual != node.Area.Name) return (false, "Dunes_node_changed");
                    var ready = AwakeningGameReader.EnsureExarch(ctx);
                    if (ready.Ready) UpdateInvitation(ctx, true);
                    return ready;
                },
                RecordActivationRequest = () =>
                {
                    if (Run.ActivationRequested || Run.Map == null) return false;
                    Run.PriorPortalIds = StrictMapRecipe.Portals(ctx.Game).Select(x => (long)x.Id).ToList();
                    Run.ActivationRequested = true; Run.CostKnown = ctx.Settings.Awakening.MapCostChaos.Value > 0;
                    Run.CostChaos = ctx.Settings.Awakening.MapCostChaos.Value;
                    foreach (var material in Run.Recipe)
                    {
                        var category = material.Name.StartsWith("Horned") ? NinjaPriceCategory.Scarab : NinjaPriceCategory.Fragment;
                        var price = ctx.NinjaPrice.GetPrice(material.Name, category).MinChaosValue;
                        if (price <= 0) Run.CostKnown = false;
                        Run.CostChaos += price;
                    }
                    _log.Event(Run, "activation.requested", new { Run.Map, Run.Recipe, Run.CostChaos, Run.CostKnown, device = AwakeningGameReader.DeviceEvidence(ctx.Game) });
                    return Supervisor.Save();
                },
                ConfirmActivation = portals => { Run.ActivationConfirmed = true; Run.PortalIds = portals.ToList(); Supervisor.Save(); _log.Event(Run, "activation.confirmed", portals); }
            };
            ctx.MapDevice.TargetMapName = node.Area.Name; ctx.MapDevice.MinMapTier = 16; ctx.MapDevice.ForceCtrlClick = true;
            ctx.MapDevice.StrictAtlasNodeOffset = ctx.Settings.Awakening.AtlasNodeOffset.Value;
            ctx.MapDevice.Start(e => e.Entity != null && Allowed(e.Entity), inventoryFragmentPath: "Metadata/Items/Maps/MapKeyTier16", scarabPaths: Run.Recipe.Select(x => x.Path).ToList(), strictRecipe: strict);
        }
        ctx.Interaction.Tick(ctx.Game);
        var result = ctx.MapDevice.Tick(ctx.Game, ctx.Navigation); Status = ctx.MapDevice.Status;
        if (Run.ActivationConfirmed)
        {
            // Use the same verified portal entry for new maps and retries. The generic
            // device path can see portals disappear a frame before the loading flag.
            ctx.MapDevice.Cancel(ctx.Game, ctx.Navigation);
            SetPhase(AwakeningPhase.EnterPortal, "activation_confirmed_enter_recorded_portal");
            return;
        }
        if (result == MapDeviceResult.Failed && !Run.ActivationRequested && !_mapRestockAttempted &&
            (Status.Contains("No matching maps") || Status.Contains("No 'Metadata/Items/Maps/MapKeyTier16'")))
        {
            _mapRestockAttempted = true; ctx.MapDevice.Cancel(ctx.Game, ctx.Navigation);
            SetPhase(AwakeningPhase.RestockMap, "atlas_maps_empty_restock_from_Tmp"); return;
        }
        if (result == MapDeviceResult.Failed) Finish(ctx, AttemptOutcome.OperationalFailure, "map_device:" + Status);
    }
    private bool _mapRestockAttempted, _mapRestockStarted;
    private void RestockMap(BotContext ctx)
    {
        if ((DateTime.UtcNow - _phaseAt).TotalSeconds > 60)
        { Finish(ctx, AttemptOutcome.OperationalFailure, "Tmp_map_restock_timeout"); return; }
        if (ctx.Game.IngameState.IngameUi.Atlas?.IsVisible == true)
        { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
        if (!_mapRestockStarted)
        {
            if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true)
            { TryClickStashLabel(ctx); return; }
            ctx.Stash.ApplyIncubators = false;
            _mapRestockStarted = ctx.Stash.Start(withdrawTabName: "Tmp", withdrawFragmentPath: "Metadata/Items/Maps/MapKeyTier16",
                withdrawCount: 1, itemFilter: _ => false,
                withdrawItemFilter: e => AwakeningMapPolicy.Rejections(AwakeningGameReader.ReadMap(ctx.Game, e, "Dunes")).Count == 0);
            return;
        }
        var result = ctx.Stash.Tick(ctx.Game, ctx.Navigation); Status = "Tmp: " + ctx.Stash.Status;
        if (result == StashResult.Failed) { Finish(ctx, AttemptOutcome.OperationalFailure, "Tmp_no_usable_map:" + Status); return; }
        if (result != StashResult.Succeeded) return;
        ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
        _log.Event(Run, "map.restocked", new { tab = "Tmp", count = 1 });
        SetPhase(AwakeningPhase.OpenMap, "Tmp_map_ready");
    }
    private void EnterPortal(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true) return;
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible == true || ctx.Game.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
        { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
        ctx.Interaction.Tick(ctx.Game);
        if (ctx.Interaction.IsBusy) return;
        var portal = StrictMapRecipe.Portals(ctx.Game).FirstOrDefault(x => Run.PortalIds.Contains(x.Id));
        if (portal == null) { Status = "Waiting for recorded portal"; return; }
        ctx.Interaction.InteractWithEntity(portal, ctx.Navigation, requireProximity: false, requireVerified: true);
    }
    private void UpdateInvitation(BotContext ctx, bool selected)
    {
        try
        {
            Run.ExarchCounter = Convert.ToInt32(ctx.Game.IngameState.ServerData.SearingExarchCounter);
            if (Run.Invitation != InvitationDecision.Yes) Run.Invitation = EldritchInvitationPolicy.Decide(Run.ExarchCounter, ctx.Settings.Awakening.ExarchReadyCounter.Value, selected);
        }
        catch { if (Run.Invitation != InvitationDecision.Yes) Run.Invitation = InvitationDecision.Unknown; }
    }
    private void Scout(BotContext ctx, bool lootDefense = false)
    {
        UpdateInvitation(ctx, Run.ActivationConfirmed);
        var now = DateTime.UtcNow;
        if (!lootDefense && AwakeningBossTracker.Complete(Run) && Vector2.Distance(ctx.Game.Player.GridPosNum, EncounterCenter()) <= 80)
        { CompleteBosses(ctx); return; }
        if (Run.Bosses.Values.Any(b => b.Life == BossLife.Alive && Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y)) < ctx.Settings.Build.CombatRange.Value))
        {
            ctx.Navigation.Stop(ctx.Game); _damage.Reset(now); _bossPositionSince = now;
            SetPhase(AwakeningPhase.Fight, "boss_priority_over_defensive_clear"); return;
        }
        if (now < _scoutRepositionUntil && (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding)) { TravelSustain(ctx); Status = "Scout repositioning toward stalled pack"; return; }
        var enemies = ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster &&
            e.IsAlive && e.IsHostile && e.IsTargetable && Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum) < 90).ToList();
        var nearby = enemies.Count;
        if (nearby > 0)
        {
            if (!_defensiveClear) { _scoutDamage.Reset(now); _scoutPositionSince = now; _log.Event(Run, "scout.defensive_clear", new { nearby }); }
            _defensiveClear = true;
            _scoutDamage.Observe(enemies.Select(e => new MonsterHealthSample(e.Id,
                (double)(e.GetComponent<Life>()?.CurHP ?? 0) + (e.GetComponent<Life>()?.CurES ?? 0))), now);
            if (_scoutDamage.NoProgressSeconds(now) >= ctx.Settings.Awakening.NoDamageSeconds.Value || (now - _scoutPositionSince).TotalSeconds >= 12)
            {
                var closest = enemies.OrderBy(e => Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum)).First();
                var delta = ctx.Game.Player.GridPosNum - closest.GridPosNum;
                var destination = closest.GridPosNum + (delta.Length() > 1 ? Vector2.Normalize(delta) * 12 : new Vector2(12, 0));
                if (ctx.Combat.TryGetOptimalRangedPosition(ctx, out var optimal) && Vector2.Distance(optimal, ctx.Game.Player.GridPosNum) > 8)
                    destination = optimal;
                ctx.Combat.Suspend(); ctx.Navigation.Stop(ctx.Game); _destination = null;
                _scoutRepositionUntil = now.AddSeconds(4); _scoutDamage.Reset(_scoutRepositionUntil); _scoutPositionSince = _scoutRepositionUntil;
                Navigate(ctx, destination);
                _log.Event(Run, "scout.no_damage_reposition", new { closest.Id, closest.Path, closest.RenderName,
                    hp = closest.GetComponent<Life>()?.CurHP, x = destination.X, y = destination.Y });
                return;
            }
            ctx.Navigation.Stop(ctx.Game);
            ctx.Combat.Profile.Enabled = true;
            ctx.Combat.Profile.SustainEnemyChannelWithoutTarget = ctx.Combat.HasEnemyChannel;
            ctx.Combat.SuppressPositioning = true; ctx.Combat.SuppressTargetedSkills = false;
            ctx.Combat.Tick(ctx); Status = "Scout Spark: clearing nearby enemies (" + nearby + ")";
            return;
        }
        if (_defensiveClear) { _defensiveClear = false; ctx.Combat.Suspend(); _log.Event(Run, "scout.clear_complete"); }
        foreach (var missing in Run.Bosses.Values.Where(b => b.Life == BossLife.Missing && Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y)) < 25))
            _missingVisits[missing.Id] = DateTime.UtcNow;
        var target = Run.Bosses.Values.Where(b => b.Life != BossLife.DeadConfirmed &&
            (b.Life != BossLife.Missing || !_missingVisits.TryGetValue(b.Id, out var visited) || (DateTime.UtcNow - visited).TotalSeconds > 30))
            .OrderBy(b => Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y))).FirstOrDefault();
        if (target != null)
        {
            if (target.Life == BossLife.Alive && Vector2.Distance(ctx.Game.Player.GridPosNum, new(target.X, target.Y)) < ctx.Settings.Build.CombatRange.Value)
            { ctx.Navigation.Stop(ctx.Game); _damage.Reset(DateTime.UtcNow); SetPhase(AwakeningPhase.Fight, "boss_in_range"); return; }
            Navigate(ctx, new(target.X, target.Y)); return;
        }
        if (AwakeningBossTracker.Complete(Run)) { CompleteBosses(ctx); return; }
        // Tile entities may expose a boss/icon beyond the regular network bubble.
        var hint = ctx.Game.IngameState.Data.TileEntities?.FirstOrDefault(e => e?.Path != null && AwakeningBossTracker.Classify(e.Path, e.RenderName ?? "").HasValue
            && !Run.Visited.Any(p => Vector2.Distance(new(p[0], p[1]), e.GridPosNum) < 25));
        if (hint != null) { Navigate(ctx, hint.GridPosNum); return; }
        Explore(ctx);
    }
    private void Explore(BotContext ctx)
    {
        TravelSustain(ctx); ctx.Exploration.Update(ctx.Game.Player.GridPosNum);
        if (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) return;
        var target = ctx.Exploration.GetNextExplorationTarget(ctx.Game.Player.GridPosNum);
        if (target.HasValue) Navigate(ctx, target.Value);
        else { Status = "Exploration exhausted; no verified complete encounter"; }
    }
    private void Navigate(BotContext ctx, Vector2 target)
    {
        TravelSustain(ctx);
        if (ctx.Navigation.IsPathfinding) return;
        if (_destination.HasValue && Vector2.Distance(target, _destination.Value) < 12 && ctx.Navigation.IsNavigating) return;
        _destination = target;
        _lastProgress = DateTime.UtcNow; _lastPosition = ctx.Game.Player.GridPosNum;
        if (!ctx.Navigation.NavigateTo(ctx.Game, target)) ctx.Exploration.MarkRegionFailed(target);
        _log.Event(Run, "navigation.requested", new { target.X, target.Y, ctx.Navigation.PathfindingStatus });
    }
    private static void TravelSustain(BotContext ctx)
    {
        ctx.Combat.Suspend();
        ctx.Combat.Profile.Enabled = true;
        ctx.Combat.SuppressPositioning = true; ctx.Combat.SuppressTargetedSkills = true;
        ctx.Combat.Tick(ctx); // Preserve flask, guard and self-buff maintenance while navigating.
    }
    private Vector2 EncounterCenter() => Run.Bosses.Count == 0 ? Vector2.Zero : new(
        Run.Bosses.Values.Average(b => b.X), Run.Bosses.Values.Average(b => b.Y));
    private void CompleteBosses(BotContext ctx)
    {
        if (!ctx.Settings.Awakening.BossCatalogValidated.Value) { Status = "All seed-roster bosses dead; catalog requires Codex validation"; return; }
        if (Vector2.Distance(ctx.Game.Player.GridPosNum, EncounterCenter()) > 80)
        { Navigate(ctx, EncounterCenter()); Status = "Returning to confirmed boss drops"; return; }
        Run.BossesCompleted = true; ctx.Combat.Suspend(); ctx.Navigation.Stop(ctx.Game);
        SetPhase(AwakeningPhase.Loot, "encounter_deaths_confirmed");
    }
    private void Fight(BotContext ctx, bool regular)
    {
        var now = DateTime.UtcNow;
        if (!regular && AwakeningBossTracker.Complete(Run)) { CompleteBosses(ctx); return; }
        if (regular)
        {
            if (Run.MapBossKilled) { SetPhase(AwakeningPhase.Loot, "map_boss_dead"); return; }
        }
        var alive = regular ? ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => Run.MapBossIds.Contains(e.Id) && e.IsAlive && e.IsTargetable).ToList()
            : ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => Run.Bosses.ContainsKey(e.Id) && e.IsAlive && e.IsTargetable).ToList();
        var closest = alive.OrderBy(e => Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum)).FirstOrDefault();
        if (closest == null) { if (regular) Explore(ctx); else SetPhase(AwakeningPhase.Scout, "boss_not_observable"); return; }
        if (Vector2.Distance(ctx.Game.Player.GridPosNum, closest.GridPosNum) > ctx.Settings.Build.CombatRange.Value)
        { Navigate(ctx, closest.GridPosNum); return; }
        if (regular)
        {
            var samples = alive.Select(e => new MonsterHealthSample(e.Id, (double)(e.GetComponent<Life>()?.CurHP ?? 0) + (e.GetComponent<Life>()?.CurES ?? 0)));
            _damage.Observe(samples, now);
        }
        if (_relocating)
        {
            ctx.Combat.Suspend();
            if ((ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) && (now - _moveAt).TotalSeconds < 4) return;
            _relocating = false; ctx.Navigation.Stop(ctx.Game); _damage.Reset(now); _bossPositionSince = now;
        }
        if (_damage.NoProgressSeconds(now) >= ctx.Settings.Awakening.NoDamageSeconds.Value ||
            (!regular && (now - _bossPositionSince).TotalSeconds >= 3))
        {
            ctx.Combat.Suspend(); BotInput.ReleaseAllKeys();
            if (ctx.Combat.TryGetOptimalRangedPosition(ctx, out var next) && Vector2.Distance(next, ctx.Game.Player.GridPosNum) > 8)
            {
                _relocating = true; _moveAt = now; Navigate(ctx, next);
                _log.Event(Run, "spark.reposition", new { reason = "boss_damage_or_position_deadline", next.X, next.Y }); return;
            }
            _damage.Reset(now); _bossPositionSince = now;
        }
        if (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) ctx.Navigation.Stop(ctx.Game);
        ctx.Combat.Profile.Enabled = true; ctx.Combat.Profile.SustainEnemyChannelWithoutTarget = ctx.Combat.HasEnemyChannel;
        ctx.Combat.SuppressPositioning = true; ctx.Combat.SuppressTargetedSkills = false;
        ctx.Combat.Tick(ctx); Status = "Spark: " + closest.RenderName; Decision = "Hold productive Spark";
    }
    private void Loot(BotContext ctx)
    {
        if (ctx.Game.EntityListWrapper.OnlyValidEntities.Any(e => e.Type == EntityType.Monster && e.IsAlive && e.IsHostile && e.IsTargetable &&
            Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum) < 60))
        {
            ctx.Interaction.Cancel(ctx.Game); _lootId = 0;
            Scout(ctx, lootDefense: true); Status = "Loot defense: " + Status; return;
        }
        ctx.Combat.Suspend();
        if (ctx.Loot.TickLabelToggle(ctx.Game)) { Status = ctx.Loot.ToggleStatus; return; }
        if (_lootId != 0)
        {
            var inv = AwakeningGameReader.Inventory(ctx.Game);
            var stillOnGround = ctx.Game.EntityListWrapper.OnlyValidEntities.Any(e => e.Id == _lootId && e.Type == EntityType.WorldItem);
            if (AwakeningLootPolicy.PickupConfirmed(inv, _lootPath, _lootBefore, _lootQuantity, stillOnGround))
            {
                var receipt = Run.Instance + ":" + _lootId;
                if (!Run.LootReceipts.Contains(receipt))
                {
                    Run.LootReceipts.Add(receipt); Run.RevenueChaos += _lootPrice;
                    if (_lootPrice <= 0) Run.RevenueKnown = false;
                    Run.UnresolvedLoot.Remove(_lootName);
                    if (_lootName.Contains("Incandescent Invitation", StringComparison.OrdinalIgnoreCase)) Run.InvitationLooted = true;
                    _log.Event(Run, "loot.confirmed", new { _lootId, _lootName, _lootPath, quantity = _lootQuantity, chaos = _lootPrice });
                    Run.UnresolvedLoot.RemoveAll(name => name == _lootName);
                    Supervisor.Save();
                }
                ctx.Interaction.Cancel(ctx.Game); _lootId = 0; _quietAt = DateTime.MinValue;
            }
            else
            {
                var interactionResult = ctx.Interaction.Tick(ctx.Game);
                if (interactionResult == InteractionResult.Failed)
                    _log.Event(Run, "loot.interaction_failed", new { _lootId, _lootName, ctx.Interaction.LastFailReason });
                if ((DateTime.UtcNow - _lootStarted).TotalSeconds < 12) return;
                _log.Event(Run, "loot.pickup_timeout", new { _lootId, _lootName, _lootBefore, _lootQuantity, stillOnGround,
                    inventoryCount = inv.Counts.GetValueOrDefault(_lootPath), ctx.Interaction.Status, ctx.Interaction.LastFailReason, ctx.Navigation.LastRecoveryAction,
                    rawInput = BotInput.RecentRawInputDiagnostics });
                ctx.Interaction.Cancel(ctx.Game); _lootAttempts[_lootId] = _lootAttempts.GetValueOrDefault(_lootId) + 1;
                if (_lootAttempts[_lootId] >= 3) { Run.UnresolvedLoot.Add(_lootName); Finish(ctx, AttemptOutcome.OperationalFailure, "loot_not_confirmed:" + _lootName); return; }
                if (ctx.Settings.Loot.LabelToggleUnstick.Value && BotInput.CanAct)
                { ctx.Loot.StartLabelToggle(ctx.Game); _log.Event(Run, "loot.label_refresh", new { _lootId }); }
                _lootId = 0; return;
            }
        }
        var candidates = new List<(Entity World, Entity Item, string Name, double Price, int Quantity)>();
        bool unresolved = false;
        foreach (var e in ctx.Entities.WorldItems)
        {
            if (Run.LootReceipts.Contains(Run.Instance + ":" + e.Id)) continue;
            try
            {
                var item = e.GetComponent<WorldItem>()?.ItemEntity;
                if (item?.IsValid != true) { unresolved |= AwaitLootMetadata(e.Id); continue; }
                var name = AwakeningGameReader.Name(ctx.Game, item);
                var price = ctx.NinjaPrice.GetPrice(ctx.Game, item);
                double? value = price.MatchCount > 0 && price.MinChaosValue > 0 ? price.MinChaosValue : null;
                var take = AwakeningLootPolicy.ShouldLoot(name, item.Path, value, ctx.Settings.Awakening.MinStackChaos.Value);
                if (_lootDecisions.Add(e.Id + ":" + value + ":" + take))
                    _log.Event(Run, "loot.valued", new { e.Id, name, item.Path, stack = AwakeningGameReader.Quantity(item), stackChaos = value,
                        price.MatchCount, take, mandatory = AwakeningLootPolicy.Mandatory(name, item.Path), threshold = ctx.Settings.Awakening.MinStackChaos.Value,
                        pricesAt = ctx.NinjaPrice.LastRefreshTime });
                if (take)
                    candidates.Add((e, item, name, value ?? 0, AwakeningGameReader.Quantity(item)));
            }
            catch { unresolved |= AwaitLootMetadata(e.Id); }
        }
        if (Run.Outcome != AttemptOutcome.None) return;
        var candidate = candidates.OrderBy(x => Vector2.Distance(ctx.Game.Player.GridPosNum, x.World.GridPosNum)).FirstOrDefault();
        if (candidate.World != null)
        {
            _quietAt = DateTime.MinValue;
            var inv = AwakeningGameReader.Inventory(ctx.Game); if (!inv.Valid) return;
            var label = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
                .FirstOrDefault(l => l.Entity?.Id == candidate.World.Id);
            var labelOnScreen = label?.Label != null && BotInput.IsRectOnScreen(label.ClientRect);
            var visibleLabel = labelOnScreen && (label!.Label.IsVisible || label.Label.IsVisibleLocal);
            // Filter-hidden labels may be absent from VisibleGroundItemLabels entirely.
            // A nearby world item is enough to request Alt; the click still needs a fresh verified label.
            var revealHiddenLabel = !visibleLabel && Vector2.Distance(ctx.Game.Player.GridPosNum, candidate.World.GridPosNum) < 80;
            if (ctx.Interaction.PickupGroundItem(candidate.World, ctx.Navigation, requireProximity: !labelOnScreen && !revealHiddenLabel, revealHiddenLabel: revealHiddenLabel))
            {
                _lootId = candidate.World.Id; _lootPath = candidate.Item.Path; _lootName = candidate.Name;
                _lootPrice = candidate.Price; _lootQuantity = candidate.Quantity; _lootBefore = inv.Counts.GetValueOrDefault(_lootPath); _lootStarted = DateTime.UtcNow;
                _log.Event(Run, "loot.requested", new { _lootId, _lootPath, _lootName, _lootPrice, _lootQuantity, visibleLabel, revealHiddenLabel,
                    x = candidate.World.GridPosNum.X, y = candidate.World.GridPosNum.Y, distance = Vector2.Distance(ctx.Game.Player.GridPosNum, candidate.World.GridPosNum) });
            }
            return;
        }
        if (unresolved) { _quietAt = DateTime.MinValue; Status = "Loot memory still hydrating"; return; }
        if (_quietAt == DateTime.MinValue) _quietAt = DateTime.UtcNow;
        if ((DateTime.UtcNow - _quietAt).TotalSeconds < ctx.Settings.Awakening.LootSettleSeconds.Value) return;
        // After the quiet metadata-complete scan, 60 grids is well inside the entity bubble.
        // Do not require walking onto corpses beyond rocks when their surrounding loot is already observable.
        foreach (var nearby in Run.Bosses.Values.Where(b => b.Life == BossLife.DeadConfirmed && Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y)) < 60))
            if (!Run.LootSweptBosses.Contains(nearby.Id))
            {
                Run.LootSweptBosses.Add(nearby.Id);
                _log.Event(Run, "loot.boss_swept", new { nearby.Id, nearby.Name,
                    distance = Vector2.Distance(ctx.Game.Player.GridPosNum, new(nearby.X, nearby.Y)), reason = "quiet_complete_scan_near_corpse" });
            }
        var sweep = Run.Bosses.Values.Where(b => b.Life == BossLife.DeadConfirmed && !Run.LootSweptBosses.Contains(b.Id))
            .OrderBy(b => Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y))).FirstOrDefault();
        if (sweep != null)
        {
            Navigate(ctx, new(sweep.X, sweep.Y)); _quietAt = DateTime.MinValue; Status = "Checking boss drop location: " + sweep.Name; return;
        }
        if (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) ctx.Navigation.Stop(ctx.Game);
        UpdateInvitation(ctx, Run.ActivationConfirmed);
        if (Run.Invitation == InvitationDecision.Unknown) { Finish(ctx, AttemptOutcome.OperationalFailure, "invitation_state_unknown"); return; }
        if (Run.Invitation == InvitationDecision.Yes && !Run.MapBossKilled) { _damage.Reset(DateTime.UtcNow); SetPhase(AwakeningPhase.MapBoss, "Exarch_invitation_due"); return; }
        if (Run.Invitation == InvitationDecision.Yes && Run.MapBossKilled && !Run.InvitationLooted) { Finish(ctx, AttemptOutcome.OperationalFailure, "invitation_drop_not_confirmed"); return; }
        if (!AwakeningBossTracker.Complete(Run)) { SetPhase(AwakeningPhase.Scout, "encounter_requires_recheck"); return; }
        CancelInput(ctx); SetPhase(AwakeningPhase.Return, "loot_complete");
    }
    private void Return(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout == true && !ctx.Game.IsLoading)
        {
            Run.ReturnedToHideout = true;
            if (Run.RecoveryRequired)
            {
                Run.RecoveryRequired = false; ctx.Settings.Running.Value = false; _lastRunning = false;
                SetPhase(AwakeningPhase.AwaitingReview, "failed_attempt_returned_to_hideout");
                _log.Event(Run, "attempt.recovery_complete", new { Run.Outcome }); WriteEvidence(ctx);
            }
            else SetPhase(AwakeningPhase.OpenStash, "hideout_confirmed");
            return;
        }
        if (!_externalIssued && BotInput.CanAct && BotInput.PressKey(Keys.F2))
        { _externalIssued = true; _log.Event(Run, "external.F2", new { plugin = "QuickPortal" }); }
    }
    private bool TryClickStashLabel(BotContext ctx)
    {
        var label = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.LabelsOnGround?
            .FirstOrDefault(l => l?.ItemOnGround?.Type == EntityType.Stash && l.ItemOnGround.Path == "Metadata/MiscellaneousObjects/Stash" &&
                l.Label?.IsVisible == true && BotInput.IsRectOnScreen(l.Label.GetClientRect()));
        if (label == null) return false;
        Status = "Waiting for stash label click";
        if ((DateTime.UtcNow - _stashClickAt).TotalSeconds < 2 || !BotInput.CanAct) return true;
        ctx.Interaction.Cancel(ctx.Game); ctx.Navigation.Stop(ctx.Game);
        var player = ctx.Game.Player.GridPosNum;
        _stashStationaryClicks = Vector2.Distance(player, _stashLastPosition) < 3 ? _stashStationaryClicks + 1 : 0;
        _stashLastPosition = player;
        if (_stashStationaryClicks >= 2)
        {
            var delta = player - label.ItemOnGround.GridPosNum;
            var tangent = new Vector2(delta.Y, -delta.X);
            if (tangent.Length() < 1) tangent = Vector2.UnitX;
            tangent = Vector2.Normalize(tangent) * (++_stashRecoveryIndex % 2 == 1 ? 22 : -22);
            var next = ctx.Navigation.FindNearestWalkable(ctx.Game, label.ItemOnGround.GridPosNum + tangent, 6);
            if (next.HasValue)
            {
                var screen = AutoExile.Systems.Pathfinding.GridToScreen(ctx.Game, next.Value);
                var window = ctx.Game.Window.GetWindowRectangle();
                if (screen.X > 20 && screen.Y > 20 && screen.X < window.Width - 20 && screen.Y < window.Height - 20 &&
                    BotInput.Click(new Vector2(window.X + screen.X, window.Y + screen.Y)))
                {
                    _stashClickAt = DateTime.UtcNow; _stashStationaryClicks = 0;
                    _log.Event(Run, "stash.approach_reposition", new { x = next.Value.X, y = next.Value.Y, attempt = _stashRecoveryIndex });
                    return true;
                }
            }
        }
        if (BotInput.ClickLabel(ctx.Game, label.Label.GetClientRect()))
        {
            _stashClickAt = DateTime.UtcNow;
            _log.Event(Run, "stash.label_clicked", new { label.ItemOnGround.Id, rect = label.Label.GetClientRect().ToString() });
        }
        return true;
    }
    private void OpenStash(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true) return;
        if (ctx.Game.IngameState.IngameUi.Atlas?.IsVisible == true)
        { Status = "Closing Atlas before stash access"; if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true)
        {
            if (TryClickStashLabel(ctx)) return;
            ctx.Interaction.Tick(ctx.Game);
            if (!ctx.Interaction.IsBusy)
            {
                var stash = ctx.Game.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Type == EntityType.Stash && e.IsTargetable);
                if (stash != null) ctx.Interaction.InteractWithEntity(stash, ctx.Navigation, false, requireVerified: true);
            }
            Status = "Opening loot stash: " + ctx.Interaction.Status; return;
        }
        CancelInput(ctx); SetPhase(AwakeningPhase.ExternalStash, "stash_visible");
    }
    private void ExternalStash(BotContext ctx)
    {
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true) { Status = "Stash closed during external operation"; return; }
        if (!_externalIssued)
        {
            if (AwakeningGameReader.StashieBusy() != false || !BotInput.CanAct || !BotInput.PressKey(Keys.F3)) return;
            _externalIssued = true; _lastKeyAt = DateTime.UtcNow; _log.Event(Run, "external.F3", new { plugin = "StashieV2" }); return;
        }
        var inventory = AwakeningGameReader.Inventory(ctx.Game);
        var stashBusy = AwakeningGameReader.StashieBusy();
        _stableEmpty = AwakeningLootPolicy.StashComplete(inventory) && stashBusy == false && Control.ModifierKeys == Keys.None ? _stableEmpty + 1 : 0;
        if (_stableEmpty == 0) _quietAt = DateTime.MinValue;
        // Await an idle interval after the final transfer; never toggle F3 to "retry" an active coroutine.
        if (_stableEmpty < 5 || (DateTime.UtcNow - _lastKeyAt).TotalSeconds < 2) return;
        if (_quietAt == DateTime.MinValue) { _quietAt = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - _quietAt).TotalSeconds < 1) return;
        Run.StashCompleted = true;
        _log.Event(Run, "external.stash_complete", new { inventory.OutsideReservedColumn, stashBusy });
        if (Run.ReturnedToHideout && Run.BossesCompleted) Finish(ctx, AttemptOutcome.Success, "bosses_loot_hideout_stash_confirmed");
        else SetPhase(AwakeningPhase.Prepare, "initial_stash_complete");
    }
    private bool AwaitLootMetadata(long id)
    {
        if (!_unreadableLoot.TryGetValue(id, out var since)) { _unreadableLoot[id] = DateTime.UtcNow; return true; }
        if ((DateTime.UtcNow - since).TotalSeconds < 5) return true;
        var marker = "unreadable_ground_item:" + id;
        if (!Run.UnresolvedLoot.Contains(marker)) { Run.UnresolvedLoot.Add(marker); _log.Event(Run, "loot.metadata_unavailable", new { id }); }
        // An unreadable drop is unresolved evidence; it cannot certify a complete loot sweep.
        Finish(_ctx!, AttemptOutcome.OperationalFailure, marker);
        return true;
    }
    private void ObserveMapBoss(BotContext ctx)
    {
        foreach (var e in ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster &&
                     Regex.IsMatch(e.Path + " " + e.RenderName, @"Blacksmith|MapBossHillock|HillockBoss", RegexOptions.IgnoreCase)))
        {
            if (!Run.MapBossIds.Contains(e.Id)) { Run.MapBossIds.Add(e.Id); _log.Event(Run, "map_boss.observed", new { e.Id, e.Path, e.RenderName }); }
            var life = e.GetComponent<Life>();
            if (e.IsValid && !e.IsAlive && life != null && life.CurHP == 0 && life.CurES == 0)
            {
                if (!Run.DeadMapBossIds.Contains(e.Id)) { Run.DeadMapBossIds.Add(e.Id); _log.Event(Run, "map_boss.death_confirmed", new { e.Id }); }
            }
            else if (e.IsAlive) Run.DeadMapBossIds.Remove(e.Id);
        }
        Run.MapBossKilled = Run.MapBossIds.Count > 0 && Run.MapBossIds.All(Run.DeadMapBossIds.Contains);
    }
    public bool WriteEvidence(BotContext ctx)
    {
        try
        {
            var data = new { utc = DateTime.UtcNow, supervisor = Snapshot(), device = AwakeningGameReader.DeviceEvidence(ctx.Game),
                portals = StrictMapRecipe.Portals(ctx.Game).Select(p => new { p.Id, p.Path, p.RenderName, area = p.GetComponent<Portal>()?.Area?.Name }).ToArray(),
                worldLabels = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.LabelsOnGround?.Take(100).Select(l => new {
                    id = l.ItemOnGround?.Id, name = l.ItemOnGround?.RenderName, text = l.Label?.Text,
                    visible = l.Label?.IsVisible, local = l.Label?.IsVisibleLocal, rect = l.Label?.GetClientRect().ToString() }).ToArray(),
                rawInput = BotInput.RecentRawInputDiagnostics,
                foreground = ctx.Game.IsForeGroundCache, loading = ctx.Game.IsLoading,
                player = new { x = ctx.Game.Player.GridPosNum.X, y = ctx.Game.Player.GridPosNum.Y },
                stashes = ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Stash).Select(e => new {
                    e.Id, e.Path, e.RenderName, e.IsTargetable, x = e.GridPosNum.X, y = e.GridPosNum.Y,
                    distance = Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum) }).ToArray(),
                prices = new { ctx.NinjaPrice.IsLoaded, ctx.NinjaPrice.LastRefreshTime, league = ctx.Game.IngameState.ServerData.League },
                stashieBusy = AwakeningGameReader.StashieBusy(), settings = AutoExile.WebServer.SettingsApi.SerializeFlat(ctx.Settings)
                    .Where(x => x.Key.StartsWith("awakening.")).ToDictionary(x => x.Key, x => x.Value.Value),
                inventory = AwakeningGameReader.Inventory(ctx.Game), rules = AwakeningMapPolicy.Rules.Select(r => new { r.Id, r.Description, r.Pattern }),
                monsters = ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.Rarity == MonsterRarity.Unique)
                    .Select(e => new { e.Id, e.Path, e.RenderName, e.IsAlive, e.IsTargetable, position = new[] { e.GridPosNum.X, e.GridPosNum.Y } }).ToArray() };
            Directory.CreateDirectory(_directory);
            var evidenceName = "evidence-" + (Run.AttemptId.Length == 0 ? "preflight" : Run.AttemptId);
            var serialized = AwakeningJson.Serialize(data);
            File.WriteAllText(Path.Combine(_directory, evidenceName + ".json"), serialized);
            File.WriteAllText(Path.Combine(_directory, evidenceName + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".json"), serialized);
            _log.Event(Run, "evidence.saved", new { directory = _directory });
            return true;
        }
        catch (Exception ex) { _log.Event(Run, "evidence.failed", new { ex.Message }); return false; }
    }
    public void Render(BotContext ctx)
    {
        if (ctx.Graphics == null) return;
        var origin = new Vector2(20, 240);
        ctx.Graphics.DrawText($"Awakening: {Run.Phase} / {Run.Outcome} ({Run.ElapsedSeconds:F0}s)", origin, SharpDX.Color.Orange);
        ctx.Graphics.DrawText(Status, origin + new Vector2(0, 20), SharpDX.Color.White);
        ctx.Graphics.DrawText($"Bosses {Run.Bosses.Values.Count(b => b.Life == BossLife.DeadConfirmed)}/{Run.Bosses.Count} | Loot {Run.RevenueChaos:F1}c | Deaths {Run.Deaths}",
            origin + new Vector2(0, 40), SharpDX.Color.Gold);
    }
    public void Dispose() { Supervisor.Save(); _log.Dispose(); }
}
