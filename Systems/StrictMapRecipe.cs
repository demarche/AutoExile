using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Modes.AwakeningBossRush;

namespace AutoExile.Systems;

/// <summary>Opt-in exact material multiset; other map modes retain their existing policy.</summary>
public sealed class StrictMapRecipe
{
    public required IReadOnlyList<RecipeItem> Materials { get; init; }
    public required Func<Entity, bool> MapAllowed { get; init; }
    public required Func<GameController, (bool Ready, string Reason)> PrepareActivation { get; init; }
    public required Func<bool> RecordActivationRequest { get; init; }
    public required Action<IReadOnlyList<long>> ConfirmActivation { get; init; }
    public HashSet<long> PriorPortals { get; set; } = new();
    public bool ActivationSent { get; set; }

    public static List<Entity>? ReadSlots(GameController gc)
    {
        var atlas = gc.IngameState.IngameUi.Atlas;
        if (atlas?.IsVisible != true || atlas.GetChildAtIndex(7)?.IsVisible != true) return null;
        var slots = atlas.GetChildFromIndices(7, 0, 2);
        if (slots == null || slots.ChildCount != 6) return null;
        var result = new List<Entity>();
        for (int i = 0; i < 6; i++)
        {
            var slot = slots.GetChildAtIndex(i);
            if (slot == null) return null;
            if (slot.ChildCount < 2) continue;
            var item = slot.GetChildAtIndex(1)?.Entity;
            if (item?.IsValid != true || string.IsNullOrEmpty(item.Path)) return null;
            result.Add(item);
        }
        return result;
    }
    public bool Verify(GameController gc, out string reason)
    {
        var slots = ReadSlots(gc);
        if (slots == null) { reason = "device_slots_unreadable"; return false; }
        var maps = slots.Where(x => x.Path.Contains("/Maps/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (maps.Count != 1 || !MapAllowed(maps[0])) { reason = "map_not_allowed"; return false; }
        if (!AwakeningLootPolicy.RecipeComplete(slots.Except(maps).Select(x => x.Path).ToList(), Materials))
        { reason = "material_multiset_mismatch"; return false; }
        reason = "verified"; return true;
    }
    public static List<Entity> Portals(GameController gc) => gc.EntityListWrapper.OnlyValidEntities.Where(e =>
        e.IsTargetable && (e.Type == ExileCore.Shared.Enums.EntityType.TownPortal || e.Path.Contains("Town_Portals"))).ToList();
}
