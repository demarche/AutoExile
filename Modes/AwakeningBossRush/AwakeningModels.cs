using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoExile.Modes.AwakeningBossRush;

public enum AwakeningPhase { Dormant, Prepare, IndexStash, Withdraw, OpenMap, EnterPortal, Scout, Fight, Loot, MapBoss, Return, OpenStash, ExternalStash, AwaitingReview, Stopped, RestockMap, Restock }
public enum AttemptOutcome { None, Success, Death, Timeout, ManualIntervention, OperationalFailure }
public enum BossLife { Alive, Dormant, Missing, DeadConfirmed }
public enum InvitationDecision { Unknown, No, Yes }

public sealed record ModObservation(string Id, string Group, string Text, string[] Stats, int[] Values, bool Implicit);
public sealed record MapObservation(string Path, string Name, int Tier, bool Identified, bool Readable, List<ModObservation> Mods, double Quantity = 0, string Layout = "", bool NormalRarity = false);
public sealed record RecipeItem(string Name, string Path, int Count = 1);
public sealed record InventoryObservation(bool Valid, int OutsideReservedColumn, Dictionary<string, int> Counts);
public sealed record BossSample(long Id, string Path, string Name, string Group, string Member, float X, float Y, bool Alive, bool Targetable, double Health, bool Valid);
public sealed class TrackedBoss
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Member { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public double Health { get; set; }
    public BossLife Life { get; set; }
    public DateTime SeenUtc { get; set; }
    public string DeathEvidence { get; set; } = "";
}
public sealed class AwakeningRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string AttemptId { get; set; } = "";
    public int AttemptNumber { get; set; }
    public long Instance { get; set; }
    public string Area { get; set; } = "";
    public AwakeningPhase Phase { get; set; } = AwakeningPhase.Dormant;
    public AttemptOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EnteredUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public double ElapsedSeconds { get; set; }
    public double OperatingSeconds { get; set; }
    public Dictionary<string, double> PhaseSeconds { get; set; } = new();
    public bool ActivationRequested { get; set; }
    public bool ActivationConfirmed { get; set; }
    public List<long> PriorPortalIds { get; set; } = new();
    public List<long> PortalIds { get; set; } = new();
    public List<RecipeItem> Recipe { get; set; } = new();
    public MapObservation? Map { get; set; }
    public Dictionary<long, TrackedBoss> Bosses { get; set; } = new();
    public List<long> LootSweptBosses { get; set; } = new();
    public DateTime? LastNewBossUtc { get; set; }
    // Where the pinnacle drops lie (encounter centre when looting started). Survives deaths so a re-entry goes straight back.
    public float[]? DropSite { get; set; }
    public List<string> LootReceipts { get; set; } = new();
    public List<string> UnresolvedLoot { get; set; } = new();
    public List<float[]> Visited { get; set; } = new();
    public int Deaths { get; set; }
    public double RevenueChaos { get; set; }
    public bool RevenueKnown { get; set; } = true;
    public double CostChaos { get; set; }
    public bool CostKnown { get; set; }
    public InvitationDecision Invitation { get; set; }
    public int? ExarchCounter { get; set; }
    public bool MapBossKilled { get; set; }
    public List<long> MapBossIds { get; set; } = new();
    public List<long> DeadMapBossIds { get; set; } = new();
    public bool InvitationLooted { get; set; }
    public bool BossesCompleted { get; set; }
    public bool ReturnedToHideout { get; set; }
    public bool StashCompleted { get; set; }
    public bool RecoveryRequired { get; set; }
    public string BuildMvid { get; set; } = "";
    public string BuildFingerprint { get; set; } = "";
    public string BuildConfiguration { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Review { get; set; } = "";
    public bool InputFault { get; set; }
}
public sealed class AwakeningCheckpoint
{
    public int Schema { get; set; } = 1;
    public bool Armed { get; set; }
    public AwakeningRun Run { get; set; } = new();
    public List<string> Requests { get; set; } = new();
    public List<AwakeningRun> History { get; set; } = new();
}
public static class AwakeningJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
