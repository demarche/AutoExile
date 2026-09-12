using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// Simulacrum farming loop:
    /// Hideout: stash items → insert simulacrum fragment → enter portal
    /// In map: find monolith → wave cycle (fight/loot/stash between waves) → exit after wave 15 or abort
    /// Death: revive (handled by BotCore) → re-enter map if portals remain
    /// </summary>
    public class SimulacrumMode : IBotMode
    {
        public string Name => "Simulacrum";

        private SimulacrumState _state = new();
        private SimPhase _phase = SimPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;

        // Settings reference
        private BotSettings.SimulacrumSettings _settings = new();

        private bool IsStationaryChannelActive =>
            _phase == SimPhase.WaveCycle &&
            _state.IsWaveActive &&
            _waveActivationConfirmed &&
            _settings.StationaryChannelDuringWave.Value;

        // Confirmation comes from observed wave state, never from merely queuing a click.
        private bool _waveActivationConfirmed;
        private DateTime _lastMonolithDiagnosticAt = DateTime.MinValue;
        private string _lastWaveObservation = "";
        private DateTime _lastMonolithClickDiagnosticAt = DateTime.MinValue;

        private Vector2? _channelPosition;
        private DateTime _channelPositionStartedAt = DateTime.MinValue;
        private bool _channelRepositioning;
        private DateTime _lastChannelDiagnosticAt = DateTime.MinValue;
        private float _channelPeakKillRate;
        private float _channelCurrentKillRate;
        private bool _channelRateEstablished;
        private const float ChannelKillRateWindowSeconds = 2f;
        private readonly SparkProgressTracker _sparkProgress = new();
        private DateTime _lastProgressSampleAt = DateTime.MinValue;
        private DateTime _repositionStartedAt = DateTime.MinValue;

        // Rolling diagnostic history — sampled every ~250ms, dumped in full on death so
        // long-running Spark stand-still/hold issues can be diagnosed after the fact.
        private readonly SparkDiagnosticSample[] _sparkDiagnosticRing = new SparkDiagnosticSample[480];
        private int _sparkDiagnosticIndex;
        private int _sparkDiagnosticCount;
        private DateTime _lastSparkDiagnosticSampleAt = DateTime.MinValue;
        private const float SparkDiagnosticSampleIntervalMs = 250f;

        // Hideout/loop tracking
        private bool _mapCompleted;
        private bool _mapAborted;
        private bool _lootSweepCompletesRun;
        private DateTime _nextStockRecheckAt = DateTime.MinValue;
        private const float StockRecheckSeconds = 60f;
        private string _lastAreaName = "";
        private int _exitPortalAttempts;
        private bool _portalKeyPressed;

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Hideout flow
        private readonly HideoutFlow _hideoutFlow = new();

        // Between-wave stash tracking
        private bool _isStashing;

        // Wave transition tracking — reset exploration seen state each wave so we re-sweep for new spawns
        private int _lastKnownWave;
        // Track whether we were searching (no monsters) last tick — reset exploration when
        // transitioning from searching → combat, so the next search re-sweeps the whole map
        private bool _wasSearching;
        private long _lastActivationSequence;
        private int _lastStatsWaveStarted;
        private int _lastStatsWaveCompleted;
        private bool _lastStatsWaveActive;

        // Wave start retry tracking — bail if we can't start the next wave
        private int _waveStartAttempts;
        private const int MaxWaveStartAttempts = 10;
        private DateTime _betweenWaveStartTime = DateTime.MinValue;
        private const float BetweenWaveTimeoutSeconds = 120f;

        // Combat stuck detection — if fighting same monsters too long, move on
        private DateTime _combatEngageTime = DateTime.MinValue;
        private int _combatEngageCount;
        private const float CombatStuckSeconds = 15f;

        // Monster blacklist — temporarily ignore monsters we can't kill so we reposition via explore
        private readonly Dictionary<long, DateTime> _blacklistedMonsters = new();
        private const float MonsterBlacklistSeconds = 10f;


        // Action cooldown
        private const float MajorActionCooldownMs = 500f;

        // Public for ImGui display
        public SimulacrumState State => _state;
        public SimPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public string Decision { get; private set; } = "";

        public void OnEnter(BotContext ctx)
        {
            _settings = ctx.Settings.Simulacrum;
            _mapCompleted = false;
            _mapAborted = false;
            _lootSweepCompletesRun = false;
            _nextStockRecheckAt = DateTime.MinValue;
            _lastAreaName = "";
            _isStashing = false;
            _lootTracker.Reset();
            _lastKnownWave = 0;
            _lastActivationSequence = ctx.MapDevice.ActivationSequence;
            _lastStatsWaveStarted = 0;
            _lastStatsWaveCompleted = 0;
            _lastStatsWaveActive = false;
            _wasSearching = false;
            _waveStartAttempts = 0;
            _betweenWaveStartTime = DateTime.MinValue;
            _exitPortalAttempts = 0;
            _portalKeyPressed = false;

            _combatEngageTime = DateTime.MinValue;
            _combatEngageCount = 0;
            _blacklistedMonsters.Clear();
            ResetChannelTracking();
            _lastChannelDiagnosticAt = DateTime.MinValue;

            // Enable combat
            ModeHelpers.EnableDefaultCombat(ctx);

            // Determine starting phase based on location
            var gc = ctx.Game;
            _waveActivationConfirmed = false;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = SimPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "In hideout — preparing";
            }
            else
            {
                // Already in a map — try to find monolith
                var areaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                ctx.Stats.BeginSimulacrumActivation($"recovered:{areaHash}",
                    gc.Area.CurrentArea.Name ?? "");
                if (areaHash != 0)
                {
                    ctx.Stats.ObserveSimulacrumEntry(areaHash, gc.Area.CurrentArea.Name ?? "");
                    // The recovered instance was accounted for above. Seed the area
                    // tracker only after a real hash was observed; otherwise the first
                    // Tick remains the retry path once the game exposes the instance.
                    _lastAreaName = gc.Area.CurrentArea.Name ?? "";
                }
                _state.Reset();
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "In map — finding monolith";

                // Initialize exploration if BotCore missed it (plugin reload mid-game)
                if (!ctx.Exploration.IsInitialized)
                {
                    var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                    var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                    if (pfGrid != null && gc.Player != null)
                    {
                        var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                            ctx.Settings.Build.BlinkRange.Value);
                    }
                }
            }
        }

        public void OnExit()
        {
            _state.Reset();
            _phase = SimPhase.Idle;
            _isStashing = false;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            if (ctx.MapDevice.ActivationSequence != _lastActivationSequence)
            {
                _lastActivationSequence = ctx.MapDevice.ActivationSequence;
                var confirmed = ctx.MapDevice.LastActivationConfirmedAtUtc ?? DateTime.UtcNow;
                ctx.Stats.BeginSimulacrumActivation(
                    $"sim:{confirmed.Ticks}:{_lastActivationSequence}",
                    gc.Area?.CurrentArea?.Name ?? "");
                var area = gc.Area?.CurrentArea;
                if (area != null && !area.IsHideout && !area.IsTown)
                {
                    var areaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                    ctx.Stats.ObserveSimulacrumEntry(areaHash, area.Name ?? "");
                }
            }

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            // Always tick state when in map; combat only during active phases
            bool inMap = gc.Area?.CurrentArea != null &&
                         !gc.Area.CurrentArea.IsHideout &&
                         !gc.Area.CurrentArea.IsTown;
            if (inMap)
            {
                RecordSparkDiagnosticSample(ctx);

                _state.Tick(gc, _settings.MinWaveDelaySeconds.Value);
                _waveActivationConfirmed = _state.HasFreshWaveState && _state.IsWaveActive;
                LogMonolithState(ctx);

                if (_state.IsWaveActive && _state.CurrentWave > _lastStatsWaveStarted)
                {
                    _lastStatsWaveStarted = _state.CurrentWave;
                    ctx.Stats.RecordWaveStarted(_state.CurrentWave);
                }
                if (_lastStatsWaveActive && !_state.IsWaveActive &&
                    _state.CurrentWave > _lastStatsWaveCompleted)
                {
                    _lastStatsWaveCompleted = _state.CurrentWave;
                    ctx.Stats.RecordWaveCompleted(_state.CurrentWave);
                }
                _lastStatsWaveActive = _state.IsWaveActive;

                var wasStationaryChannel = ctx.Combat.Profile.SustainEnemyChannelWithoutTarget;
                var emergencySpark = _phase == SimPhase.WaveCycle &&
                    !_channelRepositioning &&
                    _state.IsWaveActive &&
                    _waveActivationConfirmed &&
                    _settings.EmergencySparkBelowEs.Value &&
                    ctx.Combat.HasEnemyChannel &&
                    ctx.Combat.NearbyMonsterCount > 0 &&
                    ctx.Combat.EsPercent < 0.30f;
                var wantsSparkNow = (IsStationaryChannelActive || emergencySpark) &&
                    ctx.Combat.HasEnemyChannel &&
                    ctx.Combat.NearbyMonsterCount > 0;

                // An in-progress loot pickup suppresses every Enemy-role skill (including
                // Spark) until it finishes or times out (up to ~60s) — previously this
                // left the character standing next to live enemies with no attack and no
                // repositioning until it died. Drop the pickup and channel instead.
                if (wantsSparkNow && ctx.Interaction.IsBusy && _lootTracker.HasPending)
                {
                    ctx.Interaction.Cancel(gc);
                    _lootTracker.ClearPending();
                }

                // Only suppress normal combat positioning while actually anchored at a Spark
                // spot. While traveling between positions (_channelRepositioning), let
                // CombatSystem fight/kite freely so the character isn't defenseless mid-walk.
                var stationaryChannel = wantsSparkNow &&
                    !ctx.Interaction.IsBusy &&
                    (emergencySpark || !_channelRepositioning);
                ctx.Combat.Profile.SustainEnemyChannelWithoutTarget = stationaryChannel;

                // Disable combat during LootSweep/ExitMap — we need to navigate freely
                // to pick up remaining items and reach the portal without being dragged into fights
                bool combatAllowed = _waveActivationConfirmed &&
                    _phase != SimPhase.LootSweep && _phase != SimPhase.ExitMap;
                if (combatAllowed)
                {
                    // Suppress cursor-moving skills when interaction is busy picking up loot
                    var relocating = _settings.StationaryChannelDuringWave.Value && _channelRepositioning && !emergencySpark;
                    ctx.Combat.SuppressPositioning = stationaryChannel || ctx.Interaction.IsBusy || relocating;
                    ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy || relocating;
                    ctx.Combat.Tick(ctx);
                }
                else
                {
                    ctx.Combat.Suspend();
                }

                if (wasStationaryChannel && !stationaryChannel)
                    BotInput.ReleaseAllKeys();
            }

            // Tick interaction system
            var interactionResult = ctx.Interaction.Tick(gc);

            switch (_phase)
            {
                // --- Hideout phases ---
                case SimPhase.InHideout:
                case SimPhase.StashItems:
                case SimPhase.OpenMap:
                case SimPhase.EnterPortal:
                    // A profile/web setting can be corrected after the hideout flow has
                    // already stopped. Re-read only configuration failures here; a real
                    // empty-stash failure must remain stopped instead of retrying forever.
                    if (!_hideoutFlow.IsActive
                        && _hideoutFlow.FragmentConfigurationMissing
                        && !string.IsNullOrWhiteSpace(ctx.Settings.Stash.FragmentTabName.Value)
                        && ctx.Settings.Simulacrum.SimulacrumStock.Value > 0)
                    {
                        StartHideoutFlow(ctx);
                        StatusText = "Fragment settings updated — retrying stash";
                        break;
                    }

                    var signal = _hideoutFlow.Tick(ctx);
                    StatusText = _hideoutFlow.Status;
                    if (signal == HideoutSignal.PortalTimeout)
                    {
                        _state.Reset();
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        StartHideoutFlow(ctx);
                        StatusText = "No portal found — starting new run";
                    }
                    else if (signal == HideoutSignal.NoFragments &&
                             !_hideoutFlow.FragmentConfigurationMissing)
                    {
                        _phase = SimPhase.Done;
                        StatusText = $"Out of Simulacrums — {_state.RunsCompleted} runs completed";
                        Decision = "No Simulacrum confirmed — waiting for stock recheck";
                        _nextStockRecheckAt = DateTime.Now.AddSeconds(StockRecheckSeconds);
                    }
                    break;

                // --- Map phases ---
                case SimPhase.FindMonolith:
                    TickFindMonolith(ctx);
                    break;
                case SimPhase.NavigateToMonolith:
                    TickNavigateToMonolith(ctx);
                    break;
                case SimPhase.WaveCycle:
                    TickWaveCycle(ctx, interactionResult);
                    break;
                case SimPhase.BetweenWaveStash:
                    TickBetweenWaveStash(ctx, interactionResult);
                    break;
                case SimPhase.LootSweep:
                    TickLootSweep(ctx, interactionResult);
                    break;
                case SimPhase.ExitMap:
                    TickExitMap(ctx);
                    break;
                case SimPhase.Done:
                    // Preserve the terminal reason. Replacing an out-of-stock or
                    // failed status with "complete" makes the HUD contradict itself.
                    var doneArea = gc.Area?.CurrentArea;
                    if (doneArea != null && (doneArea.IsHideout || doneArea.IsTown))
                    {
                        var fragmentInInventory = StashSystem.CountInventoryItems(gc, FullSimulacrumPath) > 0;
                        if (fragmentInInventory || DateTime.Now >= _nextStockRecheckAt)
                        {
                            _phase = SimPhase.InHideout;
                            _phaseStartTime = DateTime.Now;
                            StartHideoutFlow(ctx);
                            StatusText = fragmentInInventory
                                ? "Simulacrum detected in inventory — resuming"
                                : "Rechecking Simulacrum stock";
                            Decision = "Stock recheck started";
                        }
                    }
                    break;
                case SimPhase.Idle:
                    StatusText = "Idle";
                    break;
            }
        }

        // =================================================================
        // Area change detection
        // =================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            // Cancel any in-flight systems
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();
            _isStashing = false;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_mapCompleted)
                {
                    // Map completed — start new cycle
                    ctx.Stats.EndSimulacrumRun(true, "wave_15_complete_and_hideout_confirmed");
                    _state.RecordRunComplete();
                    ctx.LootTracker.RecordMapComplete();
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapCompleted = false;
                    _mapAborted = false;
                    _lootTracker.ResetCount();
                    StartHideoutFlow(ctx);
                    StatusText = "Back in hideout — starting new run";
                }
                else if (_mapAborted)
                {
                    // An abort/timeout is an attempt, not a completed Simulacrum.
                    ctx.Stats.EndSimulacrumRun(false, "aborted_and_hideout_confirmed");
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapAborted = false;
                    _lootTracker.ResetCount();
                    StartHideoutFlow(ctx);
                    StatusText = "Back in hideout after aborted run — starting new attempt";
                }
                else if (_state.DeathCount > 0 && _state.DeathCount < ctx.Settings.Run.MaxDeaths.Value)
                {
                    // Died — try to re-enter
                    _phase = SimPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = $"Revived (death {_state.DeathCount}) — re-entering map";
                }
                else if (_state.DeathCount >= ctx.Settings.Run.MaxDeaths.Value)
                {
                    // Too many deaths — start fresh, but do not count a failed
                    // attempt as a completed Simulacrum.
                    ctx.Stats.EndSimulacrumRun(false, "max_deaths_reached");
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _lootTracker.ResetCount();
                    StartHideoutFlow(ctx);
                    StatusText = "Too many deaths — starting new run";
                }
                else
                {
                    // A manual/unexpected return to hideout is not a clear either.
                    ctx.Stats.EndSimulacrumRun(false, "unexpected_hideout_return");
                    _state.Reset();
                    _lootTracker.ResetCount();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    StartHideoutFlow(ctx);
                }
            }
            else
            {
                // Entered map — always reset exploration for simulacrum.
                // Simulacrum maps are small and fixed-shape, and new instances of the same map
                // share the area name + hash, so cached exploration state from a previous run
                // would make the bot think the map is already fully explored.
                var deathCount = _state.DeathCount;
                var isPortalReentry = deathCount > 0;
                _state.OnAreaChanged();
                _state.DeathCount = deathCount;
                _waveActivationConfirmed = false;
                var areaHash = gc.IngameState?.Data?.CurrentAreaHash ?? 0;
                ctx.Stats.ObserveSimulacrumEntry(areaHash, gc.Area.CurrentArea.Name ?? "");
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;

                // Force-reinitialize exploration for this new instance
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }

                // Keep current-attempt loot across death/portal re-entry.
                if (!isPortalReentry)
                    _lootTracker.ResetCount();
                StatusText = "Entered map — finding monolith";
            }
        }

        // Full Simulacrum metadata path — same item the map device consumes.
        // Splinters that combine into a Simulacrum live under a different path
        // and are NOT withdrawn here (the in-game UI auto-assembles when full).
        private const string FullSimulacrumPath = "CurrencyAfflictionFragment";

        /// <summary>
        /// Stash filter: stash everything EXCEPT full Simulacrums. Used by every
        /// Stash interaction in this mode (hideout, between-wave, end-of-run sweep)
        /// so we never accidentally deposit the fragments we just withdrew or are
        /// holding for the next run.
        /// </summary>
        private static bool KeepSimulacrumsFilter(ServerInventory.InventSlotItem item)
        {
            var path = item.Item?.Path;
            if (path != null && path.Contains(FullSimulacrumPath, StringComparison.OrdinalIgnoreCase))
                return false; // keep — don't stash
            return true;      // stash everything else
        }

        /// <summary>
        /// Start the hideout flow for a fresh simulacrum run.
        /// Reads shared stash + run settings (no per-mode duplication) and tells the
        /// hideout flow to withdraw full Simulacrums from the central Fragment tab,
        /// then insert one into the map device.
        /// </summary>
        private void StartHideoutFlow(BotContext ctx)
        {
            var stash = ctx.Settings.Stash;
            var sim   = ctx.Settings.Simulacrum;
            _lootSweepCompletesRun = false;

            // No targetMapName — Simulacrum has no atlas node. Forcing named-map flow
            // would loop trying to click a node that doesn't exist. Auto-match flow
            // instead: scan inventory (or the device's stash panel) for a fragment
            // matching the IsSimulacrum filter, open the player inventory if needed,
            // and right-click the Simulacrum to insert + activate.
            _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum,
                stashItemFilter:    KeepSimulacrumsFilter,
                stashItemThreshold: ctx.Settings.Run.StashItemThreshold.Value,
                dumpTabName:        string.IsNullOrWhiteSpace(stash.DumpTabName.Value)     ? null : stash.DumpTabName.Value,
                resourceTabName:    string.IsNullOrWhiteSpace(stash.FragmentTabName.Value) ? null : stash.FragmentTabName.Value,
                withdrawFragmentPath:  FullSimulacrumPath,
                inventoryFragmentPath: FullSimulacrumPath,
                fragmentStock:  sim.SimulacrumStock.Value,
                minFragments:   1);
        }

        // =================================================================
        // Map phases
        // =================================================================

        private void TickFindMonolith(BotContext ctx)
        {
            if (_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.NavigateToMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "Monolith found — navigating";
                return;
            }

            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Wait for entity list to settle after zone load
            if (elapsed < ctx.Settings.AreaSettleSeconds.Value)
            {
                StatusText = "Searching for monolith...";
                return;
            }

            // Explore the map until the monolith entity enters the network bubble.
            if (ctx.Exploration.IsInitialized)
            {
                ctx.Exploration.Update(gc.Player.GridPosNum);
                var playerPos = gc.Player.GridPosNum;

                // If exploration is exhausted (100% seen) but monolith not found,
                // reset seen state so we re-sweep the map. Simulacrum maps are small
                // and the monolith is always present — we just need to walk past it.
                if (ctx.Exploration.ActiveBlobCoverage >= 0.99f)
                {
                    ctx.Exploration.ResetSeen();
                    ctx.Exploration.Update(gc.Player.GridPosNum);
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                    {
                        ctx.Navigation.NavigateTo(gc, target.Value);
                    }
                }
            }

            StatusText = "Exploring to find monolith...";

            // Use the wave timeout setting for the overall FindMonolith phase too.
            // The 60s hardcoded timeout was too short for some maps and too generous
            // for a true stuck condition — use the user-configured wave timeout instead.
            if (elapsed > _settings.WaveTimeoutMinutes.Value * 60)
            {
                ctx.Navigation.Stop(gc);
                ctx.Exploration.ResetSeen();
                _phaseStartTime = DateTime.Now;
                Decision = "Monolith search timeout → reset exploration";
                StatusText = "No monolith found — restarting search inside the instance";
            }
        }

        private void TickNavigateToMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.FindMonolith;
                return;
            }

            // If wave is already active (re-entry after death), go straight to wave cycle.
            // This is a legitimate recovery — real combat is confirmed via the monolith's
            // live StateMachine, so trust it even though the bot never clicked this run.
            if (_state.IsWaveActive && _state.HasFreshWaveState)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                _waveActivationConfirmed = true;
                StatusText = "Wave already active — joining combat";
                return;
            }

            var playerPos = ctx.Game.Player.GridPosNum;
            var dist = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

            if (dist < 18f)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Near monolith — entering wave cycle";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var success = ctx.Navigation.NavigateTo(ctx.Game,
                    _state.MonolithPosition.Value);
                if (!success)
                {
                    ctx.Navigation.Stop(ctx.Game);
                    ctx.Exploration.ResetSeen();
                    _phase = SimPhase.FindMonolith;
                    _phaseStartTime = DateTime.Now;
                    Decision = "Monolith path failed → restart search";
                    StatusText = "No path to monolith — searching again inside the instance";
                    return;
                }
            }

            StatusText = $"Navigating to monolith (dist: {dist:F0})";
        }

        // =================================================================
        // Wave cycle — the main decision loop
        // =================================================================

        private void TickWaveCycle(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Handle pending loot pickup results
            _lootTracker.HandleResult(interactionResult, ctx);

            if (!_state.HasFreshWaveState)
            {
                ctx.Combat.Suspend();
                ResetChannelTracking();
                if (!ctx.Interaction.IsBusy)
                    IdleNearMonolith(ctx);
                Decision = "Wave state unavailable — returning to monolith to observe";
                StatusText = "Waiting for valid monolith state";
                return;
            }

            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                Decision = "Refreshing hidden loot labels";
                StatusText = $"Label refresh: {ctx.Loot.ToggleStatus}";
                return;
            }

            // --- Wave transition: reset exploration so we re-sweep for new spawns ---
            if (_state.CurrentWave != _lastKnownWave)
            {
                _lastKnownWave = _state.CurrentWave;
                ctx.Exploration.SeenRadiusOverride = 0; // restore normal radius for new wave
                ctx.Exploration.ResetSeen();
                ctx.Loot.ClearFailed(); // items that failed in earlier waves may be pickable now
                _blacklistedMonsters.Clear(); // new wave = fresh monster spawns
                _wasSearching = false;
                _waveStartAttempts = 0;
                _betweenWaveStartTime = DateTime.MinValue;
                ResetChannelTracking();
            }

            var emergencySpark = _state.IsWaveActive && _waveActivationConfirmed &&
                !_channelRepositioning &&
                _settings.EmergencySparkBelowEs.Value &&
                ctx.Combat.HasEnemyChannel &&
                ctx.Combat.NearbyMonsterCount > 0 &&
                ctx.Combat.EsPercent < 0.30f;

            if ((IsStationaryChannelActive || emergencySpark) &&
                (DateTime.Now - _lastChannelDiagnosticAt).TotalSeconds >= 2)
            {
                _lastChannelDiagnosticAt = DateTime.Now;
                ctx.Log($"[Simulacrum][Spark] stationary={IsStationaryChannelActive} emergency={emergencySpark} " +
                    $"enemyChannel={ctx.Combat.HasEnemyChannel} channeling={ctx.Combat.IsChanneling} " +
                    $"nearby={ctx.Combat.NearbyMonsterCount} cached={ctx.Combat.CachedMonsterCount} " +
                    $"configured=[{ctx.Combat.ConfiguredEnemyChannelDiagnostics}] " +
                    $"skills=[{ctx.Combat.EnemySkillDiagnostics}] " +
                    $"unmatched=[{ctx.Combat.UnmatchedEnemyChannelDiagnostics}] " +
                    $"input=[{ctx.Combat.ChannelInputDiagnostics}] " +
                    $"interactionBusy={ctx.Interaction.IsBusy} decision={Decision}");
            }

            // Spark/Spark of the Nova is a stationary channel build: keep the channel
            // active and only reposition when the current position stops producing kills.
            if ((IsStationaryChannelActive || emergencySpark) &&
                ctx.Combat.HasEnemyChannel &&
                !ctx.Interaction.IsBusy)
            {
                if (TickStationaryChannel(ctx, emergencySpark))
                    return;
            }

            // --- Priority 0: Don't interrupt active loot pickup ---
            // If interaction is busy (navigating to or clicking an item), let it finish.
            // Without this guard, exploration/combat navigation overwrites the loot path.
            if (ctx.Interaction.IsBusy && _lootTracker.HasPending)
            {
                Decision = $"Loot pickup in progress: {_lootTracker.PendingItemName}";
                StatusText = $"Picking up {_lootTracker.PendingItemName}";
                return;
            }

            // --- Priority 1: Pick up nearby loot (during active waves only) ---
            // Between waves, loot is handled exclusively by Priority 4 which blocks
            // all lower priorities until loot is fully cleared.
            if (_state.IsWaveActive)
            {
                if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }

                if (ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy)
                {
                    var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (candidate != null && ctx.Interaction.IsBusy)
                    {
                        _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        Decision = $"Loot: {candidate.ItemName}";
                        StatusText = $"Picking up {candidate.ItemName}";
                        return;
                    }
                }
            }

            // --- Priority 2: Wave timeout check ---
            // A long boss/search wave is not proof that the Simulacrum is finished.
            // Keep the instance alive and restart the observation window; only an
            // inactive wave 15 is allowed to enter the completion/exit path.
            if (_state.IsWaveActive &&
                (DateTime.Now - _state.WaveStartedAt).TotalMinutes > _settings.WaveTimeoutMinutes.Value)
            {
                _state.ResetWaveTimer();
                Decision = $"Wave {_state.CurrentWave} timeout → continue incomplete run";
                StatusText = $"Wave {_state.CurrentWave}/15 still active — continuing recovery/search";
                return;
            }

            // --- Priority 3: Wave active — fight and explore ---
            if (_state.IsWaveActive)
            {
                // NearbyMonsterCount = within CombatRange — monsters close enough to fight
                if (ctx.Combat.NearbyMonsterCount > 0)
                {
                    // Combat stuck detection: if monster count isn't decreasing, we're
                    // probably fighting unreachable/unkillable monsters — move on
                    if (_combatEngageTime == DateTime.MinValue || ctx.Combat.NearbyMonsterCount < _combatEngageCount)
                    {
                        // First engagement or making progress — reset timer
                        _combatEngageTime = DateTime.Now;
                        _combatEngageCount = ctx.Combat.NearbyMonsterCount;
                    }

                    var combatElapsed = (DateTime.Now - _combatEngageTime).TotalSeconds;
                    if (combatElapsed > CombatStuckSeconds)
                    {
                        // Stuck fighting same monsters too long — blacklist nearby monsters and explore elsewhere
                        _combatEngageTime = DateTime.MinValue;
                        _combatEngageCount = 0;
                        BlacklistNearbyMonsters(gc, gc.Player.GridPosNum, ctx.Settings.Build.CombatRange.Value);
                        ctx.Navigation.Stop(gc);
                        if (!_wasSearching)
                        {
                            _wasSearching = true;
                            ctx.Exploration.SeenRadiusOverride = 40;
                            ctx.Exploration.ResetSeen();
                        }
                        Decision = $"Wave {_state.CurrentWave} — combat stuck ({combatElapsed:F0}s), blacklisted {_blacklistedMonsters.Count} monsters";
                        TickExploreForMonsters(ctx);
                    }
                    else
                    {
                        if (_wasSearching)
                        {
                            _wasSearching = false;
                            // Stop stale navigation from patrolling — combat handles movement now
                            ctx.Navigation.Stop(gc);
                            // Restore normal seen radius now that we're fighting
                            ctx.Exploration.SeenRadiusOverride = 0;
                        }

                        // Aggressive positioning: CombatSystem signals WantsToMove with a dense
                        // cluster target, but defers A* pathfinding to the mode. Without this,
                        // the bot stands still fighting a few nearby monsters while ignoring
                        // a much denser pack farther away.
                        if (ctx.Combat.WantsToMove &&
                            ctx.Combat.Profile.Positioning == CombatPositioning.Aggressive &&
                            !ctx.Interaction.IsBusy)
                        {
                            var combatTarget = ctx.Combat.MoveTargetGrid;
                            // Only repath if not navigating or current destination is far from new target
                            var navPath = ctx.Navigation.CurrentNavPath;
                            var currentDest = navPath.Count > 0 ? navPath[navPath.Count - 1].Position : playerPos;
                            if (!ctx.Navigation.IsNavigating ||
                                Vector2.Distance(currentDest, combatTarget) > 20f)
                            {
                                ctx.Navigation.Stop(gc);
                                ctx.Navigation.NavigateTo(gc, combatTarget);
                            }
                            Decision = $"Wave {_state.CurrentWave} — aggressive: pathing to density @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                        }
                        else
                        {
                            Decision = $"Wave {_state.CurrentWave} — fighting ({ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} total)";
                        }
                        StatusText = $"Wave {_state.CurrentWave}/15 — fighting {ctx.Combat.NearbyMonsterCount} monsters";
                    }
                }
                else
                {
                    // Transition from fighting → searching: reset exploration and use small
                    // seen radius so the bot must physically visit each region. Simulacrum maps
                    // are tiny (~15K cells) — the default network bubble (radius 180) covers
                    // the entire map, making exploration targets useless without this.
                    if (!_wasSearching)
                    {
                        _wasSearching = true;
                        ctx.Exploration.SeenRadiusOverride = 40;
                        ctx.Exploration.ResetSeen();
                    }
                    _combatEngageTime = DateTime.MinValue;
                    _combatEngageCount = 0;

                    Decision = $"Wave {_state.CurrentWave} — patrolling ({ctx.Combat.CachedMonsterCount} distant)";
                    TickExploreForMonsters(ctx);
                }
                return;
            }

            // --- Between waves ---

            // Priority 4: Stash items if inventory above threshold (or continuing a stash cycle).
            // Must run BEFORE loot pickup — otherwise the bot picks up one item, sees it's above
            // threshold, stashes one, picks up another, loops forever.
            // Don't start StashSystem here — TickBetweenWaveStash navigates to the
            // cached stash position first so the entity loads into the entity list.
            if (!_state.IsWaveActive && _state.StashPosition.HasValue && !ctx.Interaction.IsBusy)
            {
                // Count only items the filter would actually deposit — full Simulacrums
                // are kept in inventory for future runs and must NOT trigger a stash trip.
                // Without this, holding spare fragments causes an infinite loop:
                // open stash → filter rejects everything → close → re-trigger.
                int stashableCount = 0;
                var slots = StashSystem.GetInventorySlotItems(gc);
                if (slots != null)
                    foreach (var it in slots)
                        if (KeepSimulacrumsFilter(it)) stashableCount++;

                bool shouldStartStashing    = stashableCount >= ctx.Settings.Run.StashItemThreshold.Value;
                bool shouldContinueStashing = _isStashing && stashableCount > 0;

                if (shouldStartStashing || shouldContinueStashing)
                {
                    _isStashing = true;
                    Decision = $"Between waves → Stash ({stashableCount} items)";
                    _phase = SimPhase.BetweenWaveStash;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashing items ({stashableCount} stashable in inventory)";
                    return;
                }
                _isStashing = false;
            }

            // Priority 5: Loot must be fully cleared before starting next wave.
            // Any visible loot that is pickable or awaiting a bounded retry resets the
            // wave delay timer. A transient pickup failure must not be mistaken for an
            // empty floor and open the next wave while the item is still present.
            // Also blocks if interaction is busy (mid-pickup) — stay at spawn zone, don't
            // wander to monolith.
            if (!_state.IsWaveActive)
            {
                // Force a fresh scan every tick between waves (loot can drop at any time)
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;

                bool hasLoot = ctx.Loot.HasLootNearby;
                bool hasUnresolvedLoot = ctx.Loot.HasUnresolvedLootNearby;
                bool pickingUp = ctx.Interaction.IsBusy && _lootTracker.HasPending;

                if (!hasLoot && !ctx.Interaction.IsBusy && ctx.Loot.ShouldToggleLabels(gc) &&
                    ctx.Loot.StartLabelToggle(gc))
                {
                    Decision = "Between waves — refreshing hidden loot labels";
                    StatusText = "Refreshing stacked loot labels before starting next wave";
                    return;
                }

                if (hasUnresolvedLoot || pickingUp)
                {
                    if (hasUnresolvedLoot)
                    {
                        // Loot exists or is waiting for a bounded retry — reset the delay
                        // so the encounter cannot advance inside the retry window.
                        _state.ResetWaveDelay(_settings.MinWaveDelaySeconds.Value);
                    }

                    if (hasLoot && !ctx.Interaction.IsBusy)
                    {
                        var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                        if (candidate != null && ctx.Interaction.IsBusy)
                        {
                            _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                            Decision = $"Between waves — loot: {candidate.ItemName}";
                            StatusText = $"Picking up {candidate.ItemName} (between waves)";
                            return;
                        }

                        // PickupNext can deliberately nudge the player when labels overlap.
                        // Do not replace that movement with the monolith idle route.
                        Decision = "Between waves — repositioning for loot";
                        StatusText = "Repositioning to separate loot labels";
                        return;
                    }

                    // A retry cooldown is still active. Hold position instead of starting
                    // the monolith route and forcing a second trip back to the item.
                    if (!ctx.Interaction.IsBusy && !hasLoot)
                        ctx.Navigation.Stop(gc);
                    Decision = pickingUp
                        ? "Between waves — picking up loot"
                        : hasLoot
                            ? "Between waves — clearing loot"
                            : "Between waves — waiting to retry loot";
                    StatusText = pickingUp
                        ? $"Picking up loot (between waves)"
                        : hasLoot
                            ? "Loot nearby — clearing before next wave"
                            : "Loot pickup retry pending — holding next wave";
                    return;
                }
            }

            // Priority 6: Wave 15 complete — sweep remaining loot and exit
            if (_state.CurrentWave >= 15 && !_state.IsWaveActive)
            {
                Decision = "Wave 15 complete → LootSweep";
                _lootSweepCompletesRun = true;
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = "Wave 15 complete — sweeping loot";
                return;
            }

            // Priority 7: Start next wave (loot is clear AND delay has passed)
            // If delay was never set (fresh start / MinValue), enforce it now so we
            // get at least one full delay period to scan for loot before starting
            if (_state.CanStartWaveAt == DateTime.MinValue)
            {
                _state.ResetWaveDelay(_settings.MinWaveDelaySeconds.Value);
            }

            // Track how long we've been between waves. A timeout means our local
            // monolith/navigation state needs rebuilding, not that the run is done.
            if (_betweenWaveStartTime == DateTime.MinValue)
                _betweenWaveStartTime = DateTime.Now;
            var betweenWaveElapsed = (DateTime.Now - _betweenWaveStartTime).TotalSeconds;
            if (betweenWaveElapsed > BetweenWaveTimeoutSeconds)
            {
                ctx.Navigation.Stop(gc);
                ctx.Exploration.ResetSeen();
                _betweenWaveStartTime = DateTime.Now;
                _waveStartAttempts = 0;
                _phase = _state.MonolithPosition.HasValue
                    ? SimPhase.NavigateToMonolith
                    : SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                Decision = "Between-wave timeout → rebuild monolith navigation";
                StatusText = $"Wave {_state.CurrentWave}/15 incomplete — recovering inside the instance";
                return;
            }

            if (_waveStartAttempts >= MaxWaveStartAttempts)
            {
                ctx.Navigation.Stop(gc);
                ctx.Exploration.ResetSeen();
                _waveStartAttempts = 0;
                _betweenWaveStartTime = DateTime.Now;
                _phase = _state.MonolithPosition.HasValue
                    ? SimPhase.NavigateToMonolith
                    : SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                Decision = $"Wave start failed {MaxWaveStartAttempts} times → rebuild monolith navigation";
                StatusText = $"Can't start wave {_state.CurrentWave + 1} — recovering inside the instance";
                return;
            }

            if (DateTime.Now >= _state.CanStartWaveAt && _state.CurrentWave < 15)
            {
                Decision = $"Wave {_state.CurrentWave}/15 → StartWave (attempt {_waveStartAttempts}/{MaxWaveStartAttempts})";
                TickStartWave(ctx);
                return;
            }

            // Waiting for wave delay (loot is clear, timer running)
            var waitRemaining = (_state.CanStartWaveAt - DateTime.Now).TotalSeconds;
            Decision = $"Loot clear — waiting ({waitRemaining:F1}s)";
            IdleNearMonolith(ctx);
            StatusText = $"Wave {_state.CurrentWave}/15 — loot clear, {waitRemaining:F1}s until next wave";
        }

        /// <summary>
        /// Holds Spark at the current position until kills stop, then moves to the
        /// next position selected by CombatSystem's ranged LOS calculation.
        /// </summary>
        private void ResetChannelKillRateTracking()
        {
            _sparkProgress.Reset(DateTime.Now);
            _lastProgressSampleAt = DateTime.MinValue;
            _channelPeakKillRate = 0f;
            _channelCurrentKillRate = 0f;
            _channelRateEstablished = false;
        }

        private void ResetChannelTracking()
        {
            _channelPosition = null;
            _channelPositionStartedAt = DateTime.MinValue;
            _channelRepositioning = false;
            ResetChannelKillRateTracking();
        }

        /// <summary>Reads Spark of the Nova's Intensity stacks from the player's buffs (name configurable).</summary>
        private int GetSparkIntensityStacks(BotContext ctx)
        {
            var name = _settings.IntensityBuffName.Value;
            if (string.IsNullOrWhiteSpace(name)) return 0;
            try
            {
                var buffs = ctx.Game.Player?.Buffs;
                if (buffs == null) return 0;
                foreach (var buff in buffs)
                {
                    if (buff.Name == null || !buff.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return Math.Max((int)buff.BuffCharges, (int)buff.BuffStacks);
                }
            }
            catch { }
            return 0;
        }

        private struct SparkDiagnosticSample
        {
            public DateTime Timestamp;
            public SimPhase Phase;
            public int Wave;
            public bool WaveActive;
            public float HpPercent;
            public float EsPercent;
            public int NearbyMonsters;
            public int CachedMonsters;
            public bool HasEnemyChannel;
            public bool IsChanneling;
            public int Intensity;
            public float KillRate;
            public float PeakKillRate;
            public bool Repositioning;
            public double PositionElapsed;
            public bool InteractionBusy;
            public bool IsNavigating;
            public bool IsPathfinding;
            public string PathfindingStatus;
            public bool CanAct;
            public float PlayerGridX;
            public float PlayerGridY;
            public string Decision;
            public string StatusText;
        }

        /// <summary>Samples current Spark/combat state into a bounded rolling buffer, throttled to ~250ms.</summary>
        private void RecordSparkDiagnosticSample(BotContext ctx)
        {
            var now = DateTime.Now;
            if ((now - _lastSparkDiagnosticSampleAt).TotalMilliseconds < SparkDiagnosticSampleIntervalMs)
                return;
            _lastSparkDiagnosticSampleAt = now;

            var playerGrid = ctx.Game.Player?.GridPosNum ?? default;
            var positionElapsed = _channelPositionStartedAt == DateTime.MinValue
                ? 0
                : (now - _channelPositionStartedAt).TotalSeconds;

            _sparkDiagnosticRing[_sparkDiagnosticIndex] = new SparkDiagnosticSample
            {
                Timestamp = now,
                Phase = _phase,
                Wave = _state.CurrentWave,
                WaveActive = _state.IsWaveActive,
                HpPercent = ctx.Combat.HpPercent,
                EsPercent = ctx.Combat.EsPercent,
                NearbyMonsters = ctx.Combat.NearbyMonsterCount,
                CachedMonsters = ctx.Combat.CachedMonsterCount,
                HasEnemyChannel = ctx.Combat.HasEnemyChannel,
                IsChanneling = ctx.Combat.IsChanneling,
                Intensity = GetSparkIntensityStacks(ctx),
                KillRate = _channelCurrentKillRate,
                PeakKillRate = _channelPeakKillRate,
                Repositioning = _channelRepositioning,
                PositionElapsed = positionElapsed,
                InteractionBusy = ctx.Interaction.IsBusy,
                IsNavigating = ctx.Navigation.IsNavigating,
                IsPathfinding = ctx.Navigation.IsPathfinding,
                PathfindingStatus = ctx.Navigation.PathfindingStatus,
                CanAct = BotInput.CanAct,
                PlayerGridX = playerGrid.X,
                PlayerGridY = playerGrid.Y,
                Decision = Decision,
                StatusText = StatusText,
            };
            _sparkDiagnosticIndex = (_sparkDiagnosticIndex + 1) % _sparkDiagnosticRing.Length;
            if (_sparkDiagnosticCount < _sparkDiagnosticRing.Length) _sparkDiagnosticCount++;
        }

        /// <summary>
        /// Called by BotCore on the player's death transition. Dumps the full rolling
        /// diagnostic history plus current buffs, so a "stood still and died" or
        /// "kept channeling too long" report can be diagnosed from the log alone.
        /// </summary>
        public void OnPlayerDied(BotContext ctx)
        {
            try
            {
                ctx.Log($"[Simulacrum][Death] wave={_state.CurrentWave} phase={_phase} " +
                    $"hp={ctx.Combat.HpPercent:P0} es={ctx.Combat.EsPercent:P0} " +
                    $"decision=\"{Decision}\" status=\"{StatusText}\" samples={_sparkDiagnosticCount} " +
                    $"navMs={ctx.Navigation.LastPathfindMs} navTimedOut={ctx.Navigation.LastPathfindTimedOut} " +
                    $"navigating={ctx.Navigation.IsNavigating} recovery=\"{ctx.Navigation.LastRecoveryAction}\"");

                var buffs = ctx.Game.Player?.Buffs;
                if (buffs != null)
                {
                    var buffList = string.Join(", ", buffs
                        .Where(b => b.Name != null)
                        .Select(b => $"{b.Name}(buffCharges={b.BuffCharges},buffStacks={b.BuffStacks})"));
                    ctx.Log($"[Simulacrum][Death] player buffs: [{buffList}]");
                }

                var oldestIndex = _sparkDiagnosticCount < _sparkDiagnosticRing.Length
                    ? 0
                    : _sparkDiagnosticIndex;
                for (var i = 0; i < _sparkDiagnosticCount; i++)
                {
                    var s = _sparkDiagnosticRing[(oldestIndex + i) % _sparkDiagnosticRing.Length];
                    ctx.Log($"[Simulacrum][Death][{s.Timestamp:HH:mm:ss.fff}] " +
                        $"wave={s.Wave} active={s.WaveActive} phase={s.Phase} " +
                        $"hp={s.HpPercent:P0} es={s.EsPercent:P0} pos=({s.PlayerGridX:F0},{s.PlayerGridY:F0}) " +
                        $"nearby={s.NearbyMonsters} cached={s.CachedMonsters} " +
                        $"enemyChannel={s.HasEnemyChannel} channeling={s.IsChanneling} intensity={s.Intensity} " +
                        $"rate={s.KillRate:F1}/s peak={s.PeakKillRate:F1}/s repositioning={s.Repositioning} " +
                        $"posElapsed={s.PositionElapsed:F1}s interactionBusy={s.InteractionBusy} " +
                        $"navigating={s.IsNavigating} pathfinding={s.IsPathfinding} path=\"{s.PathfindingStatus}\" " +
                        $"canAct={s.CanAct} decision=\"{s.Decision}\" status=\"{s.StatusText}\"");
                }
            }
            catch (Exception ex)
            {
                ctx.Log($"[Simulacrum][Death] diagnostic dump failed: {ex.Message}");
            }
        }

        private bool TickStationaryChannel(BotContext ctx, bool emergencySpark)
        {
            var gc = ctx.Game;
            var now = DateTime.Now;
            var enemies = ctx.Combat.NearbyMonsterCount;
            if (enemies == 0)
            {
                ctx.Combat.Suspend();
                ResetChannelTracking();
                Decision = $"Wave {_state.CurrentWave} — no nearby enemies, searching";
                return false;
            }

            SampleSparkProgress(ctx, now);
            var noDamageSeconds = _sparkProgress.NoProgressSeconds(now);
            var emergencyLimit = _settings.DangerousNoKillEscapeSeconds.Value;
            if (emergencySpark && noDamageSeconds < emergencyLimit)
            {
                // Keep recovery damage going at low ES, but never hold indefinitely
                // against an invulnerable pack. Lack of damage also triggers escape.
                ctx.Navigation.Stop(gc);
                _channelRepositioning = false;
                _channelPosition ??= gc.Player.GridPosNum;
                if (_channelPositionStartedAt == DateTime.MinValue) _channelPositionStartedAt = now;
                Decision = $"Wave {_state.CurrentWave} — emergency Spark (ES {ctx.Combat.EsPercent:P0})";
                StatusText = Decision;
                return true;
            }

            if (_channelRepositioning)
            {
                ctx.Combat.Suspend();
                if ((now - _repositionStartedAt).TotalSeconds > 4 && !BotInput.IsMovementActive)
                {
                    ctx.Navigation.Stop(gc);
                    _channelPosition = gc.Player.GridPosNum;
                    _channelRepositioning = false;
                    _channelPositionStartedAt = now;
                    ResetChannelKillRateTracking();
                    ctx.Log("[Simulacrum][SparkReposition] no movement for 4s; resume defense and retry");
                    return true;
                }
                if (ctx.Navigation.IsNavigating)
                {
                    Decision = $"Wave {_state.CurrentWave} — moving to next Spark position";
                    StatusText = Decision;
                    return true;
                }
                if (_channelPosition.HasValue && Vector2.Distance(gc.Player.GridPosNum, _channelPosition.Value) > 14f)
                {
                    ctx.Navigation.NavigateTo(gc, _channelPosition.Value);
                    return true;
                }
                _channelRepositioning = false;
                _channelPositionStartedAt = now;
                ResetChannelKillRateTracking();
                noDamageSeconds = 0;
            }

            _channelPosition ??= gc.Player.GridPosNum;
            if (_channelPositionStartedAt == DateTime.MinValue) _channelPositionStartedAt = now;
            var positionSeconds = (now - _channelPositionStartedAt).TotalSeconds;
            _channelCurrentKillRate = _sparkProgress.KillRate(now);
            _channelPeakKillRate = Math.Max(_channelPeakKillRate, _channelCurrentKillRate);
            _channelRateEstablished = positionSeconds >= ChannelKillRateWindowSeconds;
            var danger = _settings.DangerousStationaryEnemyCount.Value > 0 &&
                enemies >= _settings.DangerousStationaryEnemyCount.Value;
            var stallLimit = danger || emergencySpark ? emergencyLimit : 3.0;
            var maxSeconds = _settings.MaxChannelPositionSeconds.Value;
            var stalled = noDamageSeconds >= Math.Min(stallLimit, maxSeconds);
            var intensity = GetSparkIntensityStacks(ctx);
            var slowed = _channelRateEstablished && _channelPeakKillRate > 0 &&
                _channelCurrentKillRate <= _channelPeakKillRate * _settings.KillRateDropRatio.Value &&
                intensity >= _settings.MinIntensityStacksBeforeReposition.Value && noDamageSeconds >= 1;
            // Productive boss damage does not require kills. Intensity never vetoes a stall escape.
            if (!stalled && !slowed && positionSeconds < maxSeconds)
            {
                if (ctx.Navigation.IsNavigating) ctx.Navigation.Stop(gc);
                Decision = $"Wave {_state.CurrentWave} — Spark channel ({_channelCurrentKillRate:F1} kills/s, no damage {noDamageSeconds:F1}s)";
                StatusText = $"Wave {_state.CurrentWave}/15 — holding Spark ({positionSeconds:F1}s, {enemies} nearby)";
                return true;
            }

            var reason = stalled ? $"no damage for {noDamageSeconds:F1}s" : slowed ? "kill rate dropped" : "position time limit";
            var oldPosition = gc.Player.GridPosNum;
            var hasPosition = ctx.Combat.TryGetOptimalRangedPosition(ctx, out var nextPosition);
            ctx.Combat.Suspend();
            BotInput.ReleaseAllKeys();
            ctx.Navigation.Stop(gc);
            _channelPosition = hasPosition ? nextPosition : null;
            _channelPositionStartedAt = DateTime.MinValue;
            _channelRepositioning = true;
            _repositionStartedAt = now;
            ctx.Log($"[Simulacrum][SparkReposition] reason={reason} from={oldPosition} to={_channelPosition} " +
                $"kills={_sparkProgress.TotalKills} ES={ctx.Combat.EsPercent:P0} intensity={intensity}");
            ResetChannelKillRateTracking();
            if (hasPosition) ctx.Navigation.NavigateTo(gc, nextPosition);
            else TickExploreForMonsters(ctx);
            Decision = $"Wave {_state.CurrentWave} — repositioning ({reason})";
            StatusText = Decision;
            return true;
        }

        private void SampleSparkProgress(BotContext ctx, DateTime now)
        {
            if ((now - _lastProgressSampleAt).TotalMilliseconds < 200) return;
            _lastProgressSampleAt = now;
            var samples = new List<MonsterHealthSample>();
            foreach (var entity in ctx.Game.EntityListWrapper.OnlyValidEntities)
            {
                try
                {
                    if (entity.Type != EntityType.Monster || !entity.IsHostile ||
                        Vector2.Distance(entity.GridPosNum, ctx.Game.Player.GridPosNum) > ctx.Settings.Build.CombatRange.Value)
                        continue;
                    var life = entity.GetComponent<Life>();
                    if (life != null) samples.Add(new(entity.Id, entity.IsAlive ? (double)life.CurHP + life.CurES : 0));
                }
                catch { /* Missing memory is not a confirmed kill. */ }
            }
            _sparkProgress.Observe(samples, now);
        }
        /// <summary>
        /// Find and navigate to monsters when none are in chase range.
        /// Three-tier fallback: cached distant monsters → reset exploration and explore → orbit monolith.
        /// </summary>
        private void TickExploreForMonsters(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Expire old blacklist entries
            PruneBlacklist();

            // Tier 1: Known monsters exist — navigate toward the nearest non-blacklisted one
            if (ctx.Combat.CachedMonsterCount > 0)
            {
                var nearestPos = FindNearestNonBlacklisted(gc, playerPos, ctx.Combat.BlacklistedEnemies);
                if (nearestPos.HasValue)
                {
                    _wasSearching = true;
                    var monsterDist = Vector2.Distance(playerPos, nearestPos.Value);
                    if (monsterDist > 20f)
                    {
                        if (ctx.Navigation.IsNavigating)
                            ctx.Navigation.UpdateDestination(gc, nearestPos.Value, driftThreshold: 15f);
                        else
                            ctx.Navigation.NavigateTo(gc, nearestPos.Value);
                    }
                    StatusText = $"Wave {_state.CurrentWave}/15 — chasing nearest monster (dist: {monsterDist:F0}, {ctx.Combat.CachedMonsterCount} alive, {_blacklistedMonsters.Count} blacklisted)";
                    return;
                }
                // All cached monsters are blacklisted — fall through to explore
            }

            // Tier 2: No cached monsters — explore to find stragglers
            // (ResetSeen already called at the fighting→searching transition above)

            // Let current navigation finish before picking a new target
            if (ctx.Navigation.IsNavigating)
            {
                StatusText = $"Wave {_state.CurrentWave}/15 — searching for monsters";
                return;
            }

            if (ctx.Exploration.IsInitialized)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    StatusText = $"Wave {_state.CurrentWave}/15 — exploring for monsters";
                    return;
                }
            }

            // Tier 3: Exploration exhausted — sweep the map around the monolith
            // Simulacrum maps are small (~18K cells) — the network bubble (radius 180) covers
            // the entire map, so exploration coverage resets are useless. Instead, physically
            // patrol at varying radii to find spawned monsters.
            if (_state.MonolithPosition.HasValue)
            {
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);
                if (distToMonolith > 80f)
                {
                    ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Wave {_state.CurrentWave}/15 — returning to monolith (dist: {distToMonolith:F0})";
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    // Sweep at varying radius — cycles through the arena to find spawns
                    var angle = (float)(DateTime.Now.Ticks % 62830) / 10000f;
                    var radius = 40f + 25f * MathF.Sin(angle * 0.3f); // 15-65 radius sweep
                    var orbitTarget = _state.MonolithPosition.Value + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                    ctx.Navigation.NavigateTo(gc, orbitTarget);
                }
                StatusText = $"Wave {_state.CurrentWave}/15 — sweeping for monsters";
                return;
            }

            StatusText = $"Wave {_state.CurrentWave}/15 — searching (no exploration targets)";
        }

        // ═══════════════════════════════════════════════════
        // Monster blacklist helpers
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Blacklist all alive hostile monsters within the given grid radius.
        /// Blacklisted monsters are ignored by TickExploreForMonsters tier 1
        /// so the bot repositions via exploration instead of chasing the same unreachable pack.
        /// </summary>
        private void BlacklistNearbyMonsters(GameController gc, Vector2 playerGrid, float radius)
        {
            var now = DateTime.Now;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive)
                    continue;
                if (Vector2.Distance(entity.GridPosNum, playerGrid) <= radius)
                    _blacklistedMonsters[entity.Id] = now;
            }
        }

        /// <summary>
        /// Find the nearest alive hostile monster that isn't blacklisted.
        /// Returns null if all cached monsters are blacklisted (or none exist).
        /// </summary>
        private Vector2? FindNearestNonBlacklisted(GameController gc, Vector2 playerGrid, HashSet<string> enemyBlacklist)
        {
            float nearestDist = float.MaxValue;
            Vector2? nearestPos = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive || !entity.IsTargetable)
                    continue;
                if (_blacklistedMonsters.ContainsKey(entity.Id))
                    continue;
                if (enemyBlacklist.Count > 0 && !string.IsNullOrEmpty(entity.RenderName) &&
                    enemyBlacklist.Contains(entity.RenderName)) continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestPos = entity.GridPosNum;
                }
            }

            return nearestPos;
        }

        /// <summary>Remove expired blacklist entries.</summary>
        private void PruneBlacklist()
        {
            if (_blacklistedMonsters.Count == 0) return;
            var now = DateTime.Now;
            var expired = new List<long>();
            foreach (var kvp in _blacklistedMonsters)
            {
                if ((now - kvp.Value).TotalSeconds > MonsterBlacklistSeconds)
                    expired.Add(kvp.Key);
            }
            foreach (var id in expired)
                _blacklistedMonsters.Remove(id);
        }

        /// <summary>
        /// Idle near the monolith between waves.
        /// </summary>
        private void IdleNearMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue) return;
            var gc = ctx.Game;
            var dist = Vector2.Distance(gc.Player.GridPosNum, _state.MonolithPosition.Value);

            if (dist > 30f && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
            else if (dist <= 20f && ctx.Navigation.IsNavigating)
                ctx.Navigation.Stop(gc);
        }

        /// <summary>
        /// Navigate to monolith and click it to start the next wave.
        /// Queues a single fixed-position click; the observed wave state confirms activation.
        /// </summary>
        private void TickStartWave(BotContext ctx)
        {
            if (!_state.HasFreshWaveState || _state.IsWaveActive) return;
            LogMonolithClick(ctx, "preflight");
            if (!_state.MonolithPosition.HasValue)
            {
                StatusText = "Can't start wave — monolith not found";
                return;
            }

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var monolithPos = _state.MonolithPosition.Value;
            var dist = Vector2.Distance(playerPos, monolithPos);

            // Navigate close first
            if (dist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, monolithPos);
                StatusText = $"Navigating to monolith to start wave {_state.CurrentWave + 1} (dist: {dist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);

            // Allow the server to acknowledge activation before another click is queued.
            if (!ModeHelpers.CanAct(_lastActionTime, 650f)) return;

            // Resolve monolith entity
            Entity? monolith = null;
            if (_state.MonolithId.HasValue)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == _state.MonolithId.Value);
            }
            if (monolith == null)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);
            }

            if (monolith == null)
            {
                StatusText = "Monolith entity not found for clicking";
                return;
            }

            if (BotInput.ClickMonolith(gc, monolith, _waveStartAttempts))
            {
                _lastActionTime = DateTime.Now;
                _waveStartAttempts++;
                LogMonolithClick(ctx, "fixed click queued", force: true);
                StatusText = $"Clicking monolith to start wave {_state.CurrentWave + 1} (attempt {_waveStartAttempts})";
            }
            else
            {
                StatusText = $"Monolith off screen or gate blocked — waiting";
            }
        }

        private void LogMonolithState(BotContext ctx)
        {
            var observation = $"wave={_state.CurrentWave} active={_state.IsWaveActive} fresh={_state.HasFreshWaveState}";
            if (observation == _lastWaveObservation &&
                (DateTime.Now - _lastMonolithDiagnosticAt).TotalSeconds < 5) return;
            _lastWaveObservation = observation;
            _lastMonolithDiagnosticAt = DateTime.Now;
            string labelState;
            try
            {
                var label = ctx.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
                    .FirstOrDefault(l => l.Entity?.Id == _state.MonolithId && l.Label?.IsVisible == true);
                labelState = label == null ? "none" : label.ClientRect.ToString();
            }
            catch (Exception ex) { labelState = $"unavailable:{ex.GetType().Name}"; }
            ctx.Log($"[Simulacrum][MonolithState] {observation} age={_state.WaveStateAgeSeconds:F1}s " +
                $"phase={_phase} label={labelState} {_state.MonolithDiagnostics} " +
                $"nearby={ctx.Combat.NearbyMonsterCount} cached={ctx.Combat.CachedMonsterCount} " +
                $"channeling={ctx.Combat.IsChanneling} input=[{BotInput.InputDiagnostics}] decision={Decision}");
        }

        private void LogMonolithClick(BotContext ctx, string reason, bool force = false)
        {
            if (!force && (DateTime.Now - _lastMonolithClickDiagnosticAt).TotalSeconds < 5) return;
            _lastMonolithClickDiagnosticAt = DateTime.Now;
            var distance = _state.MonolithPosition.HasValue
                ? Vector2.Distance(ctx.Game.Player.GridPosNum, _state.MonolithPosition.Value) : -1;
            ctx.Log($"[Simulacrum][MonolithClick] {reason} attempt={_waveStartAttempts} dist={distance:F1} " +
                $"navigation={ctx.Navigation.IsNavigating} interaction={ctx.Interaction.IsBusy} " +
                $"input=[{BotInput.InputDiagnostics}] {_state.MonolithDiagnostics} " +
                $"recentInput=[{BotInput.RecentRawInputDiagnostics}]");
        }

        // =================================================================
        // Between-wave stash
        // =================================================================

        private void TickBetweenWaveStash(BotContext ctx, InteractionResult interactionResult)
        {
            // If wave started while stashing, cancel and return to wave cycle
            if (_state.IsWaveActive)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Wave started — cancelling stash";
                return;
            }

            // Timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Stash timeout — resuming wave cycle";
                return;
            }

            var gc = ctx.Game;

            // Step 1: Navigate to cached stash position so the entity loads into the entity list.
            // StashSystem.FindStashEntity only finds entities within network bubble range.
            if (_state.StashPosition.HasValue)
            {
                var playerPos = gc.Player.GridPosNum;
                var dist = Vector2.Distance(
                    new Vector2(playerPos.X, playerPos.Y),
                    _state.StashPosition.Value);

                if (dist > ctx.Interaction.InteractRadius)
                {
                    // Cancel StashSystem if it was started — we need to navigate first
                    if (ctx.Stash.IsBusy)
                        ctx.Stash.Cancel(gc, ctx.Navigation);
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                    StatusText = $"Navigating to stash (dist: {dist:F0})";
                    return;
                }
            }

            // Step 2: Close enough — start StashSystem if not already running.
            // Between waves we ONLY deposit loot — never withdraw fragments. New
            // Simulacrums are pulled when starting a fresh run from hideout, not
            // mid-encounter. The KeepSimulacrumsFilter ensures we don't deposit
            // any spare full Simulacrums sitting in inventory for future runs.
            if (!ctx.Stash.IsBusy)
            {
                ctx.Navigation.Stop(gc);
                var dumpTab = ctx.Settings.Stash.DumpTabName.Value;
                ctx.Stash.Start(
                    storeTabName: string.IsNullOrWhiteSpace(dumpTab) ? null : dumpTab,
                    itemFilter:   KeepSimulacrumsFilter);
            }

            // Step 3: Tick StashSystem
            var result = ctx.Stash.Tick(gc, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                    // Stay in stashing mode (_isStashing = true) so shouldContinueStashing
                    // keeps working. The between-waves loot logic (Priority 4) runs first,
                    // and shouldContinueStashing ensures we come back to stash any new pickups
                    // before starting the next wave. _isStashing resets when the wave starts.
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashed {ctx.Stash.ItemsStored} items — resuming wave cycle";
                    break;
                case StashResult.Failed:
                    // StashSystem failed (entity not found, no path, etc.)
                    // Don't immediately give up — go back to navigating to stash position
                    StatusText = $"Stash failed ({ctx.Stash.Status}) — retrying";
                    break;
                default:
                    StatusText = $"Between-wave stash: {ctx.Stash.Status}";
                    break;
            }
        }

        // =================================================================
        // Loot sweep — after wave 15, pick up remaining items then exit
        // =================================================================

        private DateTime _lastEmptyScanAt = DateTime.MinValue;
        private bool _sweepNearMonolith; // true once we've confirmed proximity to monolith
        private const float EmptyGraceSeconds = 5f;
        private const float LootSweepTimeoutSeconds = 60f;
        private const float SweepMonolithProximity = 25f; // grid distance to be "near" monolith for loot

        private void TickLootSweep(BotContext ctx, InteractionResult interactionResult)
        {
            _lootTracker.HandleResult(interactionResult, ctx);

            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(ctx.Game);
                StatusText = $"Sweep: refreshing hidden loot labels — {ctx.Loot.ToggleStatus}";
                return;
            }

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > LootSweepTimeoutSeconds)
            {
                EnterExitMapPhase(ctx, _lootSweepCompletesRun);
                StatusText = $"Loot sweep timeout — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            if (ctx.Interaction.IsBusy) return;

            var gc = ctx.Game;

            // Step 1: Navigate to monolith before scanning for loot.
            // Wave 15 rewards drop at the monolith — items won't appear in VisibleGroundItemLabels
            // unless the player is close enough. Grace timer must NOT start until we're in position.
            if (!_sweepNearMonolith && _state.MonolithPosition.HasValue)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

                if (distToMonolith > SweepMonolithProximity)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Sweep: returning to monolith for drops (dist: {distToMonolith:F0})";
                    // Reset grace timer — don't count travel time as empty scan time
                    _lastEmptyScanAt = DateTime.MinValue;
                    return;
                }

                // Arrived near monolith — stop navigation, begin scanning
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _sweepNearMonolith = true;
                _lastEmptyScanAt = DateTime.MinValue; // ensure grace starts fresh from arrival
            }

            // Step 2: Stash items if inventory has stashable loot above threshold.
            // Excludes spare full Simulacrums (filter rejects them), so holding
            // fragments doesn't trigger an empty stash trip.
            if (_state.StashPosition.HasValue)
            {
                int stashableCount = 0;
                var slots = StashSystem.GetInventorySlotItems(gc);
                if (slots != null)
                    foreach (var it in slots)
                        if (KeepSimulacrumsFilter(it)) stashableCount++;
                if (stashableCount >= ctx.Settings.Run.StashItemThreshold.Value)
                {
                    // Navigate close to stash so the entity loads into the entity list
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(
                        new Vector2(playerPos.X, playerPos.Y),
                        _state.StashPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                        StatusText = $"Navigating to stash before exit (dist: {dist:F0})";
                        return;
                    }

                    if (!ctx.Stash.IsBusy)
                    {
                        ctx.Navigation.Stop(gc);
                        // End-of-run sweep stashing — deposit only, no withdrawal.
                        // Keep any remaining full Simulacrums for the next run.
                        var dumpTab = ctx.Settings.Stash.DumpTabName.Value;
                        ctx.Stash.Start(
                            storeTabName: string.IsNullOrWhiteSpace(dumpTab) ? null : dumpTab,
                            itemFilter:   KeepSimulacrumsFilter);
                    }
                }
                if (ctx.Stash.IsBusy)
                {
                    var stashResult = ctx.Stash.Tick(gc, ctx.Navigation);
                    if (stashResult == StashResult.Succeeded || stashResult == StashResult.Failed)
                    {
                        // After stashing, need to return to monolith for remaining drops
                        _sweepNearMonolith = false;
                    }
                    else
                    {
                        StatusText = $"Stashing before exit: {ctx.Stash.Status}";
                        return;
                    }
                }
            }

            // Step 3: Scan and pick up loot
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                    StatusText = $"Sweep: picking up {candidate.ItemName} ({_lootTracker.PickupCount} picked)";
                }
                else
                {
                    StatusText = "Sweep: separating overlapping loot labels";
                }
                return;
            }

            if (ctx.Loot.ShouldToggleLabels(gc) && ctx.Loot.StartLabelToggle(gc))
            {
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = "Sweep: refreshing stacked loot labels";
                return;
            }

            // Step 4: Grace period — wait near monolith for items to finish dropping.
            // Timer only starts once near monolith AND scan finds nothing.
            if (_lastEmptyScanAt == DateTime.MinValue)
                _lastEmptyScanAt = DateTime.Now;

            if ((DateTime.Now - _lastEmptyScanAt).TotalSeconds >= EmptyGraceSeconds)
            {
                EnterExitMapPhase(ctx, _lootSweepCompletesRun);
                StatusText = $"Sweep complete — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            StatusText = $"Sweep: waiting for drops near monolith... ({_lootTracker.PickupCount} picked)";
        }

        // =================================================================
        // Exit map
        // =================================================================

        private void EnterExitMapPhase(BotContext ctx, bool recordCompletion = true)
        {
            _phase = SimPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = recordCompletion;
            _mapAborted = !recordCompletion;
            _exitPortalAttempts = 0;
            _portalKeyPressed = false;

            // Cancel any in-flight systems
            if (ctx.Stash.IsBusy)
                ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
            ctx.Navigation.Stop(ctx.Game);

            StatusText = "Exiting map via portal";
        }

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.Area.CurrentArea.IsHideout)
                return;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                ctx.Interaction.Cancel(gc);
                ctx.Navigation.Stop(gc);
                _phaseStartTime = DateTime.Now;
                _exitPortalAttempts = 0;
                _portalKeyPressed = false;
                StatusText = "Exit timeout — resetting portal recovery";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Close any open panels before clicking portal
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                StatusText = "Closing panels before exit";
                return;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                // Try cached portal position
                if (_state.PortalPosition.HasValue)
                {
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(playerPos, _state.PortalPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc,
                                _state.PortalPosition.Value);
                        StatusText = $"Walking to cached portal (dist: {dist:F0})";
                    }
                    else
                    {
                        TryOpenExitPortal(ctx);
                    }
                }
                else
                {
                    TryOpenExitPortal(ctx);
                }
                return;
            }

            var playerGridPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGridPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var portalDist = Vector2.Distance(playerGridPos, portalGridPos);

            if (portalDist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalGridPos);
                StatusText = $"Walking to portal (dist: {portalDist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);
            if (ctx.Interaction.IsBusy)
            {
                StatusText = $"Clicking portal to exit ({ctx.Interaction.Status})";
                return;
            }

            if (!string.IsNullOrEmpty(ctx.Interaction.LastFailReason))
                _exitPortalAttempts++;

            ctx.Interaction.InteractWithEntity(portal, ctx.Navigation);
            _lastActionTime = DateTime.Now;
            StatusText = $"Clicking portal to exit (attempt {_exitPortalAttempts + 1})";
        }

        private void TryOpenExitPortal(BotContext ctx)
        {
            if (_portalKeyPressed)
            {
                StatusText = "Waiting for exit portal";
                return;
            }

            if (BotInput.PressKey(ctx.Settings.Run.PortalKey.Value))
            {
                _portalKeyPressed = true;
                _lastActionTime = DateTime.Now;
                StatusText = "Opening exit portal";
            }
        }

        // =================================================================
        // Render
        // =================================================================

        private static string FormatHudDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h {value.Minutes:D2}m {value.Seconds:D2}s"
                : $"{(int)value.TotalMinutes}m {value.Seconds:D2}s";
        }

        private bool HasCurrentAttempt => _phase is SimPhase.FindMonolith
            or SimPhase.NavigateToMonolith
            or SimPhase.WaveCycle
            or SimPhase.BetweenWaveStash
            or SimPhase.LootSweep
            or SimPhase.ExitMap;

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var gc = ctx.Game;
            var cam = gc.IngameState.Camera;
            var g = ctx.Graphics;

            // --- HUD ---
            var hudY = ctx.ModeHudTop;
            var hudX = 20f;
            var lineH = 16f;

            g.DrawText($"SIMULACRUM • {_phase}", new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;
            g.DrawText(StatusText, new Vector2(hudX, hudY), SharpDX.Color.LightGreen);
            hudY += lineH;

            g.DrawText($"Wave {_state.CurrentWave}/15 {(_state.IsWaveActive ? "ACTIVE" : "idle")}  •  Deaths {_state.DeathCount}/{ctx.Settings.Run.MaxDeaths.Value}",
                new Vector2(hudX, hudY),
                _state.IsWaveActive ? SharpDX.Color.Red : SharpDX.Color.Cyan);
            hudY += lineH;

            var stats = ctx.Stats.Snapshot;
            var session = stats.SessionFor(Name);
            var currentRun = stats.CurrentRun;
            var attemptText = currentRun != null
                ? FormatHudDuration(DateTime.UtcNow - currentRun.ActivatedAtUtc)
                : "--";
            g.DrawText($"Current {attemptText}  •  Full clears {session.FullClears}",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (session.Entered > 0)
            {
                g.DrawText($"Average {FormatHudDuration(TimeSpan.FromMilliseconds(session.AverageRunDurationMs))}  •  {session.AverageWavesPerEnteredRun:F1} waves/run",
                    new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            g.DrawText($"Session {FormatHudDuration(TimeSpan.FromMilliseconds(session.ActiveDurationMs))}  •  Runs {session.Attempts}  •  Items {session.ItemsLooted}",
                new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;

            var chaosPerDivine = ctx.NinjaPrice.ChaosPerDivine;
            var totalValue = chaosPerDivine > 0
                ? $"{session.ChaosValue / chaosPerDivine:F2} div"
                : $"{session.ChaosValue:F1}c";
            var hourlyValue = chaosPerDivine > 0
                ? $"{session.ChaosPerHour / chaosPerDivine:F2} div/h"
                : $"{session.ChaosPerHour:F1}c/h";
            g.DrawText($"Value {totalValue}  •  Rate {hourlyValue}  •  Current loot {_lootTracker.PickupCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (stats.Health.Status != "healthy")
            {
                g.DrawText($"Stats {stats.Health.Status}", new Vector2(hudX, hudY), SharpDX.Color.OrangeRed);
                hudY += lineH;
            }

            if (!string.IsNullOrEmpty(Decision))
            {
                g.DrawText($"Decision: {Decision}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // Always-visible Spark diagnostics — makes it obvious why the bot is
            // holding, moving, or idle so a bad state (e.g. stuck repositioning) is
            // visible immediately instead of only in the debug log.
            if (_phase == SimPhase.WaveCycle && _state.IsWaveActive)
            {
                var intensity = GetSparkIntensityStacks(ctx);
                var elapsed = _channelPositionStartedAt == DateTime.MinValue
                    ? 0
                    : (DateTime.Now - _channelPositionStartedAt).TotalSeconds;
                var sparkState = _channelRepositioning ? "repositioning"
                    : _channelPosition.HasValue ? "holding"
                    : "no position";
                g.DrawText(
                    $"Spark: {sparkState} • intensity {intensity}/{_settings.MinIntensityStacksBeforeReposition.Value} • " +
                    $"{_channelCurrentKillRate:F1}/s (peak {_channelPeakKillRate:F1}/s) • {elapsed:F1}s/{_settings.MaxChannelPositionSeconds.Value:F0}s",
                    new Vector2(hudX, hudY), SharpDX.Color.Orange);
                hudY += lineH;
            }

            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // --- World drawing (only in map) ---
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            // Monolith
            if (_state.MonolithPosition.HasValue)
            {
                var monolithWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.MonolithPosition.Value);
                g.DrawText("MONOLITH", cam.WorldToScreen(monolithWorld), SharpDX.Color.Purple);
                g.DrawCircleInWorld(monolithWorld, 30f, SharpDX.Color.Purple, 2f);
            }

            // Portal
            if (_state.PortalPosition.HasValue)
            {
                var portalWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.PortalPosition.Value);
                g.DrawText("PORTAL", cam.WorldToScreen(portalWorld) + new Vector2(-20, -15),
                    SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            // Stash
            if (_state.StashPosition.HasValue)
            {
                g.DrawText("STASH", Systems.Pathfinding.GridToScreen(gc, _state.StashPosition.Value) + new Vector2(-15, -15),
                    SharpDX.Color.Gold);
            }

            // Navigation path
            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Systems.Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Systems.Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            // Monster count
            g.DrawText($"Monsters: {ctx.Combat.NearbyMonsterCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // Failed loot count
            if (ctx.Loot.FailedCount > 0)
            {
                g.DrawText($"Ignored items: {ctx.Loot.FailedCount}",
                    new Vector2(hudX, hudY), SharpDX.Color.OrangeRed);
                hudY += lineH;
            }

            // Draw failed/ignored items in world with reason labels
            foreach (var entry in ctx.Loot.FailedEntries.Values)
            {
                // Find the entity to get its world position
                Entity? failedEntity = null;
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (e.Id == entry.EntityId)
                    {
                        failedEntity = e;
                        break;
                    }
                }
                if (failedEntity == null) continue;

                var worldPos = failedEntity.BoundsCenterPosNum;
                var screenPos = cam.WorldToScreen(worldPos);
                if (screenPos.X < 0 || screenPos.X > gc.Window.GetWindowRectangle().Width ||
                    screenPos.Y < 0 || screenPos.Y > gc.Window.GetWindowRectangle().Height)
                    continue;

                var age = (DateTime.Now - entry.FailedAt).TotalSeconds;
                g.DrawText($"X {entry.Reason} ({age:F0}s ago)",
                    screenPos + new Vector2(5, -10), SharpDX.Color.OrangeRed);
            }
        }

    }

    public enum SimPhase
    {
        Idle,

        // Hideout phases
        InHideout,
        StashItems,
        OpenMap,
        EnterPortal,

        // Map phases
        FindMonolith,
        NavigateToMonolith,
        WaveCycle,
        BetweenWaveStash,
        LootSweep,
        ExitMap,
        Done,
    }
}
