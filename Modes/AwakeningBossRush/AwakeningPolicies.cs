using System.Text.RegularExpressions;

namespace AutoExile.Modes.AwakeningBossRush;

public static class AwakeningMapPolicy
{
    // Match both language-independent game IDs and English stat translations. All implicit mods are included.
    // 2026-09-21 (user decision): Less Armour, Less Area of Effect and Less Cooldown Recovery are allowed for the Spark build.
    public static readonly (string Id, string Description, string Pattern)[] Rules =
    [
        ("no_regen", "Players cannot Regenerate Life, Mana or Energy Shield", @"MapNoRegen|no_life_mana_energy_shield_regeneration|cannot Regenerate"),
        ("less_recovery", "Players have #% less Recovery Rate of Life and Energy Shield", @"MapReducedRecovery|recovery.*life.*energy.shield|less Recovery Rate"),
        ("physical_thorns", "Rare Monsters have Physical Thorns reflecting # Physical Damage", @"physical.*thorns|thorns.*physical|Physical Thorns"),
        ("elemental_thorns", "Rare Monsters have Elemental Thorns reflecting # Elemental Damage", @"elemental.*thorns|thorns.*elemental|Elemental Thorns"),
        ("shaper_touched", "Rare monsters in area are Shaper-Touched", @"shaper.?touched"),
        ("max_resists", "Players have #% to all maximum Resistances", @"MapPlayerMaxResists|map_player.*maximum.*resist|to all maximum Resistances"),
        ("defences", "Players have #% more Defences", @"Map.*Defen[cs]|map_player.*defen[cs]|more Defences"),
        ("shaper_influence", "Area is influenced by The Shaper", @"MapShaperInfluence|map_shaper_influence|Area is influenced by The Shaper"),
        ("eradicator", "Map is occupied by The Eradicator", @"ElderMapBoss.*(?:Eradicator|Lightning)|Map.*Eradicator|occupied by The Eradicator"),
        ("purifier", "Map is occupied by The Purifier", @"ElderMapBoss.*(?:Purifier|Holy)|Map.*Purifier|occupied by The Purifier"),
        ("constrictor", "Map is occupied by The Constrictor", @"ElderMapBoss.*(?:Constrictor|Poison)|Map.*Constrictor|occupied by The Constrictor"),
        ("enslaver", "Map is occupied by The Enslaver", @"ElderMapBoss.*(?:Enslaver|Fire)|Map.*Enslaver|occupied by The Enslaver"),
        ("no_leech", "Monsters cannot be Leeched from", @"MapCannotLeech|cannot_be_leeched|cannot.*(?:life|mana).*leech|cannot be Leeched"),
        ("flask_charges", "Players gain #% reduced Flask Charges", @"Map.*FlaskCharges|map_player.*flask.*charge|reduced Flask Charges"),
        // 2026-09-21 (user decision A): one-shots at full ES on a map with these mods.
        ("elemental_weakness", "Players are Cursed with Elemental Weakness", @"MapPlayerCurseElementalWeakness|Cursed with Elemental Weakness"),
        ("extra_lightning", "Monsters deal #% extra Physical Damage as Lightning", @"MapMonsterLightningDamage|extra Physical Damage as Lightning|extra Damage as Lightning"),
        // 2026-09-22 (user decision): Spark is a spell; suppression halves its damage (death on 04:41).
        ("spell_suppress", "Monsters have +#% chance to Suppress Spell Damage", @"spell_suppression|Suppress Spell Damage"),
        // 2026-09-22 (user decision): every "Area has patches of ... Ground" map mod (death rate 2/2).
        ("ground_patches", "Area has patches of # Ground", @"map_ground_effect_patches|map_ground_\w+_base_magnitude|patches of \w+ Ground")
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
    // Seed catalog: metadata/name hints are explicitly logged. Rosters are the known full compositions;
    // MinMembers also accepts other compositions of the same size (e.g. the older Feared line-up).
    public static readonly Dictionary<string, string[]> Rosters = new()
    {
        ["formed"] = ["hydra", "chimera", "minotaur", "phoenix"],
        ["twisted"] = ["eradicator", "purifier", "constrictor", "enslaver"],
        ["forgotten"] = ["altered", "augmented", "twistedsynthete", "rewritten"],
        ["hidden"] = ["xoph", "tul", "esh", "uul"],
        ["remembered"] = ["neglectedflame", "cardinaloffear", "deceitfulgod"],
        ["elderslayers"] = ["baran", "veritania", "alhezmin", "drox"],
        // poewiki (3.28+): the scarab spawns the bosses of a random Maven Invitation: Formed, Twisted, Forgotten,
        // Remembered, Elderslayers or Feared (Hidden kept for older data).
        // Observed 2026-09-21 (Horned Scarab of Awakening): Sirus, Shaper, Incarnation of Dread, Elder, Synthete Nightmare.
        ["feared"] = ["sirus", "shaper", "dread", "elder", "nightmare"]
    };
    public static readonly Dictionary<string, int> MinMembers = new()
    { ["formed"] = 4, ["twisted"] = 4, ["forgotten"] = 4, ["hidden"] = 4, ["remembered"] = 3, ["elderslayers"] = 4, ["feared"] = 5 };
    // The encounter spawns its bosses together. Once every tracked boss is dead and nothing new has appeared for this
    // long, the encounter is treated as complete even if the roster looked short (bosses outside the bubble, new roster).
    public const double SettleSeconds = 10, SettleSecondsFew = 25, RosterShortSeconds = 60;
    // Known group whose members are all dead but fewer than its size: returns the missing roster members.
    public static bool RosterShort(AwakeningRun run, out string[] missing)
    {
        missing = [];
        if (!AllTrackedDead(run)) return false;
        var known = run.Bosses.Values.Where(b => b.Group != "unknown" && !string.IsNullOrEmpty(b.Group)).GroupBy(b => b.Group).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (known == null || !Rosters.TryGetValue(known.Key, out var expected)) return false;
        if (run.Bosses.Values.Select(b => b.Member).Distinct().Count() >= MinMembers.GetValueOrDefault(known.Key, 4)) return false;
        missing = expected.Where(m => !known.Any(b => b.Member == m)).ToArray();
        return missing.Length > 0;
    }
    private static readonly (string Group, string Member, string Pattern)[] Catalog =
    [
        ("formed","hydra",@"Hydra"), ("formed","chimera",@"Chimera"), ("formed","minotaur",@"Minotaur"), ("formed","phoenix",@"Phoenix"),
        ("twisted","eradicator",@"Eradicator|ElderGuardianLightning|ElderGuardian2"), ("twisted","purifier",@"Purifier|ElderGuardianHoly|ElderGuardian4"),
        ("twisted","constrictor",@"Constrictor|ElderGuardianPoison|ElderGuardian3"), ("twisted","enslaver",@"Enslaver|ElderGuardianFire|ElderGuardian1"),
        ("forgotten","altered",@"Altered Synthete|SynthesisGolem"), ("forgotten","augmented",@"Augmented Synthete|SynthesisFabricator"),
        ("forgotten","rewritten",@"Rewritten Synthete|SynthesisGuardian"), ("forgotten","twistedsynthete",@"Twisted Synthete"),
        ("remembered","neglectedflame",@"Neglected Flame|FragmentOfIgnorance"), ("remembered","cardinaloffear",@"Cardinal of Fear|FragmentOfAnger"),
        ("remembered","deceitfulgod",@"Deceitful God|FragmentOfBenevolence"),
        ("hidden","xoph",@"Xoph|BreachBossFire"), ("hidden","tul",@"\bTul\b|BreachBossCold"), ("hidden","esh",@"\bEsh\b|BreachBossLightning"), ("hidden","uul",@"Uul|BreachBossPhysical"),
        ("elderslayers","baran",@"Baran|Crusader"), ("elderslayers","veritania",@"Veritania|Redeemer"),
        ("elderslayers","alhezmin",@"Al.Hezmin|Basilisk"), ("elderslayers","drox",@"Drox|Warlord"),
        ("feared","sirus",@"Sirus|AtlasExile5"), ("feared","dread",@"Incarnation of Dread|BenevolenceBoss"),
        ("feared","nightmare",@"Synthete Nightmare|Synthete Masterpiece|SynthesisSoulstealerBoss"),
        ("feared","atziri",@"Atziri"), ("feared","chayula",@"Chayula|BreachBossChaos"),
        ("feared","shaper",@"ShaperBoss|The Shaper"), ("feared","elder",@"ElderBoss|The Elder"),
        ("feared","cortex",@"SynthesisBoss|Venarius|Synthesised Nightmare")
    ];
    public static (string Group, string Member)? Classify(string path, string name)
    {
        // Boss skills spawn clones (e.g. ElderGuardian3Clone "Empty") that are not roster members and may never die.
        if (Regex.IsMatch(path, @"Clone|Minion|Summoned", RegexOptions.IgnoreCase)) return null;
        foreach (var c in Catalog)
            if (Regex.IsMatch(path + " " + name, c.Pattern, RegexOptions.IgnoreCase)) return (c.Group, c.Member);
        // Unknown pinnacle apparitions still have to die before looting; they never block completion by roster.
        if (Regex.IsMatch(path, @"Standalone", RegexOptions.IgnoreCase) && !Regex.IsMatch(path, @"Banner", RegexOptions.IgnoreCase))
            return ("unknown", path.Split('/').Last().Split('@')[0]);
        return null;
    }
    public static void Observe(AwakeningRun run, IEnumerable<BossSample> samples, DateTime now)
    {
        var seen = new HashSet<long>();
        foreach (var s in samples.Where(x => x.Valid && double.IsFinite(x.Health) && x.Health >= 0 && float.IsFinite(x.X) && float.IsFinite(x.Y)))
        {
            seen.Add(s.Id);
            if (!run.Bosses.TryGetValue(s.Id, out var boss))
            {
                run.Bosses[s.Id] = boss = new() { Id = s.Id, Path = s.Path, Name = s.Name, Group = s.Group, Member = s.Member };
                run.LastNewBossUtc = now;
            }
            boss.X = s.X; boss.Y = s.Y; boss.Health = s.Health; boss.SeenUtc = now;
            // A valid living flag wins over zero health (component hydration / phased entity).
            boss.Life = !s.Alive && s.Health == 0 ? BossLife.DeadConfirmed : s.Targetable ? BossLife.Alive : BossLife.Dormant;
            boss.DeathEvidence = boss.Life == BossLife.DeadConfirmed ? "valid_entity_not_alive_and_hp_es_zero" : "";
            if (boss.Life != BossLife.DeadConfirmed) run.LootSweptBosses.Remove(s.Id);
        }
        foreach (var b in run.Bosses.Values.Where(x => !seen.Contains(x.Id) && x.Life != BossLife.DeadConfirmed)) b.Life = BossLife.Missing;
    }
    public static bool AllTrackedDead(AwakeningRun run) =>
        run.Bosses.Count > 0 && run.Bosses.Values.All(x => x.Life == BossLife.DeadConfirmed);
    public static bool Complete(AwakeningRun run) => Complete(run, DateTime.UtcNow, out _);
    public static bool Complete(AwakeningRun run, DateTime now, out string basis)
    {
        basis = "";
        if (!AllTrackedDead(run)) return false;
        var bosses = run.Bosses.Values.ToList();
        var members = bosses.Select(b => b.Member).Distinct().Count();
        var known = bosses.Where(b => b.Group != "unknown" && !string.IsNullOrEmpty(b.Group)).GroupBy(b => b.Group).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (known != null && Rosters.TryGetValue(known.Key, out var expected) && expected.All(m => known.Any(b => b.Member == m)))
        { basis = "roster:" + known.Key; return true; }
        if (known != null && members >= MinMembers.GetValueOrDefault(known.Key, 4)) { basis = "member_count:" + known.Key; return true; }
        var quiet = run.LastNewBossUtc.HasValue ? (now - run.LastNewBossUtc.Value).TotalSeconds : double.MaxValue;
        // A known group that is short of members keeps searching for up to RosterShortSeconds after the last new boss.
        if (known != null && Rosters.ContainsKey(known.Key))
        {
            if (quiet >= RosterShortSeconds) { basis = "roster_short_timeout:" + known.Key; return true; }
            return false;
        }
        if (members >= 3 && quiet >= SettleSeconds) { basis = "settled"; return true; }
        if (quiet >= SettleSecondsFew) { basis = "settled_few"; return true; }
        return false;
    }
}

public static class EldritchInvitationPolicy
{
    // Configured calibration is explicit: counters differ across game versions / atlas passives.
    public static InvitationDecision Decide(int? counter, int readyCounter, bool influenceConfirmed) =>
        !influenceConfirmed || counter is null || readyCounter < 0 || counter < 0 ? InvitationDecision.Unknown :
        counter >= readyCounter ? InvitationDecision.Yes : InvitationDecision.No;
}
