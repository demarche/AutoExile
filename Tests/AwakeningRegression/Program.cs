using System.Text.Json;
using AutoExile.Modes.AwakeningBossRush;
using AutoExile.Systems;

int checks = 0;
string lastControlResponse = "";
void Check(bool condition, string name) { if (!condition) throw new Exception(name + " / " + lastControlResponse); checks++; }
ModObservation Mod(string id, string text = "", bool implicitMod = false) => new(id, "", text, [], [25], implicitMod);
MapObservation Map(params ModObservation[] mods) => new("Metadata/Items/Maps/MapKey", "Tier 16 Map", 16, true, true, mods.ToList(), 80, "Dunes");
var harmless = Map(Mod("MapMonsterFireResistance", "Monsters have 40% increased Fire Resistance"));
Check(AwakeningMapPolicy.Rejections(harmless).Count == 0, "Generic T16 map key is allowed with Dunes selected");
Check(AwakeningMapPolicy.Rejections(harmless with { Layout = "Strand" }).Contains("requires_Dunes"), "Wrong atlas node rejected independently of key");
Check(AwakeningMapPolicy.Rejections(harmless with { Tier = 15 }).Contains("requires_T16"), "T15 rejected");
Check(AwakeningMapPolicy.Rejections(harmless with { Identified = false }).Count > 0, "Unidentified map rejected");
Check(AwakeningMapPolicy.Rejections(harmless with { Readable = false }).Count > 0, "Missing memory is not empty mods");
Check(AwakeningMapPolicy.Rejections(Map()).Count > 0, "Non-normal empty mods need hydration");
Check(AwakeningMapPolicy.Rejections(Map() with { NormalRarity = true }).Count == 0, "Readable white map may have no mods");
// Independent fixtures: the exact requirements supplied by the user, with representative rolls.
string[] forbidden = [
    "Players cannot Regenerate Life, Mana or Energy Shield",
    "Players have 60% less Recovery Rate of Life and Energy Shield",
    "Players have 40% less Armour",
    "Rare Monsters have Physical Thorns reflecting 300 Physical Damage",
    "Rare Monsters have Elemental Thorns reflecting 300 Elemental Damage",
    "Rare monsters in area are Shaper-Touched",
    "Players have 40% less Area of Effect",
    "Players have -12% to all maximum Resistances",
    "Players have 30% more Defences",
    "Area is influenced by The Shaper",
    "Map is occupied by The Eradicator",
    "Map is occupied by The Purifier",
    "Map is occupied by The Constrictor",
    "Map is occupied by The Enslaver",
    "Monsters cannot be Leeched from",
    "Players have 40% more Cooldown Recovery Rate",
    "Players gain 50% reduced Flask Charges"
];
foreach (var text in forbidden)
    Check(AwakeningMapPolicy.Rejections(Map(Mod("unmapped_current_stat", text, text.StartsWith("Map is") || text.StartsWith("Area is")))).Count > 0, "Forbidden: " + text);
Check(AwakeningMapPolicy.Rejections(Map(new ModObservation("unknown", "", "", ["map_shaper_influence"], [1], true))).Contains("shaper_influence"), "Localized implicit matched through stat ID");
Check(AwakeningLootPolicy.ShouldLoot("Chaos Orb", "currency", 1 * 5), "Five 1c items qualify as one stack");
Check(!AwakeningLootPolicy.ShouldLoot("Chaos Orb", "currency", 4.999), "Strict lower threshold");
Check(!AwakeningLootPolicy.ShouldLoot("Unknown", "unknown", null), "No invented price");
Check(!AwakeningLootPolicy.ShouldLoot("Unknown", "unknown", double.NaN), "NaN price rejected");
foreach (var item in new[] { "Cartographer's Chisel", "Maven's Chisel of Proliferation", "Crescent Splinter", "Maven's Writ", "Incandescent Invitation" })
    Check(AwakeningLootPolicy.ShouldLoot(item, "unknown", null), "Mandatory without price: " + item);
Check(!AwakeningLootPolicy.StashComplete(new(false, 0, new())), "Unreadable inventory does not finish Stashie");
Check(!AwakeningLootPolicy.StashComplete(new(true, 1, new())), "One item left outside reserved column blocks completion");
Check(AwakeningLootPolicy.StashComplete(new(true, 0, new() { ["reserved"] = 100 })), "Right column contents retained");
Check(!AwakeningLootPolicy.PickupConfirmed(new(true, 1, new() { ["chaos"] = 10 }), "chaos", 10, 5, false), "Ground disappearance alone cannot confirm pickup");
Check(!AwakeningLootPolicy.PickupConfirmed(new(true, 1, new() { ["chaos"] = 15 }), "chaos", 10, 5, true), "Inventory increase while original drop remains is not receipt");
Check(AwakeningLootPolicy.PickupConfirmed(new(true, 1, new() { ["chaos"] = 15 }), "chaos", 10, 5, false), "Matching delta plus ground removal confirms stack");
var recipe = new[] { new RecipeItem("Dawn", "dawn"), new("Noon", "noon"), new("Dusk", "dusk"), new("Midnight", "midnight"), new("Awakening", "awakening") };
Check(AwakeningLootPolicy.RecipeComplete(["noon", "dawn", "dusk", "midnight", "awakening"], recipe), "Exact recipe ignores slot ordering");
Check(!AwakeningLootPolicy.RecipeComplete(["dawn", "dawn", "dusk", "midnight", "awakening"], recipe), "Duplicate sacrifice cannot replace a missing type");
Check(!AwakeningLootPolicy.RecipeComplete(["dawn", "noon", "dusk", "midnight"], recipe), "Missing Awakening cannot activate");
Check(!AwakeningLootPolicy.RecipeComplete(["dawn", "noon", "dusk", "midnight", "awakening", "extra"], recipe), "Extra materials rejected");
Check(EldritchInvitationPolicy.Decide(null, 28, true) == InvitationDecision.Unknown, "Missing Exarch counter stays unknown");
Check(EldritchInvitationPolicy.Decide(28, -1, true) == InvitationDecision.Unknown, "Uncalibrated counter stays unknown");
Check(EldritchInvitationPolicy.Decide(28, 28, false) == InvitationDecision.Unknown, "Unselected influence stays unknown");
Check(EldritchInvitationPolicy.Decide(27, 28, true) == InvitationDecision.No, "Below configured ready point skips ordinary boss");
Check(EldritchInvitationPolicy.Decide(28, 28, true) == InvitationDecision.Yes, "At configured ready point requires invitation");

var now = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
BossSample Sample(long id, string member, bool alive, double health, bool valid = true, bool targetable = true) =>
    new(id, "test/" + member, member, "formed", member, 50, 50, alive, targetable, health, valid);
var encounter = new AwakeningRun();
Check(!AwakeningBossTracker.Complete(encounter), "No encounter is not success");
var living = new[] { Sample(1,"hydra",true,100), Sample(2,"chimera",true,100), Sample(3,"minotaur",true,100), Sample(4,"phoenix",true,100) };
AwakeningBossTracker.Observe(encounter, living, now);
AwakeningBossTracker.Observe(encounter, [], now.AddSeconds(1));
Check(encounter.Bosses.Values.All(b => b.Life == BossLife.Missing) && !AwakeningBossTracker.Complete(encounter), "Network bubble exit never kills bosses");
AwakeningBossTracker.Observe(encounter, living.Select(s => s with { Health = 0, Targetable = false }), now.AddSeconds(2));
Check(!AwakeningBossTracker.Complete(encounter), "Alive phased boss with zero health is not dead");
AwakeningBossTracker.Observe(encounter, living.Select(s => s with { Alive = false, Health = 0, Valid = false }), now.AddSeconds(3));
Check(!AwakeningBossTracker.Complete(encounter), "Invalid dead reads are not evidence");
AwakeningBossTracker.Observe(encounter, living.Take(3).Select(s => s with { Alive = false, Health = 0 }), now.AddSeconds(4));
Check(!AwakeningBossTracker.Complete(encounter), "Three of four guardians cannot finish");
AwakeningBossTracker.Observe(encounter, living.Select(s => s with { Alive = false, Health = 0 }), now.AddSeconds(5));
Check(AwakeningBossTracker.Complete(encounter), "Every required member has direct death evidence");
AwakeningBossTracker.Observe(encounter, [], now.AddSeconds(6));
Check(AwakeningBossTracker.Complete(encounter), "Recorded deaths survive unload");
AwakeningBossTracker.Observe(encounter, [Sample(1,"hydra",true,100)], now.AddSeconds(7));
Check(!AwakeningBossTracker.Complete(encounter), "A new living phase invalidates completion");
var progress = new SparkProgressTracker(); progress.Reset(now);
progress.Observe([new(1, 1000)], now);
progress.Observe([new(1, 900)], now.AddSeconds(2));
Check(progress.NoProgressSeconds(now.AddSeconds(3)) == 1 && progress.TotalKills == 0, "Productive boss damage preserves Spark without kills");
progress.Observe([], now.AddSeconds(4));
Check(progress.NoProgressSeconds(now.AddSeconds(5)) == 3, "Disappearance cannot reset no-damage timer");

// Replay persistence boundaries in a test-only output directory; no ExileAPI or input dependencies.
var testDir = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDir);
string review = AwakeningJson.Serialize(new { observations = "test log observed", diagnosis = "test diagnosis", changes = "test change", validation = "offline replay passed", nextAction = "retry_existing_portal" });
var supervisor = new AwakeningSupervisor(testDir, "build1");
string Command(string action, string? id = null, string? generation = null, string? mvid = null, string? body = null) =>
    lastControlResponse = supervisor.Command(action, id ?? Guid.NewGuid().ToString("N"), generation ?? supervisor.Generation, body ?? review, mvid ?? supervisor.Mvid);
Check(!supervisor.State.Armed, "Cold plugin is dormant");
Check(Command("begin").StartsWith("rejected:"), "Begin cannot arm itself");
Check(Command("arm", generation: "stale").StartsWith("rejected:"), "Old generation cannot send commands");
Check(Command("arm").EndsWith(":armed"), "Explicit arm accepted");
Check(Command("begin", mvid: "wrong").StartsWith("rejected:"), "Wrong loaded build rejected");
Check(Command("begin", "first").EndsWith(":begun"), "Verified begin starts one attempt");
Check(Command("begin", "first").StartsWith("duplicate:") && supervisor.Run.AttemptNumber == 1, "Retrying acknowledged command cannot double begin");
Check(Command("begin").StartsWith("rejected:"), "Unfinished attempt cannot be overwritten");
supervisor.Run.ActivationRequested = true; supervisor.Run.ActivationConfirmed = true;
supervisor.Run.PortalIds = [12, 13]; supervisor.Run.Instance = 777; supervisor.Run.Map = harmless;
supervisor.Run.EnteredUtc = now; supervisor.Run.ElapsedSeconds = 299.9;
Check(AwakeningSupervisor.Watchdog(supervisor.Run, true, true) == AttemptOutcome.None, "No early timeout");
supervisor.Run.ElapsedSeconds = 300;
Check(AwakeningSupervisor.Watchdog(supervisor.Run, true, true) == AttemptOutcome.Timeout, "300 second map deadline");
Check(AwakeningSupervisor.Watchdog(supervisor.Run, false, true) == AttemptOutcome.Death, "Death is a distinct outcome");
Check(AwakeningSupervisor.Watchdog(supervisor.Run, true, false) == AttemptOutcome.None, "Hideout is not a map timeout");
supervisor.Run.Deaths = 1;
supervisor.Finish(AttemptOutcome.Death, "test death", now.AddMinutes(5));
Check(!supervisor.Finish(AttemptOutcome.Success, "false success", now), "Terminal cause cannot be overwritten");
Check(Command("begin").StartsWith("rejected:"), "Unreviewed terminal attempt cannot restart");
Check(Command("review", body: new string('x', 100)).StartsWith("rejected:"), "Review requires fields, not a long acknowledgement");
supervisor.Run.RecoveryRequired = true;
Check(Command("review").StartsWith("rejected:"), "Pending recovery blocks review");
supervisor.Run.RecoveryRequired = false;
Check(Command("review").EndsWith(":reviewed"), "Structured review accepted after recovery");
var runId = supervisor.Run.RunId;
var retryResult = Command("begin");
Check(retryResult.EndsWith(":begun") && supervisor.Run.RunId == runId && supervisor.Run.Instance == 777 && supervisor.Run.AttemptNumber == 2,
    "Failure reuses recorded instance and portal set: " + retryResult + " / " + supervisor.StorageError);
Check(supervisor.Run.Deaths == 1 && supervisor.State.History.Count == 1, "Retry preserves per-map losses");
supervisor.Save();
var loaded = new AwakeningSupervisor(testDir, "build2");
Check(!loaded.State.Armed && loaded.Generation != supervisor.Generation && loaded.Run.RunId == runId, "Reload disarms but preserves checkpoint");
Check(loaded.Command("begin", "new", loaded.Generation, review, "build2").StartsWith("rejected:"), "Reload cannot silently resume");
supervisor.Finish(AttemptOutcome.Success, "verified", now.AddMinutes(8));
Command("review");
Check(Command("begin").EndsWith(":begun") && supervisor.Run.RunId != runId && !supervisor.Run.ActivationConfirmed && supervisor.Run.PriorPortalIds.SequenceEqual(new long[] {12,13}),
    "Reviewed success starts a fresh map and authorizes replacing only its old portals");
var roundTrip = JsonSerializer.Deserialize<AwakeningCheckpoint>(AwakeningJson.Serialize(supervisor.State), AwakeningJson.Options)!;
Check(roundTrip.History.Count == 2 && roundTrip.History[0].Outcome == AttemptOutcome.Death, "JSON preserves outcome enums and attempt history");
var staleRunId = supervisor.Run.RunId;
supervisor.Run.ActivationConfirmed = true;
supervisor.Run.Instance = 123;
supervisor.Run.LootReceipts.Add("123:drop");
supervisor.Finish(AttemptOutcome.OperationalFailure, "unexpected_area_change", now);
Check(Command("begin").StartsWith("rejected:"), "Instance mismatch still requires review before replacing map");
Command("review");
Check(Command("begin").EndsWith(":begun") && supervisor.Run.RunId != staleRunId &&
    supervisor.Run.Instance == 0 && !supervisor.Run.ActivationConfirmed && supervisor.Run.LootReceipts.Count == 0,
    "Reviewed foreign instance cannot inherit stale boss and loot ownership");
File.WriteAllText(Path.Combine(testDir, "checkpoint.json"), "{invalid");
var broken = new AwakeningSupervisor(testDir, "build");
Check(broken.StorageError.Length > 0 && broken.Command("arm", "x", broken.Generation).StartsWith("rejected:"), "Corruption never resets into an armed run");
File.WriteAllText(Path.Combine(testDir, "checkpoint.json"), "{\"schema\":1,\"run\":null}");
Check(new AwakeningSupervisor(testDir, "build").StorageError.Length > 0, "Null checkpoint state also fails closed");
File.WriteAllText(Path.Combine(testDir, "checkpoint.json"), "{\"schema\":1,\"history\":[null]}");
Check(new AwakeningSupervisor(testDir, "build").StorageError.Length > 0, "Corrupt history cannot crash plugin initialization");
var fileAsDirectory = Path.Combine(testDir, "blocked"); File.WriteAllText(fileAsDirectory, "fixture");
var unwritable = new AwakeningSupervisor(fileAsDirectory, "build");
Check(unwritable.Command("arm", "x", unwritable.Generation).StartsWith("rejected:") && !unwritable.State.Armed, "Persistence failure disables arming");
int replaceCalls = 0;
var transient = new AwakeningSupervisor(Path.Combine(testDir, "transient"), "build", (source, destination) => {
    if (++replaceCalls <= 2) throw new UnauthorizedAccessException("fixture transient file lock");
    File.Move(source, destination, true);
});
Check(transient.Command("arm", "once", transient.Generation).EndsWith(":armed") && transient.PersistenceRetries == 2,
    "Transient checkpoint replacement retries the write and succeeds");
Check(transient.State.Requests.Count == 1 && transient.Run.AttemptNumber == 0, "Persistence retry never replays a command or starts an attempt");
var permanent = new AwakeningSupervisor(Path.Combine(testDir, "permanent"), "build", (_, _) => throw new UnauthorizedAccessException("fixture persistent denial"));
Check(permanent.Command("arm", "once", permanent.Generation).StartsWith("rejected:") && !permanent.State.Armed && permanent.PersistenceRetries == 3,
    "Persistent access denial exhausts bounded retry and disarms");

AwakeningRun StatRun(int index, bool exposed, bool death) => new() {
    RunId = "map-" + index, AttemptNumber = 1, Outcome = death ? AttemptOutcome.Death : AttemptOutcome.Success,
    ActivationConfirmed = true, Map = exposed ? Map(Mod("danger"), Mod("paired")) : Map(Mod("control")),
    BuildFingerprint = "same-build", BuildMvid = "same-code", Deaths = death ? 1 : 0,
    PhaseSeconds = new() { ["Fight"] = exposed ? 100 : 50 }, OperatingSeconds = 150,
    CostKnown = true, RevenueChaos = 150, CostChaos = 50
};
var few = Enumerable.Range(0, 8).Select(i => StatRun(i, i < 4, i < 4)).ToList();
Check(AwakeningModRiskAnalyzer.Analyze(few).All(x => x.Status == "InsufficientEvidence"), "Single-map anecdotes cannot become NG candidates");
var population = Enumerable.Range(0, 60).Select(i => StatRun(i, i < 30, i < 30)).ToList();
population.AddRange(population.Take(10));
var risks = AwakeningModRiskAnalyzer.Analyze(population);
var danger = risks.Single(x => x.Mod == "danger");
Check(danger.Exposed == 30 && danger.Controls == 30, "Retries cannot inflate independent-map sample counts");
Check(danger.Status == "CandidateNG" && danger.LowerDifference > 0, "Repeated stratified death association produces candidate");
Check(danger.InseparableMods.Contains("paired"), "Perfectly paired mods are reported as inseparable");
Check(danger.FightMedian == 100 && danger.FightP90 == 100 && danger.FightRatio == 2, "Fight timing supports throughput comparison");
population.ForEach(r => r.CostKnown = false);
Check(AwakeningModRiskAnalyzer.Analyze(population).All(x => x.ProfitDifference == null), "Unknown costs cannot produce invented net profit");
population.Where(r => r.Map!.Mods.Any(m => m.Id == "control")).ToList().ForEach(r => r.BuildMvid = "different-code");
Check(AwakeningModRiskAnalyzer.Analyze(population).All(x => x.Status == "InsufficientEvidence"), "Different code cohorts cannot certify a mod effect");
population.ForEach(r => r.InputFault = true);
Check(AwakeningModRiskAnalyzer.Analyze(population).Count == 0, "Input failures excluded from mod-causality candidates");
var telemetryDir = Path.Combine(testDir, "telemetry");
using (var telemetry = new AwakeningTelemetry(telemetryDir, _ => throw new IOException("fixture log sink unavailable")))
{
    telemetry.Sample(new { hp = 0, position = new[] { double.NaN, 10 } });
    telemetry.FlushRing(new(), "death_fixture");
    Check(telemetry.Error.Contains("Verbose sink"), "Verbose failure is recorded without escaping stop logic");
}
var recorded = File.ReadAllLines(Path.Combine(telemetryDir, "events.jsonl"));
Check(recorded.Length == 2 && recorded[1].Contains("diagnostic.sample"), "Evidence sidecar survives primary logger failure");
Console.WriteLine($"Awakening regression: {checks} checks passed. No game process, network, or input used.");
