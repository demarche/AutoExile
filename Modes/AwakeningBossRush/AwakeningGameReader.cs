using System.Collections;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

public static class AwakeningGameReader
{
    public static string Name(GameController gc, Entity item) => gc.Files.BaseItemTypes.Translate(item.Path)?.BaseName ?? item.GetComponent<Base>()?.Name ?? item.Path;
    public static int Quantity(Entity e) => Math.Max(1, e.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1);
    // Newer host APIs are optional at compile time: the shipped Resources reference can lag the live host.
    public static object? Property(object? value, string property) => value?.GetType().GetProperty(property)?.GetValue(value);
    private static readonly System.Reflection.MethodInfo? GetComponent = typeof(Entity).GetMethods().FirstOrDefault(x => x.Name == "GetComponent" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
    private static int ReadTier(Entity entity)
    {
        foreach (var name in new[] { "MapKey", "Map" })
        {
            var type = typeof(Entity).Assembly.GetType("ExileCore.PoEMemory.Components." + name);
            if (type == null) continue;
            var component = GetComponent?.MakeGenericMethod(type).Invoke(entity, null);
            var tier = Property(component, "Tier");
            if (tier != null) return Convert.ToInt32(tier);
        }
        return 0;
    }
    private static IEnumerable<object> ExarchChoices(object atlas) =>
        (Property(Property(atlas, "MapDeviceWindow"), "PrimordialBossSelectorButtons") as IEnumerable)?.Cast<object>() ?? [];
    private static string ChoiceName(object choice) => Property(Property(choice, "Option"), "Name")?.ToString() ?? "";
    private static string Translation(object? value) => value is string text ? text : value is IEnumerable lines
        ? string.Join("\n", lines.Cast<object>().Select(x => x?.ToString())) : value?.ToString() ?? "";
    /// <summary>True while the chat input box is open (keys then type into chat instead of acting as hotkeys).</summary>
    public static bool ChatOpen(GameController gc)
    {
        try { return (Property(Property(gc.IngameState.IngameUi, "ChatPanel"), "ChatInputElement") as Element)?.IsVisible == true; }
        catch { return false; }
    }
    public static bool? StashieBusy()
    {
        try { return Core.ParallelRunner.FindByName("Stashie_DropItemsToStash") != null || Core.ParallelRunner.FindByName("Drop To Stash") != null; }
        catch { return null; }
    }
    public static MapObservation ReadMap(GameController gc, Entity entity, string layout = "")
    {
        try
        {
            var mods = entity.GetComponent<Mods>();
            var tier = ReadTier(entity);
            var implicitNames = mods?.ImplicitMods?.Select(x => x.RawName).ToHashSet() ?? new();
            var observations = mods?.ItemMods?.Select(m => new ModObservation(m.RawName, m.Group,
                Translation(m.Translation) + " " + m.DisplayName, m.ModRecord?.StatNames?.Select(x => x.ToString()).ToArray() ?? [],
                m.Values.Select(x => (int)x).ToArray(), implicitNames.Contains(m.RawName))).ToList() ?? new();
            return new(entity.Path, Name(gc, entity), tier, mods?.Identified == true, entity.IsValid && mods?.ItemMods != null && tier > 0,
                observations, MapModChecker.GetItemQuantity(entity), layout, mods?.ItemRarity == ItemRarity.Normal);
        }
        catch { return new(entity.Path ?? "", "", 0, false, false, []); }
    }
    public static InventoryObservation Inventory(GameController gc)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var items = StashSystem.GetInventorySlotItems(gc);
            if (items == null || gc.IsLoading || gc.Player?.IsValid != true) return new(false, -1, counts);
            int outside = 0;
            foreach (var slot in items)
            {
                if (slot.Item?.IsValid != true || string.IsNullOrEmpty(slot.Item.Path) || slot.PosX < 0 || slot.PosY < 0 || slot.SizeX < 1 || slot.SizeY < 1
                    || slot.PosX + slot.SizeX > 12 || slot.PosY + slot.SizeY > 5) return new(false, -1, counts);
                counts[slot.Item.Path] = counts.GetValueOrDefault(slot.Item.Path) + Quantity(slot.Item);
                // Every item starting left of column 11 occupies at least one nonreserved cell.
                if (slot.PosX < 11) outside++;
            }
            return new(true, outside, counts);
        }
        catch { return new(false, -1, counts); }
    }
    public static List<BossSample> Bosses(BotContext ctx, AwakeningRun run)
    {
        var result = new List<BossSample>();
        foreach (var e in ctx.Game.EntityListWrapper.OnlyValidEntities)
        {
            try
            {
                if (e.Type != EntityType.Monster || (!run.Bosses.ContainsKey(e.Id) && (!e.IsHostile || e.Rarity != MonsterRarity.Unique))) continue;
                var classification = AwakeningBossTracker.Classify(e.Path, e.RenderName ?? "");
                if (!classification.HasValue && !run.Bosses.ContainsKey(e.Id)) continue;
                var life = e.GetComponent<Life>();
                if (life == null) continue;
                var old = run.Bosses.GetValueOrDefault(e.Id);
                result.Add(new(e.Id, e.Path, e.RenderName ?? "", classification?.Group ?? old!.Group,
                    classification?.Member ?? old!.Member, e.GridPosNum.X, e.GridPosNum.Y, e.IsAlive, e.IsTargetable,
                    (double)life.CurHP + life.CurES, e.IsValid));
            }
            catch { /* No fabricated deaths on an invalid read. */ }
        }
        return result;
    }
    public static Element? Child(Element? root, string path)
    {
        if (root == null || string.IsNullOrWhiteSpace(path)) return null;
        try { return root.GetChildFromIndices(path.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToArray()); }
        catch { return null; }
    }
    public static (bool Ready, string Reason) EnsureExarch(BotContext ctx)
    {
        try
        {
            var atlas = ctx.Game.IngameState.IngameUi.Atlas;
            var choices = ExarchChoices(atlas).ToList();
            var exarch = choices.FirstOrDefault(x => ChoiceName(x).Contains("Exarch", StringComparison.OrdinalIgnoreCase)) as Element;
            var calibrated = ctx.Settings.Awakening.ExarchChoiceIndex.Value;
            if (exarch == null && calibrated >= 0 && calibrated < choices.Count) exarch = choices[calibrated] as Element;
            if (exarch != null)
            {
                if (!exarch.IsVisible) return (false, "exarch_selector_not_visible");
                if (Property(exarch, "OptionIsSelected") is true) return (true, "typed_exarch_selected");
                if (exarch.IsVisible && BotInput.CanAct) BotInput.ClickLabel(ctx.Game, exarch.GetClientRect());
                return (false, "selecting_exarch");
            }
            var settings = ctx.Settings.Awakening;
            var selected = Child(atlas, settings.ExarchSelectedPath.Value);
            if (selected?.IsVisible == true) return (true, "configured_exarch_selected_indicator");
            var selector = Child(atlas, settings.ExarchSelectorPath.Value);
            if (selector?.IsVisible == true && BotInput.CanAct) BotInput.ClickLabel(ctx.Game, selector.GetClientRect());
            return (false, "Exarch selector unavailable; inspect device evidence before configuring UI paths");
        }
        catch (Exception ex) { return (false, "Exarch read: " + ex.Message); }
    }
    public static object DeviceEvidence(GameController gc)
    {
        try
        {
            var atlas = gc.IngameState.IngameUi.Atlas;
            var mapPanel = atlas.GetChildFromIndices(3, 0, 1);
            var maps = new List<object>();
            if (atlas.IsVisible && mapPanel != null)
                for (int i = 0; i < mapPanel.ChildCount; i++)
                {
                    var child = mapPanel.GetChildAtIndex(i);
                    if (child?.Type == ElementType.InventoryItem && child.Entity?.IsValid == true && child.Entity.Path.Contains("/Maps/"))
                        maps.Add(new { child.Entity.Id, map = ReadMap(gc, child.Entity) });
                }
            return new { exarchCounter = gc.IngameState.ServerData.SearingExarchCounter,
                exarchText = atlas.SearingExarchCounterElement?.Text,
                atlasVisible = atlas.IsVisible, deviceVisible = atlas.GetChildAtIndex(7)?.IsVisible == true,
                selectedLayout = atlas.GetChildAtIndex(7)?.IsVisible == true ? atlas.GetChildFromIndices(7, 0, 1, 0, 0)?.Text : null, maps,
                choices = ExarchChoices(atlas).Select(x => new { option = ChoiceName(x), description = Property(Property(x, "Option"), "Description")?.ToString(),
                    descriptionActive = Property(Property(x, "Option"), "DescriptionActive")?.ToString(), selected = Property(x, "OptionIsSelected"), visible = (x as Element)?.IsVisible }).ToArray(),
                nodes = gc.Files.AtlasNodes.EntriesList.Select((n, i) => new { index = i, id = Property(n, "Id")?.ToString(), name = n.Area?.Name, area = n.Area?.Id,
                    ui = AtlasNodeEvidence(atlas.GetChildAtIndex(0)?.GetChildAtIndex(i + 2)) }).ToArray(),
                choicesUi = ExarchChoices(atlas).Select(x => AtlasNodeEvidence(x as Element)).ToArray(),
                items = StrictMapRecipe.ReadSlots(gc)?.Select(x => new { x.Path, name = Name(gc, x) }).ToArray() };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }
    private static object? AtlasNodeEvidence(Element? element)
    {
        if (element == null) return null;
        var rect = element.GetClientRect();
        return new { element.Text, element.IsVisible, element.ChildCount, x = rect.Center.X, y = rect.Center.Y, rect.Width, rect.Height };
    }
}
