using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileCore.PoEMemory;
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
    private readonly AwakeningLedger _ledger;
    private readonly AwakeningExchange _exchange;
    private readonly AwakeningMarketBuyer _market;
    private bool _marketManual, _marketTried, _marketStarted;
    private bool _exchangeManual;
    private readonly Queue<Keys> _uiKeys = new();
    private static Element? ResolveUi(BotContext ctx, string spec)
    {
        var root = ctx.Game.IngameState.IngameUi;
        var at = spec.IndexOf('@'); string? under = at > 0 ? spec[(at + 1)..] : null; if (at > 0) spec = spec[..at];
        if (spec.StartsWith("path:")) return AwakeningUiInspector.FindByPath(root, spec[5..]);
        if (spec.StartsWith("text:")) return AwakeningUiInspector.FindByText(root, spec[5..], under);
        return null;
    }
    private readonly Queue<ExchangeRequest> _restockQueue = new();
    private ExchangeRequest? _restockCurrent;
    private int _restockSales;
    private static readonly string[] SellableNames = ["Maven's Chisel of Proliferation", "Maven's Chisel of Avarice", "Maven's Chisel of Divination",
        "Maven's Chisel of Procurement", "Maven's Chisel of Scarabs", "The Maven's Writ"];
    private readonly List<object> _runLoot = new();
    private int _runEscapes;
    private string _countedRunId = "";
    private double _countedSeconds;
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
    private DateTime _deviceStockFirstRead = DateTime.MinValue, _priceWaitSince = DateTime.MinValue;
    private bool _defensiveClear;
    private readonly SparkProgressTracker _scoutDamage = new();
    // Loot defense: hold position and Spark only enemies that are actually close to the drops.
    private readonly SparkProgressTracker _lootDefenseDamage = new();
    private bool _lootDefenseActive;
    private DateTime _areaMismatchSince = DateTime.MinValue;
    private readonly Dictionary<long, int> _unblockAttempts = new();
    private DateTime _unblockUntil = DateTime.MinValue;
    private readonly HashSet<long> _scoutIgnored = new();
    private readonly Dictionary<long, int> _scoutStalls = new();
    private Vector2 _stallPos; private int _stallCount;
    private readonly Dictionary<long, DateTime> _pushIgnoredUntil = new();
    private bool _dropSiteReached;
    // Safety: degen ground, Exarch daemons, known dangerous boss effects and "Bearer" monsters are never stood in;
    // a burst of damage or standing in a hazard triggers an escape (movement skill such as Frostblink when ready).
    private readonly Queue<(DateTime At, float Pool)> _poolHistory = new();
    private DateTime _escapeUntil = DateTime.MinValue, _lastBlinkAt = DateTime.MinValue, _lastKiteAt = DateTime.MinValue;
    private int _runKites;
    private readonly HashSet<string> _hazardKinds = new();
    private static readonly Regex DangerAnimation = new(@"Sirus/desolation|Maven/gravity_well|Shaper/vortex|Elder/decay|Exarch/searing_rune|Exarch/flame_wall|/slam/buildup|/explosion/buildup",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public const float BearerAvoidRadius = 30, BurstFraction = 0.35f;
    // Skipped drops are only skipped for this entry: a re-entry (new camera, no HUD overlap) retries them.
    private readonly HashSet<long> _lootSkipped = new();
    private bool _lootInterrupted, _enRouteLoot;
    private DateTime _enRouteScanAt = DateTime.MinValue;
    public const double EnRouteMinChaos = 10, EnRouteRadius = 60;
    private DateTime _lootDefenseStarted, _lootDefenseSuppressedUntil;
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
        _ledger = new(_directory);
        _exchange = new((name, data) => _log.Event(Run, name, data));
        _market = new((name, data) => _log.Event(Run, name, data));
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
        phase = Run.Phase.ToString(), Status, Decision, manualContinuous = _manualContinuous, stopAfterMap = _stopAfterMap, run = Run, lastCommand = Supervisor.LastCommand,
        phaseStartedUtc = _phaseAt,
        inspecting = _inspecting, inspectionStatus = _inspectionStatus,
        commandRequestId = _commandRequestId, commandResult = _commandResult, observedUtc = _observedUtc,
        checkpointError = Supervisor.StorageError, telemetryError = _log.Error, telemetryDropped = _log.Dropped,
        Supervisor.PersistenceRetries, Supervisor.LastPersistenceWarning,
        netChaosPerHour = Run.CostKnown && Run.RevenueKnown && Run.OperatingSeconds > 0 ? (double?)((Run.RevenueChaos - Run.CostChaos) * 3600 / Run.OperatingSeconds) : null,
        modRisk = _risks, economy = _ledger.Summary(),
        exchange = new { busy = _exchange.Busy, status = _exchange.Status, fail = _exchange.FailReason, quote = _exchange.LastQuote, manual = _exchangeManual },
        market = new { busy = _market.Busy, status = _market.Status, fail = _market.FailReason, bought = _market.Bought, spent = _market.Spent, manual = _marketManual } }, AwakeningJson.Options);
    private void RefreshAnalysis() => _risks = AwakeningModRiskAnalyzer.Analyze(Supervisor.State.History.Append(Run)).Take(30).ToArray();

    private bool _manualContinuous;
    private bool _stopAfterMap, _ledgerStarted, _restockTried, _listingChecked, _restockBackToPrepare;
    private readonly HashSet<string> _listedThisSession = new();
    private DateTime _restockEmptySince = DateTime.MinValue;

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
        _manualContinuous = true; _stopAfterMap = false;
        if (source == "user_insert" || !_ledgerStarted) { _ledger.ResetSession(); _ledgerStarted = true; }
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
        if (action == "inspect_ui")
        {
            // Read-only: dumps the visible UI trees (e.g. an open Faustus exchange or market) for calibration.
            // value = "label" or "label|rootPath|depth" (+ "|hidden" to include invisible children)
            var parts = (review ?? "").Split('|');
            var label = Regex.Replace(string.IsNullOrWhiteSpace(parts[0]) ? "manual" : parts[0], @"[^A-Za-z0-9_-]", "");
            var depth = parts.Length > 2 && int.TryParse(parts[2], out var dd) ? dd : 9;
            var file = AwakeningUiInspector.Dump(ctx.Game, _directory, label, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, depth, parts.Length > 3 && parts[3] == "hidden");
            _log.Event(Run, "ui.inspected", new { file });
            return Supervisor.RecordCommand(id, "inspected:" + Path.GetFileName(file));
        }
        if (action == "inspect_items")
        {
            // Read-only: value = "label|rootPath" — dumps the items under a grid element (seller stash, etc.).
            var parts = (review ?? "").Split('|');
            if (parts.Length < 2) return "rejected: value = label|rootPath";
            var label = Regex.Replace(parts[0], @"[^A-Za-z0-9_-]", "");
            var file = AwakeningUiInspector.DumpItems(ctx.Game, _directory, label, parts[1]);
            return Supervisor.RecordCommand(id, "inspected:" + Path.GetFileName(file));
        }
        if (action is "exchange_quote" or "exchange_buy" or "exchange_sell" or "exchange_list")
        {
            // Manual/calibration orders: value = "Item|Quantity|MaxUnitChaos" (quote: "Want|Have").
            if (ctx.Settings.Running.Value || HasActiveAttempt) return "rejected: stop the loop first";
            if (ctx.Game.Area?.CurrentArea?.IsHideout != true) return "rejected: hideout required";
            var parts = review.Split('|');
            int qty = parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1;
            double cap = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;
            var request = action switch
            {
                "exchange_quote" => new ExchangeRequest(ExchangeKind.Quote, parts[0], parts.Length > 1 ? parts[1] : "Chaos Orb", 1),
                "exchange_buy" => new ExchangeRequest(ExchangeKind.BuyAtAsk, parts[0], "Chaos Orb", qty, cap),
                "exchange_sell" => new ExchangeRequest(ExchangeKind.SellAtBid, "Chaos Orb", parts[0], qty),
                _ => new ExchangeRequest(ExchangeKind.ListAtAskMinus, "Chaos Orb", parts[0], qty, 0, ctx.Settings.Awakening.Economy.ListUndercutChaos.Value)
            };
            _exchange.Start(request); _exchangeManual = true;
            return Supervisor.RecordCommand(id, "exchange_started:" + request.Kind);
        }
        if (action == "market_buy")
        {
            // Manual map purchase through the in-game market: value = count (default MapBuyCount). Bot must be stopped, in the hideout.
            if (ctx.Settings.Running.Value || HasActiveAttempt) return "rejected: stop the loop first";
            if (ctx.Game.Area?.CurrentArea?.IsHideout != true) return "rejected: hideout required";
            if (_market.Busy) return "rejected: market purchase already running";
            var count = int.TryParse(review, out var n) && n > 0 ? n : ctx.Settings.Awakening.Economy.MapBuyCount.Value;
            _market.Start(ctx.Game, MarketRequest(ctx, count)); _marketManual = true;
            return Supervisor.RecordCommand(id, "market_started:" + count);
        }
        if (action == "market_cancel") { _market.Cancel(); _marketManual = false; return Supervisor.RecordCommand(id, "market_cancelled"); }
        if (action == "exchange_cancel") { _exchange.Cancel(); _exchangeManual = false; return Supervisor.RecordCommand(id, "exchange_cancelled"); }
        if (action is "ui_click" or "ui_type" or "ui_key")
        {
            // Calibration helpers (bot stopped): value "path:50,2,3" or "text:Search" [+ "@underPath"], then "|right|ctrl" or "|<text to type>".
            if (ctx.Settings.Running.Value || HasActiveAttempt) return "rejected: stop the loop first";
            var parts = (review ?? "").Split('|');
            if (action == "ui_key")
            {
                foreach (var k in parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (Enum.TryParse<Keys>(k, true, out var key)) _uiKeys.Enqueue(key); else return "rejected: unknown key " + k;
                return Supervisor.RecordCommand(id, "keys_queued:" + _uiKeys.Count);
            }
            var target = ResolveUi(ctx, parts[0]);
            if (target == null) return Supervisor.RecordCommand(id, "not_found:" + parts[0]);
            var rect = target.GetClientRect(); var w = ctx.Game.Window.GetWindowRectangle();
            var abs = new Vector2(w.X + rect.Center.X, w.Y + rect.Center.Y);
            if (action == "ui_click")
            {
                var right = parts.Contains("right"); var ctrl = parts.Contains("ctrl");
                var ok = ctrl ? (right ? BotInput.CtrlRightClick(abs) : BotInput.CtrlClick(abs)) : right ? BotInput.RightClick(abs) : BotInput.Click(abs);
                return Supervisor.RecordCommand(id, (ok ? "clicked:" : "click_blocked:") + rect);
            }
            if (!BotInput.Click(abs)) return Supervisor.RecordCommand(id, "click_blocked");
            _uiKeys.Enqueue(Keys.End);
            for (var i = 0; i < 40; i++) _uiKeys.Enqueue(Keys.Back);
            foreach (var ch in (parts.Length > 1 ? parts[1] : "").ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch)) _uiKeys.Enqueue((Keys)ch);
                else if (ch == ' ') _uiKeys.Enqueue(Keys.Space);
                else if (ch == '.') _uiKeys.Enqueue(Keys.OemPeriod);
                else if (ch == '-') _uiKeys.Enqueue(Keys.OemMinus);
                // JIS keyboard layout (this PC): Shift+8 = '(' and Shift+9 = ')'.
                else if (ch == '(') _uiKeys.Enqueue(Keys.D8 | Keys.Shift);
                else if (ch == ')') _uiKeys.Enqueue(Keys.D9 | Keys.Shift);
            }
            return Supervisor.RecordCommand(id, "typing_queued:" + _uiKeys.Count + ":" + rect);
        }
        if (action == "economy_reset") { _ledger.ResetSession(); return Supervisor.RecordCommand(id, "economy_reset"); }
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
        if (action == "stop_after_map")
        {
            // Graceful stop: never abandon a map whose materials are already spent. The loop finishes the current
            // map (loot, return, stash) and stops in the Hideout before opening the next one.
            if (!_manualContinuous) return Supervisor.RecordCommand(id, "not_running");
            _stopAfterMap = true;
            _log.Event(Run, "manual_loop.stop_requested", new { Run.Phase, Run.ActivationRequested, Run.ActivationConfirmed });
            return Supervisor.RecordCommand(id, "stop_after_map_armed");
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
            _deviceStockChecked = false; _deviceStockFirstRead = DateTime.MinValue;
            _mapRestockAttempted = false; _mapRestockStarted = false; _marketTried = false; _marketStarted = false; _mapBossSkipped = false;
            _defensiveClear = false;
            _lootDefenseActive = false; _lootDefenseSuppressedUntil = DateTime.MinValue;
            _uniqueEvidence.Clear();
            _scoutRepositionUntil = DateTime.MinValue;
            _deviceMaterialPaths.Clear();
            _materialIndex = 0; _withdrawing = false; _indexStarted = false; _lootId = 0;
            _lootAttempts.Clear(); _unblockAttempts.Clear(); _scoutIgnored.Clear(); _scoutStalls.Clear(); _lootSkipped.Clear(); _lootInterrupted = false; _enRouteLoot = false; _pushIgnoredUntil.Clear(); _dropSiteReached = false; _restockTried = false; _listingChecked = false; _mapChecks.Clear(); _lootDecisions.Clear(); _unreadableLoot.Clear(); _missingVisits.Clear(); _destination = null; _relocating = false;
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
        if (_uiKeys.Count > 0 && BotInput.CanAct)
        {
            var next = _uiKeys.Peek();
            var sent = (next & Keys.Shift) != 0 ? BotInput.PressShiftKey(next & Keys.KeyCode) : BotInput.PressKey(next);
            if (sent) _uiKeys.Dequeue();
        }
        if (_marketManual)
        {
            if (_market.Busy) { _market.Tick(ctx); Status = _market.Status; }
            else { _marketManual = false; Status = _market.Status; RecordMarketResult("manual"); }
        }
        if (_exchangeManual)
        {
            if (_exchange.Busy) { _exchange.Tick(ctx); Status = _exchange.Status; }
            else { _exchangeManual = false; Status = _exchange.Status; RecordExchangeResult(ctx, "manual"); }
        }
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
            // CurrentAreaHash flips to the Hideout before Area.CurrentArea does, so a return portal briefly looks like
            // "in a map with a different instance". Only a mismatch that persists (and never while returning) is real.
            bool areaMismatch = inMap && Run.Instance != 0 && Run.Instance != (long)gc.IngameState.Data.CurrentAreaHash && Run.Outcome == AttemptOutcome.None;
            if (!areaMismatch || Run.Phase is AwakeningPhase.Return or AwakeningPhase.OpenStash or AwakeningPhase.ExternalStash)
                _areaMismatchSince = DateTime.MinValue;
            else
            {
                if (_areaMismatchSince == DateTime.MinValue) _areaMismatchSince = now;
                if ((now - _areaMismatchSince).TotalSeconds >= 2)
                { _areaMismatchSince = DateTime.MinValue; Finish(ctx, AttemptOutcome.OperationalFailure, "unexpected_area_change"); }
                return;
            }
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
                if (Run.BossesCompleted && !AwakeningBossTracker.AllTrackedDead(Run))
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
                    // 2026-09-21 13:25: the scout stayed stuck at one spot for 70 s (stall → repath → stall). From the 2nd stall
                    // at the same spot, jump with the movement skill (Frostblink can cross terrain) toward the destination,
                    // rotating the direction on each further stall.
                    _stallCount = Vector2.Distance(pos, _stallPos) < 15 ? _stallCount + 1 : 1; _stallPos = pos;
                    if (_stallCount >= 2)
                    {
                        var baseDir = _destination.HasValue && Vector2.Distance(_destination.Value, pos) > 1 ? Vector2.Normalize(_destination.Value - pos) : new Vector2(1, 0);
                        var angle = MathF.Atan2(baseDir.Y, baseDir.X) + (_stallCount - 2) * MathF.PI / 3 * ((_stallCount % 2 == 0) ? 1 : -1);
                        var jump = pos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 25;
                        var blinked = TryEscapeBlink(ctx, jump);
                        _log.Event(Run, "navigation.stall_blink", new { stalls = _stallCount, blinked, x = jump.X, y = jump.Y });
                    }
                    ctx.Navigation.Stop(gc);
                    if (_destination.HasValue) ctx.Exploration.MarkRegionFailed(_destination.Value);
                    _lastProgress = now; _destination = null;
                }
            }
            var outcome = AwakeningSupervisor.Watchdog(Run, gc.Player.IsAlive, inMap, ctx.Settings.Awakening.TimeoutSeconds.Value);
            if (outcome != AttemptOutcome.None)
            {
                if (outcome == AttemptOutcome.Death) { Run.Deaths++; LogThreatContext(ctx, "death.context"); }
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
                { Run.RecoveryRequired = false; ctx.Settings.Running.Value = false; CancelInput(ctx); StopContinuousLoop("return_failed_after_terminal_attempt"); SetPhase(AwakeningPhase.AwaitingReview, "return_failed_after_terminal_attempt"); }
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
                case AwakeningPhase.Restock: Restock(ctx); break;
                case AwakeningPhase.MarketBuy: MarketBuy(ctx); break;
                case AwakeningPhase.EnterPortal: EnterPortal(ctx); break;
                case AwakeningPhase.Scout: if (!SafetyTick(ctx)) Scout(ctx); break;
                case AwakeningPhase.Fight: if (!SafetyTick(ctx)) Fight(ctx, false); break;
                case AwakeningPhase.Loot: if (!SafetyTick(ctx)) Loot(ctx); break;
                case AwakeningPhase.MapBoss: if (!SafetyTick(ctx)) Fight(ctx, true); break;
                case AwakeningPhase.Return: Return(ctx); break;
                case AwakeningPhase.OpenStash: OpenStash(ctx); break;
                case AwakeningPhase.ExternalStash: ExternalStash(ctx); break;
            }
        }
        catch (Exception ex)
        {
            if (Run.RecoveryRequired)
            { Run.RecoveryRequired = false; ctx.Settings.Running.Value = false; CancelInput(ctx); StopContinuousLoop("recovery_exception:" + ex.Message); SetPhase(AwakeningPhase.AwaitingReview, "recovery_exception:" + ex.Message); }
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
        _quietAt = DateTime.MinValue; _externalIssued = false; _stableEmpty = 0; _mapBankStarted = false; _mapBankDone = false;
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
        RecordRun(ctx, outcome, reason);
        if (_manualContinuous && _stopAfterMap && outcome == AttemptOutcome.Success)
        { _stopAfterMap = false; StopContinuousLoop("stop_after_map"); return; }
        var economy = ctx.Settings.Awakening.Economy;
        if (_manualContinuous && outcome == AttemptOutcome.Success && economy.LedgerEnabled.Value && economy.StopIfNegativeAfterMaps.Value > 0 &&
            _ledger.Maps >= economy.StopIfNegativeAfterMaps.Value && _ledger.ChaosPerHour is < 0)
        { _log.Event(Run, "economy.stop_negative", _ledger.Summary()); StopContinuousLoop("economy_negative_after_" + _ledger.Maps + "_maps"); return; }
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
        // A death is not a reason to stop the Insert loop: recover to the Hideout (below), then
        // Return() re-enters via ContinueAfterDeath() until the supplies are gone.
        if (_manualContinuous && outcome is (AttemptOutcome.Death or AttemptOutcome.Timeout) && ctx.Settings.Awakening.ContinueAfterDeath.Value && Supervisor.StorageError.Length == 0)
            _log.Event(Run, "manual_loop.death_recovery", new { reason, Run.Deaths, Run.Instance, Run.RevenueChaos });
        else StopContinuousLoop(outcome + ":" + reason);
        if (Supervisor.StorageError.Length == 0 && ((outcome is AttemptOutcome.Death or AttemptOutcome.Timeout) || reason is "unexpected_map_before_activation" or "wrong_instance_on_reentry") && ctx.Game.Area?.CurrentArea?.IsHideout != true)
        {
            Run.RecoveryRequired = true; SetPhase(AwakeningPhase.Return, "recover_after_" + outcome);
            ctx.Settings.Running.Value = true; _lastRunning = true;
        }
    }
    // Cleared maps leave portals labelled "Complete". The label text is only used to skip them when it is readable.
    private static bool PortalLooksComplete(BotContext ctx, Entity portal)
    {
        try
        {
            var label = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.LabelsOnGround?.FirstOrDefault(l => l?.ItemOnGround?.Id == portal.Id);
            var text = label?.Label?.Text;
            return !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"Complete|Cleared", RegexOptions.IgnoreCase);
        }
        catch { return false; }
    }
    // One line per attempt in runs.jsonl: the map's mods next to deaths, time and revenue, for "which mod kills/slows us" analysis.
    private void RecordRun(BotContext ctx, AttemptOutcome outcome, string reason)
    {
        try
        {
            // Every attempt's time counts; a map is counted once it succeeds (deaths retry the same map).
            // Run.OperatingSeconds accumulates across the retries of one map; only the new part is added.
            if (_countedRunId != Run.RunId) { _countedRunId = Run.RunId; _countedSeconds = 0; }
            _ledger.AddTime(Run.OperatingSeconds - _countedSeconds, outcome == AttemptOutcome.Success);
            _countedSeconds = Run.OperatingSeconds;
            _ledger.Run(new
            {
                utc = DateTime.UtcNow, Run.RunId, Run.AttemptId, Run.AttemptNumber, Run.Instance, outcome = outcome.ToString(), reason,
                mapName = Run.Map?.Name, quantity = Run.Map?.Quantity, mods = Run.Map?.Mods.Select(m => new { m.Id, m.Text }).ToArray(),
                group = Run.Bosses.Values.Select(b => b.Group).Distinct().ToArray(), bosses = Run.Bosses.Values.Select(b => new { b.Member, life = b.Life.ToString() }).ToArray(),
                Run.Deaths, Run.OperatingSeconds, Run.ElapsedSeconds, Run.PhaseSeconds, revenue = Run.RevenueChaos, cost = Run.CostChaos,
                loot = _runLoot.ToArray(), escapes = _runEscapes, hazards = _hazardKinds.ToArray(), Run.UnresolvedLoot
            });
        }
        catch { }
        _runLoot.Clear(); _runEscapes = 0;
    }
    private void StopContinuousLoop(string reason)
    {
        if (!_manualContinuous) return;
        _manualContinuous = false;
        _log.Event(Run, "manual_loop.stopped", new { reason, Run.Outcome, Status });
    }
    /// <summary>
    /// Runs once the state machine has confirmed a death recovery reached the Hideout. Retries the
    /// remaining portals of the same map or opens a new one; supplies or an operational fault end the loop.
    /// </summary>
    private void ContinueAfterDeath(BotContext ctx)
    {
        if (!_manualContinuous) return;
        if (Run.Outcome is not (AttemptOutcome.Death or AttemptOutcome.Timeout) || !ctx.Settings.Awakening.ContinueAfterDeath.Value || Supervisor.StorageError.Length > 0 ||
            ctx.Game.Area?.CurrentArea?.IsHideout != true)
        { StopContinuousLoop("death_continue_not_allowed"); return; }
        string Send(string action, string review = "") => Command(ctx, action, Guid.NewGuid().ToString("N"), Supervisor.Generation, review, Supervisor.Mvid);
        var ack = Send("review", AwakeningJson.Serialize(new {
            observations = $"{Run.Outcome} (deaths {Run.Deaths}) in {Run.Area} instance {Run.Instance}; returned to the Hideout and confirmed by the state machine",
            diagnosis = "User-authorized continuous farming keeps going after a death until supplies run out; no Codex diagnosis performed",
            changes = "None", validation = "Recovery to Hideout confirmed; retry or new map is decided from the portal state",
            nextAction = "retry_existing_portal or new_map until supplies are exhausted" }));
        if (!ack.StartsWith("rejected:") && !Supervisor.State.Armed) ack = Send("arm");
        if (!ack.StartsWith("rejected:")) ack = Send("begin");
        if (ack.StartsWith("rejected:"))
        {
            Status = ack;
            StopContinuousLoop("death_continue_rejected:" + ack);
            return;
        }
        _log.Event(Run, "manual_loop.continued", new { reason = "previous_attempt_death", Run.AttemptId, Run.Deaths, Run.Instance });
    }
    private void Prepare(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true) { Status = "Waiting for hideout"; return; }
        if (!Run.ActivationRequested && !Run.ActivationConfirmed && !_listingChecked && PlanListings(ctx)) return;
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
            // Portal entities stream in after the Hideout loads; after a death recovery allow a little longer
            // so a slow load is never mistaken for exhausted portals (which would spend a fresh set of materials).
            if ((DateTime.UtcNow - _phaseAt).TotalSeconds < (Run.Deaths > 0 ? 8 : 5)) { Status = "Confirming exhausted portals"; return; }
            var present = StrictMapRecipe.Portals(ctx.Game);
            if (present.Count > 0)
            {
                // Portal entity IDs are regenerated whenever the Hideout is re-entered (F2 after a death, etc.).
                // A map we already entered (Run.Instance != 0) keeps its portals, so adopt the visible Dunes
                // portals instead of stopping. The instance hash is still verified on entry (wrong_instance_on_reentry).
                var candidates = present.Where(p => !PortalLooksComplete(ctx, p)).ToList();
                if (Run.Instance != 0 && candidates.Count > 0 &&
                    candidates.All(p => (p.GetComponent<Portal>()?.Area?.Name ?? p.RenderName) == "Dunes"))
                {
                    var previous = Run.PortalIds.ToArray();
                    Run.PortalIds = candidates.Select(p => (long)p.Id).ToList();
                    Supervisor.Save();
                    _log.Event(Run, "portals.auto_rebound", new { previous, current = Run.PortalIds, skippedComplete = present.Count - candidates.Count, Run.Instance, Run.Deaths });
                    SetPhase(AwakeningPhase.EnterPortal, "portals_rebound_after_return"); return;
                }
                // Unrelated existing portals are never overwritten or entered.
                Finish(ctx, AttemptOutcome.OperationalFailure, "unrecognized_portal_set"); return;
            }
            _log.Event(Run, "run.portals_exhausted");
            var attempt = Run.AttemptId;
            Supervisor.State.Run = new() { AttemptId = attempt, AttemptNumber = 1, Phase = AwakeningPhase.Prepare,
                BuildMvid = Supervisor.Mvid, BuildFingerprint = Run.BuildFingerprint, BuildConfiguration = Run.BuildConfiguration };
            Supervisor.Save();
        }
        if (Run.ActivationRequested) { Finish(ctx, AttemptOutcome.OperationalFailure, "activation_indeterminate_do_not_reactivate"); return; }
        // stop_after_map must also hold when the previous map ended in deaths (the death path re-enters Prepare, not Finish(Success)).
        if (_manualContinuous && _stopAfterMap) { _stopAfterMap = false; Finish(ctx, AttemptOutcome.OperationalFailure, "stop_after_map_before_new_map"); return; }
        if (StrictMapRecipe.Portals(ctx.Game).Any(p => !Run.PriorPortalIds.Contains(p.Id)))
        { Finish(ctx, AttemptOutcome.OperationalFailure, "unrecognized_portal_set_before_activation"); return; }
        if (!ctx.Settings.Awakening.ModCatalogValidated.Value) { WriteEvidence(ctx); Finish(ctx, AttemptOutcome.OperationalFailure, "preflight: validate 17 NG rules using map evidence"); return; }
        if (ctx.Settings.Awakening.ExarchReadyCounter.Value < 0) { WriteEvidence(ctx); Finish(ctx, AttemptOutcome.OperationalFailure, "preflight: calibrate Exarch ready counter"); return; }
        if (!ctx.NinjaPrice.IsLoaded || (DateTime.Now - ctx.NinjaPrice.LastRefreshTime).TotalMinutes > ctx.Settings.Awakening.MaxPriceAgeMinutes.Value)
        {
            // poe.ninja takes up to a minute after a host restart; that wait must not trip the 60 s Prepare deadline.
            if (_priceWaitSince == DateTime.MinValue) _priceWaitSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _priceWaitSince).TotalSeconds < 240) _phaseAt = DateTime.UtcNow;
            Status = "Waiting for fresh Allflame prices"; return;
        }
        _priceWaitSince = DateTime.MinValue;
        if (ctx.Game.IngameState.ServerData.League != "Allflame") { Finish(ctx, AttemptOutcome.OperationalFailure, "expected_Allflame_price_league"); return; }
        if (!_deviceStockChecked)
        {
            var atlas = ctx.Game.IngameState.IngameUi.Atlas;
            if (atlas?.IsVisible == true) _deviceTries = 0;
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
            var materials = MaterialNames.Select(name => slots.FirstOrDefault(item => AwakeningGameReader.Name(ctx.Game, item) == name)).ToArray();
            // The 5 device slots populate a moment after the panel opens; an early read looks empty.
            // Keep re-reading for 2.5 s unless every material is already there.
            if (_deviceStockFirstRead == DateTime.MinValue) _deviceStockFirstRead = DateTime.UtcNow;
            if (materials.Any(m => m == null) && (DateTime.UtcNow - _deviceStockFirstRead).TotalSeconds < 2.5)
            { Status = "Reading device slots (" + materials.Count(m => m != null) + "/" + materials.Length + ")"; return; }
            _deviceStockChecked = true; _deviceStockFirstRead = DateTime.MinValue;
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
        if (device == null) { Status = "Map device not found"; return; }
        if (!NearDevice(ctx)) return;
        var render = device.GetComponent<Render>();
        var point = ctx.Game.IngameState.Camera.WorldToScreen(render?.InteractCenterNum ?? device.BoundsCenterPosNum);
        // 2026-09-21: the verified interaction sometimes never leaves its "cursor-move" stage (device on screen, clicked
        // 30+ times, Atlas never opened). From the 4th try, click the device point directly like a UI click.
        _deviceTries++;
        if (_deviceTries > 3 && _deviceTries % 2 == 0)
        {
            ctx.Interaction.Cancel(ctx.Game);
            var w = ctx.Game.Window.GetWindowRectangle();
            if (BotInput.CanAct && BotInput.Click(new Vector2(w.X + point.X, w.Y + point.Y)))
                _log.Event(Run, "prepare.device_direct_click", new { tries = _deviceTries, point.X, point.Y });
            return;
        }
        ctx.Interaction.InteractWithEntity(device, ctx.Navigation, false, requireVerified: true);
        _log.Event(Run, "prepare.device_interaction", new { device.Id, device.Path, point.X, point.Y, anchor = "interaction" });
    }
    // After /hideout (market purchase) the character spawns at the hideout entrance, out of click range of the device.
    // 2026-09-21: typing "/hideout" when the chat did not open sent the letters as hotkeys and left the hideout in
    // edit mode ("Editing"), where clicking the map device selects the decoration instead of opening the Atlas.
    private DateTime _editCheckAt;
    private bool ExitHideoutEditing(BotContext ctx)
    {
        if ((DateTime.UtcNow - _editCheckAt).TotalSeconds < 2) return false;
        _editCheckAt = DateTime.UtcNow;
        var editing = AwakeningUiInspector.FindByText(ctx.Game.IngameState.IngameUi, "Editing");
        if (editing == null || !BotInput.CanAct) return false;
        var rect = editing.GetClientRect(); var w = ctx.Game.Window.GetWindowRectangle();
        if (BotInput.Click(new Vector2(w.X + rect.Center.X, w.Y + rect.Center.Y)))
        { _log.Event(Run, "hideout.exit_edit_mode", new { rect = rect.ToString() }); Status = "Leaving hideout edit mode"; }
        return true;
    }
    private bool NearDevice(BotContext ctx)
    {
        if (ExitHideoutEditing(ctx)) return false;
        var device = ctx.Game.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Path.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase));
        // Only walk when the device is off-screen: walking (click-to-move) through the old map's portals next to the
        // device entered that map once (unexpected_map_before_activation, 13:10).
        bool onScreen = false;
        if (device != null)
        {
            try
            {
                var p = ctx.Game.IngameState.Camera.WorldToScreen(device.GetComponent<Render>()?.InteractCenterNum ?? device.BoundsCenterPosNum);
                var w = ctx.Game.Window.GetWindowRectangle();
                onScreen = p.X > 60 && p.Y > 60 && p.X < w.Width - 60 && p.Y < w.Height - 160;
            }
            catch { }
        }
        if (device == null || onScreen || device.DistancePlayer <= 40) { if (device != null && _walkingToDevice) { _walkingToDevice = false; ctx.Navigation.Stop(ctx.Game); } return true; }
        if (!_walkingToDevice) _log.Event(Run, "hideout.walk_to_device", new { distance = device.DistancePlayer });
        _walkingToDevice = true; _phaseAt = DateTime.UtcNow;
        Navigate(ctx, device.GridPosNum); Status = $"Walking to the map device ({device.DistancePlayer:0})";
        return false;
    }
    private bool _walkingToDevice;
    private int _deviceTries;
    private static string? InventoryMaterialPath(BotContext ctx, string name)
    {
        try
        {
            var items = AutoExile.Systems.StashSystem.GetInventorySlotItems(ctx.Game);
            if (items == null) return null;
            foreach (var slot in items)
                if (slot.Item?.IsValid == true && AwakeningGameReader.Name(ctx.Game, slot.Item).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return slot.Item.Path;
        }
        catch { }
        return null;
    }
    // Supplies: bring every material up to its target through Faustus at the lowest ask. Chaos short → sell Chisels/Writ at the best bid first.
    private int HeldMaterial(BotContext ctx, string name)
    {
        var held = AwakeningExchange.CountHeld(ctx.Game, name);
        try { held = Math.Max(held, ctx.StashIndex.Tabs.SelectMany(t => t.Items).Where(e => e.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Stack)); } catch { }
        if (_deviceMaterialPaths.ContainsKey(name)) held++;
        return held;
    }
    private void PlanRestock(BotContext ctx)
    {
        var economy = ctx.Settings.Awakening.Economy;
        _restockTried = true; _restockQueue.Clear(); _restockSales = 0;
        foreach (var name in MaterialNames)
        {
            var target = name.StartsWith("Horned") ? economy.ScarabTarget.Value : economy.SacrificeTarget.Value;
            var need = target - HeldMaterial(ctx, name);
            if (need <= 0) continue;
            var category = name.StartsWith("Horned") ? NinjaPriceCategory.Scarab : NinjaPriceCategory.Fragment;
            var ninja = ctx.NinjaPrice.GetPrice(name, category).MinChaosValue;
            // Safety cap: never pay more than 1.5x the poe.ninja price (or 300c when unknown).
            _restockQueue.Enqueue(new ExchangeRequest(ExchangeKind.BuyAtAsk, name, "Chaos Orb", need, ninja > 0 ? ninja * 1.5 : 300));
        }
        _log.Event(Run, "restock.planned", _restockQueue.ToArray());
        CancelInput(ctx);
        SetPhase(AwakeningPhase.Restock, "restock_via_faustus");
    }
    // Held Chaos below the threshold: list Chisels/Writ at (lowest competing ask - undercut) once per session each.
    // The orders fill later; the items leave the stash, so they are never listed twice.
    private bool PlanListings(BotContext ctx)
    {
        _listingChecked = true;
        var economy = ctx.Settings.Awakening.Economy;
        if (!economy.AutoRestock.Value || economy.ListBelowChaos.Value <= 0) return false;
        var chaos = AwakeningExchange.CountHeld(ctx.Game, "Chaos Orb");
        if (chaos >= economy.ListBelowChaos.Value) return false;
        _restockQueue.Clear();
        foreach (var name in SellableNames.Where(n => !_listedThisSession.Contains(n)))
        {
            var held = AwakeningExchange.CountHeld(ctx.Game, name);
            if (held <= 0) continue;
            _listedThisSession.Add(name);
            _restockQueue.Enqueue(new ExchangeRequest(ExchangeKind.ListAtAskMinus, "Chaos Orb", name, held, 0, economy.ListUndercutChaos.Value));
        }
        if (_restockQueue.Count == 0) return false;
        _log.Event(Run, "listing.planned", new { chaos, threshold = economy.ListBelowChaos.Value, orders = _restockQueue.ToArray() });
        _restockBackToPrepare = true; CancelInput(ctx);
        SetPhase(AwakeningPhase.Restock, "list_below_chaos_threshold");
        return true;
    }
    private void Restock(BotContext ctx)
    {
        var economy = ctx.Settings.Awakening.Economy;
        if (_exchange.Busy) { _exchange.Tick(ctx); Status = _exchange.Status; _phaseAt = DateTime.UtcNow; return; }
        if (_restockCurrent != null)
        {
            var done = _restockCurrent; _restockCurrent = null;
            RecordExchangeResult(ctx, "restock");
            if (!_exchange.Succeeded && done.Kind == ExchangeKind.ListAtAskMinus) _log.Event(Run, "listing.skipped", new { done.HaveName, _exchange.FailReason });
            if (!_exchange.Succeeded && done.Kind == ExchangeKind.BuyAtAsk)
            {
                if (_exchange.FailReason.StartsWith("price_above_cap")) { Finish(ctx, AttemptOutcome.OperationalFailure, "restock_price_above_cap:" + done.WantName); return; }
                if (economy.StopWhenOutOfChaos.Value) { Finish(ctx, AttemptOutcome.OperationalFailure, "supplies_exhausted:restock_failed:" + _exchange.FailReason); return; }
            }
        }
        if (_restockQueue.Count == 0)
        {
            if (ctx.Game.IngameState.IngameUi.CurrencyExchangePanel?.IsVisible == true) { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
            if (_restockBackToPrepare) { _restockBackToPrepare = false; SetPhase(AwakeningPhase.Prepare, "listing_complete"); return; }
            _indexStarted = false; ctx.StashIndex.Reset();
            SetPhase(AwakeningPhase.IndexStash, "restock_complete_reindex"); return;
        }
        var next = _restockQueue.Peek();
        if (next.Kind == ExchangeKind.BuyAtAsk)
        {
            var chaos = AwakeningExchange.CountHeld(ctx.Game, "Chaos Orb");
            var estimate = next.MaxUnitChaos / 1.5 * next.Quantity;
            if (chaos < estimate)
            {
                var sellable = economy.SellWhenChaosShort.Value && _restockSales < SellableNames.Length
                    ? SellableNames.Skip(_restockSales).FirstOrDefault(n => AwakeningExchange.CountHeld(ctx.Game, n) > 0) : null;
                if (sellable != null)
                {
                    _restockSales = Array.IndexOf(SellableNames, sellable) + 1;
                    _restockCurrent = new ExchangeRequest(ExchangeKind.SellAtBid, "Chaos Orb", sellable, AwakeningExchange.CountHeld(ctx.Game, sellable));
                    _log.Event(Run, "restock.sell_for_chaos", new { chaos, estimate, sellable });
                    _exchange.Start(_restockCurrent); return;
                }
                if (chaos < next.MaxUnitChaos / 1.5 && economy.StopWhenOutOfChaos.Value)
                { Finish(ctx, AttemptOutcome.OperationalFailure, "supplies_exhausted:out_of_chaos"); return; }
                // Buy what the chaos allows.
                var affordable = (int)Math.Floor(chaos / Math.Max(1, next.MaxUnitChaos / 1.5));
                if (affordable < next.Quantity) { _restockQueue.Dequeue(); _restockCurrent = next with { Quantity = Math.Max(1, affordable) }; _exchange.Start(_restockCurrent); return; }
            }
        }
        _restockCurrent = _restockQueue.Dequeue();
        _exchange.Start(_restockCurrent);
    }
    private void RecordExchangeResult(BotContext ctx, string source)
    {
        var r = _exchange.Succeeded ? _exchangeLastRequest() : null;
        if (r == null || (r.Kind != ExchangeKind.ListAtAskMinus && _exchange.FilledWant <= 0)) return;
        if (r.Kind == ExchangeKind.BuyAtAsk)
        { _ledger.Price(r.WantName, _exchange.UnitChaos, "faustus_ask"); _ledger.Purchase(r.WantName, _exchange.FilledWant, _exchange.UnitChaos, source); }
        else if (r.Kind == ExchangeKind.SellAtBid)
            _ledger.AddSale(r.HaveName, _exchange.PaidHave, _exchange.UnitChaos, "faustus_bid_" + source);
        else if (r.Kind == ExchangeKind.ListAtAskMinus)
            _ledger.Listing(r.HaveName, r.Quantity, _exchange.UnitChaos, source);
    }
    private ExchangeRequest? _exchangeLastRequest() => _exchange.Request;
    private void Index(BotContext ctx)
    {
        // After a Faustus restock the stash is closed ("Stash closed — index scan aborted" stopped the loop): open it first.
        if (!_indexStarted && ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true)
        {
            if (ctx.Game.IngameState.IngameUi.CurrencyExchangePanel?.IsVisible == true) { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
            TryClickStashLabel(ctx); Status = "Opening the stash for the index"; return;
        }
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
            // A material left in the inventory (e.g. withdrawn for a map that was then rejected) is used first.
            var carried = InventoryMaterialPath(ctx, name);
            if (carried != null) { materials.Add(new(name, carried)); _log.Event(Run, "recipe.inventory_stock", new { name, path = carried }); continue; }
            var matches = entries.Where(e => e.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (ctx.Game.Files.BaseItemTypes.Translate(e.ItemPath)?.BaseName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)).ToList();
            if (matches.Count == 0)
            {
                _log.Event(Run, "materials.unresolved", entries.Select(x => new { x.ItemPath, x.BaseName, x.TabName }));
                if (ctx.Settings.Awakening.Economy.AutoRestock.Value && !_restockTried) { PlanRestock(ctx); return; }
                Finish(ctx, AttemptOutcome.OperationalFailure, "material_not_found:" + name); return;
            }
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
            if (!NearDevice(ctx)) return;
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
                        if (ctx.Settings.Awakening.Economy.LedgerEnabled.Value)
                        { _ledger.Price(material.Name, price, "poe.ninja"); _ledger.AddInvest(material.Name, material.Count, price, "consumed_at_market_price"); }
                    }
                    if (ctx.Settings.Awakening.Economy.LedgerEnabled.Value && ctx.Settings.Awakening.MapCostChaos.Value > 0)
                        _ledger.AddInvest("Map (Tier 16)", 1, ctx.Settings.Awakening.MapCostChaos.Value, "consumed_setting_price");
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
    private bool _mapRestockAttempted, _mapRestockStarted, _mapBossSkipped;
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
        // The tab is open and holds no acceptable map: that is "supplies exhausted", not a 30 s input timeout.
        if (ctx.Stash.Status.StartsWith("Waiting for 'Metadata/Items/Maps/MapKeyTier16'", StringComparison.Ordinal))
        {
            if (_restockEmptySince == DateTime.MinValue)
            {
                _restockEmptySince = DateTime.UtcNow;
                // Diagnostics: why is no map in the visible tab acceptable?
                try
                {
                    var stash = ctx.Game.IngameState.IngameUi.StashElement;
                    var maps = stash?.VisibleStash?.VisibleInventoryItems?.Where(i => i.Item?.Path?.Contains("MapKey") == true)
                        .Select(i => { var m = AwakeningGameReader.ReadMap(ctx.Game, i.Item, "Dunes"); return new { m.Name, m.Tier, reject = AwakeningMapPolicy.Rejections(m) }; }).ToArray();
                    var tabs = ctx.Game.IngameState.ServerData.PlayerStashTabs?.Select(t => new { t.Name, type = t.TabType.ToString(), t.VisibleIndex }).ToArray();
                    var all = stash?.VisibleStash?.VisibleInventoryItems;
                    _log.Event(Run, "map.restock_candidates", new { tab = stash?.IndexVisibleStash, visibleName = stash?.VisibleStash?.GetType().Name,
                        total = all?.Count, sample = all?.Take(8).Select(i => i.Item?.Path).ToArray(), count = maps?.Length, maps, tabs });
                }
                catch (Exception ex) { _log.Event(Run, "map.restock_candidates", new { error = ex.Message }); }
            }
            else if ((DateTime.UtcNow - _restockEmptySince).TotalSeconds > 4)
            {
                ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                if (ctx.Settings.Awakening.Economy.AutoBuyMaps.Value && !_marketTried)
                { _marketTried = true; _marketStarted = false; SetPhase(AwakeningPhase.MarketBuy, "no_T16_in_Tmp_buy_from_market"); return; }
                Finish(ctx, AttemptOutcome.OperationalFailure, "supplies_exhausted:no_acceptable_T16_in_Tmp"); return;
            }
        }
        else _restockEmptySince = DateTime.MinValue;
        if (result == StashResult.Failed) { Finish(ctx, AttemptOutcome.OperationalFailure, "Tmp_no_usable_map:" + Status); return; }
        if (result != StashResult.Succeeded) return;
        ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
        _log.Event(Run, "map.restocked", new { tab = "Tmp", count = 1 });
        SetPhase(AwakeningPhase.OpenMap, "Tmp_map_ready");
    }
    private MarketMapRequest MarketRequest(BotContext ctx, int count)
    {
        var e = ctx.Settings.Awakening.Economy;
        return new(count, e.MapMaxUnitChaos.Value, e.MapFollowUpOverChaos.Value, e.MapMinQuantity.Value, e.MapMinPackSize.Value, e.MapMinAffixesEach.Value);
    }
    private void RecordMarketResult(string source)
    {
        foreach (var p in _market.Purchases) { _ledger.Purchase("Map (Tier 16)", 1, p.Price, "market:" + p.Seller); _ledger.Price("Map (Tier 16)", p.Price, "market:" + source); }
        _market.Purchases.Clear();
    }
    private void MarketBuy(BotContext ctx)
    {
        // Maps ran out (Atlas + Tmp): buy MapBuyCount maps through the in-game market, then open the map from the inventory.
        if (!_marketStarted)
        {
            if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible == true || ctx.Game.IngameState.IngameUi.Atlas?.IsVisible == true)
            { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
            _market.Start(ctx.Game, MarketRequest(ctx, ctx.Settings.Awakening.Economy.MapBuyCount.Value)); _marketStarted = true; return;
        }
        if (_market.Busy) { _market.Tick(ctx); Status = _market.Status; return; }
        // The market / results pane stays open after a purchase and blocks the map device click.
        if (AwakeningMarketBuyer.MarketOpen(ctx.Game)) { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); Status = "Closing the market"; return; }
        RecordMarketResult("auto");
        _log.Event(Run, "market.restock_done", new { ok = _market.Succeeded, bought = _market.Bought, spent = _market.Spent, reason = _market.FailReason });
        if (_market.Succeeded) { _mapRestockAttempted = false; SetPhase(AwakeningPhase.OpenMap, "market_maps_bought"); return; }
        Finish(ctx, AttemptOutcome.OperationalFailure, "supplies_exhausted:market_" + _market.FailReason);
    }
    private void EnterPortal(BotContext ctx)
    {
        if (ctx.Game.Area?.CurrentArea?.IsHideout != true) return;
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible == true || ctx.Game.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
        { if (BotInput.CanAct) BotInput.PressKey(Keys.Escape); return; }
        ctx.Interaction.Tick(ctx.Game);
        if (ctx.Interaction.IsBusy) return;
        var portal = StrictMapRecipe.Portals(ctx.Game).Where(x => Run.PortalIds.Contains(x.Id)).OrderBy(x => x.DistancePlayer).FirstOrDefault();
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
        if (!lootDefense && Run.BossesCompleted && AwakeningBossTracker.Complete(Run))
        { SetPhase(AwakeningPhase.Loot, "roster_search_done"); return; }
        if (!lootDefense && !Run.BossesCompleted && AwakeningBossTracker.AllTrackedDead(Run) && Vector2.Distance(ctx.Game.Player.GridPosNum, EncounterCenter()) <= 80)
        { CompleteBosses(ctx); return; }
        if (Run.Bosses.Values.Any(b => b.Life == BossLife.Alive && Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y)) < ctx.Settings.Build.CombatRange.Value))
        {
            ctx.Navigation.Stop(ctx.Game); _damage.Reset(now); _bossPositionSince = now;
            SetPhase(AwakeningPhase.Fight, "boss_priority_over_defensive_clear"); return;
        }
        if (now < _scoutRepositionUntil && (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding)) { TravelSustain(ctx); Status = "Scout repositioning toward stalled pack"; return; }
        // Known drop site / boss position (e.g. after a death): head straight there. Only enemies close enough to hurt
        // (35 grids) are fought on the way, and a pack that takes no damage is walked past instead of chased.
        var dropSite = KnownDropSite();
        var pushing = dropSite.HasValue && Vector2.Distance(ctx.Game.Player.GridPosNum, dropSite.Value) > 45;
        // 2026-09-21: 300 s scouts were mostly "clearing nearby enemies" for packs up to 90 grid away. Bosses (not packs)
        // pay; only packs that can reach us (50 grid) are fought, the kite/escape layer handles the rest.
        var clearRadius = pushing ? 35f : 50f;
        // Stragglers that take no damage (captured beasts at 1 HP, immune crystals...) are ignored for the rest of the map.
        var enemies = ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster &&
            e.IsAlive && e.IsHostile && e.IsTargetable && !_scoutIgnored.Contains(e.Id) &&
            !(_pushIgnoredUntil.TryGetValue(e.Id, out var until) && until > now) &&
            (e.GetComponent<Life>()?.CurHP ?? 0) > 1 && Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum) < clearRadius).ToList();
        var nearby = enemies.Count;
        if (nearby > 0)
        {
            if (!_defensiveClear) { _scoutDamage.Reset(now); _scoutPositionSince = now; _log.Event(Run, "scout.defensive_clear", new { nearby }); }
            _defensiveClear = true;
            _scoutDamage.Observe(enemies.Select(e => new MonsterHealthSample(e.Id,
                (double)(e.GetComponent<Life>()?.CurHP ?? 0) + (e.GetComponent<Life>()?.CurES ?? 0))), now);
            if (_scoutDamage.NoProgressSeconds(now) >= ctx.Settings.Awakening.NoDamageSeconds.Value || (now - _scoutPositionSince).TotalSeconds >= 12)
            {
                if (pushing)
                {
                    foreach (var e in enemies) _pushIgnoredUntil[e.Id] = now.AddSeconds(6);
                    _log.Event(Run, "scout.push_to_drops", new { enemies = enemies.Count, x = dropSite!.Value.X, y = dropSite.Value.Y,
                        distance = Vector2.Distance(ctx.Game.Player.GridPosNum, dropSite.Value) });
                    _defensiveClear = false; ctx.Combat.Suspend(); _destination = null;
                    Navigate(ctx, dropSite.Value); return;
                }
                if (enemies.Count <= 2 && !enemies.Any(e => e.Rarity is MonsterRarity.Rare or MonsterRarity.Unique))
                {
                    // Chasing one or two harmless stragglers costs more time than it saves: keep exploring.
                    foreach (var e in enemies) _scoutIgnored.Add(e.Id);
                    _log.Event(Run, "scout.ignore_stragglers", new { ids = enemies.Select(e => e.Id).ToArray(), names = enemies.Select(e => e.RenderName).ToArray() });
                    _defensiveClear = false; ctx.Combat.Suspend();
                    return;
                }
                var closest = enemies.OrderBy(e => Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum)).First();
                // 2026-09-21: one undamageable rare kept the scout "clearing (1)" for 40+ s. After two repositions
                // without any HP progress it is walked past for the rest of the map (bosses are what pays).
                var stalls = _scoutStalls[closest.Id] = _scoutStalls.GetValueOrDefault(closest.Id) + 1;
                if (stalls > 2 && closest.Rarity != MonsterRarity.Unique)
                {
                    foreach (var e in enemies.Where(e => _scoutStalls.GetValueOrDefault(e.Id) > 2 || e.Rarity != MonsterRarity.Unique)) _scoutIgnored.Add(e.Id);
                    _log.Event(Run, "scout.ignore_stubborn", new { closest.Id, closest.RenderName, closest.Path, stalls, rarity = closest.Rarity.ToString(), nearby = enemies.Count });
                    _defensiveClear = false; ctx.Combat.Suspend(); return;
                }
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
        if (!lootDefense && EnRouteLootAvailable(ctx))
        { ctx.Navigation.Stop(ctx.Game); _destination = null; _enRouteLoot = true; SetPhase(AwakeningPhase.Loot, "en_route_valuable_drop"); return; }
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
        if (!Run.BossesCompleted && AwakeningBossTracker.AllTrackedDead(Run)) { CompleteBosses(ctx); return; }
        // Tile entities may expose a boss/icon beyond the regular network bubble.
        var hint = ctx.Game.IngameState.Data.TileEntities?.FirstOrDefault(e => e?.Path != null && AwakeningBossTracker.Classify(e.Path, e.RenderName ?? "").HasValue
            && !Run.Visited.Any(p => Vector2.Distance(new(p[0], p[1]), e.GridPosNum) < 25));
        if (hint != null) { Navigate(ctx, hint.GridPosNum); return; }
        Explore(ctx);
    }
    private static bool IsBearer(Entity e)
    {
        if ((e.Path?.Contains("bearer", StringComparison.OrdinalIgnoreCase) ?? false) || (e.RenderName?.Contains("bearer", StringComparison.OrdinalIgnoreCase) ?? false)) return true;
        try { return e.GetComponent<ObjectMagicProperties>()?.Mods?.Any(m => m != null && m.Contains("bearer", StringComparison.OrdinalIgnoreCase)) == true; } catch { return false; }
    }
    private List<(Vector2 Pos, float Radius, string Kind)> _hazardCache = new();
    private DateTime _hazardCacheAt;
    private List<(Vector2 Pos, float Radius, string Kind)> Hazards(BotContext ctx)
    {
        if ((DateTime.UtcNow - _hazardCacheAt).TotalMilliseconds < 200) return _hazardCache;
        _hazardCacheAt = DateTime.UtcNow;
        var list = new List<(Vector2 Pos, float Radius, string Kind)>();
        _hazardCache = list;
        var gc = ctx.Game; var player = gc.Player.GridPosNum;
        try
        {
            if (gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Effect, out var effects))
                foreach (var e in effects)
                {
                    if (e?.Path == null || !e.IsHostile || Vector2.Distance(player, e.GridPosNum) > 80) continue;
                    string? kind = null;
                    if (e.Path.Contains("ground_effects", StringComparison.OrdinalIgnoreCase)) kind = "ground:" + e.Path.Split('/').Last().Split('@')[0];
                    else if (e.Path.Contains("ServerEffect", StringComparison.OrdinalIgnoreCase))
                    {
                        var animation = e.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Path;
                        if (animation != null && DangerAnimation.IsMatch(animation)) kind = "effect:" + animation;
                    }
                    else if (IsBearer(e)) kind = "bearer_effect:" + e.Path;
                    if (kind == null) continue;
                    var size = e.GetComponent<Positioned>()?.Size ?? 10;
                    list.Add((e.GridPosNum, Math.Clamp((float)size, 8f, kind.StartsWith("ground:", StringComparison.Ordinal) ? 22f : 40f), kind));
                }
            if (gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Daemon, out var daemons))
                foreach (var e in daemons)
                    if (e?.Path?.Contains("UberMapExarchDaemon") == true && e.IsHostile && Vector2.Distance(player, e.GridPosNum) < 80)
                        list.Add((e.GridPosNum, Math.Clamp((float)(e.GetComponent<Positioned>()?.Size ?? 15), 10f, 40f), "exarch_daemon"));
            // "Bearer" monsters (name, metadata path or monster mod) hurt a lot around them; any non-effect entity so a
            // corpse/remnant that keeps a Bearer path is avoided as well.
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                if (e.Type != EntityType.Effect && e.Type != EntityType.Player && Vector2.Distance(player, e.GridPosNum) < 80 &&
                    (e.Type != EntityType.Monster || (e.IsAlive && e.IsHostile)) && IsBearer(e))
                    list.Add((e.GridPosNum, BearerAvoidRadius, "bearer:" + (e.RenderName ?? e.Path)));
        }
        catch { }
        foreach (var h in list)
            if (_hazardKinds.Add(h.Kind)) _log.Event(Run, "hazard.observed", new { h.Kind, h.Radius, x = h.Pos.X, y = h.Pos.Y });
        return list;
    }
    // Returns true when this tick was spent getting out of danger.
    private bool SafetyTick(BotContext ctx)
    {
        var gc = ctx.Game; var now = DateTime.UtcNow;
        if (gc.Player == null || !gc.Player.IsAlive) return false;
        var player = gc.Player.GridPosNum;
        var life = gc.Player.GetComponent<Life>();
        float pool = (life?.CurHP ?? 0) + (life?.CurES ?? 0), max = (life?.MaxHP ?? 0) + (life?.MaxES ?? 0);
        _poolHistory.Enqueue((now, pool));
        while (_poolHistory.Count > 0 && (now - _poolHistory.Peek().At).TotalSeconds > 1.2) _poolHistory.Dequeue();
        var peak = _poolHistory.Max(x => x.Pool);
        var burst = max > 0 && (peak - pool) / max >= BurstFraction;
        var hazards = Hazards(ctx);
        // Ground degen and Bearers: keep a wider margin so the character steps away before standing in them.
        // While picking up loot with a healthy pool (>= 70 %), brief contact with degen ground is accepted (it is damage over time).
        var healthy = max > 0 && pool / max >= 0.7f;
        // 2026-09-21 13:17: maps with "patches of ... ground" put radius-40 FillGroundEffects everywhere and the scout
        // escaped every 1–2 s with 0 % pool loss. Ground only counts when it is actually hurting (>= 5 % in 1.2 s) or the pool is low.
        var hurting = max > 0 && ((peak - pool) / max >= 0.05f || pool / max < 0.5f);
        var inside = hazards.Where(h =>
        {
            var ground = h.Kind.StartsWith("ground:", StringComparison.Ordinal);
            if (ground && !hurting) return false;
            if (ground && Run.Phase == AwakeningPhase.Loot && healthy) return false;
            var margin = ground || h.Kind.StartsWith("bearer", StringComparison.Ordinal) ? 6 : 3;
            return Vector2.Distance(player, h.Pos) < h.Radius + margin;
        }).ToList();
        if (now < _escapeUntil && (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding))
        { TravelSustain(ctx); Status = "Escaping danger"; return true; }
        // 2026-09-21 death.context: the deaths were melee packs (Void Skulker / Carnage Chieftain / Seething Brine)
        // standing 4–15 grid from the Spark character, not ground. Kite before the burst: 4+ living enemies within 12
        // grid (or a rare/unique within 8) → step away, at most every 2.5 s.
        var crowdNow = gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.IsAlive && e.IsHostile && e.IsTargetable)
            .Select(e => (e, d: Vector2.Distance(player, e.GridPosNum))).ToList();
        var crowded = crowdNow.Count(x => x.d < 12) >= 4 || crowdNow.Any(x => x.d < 8 && x.e.Rarity is MonsterRarity.Rare or MonsterRarity.Unique);
        var kite = crowded && !burst && inside.Count == 0 && (now - _lastKiteAt).TotalSeconds >= 2.5 && Run.Phase is not AwakeningPhase.MapBoss;
        if (inside.Count == 0 && !burst && !kite) return false;
        if (kite) _lastKiteAt = now;
        var away = Vector2.Zero;
        foreach (var h in inside)
        {
            var d = player - h.Pos;
            away += d.Length() > 0.5f ? Vector2.Normalize(d) : new Vector2(1, 0);
        }
        var threats = gc.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.IsAlive && e.IsHostile &&
            Vector2.Distance(player, e.GridPosNum) < 40).Select(e => e.GridPosNum).ToList();
        if ((burst || kite) && threats.Count > 0)
        {
            var centroid = new Vector2(threats.Average(p => p.X), threats.Average(p => p.Y));
            var d = player - centroid;
            if (d.Length() > 0.5f) away += Vector2.Normalize(d) * 1.5f;
        }
        if (away.Length() < 0.1f) away = new Vector2(1, 0);
        away = Vector2.Normalize(away);
        Vector2? best = null; var bestScore = float.MinValue;
        for (var k = -3; k <= 3; k++)
            for (var dist = 20; dist <= 40; dist += 10)
            {
                var angle = MathF.Atan2(away.Y, away.X) + k * MathF.PI / 8;
                var walk = ctx.Navigation.FindNearestWalkable(gc, player + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist, 5);
                if (!walk.HasValue || hazards.Any(h => Vector2.Distance(walk.Value, h.Pos) < h.Radius + 4)) continue;
                var crowd = threats.Count(t => Vector2.Distance(t, walk.Value) < 20);
                var score = -Math.Abs(k) * 2 - crowd * 3 + dist * 0.1f;
                if (score > bestScore) { bestScore = score; best = walk; }
            }
        if (!best.HasValue) return false;
        var blinked = (burst || inside.Count > 0) && TryEscapeBlink(ctx, best.Value);
        if (kite) { _escapeUntil = now.AddSeconds(0.8); ctx.Navigation.Stop(gc); _destination = null; Navigate(ctx, best.Value); _runKites++;
            Status = "Kiting away from a close pack"; return true; }
        ctx.Interaction.Cancel(gc); if (_lootId != 0) _lootInterrupted = true;
        ctx.Navigation.Stop(gc); _destination = null;
        Navigate(ctx, best.Value);
        _escapeUntil = now.AddSeconds(1.2);
        _runEscapes++;
        if (burst) LogThreatContext(ctx, "safety.burst_context");
        _log.Event(Run, "safety.escape", new { burst, poolLostPct = max > 0 ? Math.Round((peak - pool) / max * 100) : 0,
            hazards = inside.Select(h => h.Kind).Distinct().ToArray(), blinked, x = best.Value.X, y = best.Value.Y, Run.Phase });
        Status = blinked ? "Escaping danger (movement skill)" : "Escaping danger";
        return true;
    }
    private bool TryEscapeBlink(BotContext ctx, Vector2 target)
    {
        if ((DateTime.UtcNow - _lastBlinkAt).TotalMilliseconds < 600) return false;
        var skill = ctx.Combat.MovementSkills.FirstOrDefault(m => m.IsReady &&
            (DateTime.Now - m.LastUsedAt).TotalMilliseconds >= m.MinCastIntervalMs);
        if (skill == null) return false;
        var screen = AutoExile.Systems.Pathfinding.GridToScreen(ctx.Game, target);
        var window = ctx.Game.Window.GetWindowRectangle();
        if (screen.X < 20 || screen.Y < 20 || screen.X > window.Width - 20 || screen.Y > window.Height - 20) return false;
        if (!BotInput.CursorPressKey(new Vector2(window.X + screen.X, window.Y + screen.Y), skill.Key)) return false;
        skill.LastUsedAt = DateTime.Now; _lastBlinkAt = DateTime.UtcNow;
        return true;
    }
    private Vector2? KnownDropSite()
    {
        if (Run.DropSite is { Length: 2 } site) return new Vector2(site[0], site[1]);
        var unswept = Run.Bosses.Values.Where(b => b.Life == BossLife.DeadConfirmed && !Run.LootSweptBosses.Contains(b.Id)).ToList();
        if (unswept.Count > 0) return new Vector2(unswept.Average(b => b.X), unswept.Average(b => b.Y));
        var pending = Run.Bosses.Values.Where(b => b.Life != BossLife.DeadConfirmed).ToList();
        if (pending.Count > 0) return new Vector2(pending.Average(b => b.X), pending.Average(b => b.Y));
        return null;
    }
    // Drops worth at least EnRouteMinChaos (or mandatory Maven items) within EnRouteRadius, checked once a second.
    private bool EnRouteLootAvailable(BotContext ctx)
    {
        var now = DateTime.UtcNow;
        if ((now - _enRouteScanAt).TotalSeconds < 1) return false;
        _enRouteScanAt = now;
        var player = ctx.Game.Player.GridPosNum;
        var threshold = Math.Max(EnRouteMinChaos, ctx.Settings.Awakening.MinStackChaos.Value);
        foreach (var e in ctx.Entities.WorldItems)
        {
            if (Vector2.Distance(player, e.GridPosNum) > EnRouteRadius || Run.LootReceipts.Contains(Run.Instance + ":" + e.Id) || _lootSkipped.Contains(e.Id)) continue;
            try
            {
                var item = e.GetComponent<WorldItem>()?.ItemEntity;
                if (item?.IsValid != true) continue;
                var name = AwakeningGameReader.Name(ctx.Game, item);
                var price = ctx.NinjaPrice.GetPrice(ctx.Game, item);
                double? value = price.MatchCount > 0 && price.MinChaosValue > 0 ? price.MinChaosValue : null;
                if (AwakeningLootPolicy.ShouldLoot(name, item.Path, value, threshold))
                {
                    _log.Event(Run, "loot.en_route", new { e.Id, name, stackChaos = value, distance = Vector2.Distance(player, e.GridPosNum) });
                    return true;
                }
            }
            catch { }
        }
        return false;
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
        target = SafeDestination(ctx, target);
        TravelSustain(ctx);
        if (ctx.Navigation.IsPathfinding) return;
        if (_destination.HasValue && Vector2.Distance(target, _destination.Value) < 12 && ctx.Navigation.IsNavigating) return;
        _destination = target;
        _lastProgress = DateTime.UtcNow; _lastPosition = ctx.Game.Player.GridPosNum;
        if (!ctx.Navigation.NavigateTo(ctx.Game, target)) ctx.Exploration.MarkRegionFailed(target);
        _log.Event(Run, "navigation.requested", new { target.X, target.Y, ctx.Navigation.PathfindingStatus });
    }
    // Positioning targets (scout, drop site, reposition) are moved out of degen ground / Bearer areas.
    // Item pickups are exempt: the item itself decides where we must stand (SafetyTick guards the pool there).
    private bool _navExact;
    public const float LootApproachDistance = 28;
    private Vector2 SafeDestination(BotContext ctx, Vector2 target)
    {
        if (_navExact || (Run.Phase == AwakeningPhase.Loot && _lootId != 0)) return target;
        List<(Vector2 Pos, float Radius, string Kind)> hazards;
        try { hazards = Hazards(ctx).Where(h => h.Kind.StartsWith("bearer", StringComparison.Ordinal) || h.Kind == "exarch_daemon").ToList(); }
        catch { return target; }
        if (hazards.Count == 0) return target;
        for (var pass = 0; pass < 3; pass++)
        {
            var hit = hazards.FirstOrDefault(h => Vector2.Distance(target, h.Pos) < h.Radius + 6);
            if (hit.Kind == null) return target;
            var dir = target - hit.Pos; if (dir.Length() < 0.5f) dir = ctx.Game.Player.GridPosNum - hit.Pos;
            if (dir.Length() < 0.5f) dir = new Vector2(1, 0);
            var moved = hit.Pos + Vector2.Normalize(dir) * (hit.Radius + 10);
            var walk = ctx.Navigation.FindNearestWalkable(ctx.Game, moved, 6);
            if (!walk.HasValue) return target;
            if (pass == 0) _log.Event(Run, "safety.destination_moved", new { from = new[] { target.X, target.Y }, to = new[] { walk.Value.X, walk.Value.Y }, hit.Kind });
            target = walk.Value;
        }
        return target;
    }
    // Forensics for the improvement loop: what was around the character on a damage burst or a death.
    private DateTime _threatLogAt;
    private void LogThreatContext(BotContext ctx, string name)
    {
        if (name != "death.context" && (DateTime.UtcNow - _threatLogAt).TotalSeconds < 3) return;
        _threatLogAt = DateTime.UtcNow;
        try
        {
            var gc = ctx.Game; var player = gc.Player.GridPosNum;
            var near = gc.EntityListWrapper.OnlyValidEntities
                .Where(e => e.Type is EntityType.Monster or EntityType.Effect or EntityType.Daemon && Vector2.Distance(player, e.GridPosNum) < 50)
                .OrderBy(e => Vector2.Distance(player, e.GridPosNum)).Take(14)
                .Select(e =>
                {
                    string[] mods = [];
                    try { mods = e.GetComponent<ObjectMagicProperties>()?.Mods?.Take(8).ToArray() ?? []; } catch { }
                    string? anim = null;
                    try { anim = e.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Path; } catch { }
                    return new { type = e.Type.ToString(), e.RenderName, e.Path, e.IsAlive, e.IsHostile, rarity = e.Rarity.ToString(),
                        d = Math.Round(Vector2.Distance(player, e.GridPosNum)), size = e.GetComponent<Positioned>()?.Size, mods, anim };
                }).ToArray();
            var life = gc.Player.GetComponent<Life>();
            _log.Event(Run, name, new { Run.Phase, hp = life?.CurHP, es = life?.CurES, x = player.X, y = player.Y,
                hazards = Hazards(ctx).Select(h => new { h.Kind, h.Radius, d = Math.Round(Vector2.Distance(player, h.Pos)) }).ToArray(), near });
        }
        catch (Exception ex) { _log.Event(Run, name, new { error = ex.Message }); }
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
        Run.BossesCompleted = true; _enRouteLoot = false; ctx.Combat.Suspend(); ctx.Navigation.Stop(ctx.Game);
        if (Run.DropSite == null) { var c = EncounterCenter(); Run.DropSite = [c.X, c.Y]; }
        SetPhase(AwakeningPhase.Loot, "encounter_deaths_confirmed");
    }
    private void Fight(BotContext ctx, bool regular)
    {
        var now = DateTime.UtcNow;
        if (!regular && AwakeningBossTracker.AllTrackedDead(Run)) { CompleteBosses(ctx); return; }
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
    /// <summary>
    /// Only enemies close to the drops interrupt pickup, and the response is Spark in place.
    /// The previous 60-grid "chase the pack" defense pulled the bot 170 grids away from a
    /// pile of Maven's Chisels (2026-09-21 09:51) and got it killed before they were taken.
    /// Enemies that take no damage for several seconds never hold the loot hostage.
    /// </summary>
    private bool LootDefense(BotContext ctx)
    {
        var now = DateTime.UtcNow;
        var player = ctx.Game.Player.GridPosNum;
        float radius = ctx.Settings.Awakening.LootDefenseRadius.Value;
        var threats = ctx.Game.EntityListWrapper.OnlyValidEntities.Where(e => e.Type == EntityType.Monster && e.IsAlive && e.IsHostile &&
            e.IsTargetable && Vector2.Distance(player, e.GridPosNum) < radius).ToList();
        if (threats.Count == 0)
        {
            if (_lootDefenseActive)
            {
                _lootDefenseActive = false; ctx.Combat.Suspend();
                _log.Event(Run, "loot.defense_clear", new { seconds = (now - _lootDefenseStarted).TotalSeconds });
            }
            return false;
        }
        if (now < _lootDefenseSuppressedUntil) return false;
        if (!_lootDefenseActive)
        {
            _lootDefenseActive = true; _lootDefenseStarted = now; _lootDefenseDamage.Reset(now);
            _log.Event(Run, "loot.defense_started", new { threats = threats.Count, radius, x = player.X, y = player.Y });
        }
        _lootDefenseDamage.Observe(threats.Select(e => new MonsterHealthSample(e.Id,
            (double)(e.GetComponent<Life>()?.CurHP ?? 0) + (e.GetComponent<Life>()?.CurES ?? 0))), now);
        if (_lootDefenseDamage.NoProgressSeconds(now) >= Math.Max(6.0, ctx.Settings.Awakening.NoDamageSeconds.Value) ||
            (now - _lootDefenseStarted).TotalSeconds >= 15)
        {
            _lootDefenseActive = false; _lootDefenseSuppressedUntil = now.AddSeconds(10); ctx.Combat.Suspend();
            _log.Event(Run, "loot.defense_suppressed", new { threats = threats.Count, noProgressSeconds = _lootDefenseDamage.NoProgressSeconds(now),
                seconds = (now - _lootDefenseStarted).TotalSeconds });
            return false;
        }
        ctx.Interaction.Cancel(ctx.Game); if (_lootId != 0) _lootInterrupted = true;
        if (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) ctx.Navigation.Stop(ctx.Game);
        ctx.Combat.Profile.Enabled = true;
        ctx.Combat.Profile.SustainEnemyChannelWithoutTarget = ctx.Combat.HasEnemyChannel;
        ctx.Combat.SuppressPositioning = true; ctx.Combat.SuppressTargetedSkills = false;
        ctx.Combat.Tick(ctx);
        Status = "Loot defense: Spark on " + threats.Count + " enemies within " + radius + " grids"; Decision = "Loot defense (hold position)";
        return true;
    }
    // Walk to a nearby spot that shifts the (blocked) label towards the screen centre. Labels move with the camera,
    // so the label lands at labelPos + (screen(player) - screen(candidate)).
    private bool TryUnblockLabel(BotContext ctx, long id)
    {
        var attempts = _unblockAttempts.GetValueOrDefault(id);
        if (attempts >= 3) return false;
        try
        {
            var gc = ctx.Game;
            var player = gc.Player.GridPosNum;
            var window = gc.Window.GetWindowRectangle();
            var centre = new Vector2(window.Width / 2f, window.Height * 0.45f);
            var playerScreen = AutoExile.Systems.Pathfinding.GridToScreen(gc, player);
            var world = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Id == id);
            var label = gc.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?.FirstOrDefault(l => l.Entity?.Id == id);
            Vector2 labelPos;
            if (label != null) { var r = label.ClientRect; labelPos = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2); }
            else if (world != null) labelPos = AutoExile.Systems.Pathfinding.GridToScreen(gc, world.GridPosNum);
            else return false;
            Vector2? best = null; var bestScore = float.MaxValue;
            for (var radius = 10; radius <= 40; radius += 10)
                for (var k = 0; k < 16; k++)
                {
                    var angle = k * MathF.PI / 8;
                    var walk = ctx.Navigation.FindNearestWalkable(gc, player + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, 4);
                    if (!walk.HasValue) continue;
                    var shifted = labelPos + (playerScreen - AutoExile.Systems.Pathfinding.GridToScreen(gc, walk.Value));
                    var score = Vector2.Distance(shifted, centre) + Vector2.Distance(walk.Value, player) * 0.5f;
                    if (score < bestScore) { bestScore = score; best = walk; }
                }
            if (!best.HasValue) return false;
            _unblockAttempts[id] = attempts + 1;
            _unblockUntil = DateTime.UtcNow.AddSeconds(2.5);
            ctx.Navigation.Stop(gc); _destination = null;
            Navigate(ctx, best.Value);
            _log.Event(Run, "loot.unblock_reposition", new { id, attempt = attempts + 1, labelX = labelPos.X, labelY = labelPos.Y,
                x = best.Value.X, y = best.Value.Y, labelFound = label != null });
            return true;
        }
        catch { return false; }
    }
    private void Loot(BotContext ctx)
    {
        if (_enRouteLoot && Run.Bosses.Values.Any(b => b.Life == BossLife.Alive && Vector2.Distance(ctx.Game.Player.GridPosNum, new(b.X, b.Y)) < ctx.Settings.Build.CombatRange.Value))
        { ctx.Interaction.Cancel(ctx.Game); _lootId = 0; _enRouteLoot = false; SetPhase(AwakeningPhase.Scout, "boss_interrupts_en_route_loot"); return; }
        if (LootDefense(ctx)) return;
        ctx.Combat.Suspend();
        if (DateTime.UtcNow < _unblockUntil && (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding))
        { Status = "Moving so the loot label is clear of the HUD"; return; }
        if (ctx.Loot.TickLabelToggle(ctx.Game)) { Status = ctx.Loot.ToggleStatus; return; }
        if (_lootId != 0)
        {
            var inv = AwakeningGameReader.Inventory(ctx.Game);
            var stillOnGround = ctx.Game.EntityListWrapper.OnlyValidEntities.Any(e => e.Id == _lootId && e.Type == EntityType.WorldItem);
            var confirmed = AwakeningLootPolicy.PickupConfirmed(inv, _lootPath, _lootBefore, _lootQuantity, stillOnGround);
            if (!confirmed && _lootInterrupted)
            {
                // Loot defense cancelled this pickup before it completed: request it again, without counting a failure.
                _lootInterrupted = false; _lootId = 0; return;
            }
            _lootInterrupted = false;
            if (confirmed)
            {
                var receipt = Run.Instance + ":" + _lootId;
                if (!Run.LootReceipts.Contains(receipt))
                {
                    Run.LootReceipts.Add(receipt); Run.RevenueChaos += _lootPrice;
                    if (_lootPrice <= 0) Run.RevenueKnown = false;
                    Run.UnresolvedLoot.Remove(_lootName);
                    if (_lootName.Contains("Incandescent Invitation", StringComparison.OrdinalIgnoreCase)) Run.InvitationLooted = true;
                    _log.Event(Run, "loot.confirmed", new { _lootId, _lootName, _lootPath, quantity = _lootQuantity, chaos = _lootPrice });
                    _runLoot.Add(new { name = _lootName, quantity = _lootQuantity, chaos = _lootPrice, phase = _enRouteLoot ? "en_route" : "boss" });
                    if (ctx.Settings.Awakening.Economy.LedgerEnabled.Value) _ledger.AddLoot(_lootName, _lootQuantity, _lootPrice);
                    Run.UnresolvedLoot.RemoveAll(name => name == _lootName);
                    Supervisor.Save();
                }
                ctx.Interaction.Cancel(ctx.Game); _lootId = 0; _quietAt = DateTime.MinValue;
            }
            else
            {
                var interactionResult = ctx.Interaction.Tick(ctx.Game);
                if (interactionResult == InteractionResult.Failed)
                {
                    _log.Event(Run, "loot.interaction_failed", new { _lootId, _lootName, ctx.Interaction.LastFailReason });
                    // A label under the HUD never becomes clickable by waiting: move the camera instead.
                    if (ctx.Interaction.LastFailReason == "blocked by UI" && TryUnblockLabel(ctx, _lootId))
                    { ctx.Interaction.Cancel(ctx.Game); _lootId = 0; return; }
                }
                // A failed interaction is final; only an in-progress pickup deserves the full 12 seconds.
                if (interactionResult != InteractionResult.Failed && (DateTime.UtcNow - _lootStarted).TotalSeconds < 12) return;
                _log.Event(Run, "loot.pickup_timeout", new { _lootId, _lootName, _lootBefore, _lootQuantity, stillOnGround,
                    inventoryCount = inv.Counts.GetValueOrDefault(_lootPath), ctx.Interaction.Status, ctx.Interaction.LastFailReason, ctx.Navigation.LastRecoveryAction,
                    rawInput = BotInput.RecentRawInputDiagnostics });
                ctx.Interaction.Cancel(ctx.Game); _lootAttempts[_lootId] = _lootAttempts.GetValueOrDefault(_lootId) + 1;
                if (_lootAttempts[_lootId] >= 3)
                {
                    // Hourly rate: one stubborn item must not end the map. Record it and move on.
                    _lootSkipped.Add(_lootId); Run.UnresolvedLoot.Add(_lootName);
                    _log.Event(Run, "loot.skipped", new { _lootId, _lootName, _lootPrice, attempts = _lootAttempts[_lootId] });
                    _lootId = 0; return;
                }
                if (ctx.Settings.Loot.LabelToggleUnstick.Value && BotInput.CanAct)
                { ctx.Loot.StartLabelToggle(ctx.Game); _log.Event(Run, "loot.label_refresh", new { _lootId }); }
                _lootId = 0; return;
            }
        }
        var candidates = new List<(Entity World, Entity Item, string Name, double Price, int Quantity)>();
        bool unresolved = false;
        foreach (var e in ctx.Entities.WorldItems)
        {
            if (Run.LootReceipts.Contains(Run.Instance + ":" + e.Id) || _lootSkipped.Contains(e.Id)) continue;
            if (_enRouteLoot && Vector2.Distance(ctx.Game.Player.GridPosNum, e.GridPosNum) > EnRouteRadius) continue;
            try
            {
                var item = e.GetComponent<WorldItem>()?.ItemEntity;
                if (item?.IsValid != true) { unresolved |= AwaitLootMetadata(e.Id); continue; }
                var name = AwakeningGameReader.Name(ctx.Game, item);
                var price = ctx.NinjaPrice.GetPrice(ctx.Game, item);
                double? value = price.MatchCount > 0 && price.MinChaosValue > 0 ? price.MinChaosValue : null;
                var take = AwakeningLootPolicy.ShouldLoot(name, item.Path, value, _enRouteLoot ? Math.Max(EnRouteMinChaos, ctx.Settings.Awakening.MinStackChaos.Value) : ctx.Settings.Awakening.MinStackChaos.Value);
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
        // Value per travel distance: if the run ends mid-pickup, the cheapest drops are the ones left behind.
        var candidate = candidates.OrderByDescending(x => Math.Max(x.Price, AwakeningLootPolicy.Mandatory(x.Name, x.Item.Path) ? 5.0 : 0.0) /
            (Vector2.Distance(ctx.Game.Player.GridPosNum, x.World.GridPosNum) + 15)).FirstOrDefault();
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
            // 2026-09-21: drops 58–76 grid away were requested from where the character stood; the label was off-screen or
            // hidden, the interaction never clicked ("timeout 5s, 0 clicks") and 100c of Chisels/Splinters were skipped.
            // Walk up to the drop first, then pick it up with its label on screen.
            var dropDistance = Vector2.Distance(ctx.Game.Player.GridPosNum, candidate.World.GridPosNum);
            if (dropDistance > LootApproachDistance)
            {
                _navExact = true; Navigate(ctx, candidate.World.GridPosNum); _navExact = false;
                Status = $"Walking to {candidate.Name} ({dropDistance:0})"; return;
            }
            if (ctx.Navigation.IsNavigating || ctx.Navigation.IsPathfinding) { ctx.Navigation.Stop(ctx.Game); _destination = null; }
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
        if (_enRouteLoot)
        {
            _enRouteLoot = false;
            SetPhase(AwakeningPhase.Scout, "en_route_loot_done"); return;
        }
        if (unresolved) { _quietAt = DateTime.MinValue; Status = "Loot memory still hydrating"; return; }
        if (Run.DropSite is { Length: 2 } drop && !_dropSiteReached)
        {
            var site = new Vector2(drop[0], drop[1]);
            if (Vector2.Distance(ctx.Game.Player.GridPosNum, site) > 40)
            { Navigate(ctx, site); _quietAt = DateTime.MinValue; Status = "Returning to the pinnacle drop site"; return; }
            _dropSiteReached = true;
        }
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
        // 2026-09-21: the map boss (Exarch invitation) killed the character 4 times in a row on one map and burned the portals.
        // After 2 deaths on this map the invitation is given up: bank the pinnacle loot and leave instead.
        if (Run.Invitation == InvitationDecision.Yes && !Run.MapBossKilled && Run.Deaths >= 2)
        {
            if (!_mapBossSkipped) { _mapBossSkipped = true; _log.Event(Run, "mapboss.skipped", new { Run.Deaths, reason = "deaths_on_this_map" }); }
        }
        else if (Run.Invitation == InvitationDecision.Yes && !Run.MapBossKilled) { _damage.Reset(DateTime.UtcNow); SetPhase(AwakeningPhase.MapBoss, "Exarch_invitation_due"); return; }
        if (Run.Invitation == InvitationDecision.Yes && Run.MapBossKilled && !Run.InvitationLooted) { Finish(ctx, AttemptOutcome.OperationalFailure, "invitation_drop_not_confirmed"); return; }
        if (!AwakeningBossTracker.AllTrackedDead(Run)) { SetPhase(AwakeningPhase.Scout, "encounter_requires_recheck"); return; }
        if (!AwakeningBossTracker.Complete(Run, DateTime.UtcNow, out var basis))
        {
            if (AwakeningBossTracker.RosterShort(Run, out var missing))
            {
                // The drops of the bosses we found are collected; the rest of the group is still somewhere else.
                _log.Event(Run, "encounter.roster_short", new { missing, bosses = Run.Bosses.Values.Select(b => b.Member).ToArray() });
                SetPhase(AwakeningPhase.Scout, "roster_short_search"); return;
            }
            Status = "Drops collected; waiting for the encounter to settle (no new boss for " + AwakeningBossTracker.SettleSeconds + "s)"; return;
        }
        _log.Event(Run, "encounter.complete", new { basis, bosses = Run.Bosses.Values.Select(b => b.Member).ToArray() });
        Run.DropSite = null;
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
                ContinueAfterDeath(ctx);
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
    private bool _mapBankStarted, _mapBankDone;
    private bool IsBankableMap(BotContext ctx, Entity? item)
    {
        try
        {
            if (item?.Path?.Contains("MapKeyTier16") != true) return false;
            return AwakeningMapPolicy.Rejections(AwakeningGameReader.ReadMap(ctx.Game, item, "Dunes")).Count == 0;
        }
        catch { return false; }
    }
    private void ExternalStash(BotContext ctx)
    {
        if (ctx.Game.IngameState.IngameUi.StashElement?.IsVisible != true)
        {
            if (_mapBankStarted && !_externalIssued) { SetPhase(AwakeningPhase.OpenStash, "reopen_stash_after_map_bank"); return; }
            Status = "Stash closed during external operation"; return;
        }
        // Map banking: StashieV2 sends maps to the MAP (map stash) tab, which RestockMap cannot read. Acceptable T16
        // maps (dropped or bought in bulk) are first stored in "Tmp" so the next map opens without a market trip.
        if (!_externalIssued && !_mapBankDone && ctx.Settings.Awakening.Economy.BankMapsInTmp.Value)
        {
            if (!_mapBankStarted)
            {
                var bankable = StashSystem.GetInventorySlotItems(ctx.Game)?.Count(i => IsBankableMap(ctx, i.Item)) ?? 0;
                if (bankable == 0) { _mapBankDone = true; return; }
                ctx.Stash.ApplyIncubators = false;
                _mapBankStarted = ctx.Stash.Start(storeTabName: "Tmp", itemFilter: i => IsBankableMap(ctx, i.Item));
                if (_mapBankStarted) _log.Event(Run, "map.bank_started", new { bankable });
                else _mapBankDone = true;
                return;
            }
            var banked = ctx.Stash.Tick(ctx.Game, ctx.Navigation); Status = "Banking maps in Tmp: " + ctx.Stash.Status;
            if (banked is StashResult.InProgress or StashResult.None) { _phaseAt = DateTime.UtcNow; return; }
            ctx.Stash.Cancel(ctx.Game, ctx.Navigation); _mapBankDone = true;
            _log.Event(Run, "map.bank_done", new { result = banked.ToString(), ctx.Stash.Status });
            return;
        }
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
