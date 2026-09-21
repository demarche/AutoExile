using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;

namespace AutoExile.Modes.AwakeningBossRush;

[Submenu]
public sealed class AwakeningSettings
{
    [Menu("Allow manual Insert start", "From Hideout, Insert starts continuous farming. Verified success and stash completion start the next map. Death also returns to the Hideout and continues (see Continue after death). Insert, missing supplies or an operational failure stop the loop. No Codex code review is claimed.")]
    public ToggleNode AllowManualStart { get; set; } = new(false);
    [Menu("Continue after death", "During Insert continuous farming, a death returns to the Hideout, stashes, and retries the remaining portals or opens a new map until materials run out. Off = stop after a death.")]
    public ToggleNode ContinueAfterDeath { get; set; } = new(true);
    [Menu("Loot defense radius (grids)", "While looting, only enemies closer than this interrupt pickup. Farther enemies never pull the bot away from the drops.")]
    public RangeNode<int> LootDefenseRadius { get; set; } = new(30, 10, 60);
    [Menu("Calibrated Atlas node offset")]
    public RangeNode<int> AtlasNodeOffset { get; set; } = new(2, -10, 10);
    [Menu("Calibrated Exarch choice index", "-1 uses the typed option name. Set only after observing the actual device icons.")]
    public RangeNode<int> ExarchChoiceIndex { get; set; } = new(-1, -1, 2);
    [Menu("Minimum extra loot (chaos/stack)")]
    public RangeNode<float> MinStackChaos { get; set; } = new(1, 0, 1000);
    [Menu("Attempt timeout (seconds)")]
    public RangeNode<int> TimeoutSeconds { get; set; } = new(300, 30, 300);
    [Menu("Map purchase cost (chaos)", "0 = unknown; prevents misleading net profit estimates.")]
    public RangeNode<float> MapCostChaos { get; set; } = new(0, 0, 5000);
    [Menu("Material stash tab", "Blank = discover each material using StashIndexer.")]
    public TextNode SupplyTab { get; set; } = new("");
    [Menu("Exarch ready counter", "-1 until calibrated against the atlas in this game version. Unknown never silently skips the map boss.")]
    public RangeNode<int> ExarchReadyCounter { get; set; } = new(-1, -1, 100);
    [Menu("Exarch UI path", "Child indices of the Exarch selector relative to Atlas. Inspect before configuring; no blind clicks.")]
    public TextNode ExarchSelectorPath { get; set; } = new("");
    [Menu("Exarch selected child path", "Relative to Atlas; a visible child that only exists when Exarch is selected. Used to verify selection.")]
    public TextNode ExarchSelectedPath { get; set; } = new("");
    [Menu("Spark no-damage reposition (s)")]
    public RangeNode<float> NoDamageSeconds { get; set; } = new(3, 1, 10);
    [Menu("Loot settle (s)")]
    public RangeNode<float> LootSettleSeconds { get; set; } = new(2, 1, 5);
    [Menu("Maximum price age (minutes)")]
    public RangeNode<int> MaxPriceAgeMinutes { get; set; } = new(60, 5, 240);
    [Menu("NG stat catalog validated", "Enable only after verifying all 17 user rules against current game stat IDs. Read-only inspection is available in the evidence dump.")]
    public ToggleNode ModCatalogValidated { get; set; } = new(false);
    [Menu("Boss catalog validated", "Enable after verifying the seed metadata classifiers and invitation rosters on current game data. Combat works before this; success is withheld.")]
    public ToggleNode BossCatalogValidated { get; set; } = new(false);
    public AwakeningEconomySettings Economy { get; set; } = new();
}
