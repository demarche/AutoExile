using System.Text.RegularExpressions;

namespace AutoExile.Modes.AwakeningBossRush;

public static class AwakeningMapPolicy
{
    // Match both language-independent game IDs and English stat translations. All implicit mods are included.
    public static readonly (string Id, string Description, string Pattern)[] Rules =
    [
        ("no_regen", "Players cannot Regenerate Life, Mana or Energy Shield", @"MapNoRegen|no_life_mana_energy_shield_regeneration|cannot Regenerate"),
        ("less_recovery", "Players have #% less Recovery Rate of Life and Energy Shield", @"MapReducedRecovery|recovery.*life.*energy.shield|less Recovery Rate"),
        ("less_armour", "Players have #% less Armour", @"Map.*(?:Reduced|Less)Armour|(?:armour.*final|less Armour)"),
        ("physical_thorns", "Rare Monsters have Physical Thorns reflecting # Physical Damage", @"physical.*thorns|thorns.*physical|Physical Thorns"),
        ("elemental_thorns", "Rare Monsters have Elemental Thorns reflecting # Elemental Damage", @"elemental.*thorns|thorns.*elemental|Elemental Thorns"),
        ("shaper_touched", "Rare monsters in area are Shaper-Touched", @"shaper.?touched"),
        ("less_aoe", "Players have #% less Area of Effect", @"Map.*(?:Reduced|Less)(?:Area|AoE)|map_player.*area_of_effect|less Area of Effect"),
        ("max_resists", "Players have #% to all maximum Resistances", @"MapPlayerMaxResists|map_player.*maximum.*resist|to all maximum Resistances"),
        ("defences", "Players have #% more Defences", @"Map.*Defen[cs]|map_player.*defen[cs]|more Defences"),
        ("shaper_influence", "Area is influenced by The Shaper", @"MapShaperInfluence|map_shaper_influence|Area is influenced by The Shaper"),
        ("eradicator", "Map is occupied by The Eradicator", @"ElderMapBoss.*(?:Eradicator|Lightning)|Map.*Eradicator|occupied by The Eradicator"),
        ("purifier", "Map is occupied by The Purifier", @"ElderMapBoss.*(?:Purifier|Holy)|Map.*Purifier|occupied by The Purifier"),
        ("constrictor", "Map is occupied by The Constrictor", @"ElderMapBoss.*(?:Constrictor|Poison)|Map.*Constrictor|occupied by The Constrictor"),
        ("enslaver", "Map is occupied by The Enslaver", @"ElderMapBoss.*(?:Enslaver|Fire)|Map.*Enslaver|occupied by The Enslaver"),
        ("no_leech", "Monsters cannot be Leeched from", @"MapCannotLeech|cannot_be_leeched|cannot.*(?:life|mana).*leech|cannot be Leeched"),
        ("cooldown", "Players have #% more Cooldown Recovery Rate", @"Map.*Cooldown|map_player.*cooldown|more Cooldown Recovery Rate"),
        ("flask_charges", "Players gain #% reduced Flask Charges", @"Map.*FlaskCharges|map_player.*flask.*charge|reduced Flask Charges")
    ];

    public static List<string> Rejections(MapObservation map)
    {
        var result = new List<string>();
        if (!map.Readable || !map.Identified || (map.Mods.Count == 0 && !map.NormalRarity)) result.Add("map_unreadable_or_unidentified");
        if (map.Tier != 16) result.Add("requires_T16");
        if (!Regex.IsMatch(map.Layout + " " + map.Path + " " + map.Name, @"(?:Dunes?)(?:\b|Map|$)", RegexOptions.IgnoreCase)) result.Add("requires_Dunes");
        foreach (var rule in Rules)
            if (map.Mods.Any(m => Regex.IsMatch(m.Id + " " + m.Group + " " + m.Text + " " + string.Join(" ", m.Stats), rule.Pattern, RegexOptions.IgnoreCase)))
                result.Add(rule.Id);
        return result;
    }
}

public static class AwakeningLootPolicy
{
    public static bool Mandatory(string name, string path) => Regex.IsMatch(name + " " + path,
        @"Maven.?s Chisel|Cartographer.?s Chisel|Crescent Splinter|Maven.?s Writ|Maven.?s Invitation|Incandescent Invitation|MavenChisel|CurrencyMapQuality|Maven(?:Fragment|Key)|MavenInvitation|CurrencyMaven|QuestItemMaven|SearingExarch.*Key",
        RegexOptions.IgnoreCase);
    public static bool ShouldLoot(string name, string path, double? stackChaos, double threshold = 5) =>
        Mandatory(name, path) || (stackChaos.HasValue && double.IsFinite(stackChaos.Value) && stackChaos.Value >= threshold);
    public static bool StashComplete(InventoryObservation inventory) => inventory.Valid && inventory.OutsideReservedColumn == 0;
    public static bool PickupConfirmed(InventoryObservation inventory, string path, int before, int quantity, bool stillOnGround) =>
        inventory.Valid && quantity > 0 && !stillOnGround && inventory.Counts.GetValueOrDefault(path) >= before + quantity;
    public static bool RecipeComplete(IReadOnlyList<string> actual, IReadOnlyList<RecipeItem> recipe) =>
        actual.Count == recipe.Sum(x => x.Count) && recipe.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .All(g => actual.Count(x => x.Equals(g.Key, StringComparison.OrdinalIgnoreCase)) == g.Sum(x => x.Count));
}

public sealed class AwakeningBossTracker
{
    // Seed catalog: metadata/name hints are explicitly logged; unknown families never complete a run.
    public static readonly Dictionary<string, string[]> Rosters = new()
    {
        ["formed"] = ["hydra", "chimera", "minotaur", "phoenix"],
        ["twisted"] = ["eradicator", "purifier", "constrictor", "enslaver"],
        ["forgotten"] = ["altered", "augmented", "rewritten"],
        ["hidden"] = ["xoph", "tul", "esh", "uul"],
        ["elderslayers"] = ["baran", "veritania", "alhezmin", "drox"],
        ["feared"] = ["atziri", "chayula", "shaper", "elder", "cortex"]
    };
    private static readonly (string Group, string Member, string Pattern)[] Catalog =
    [
        ("formed","hydra",@"Hydra"), ("formed","chimera",@"Chimera"), ("formed","minotaur",@"Minotaur"), ("formed","phoenix",@"Phoenix"),
        ("twisted","eradicator",@"Eradicator|ElderGuardianLightning"), ("twisted","purifier",@"Purifier|ElderGuardianHoly"),
        ("twisted","constrictor",@"Constrictor|ElderGuardianPoison"), ("twisted","enslaver",@"Enslaver|ElderGuardianFire"),
        ("forgotten","altered",@"Altered Synthete|SynthesisGolem"), ("forgotten","augmented",@"Augmented Synthete|SynthesisFabricator"),
        ("forgotten","rewritten",@"Rewritten Synthete|SynthesisGuardian"),
        ("hidden","xoph",@"Xoph|BreachBossFire"), ("hidden","tul",@"\bTul\b|BreachBossCold"), ("hidden","esh",@"\bEsh\b|BreachBossLightning"), ("hidden","uul",@"Uul|BreachBossPhysical"),
        ("elderslayers","baran",@"Baran|Crusader"), ("elderslayers","veritania",@"Veritania|Redeemer"),
        ("elderslayers","alhezmin",@"Al.Hezmin|Basilisk"), ("elderslayers","drox",@"Drox|Warlord"),
        ("feared","atziri",@"Atziri"), ("feared","chayula",@"Chayula|BreachBossChaos"),
        ("feared","shaper",@"ShaperBoss|The Shaper"), ("feared","elder",@"ElderBoss|The Elder"),
        ("feared","cortex",@"SynthesisBoss|Venarius|Synthesised Nightmare")
    ];
    public static (string Group, string Member)? Classify(string path, string name)
    {
        foreach (var c in Catalog)
            if (Regex.IsMatch(path + " " + name, c.Pattern, RegexOptions.IgnoreCase)) return (c.Group, c.Member);
        return null;
    }
    public static void Observe(AwakeningRun run, IEnumerable<BossSample> samples, DateTime now)
    {
        var seen = new HashSet<long>();
        foreach (var s in samples.Where(x => x.Valid && double.IsFinite(x.Health) && x.Health >= 0 && float.IsFinite(x.X) && float.IsFinite(x.Y)))
        {
            seen.Add(s.Id);
            if (!run.Bosses.TryGetValue(s.Id, out var boss)) run.Bosses[s.Id] = boss = new() { Id = s.Id, Path = s.Path, Name = s.Name, Group = s.Group, Member = s.Member };
            boss.X = s.X; boss.Y = s.Y; boss.Health = s.Health; boss.SeenUtc = now;
            // A valid living flag wins over zero health (component hydration / phased entity).
            boss.Life = !s.Alive && s.Health == 0 ? BossLife.DeadConfirmed : s.Targetable ? BossLife.Alive : BossLife.Dormant;
            boss.DeathEvidence = boss.Life == BossLife.DeadConfirmed ? "valid_entity_not_alive_and_hp_es_zero" : "";
            if (boss.Life != BossLife.DeadConfirmed) run.LootSweptBosses.Remove(s.Id);
        }
        foreach (var b in run.Bosses.Values.Where(x => !seen.Contains(x.Id) && x.Life != BossLife.DeadConfirmed)) b.Life = BossLife.Missing;
    }
    public static bool Complete(AwakeningRun run)
    {
        if (run.Bosses.Count == 0 || run.Bosses.Values.Any(x => x.Life != BossLife.DeadConfirmed || string.IsNullOrEmpty(x.Group))) return false;
        var groups = run.Bosses.Values.GroupBy(x => x.Group).ToList();
        return groups.Count == 1 && Rosters.TryGetValue(groups[0].Key, out var expected)
            && expected.All(member => groups[0].Any(b => b.Member == member));
    }
}

public static class EldritchInvitationPolicy
{
    // Configured calibration is explicit: counters differ across game versions / atlas passives.
    public static InvitationDecision Decide(int? counter, int readyCounter, bool influenceConfirmed) =>
        !influenceConfirmed || counter is null || readyCounter < 0 || counter < 0 ? InvitationDecision.Unknown :
        counter >= readyCounter ? InvitationDecision.Yes : InvitationDecision.No;
}
