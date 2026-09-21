using System.Text.Json;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

/// <summary>Supply / sale automation and the economy ledger. Every value is editable from the Web dashboard.</summary>
[Submenu]
public sealed class AwakeningEconomySettings
{
    [Menu("Economy ledger", "Record material/map prices (Invest), sales and loot (Profit) and show a separate economy chaos/hour.")]
    public ToggleNode LedgerEnabled { get; set; } = new(true);
    [Menu("Stop when economy is negative after N maps", "0 = never. After this many maps in the session, stop the loop if the economy chaos/hour is below zero.")]
    public RangeNode<int> StopIfNegativeAfterMaps { get; set; } = new(20, 0, 500);

    [Menu("Auto restock via Faustus", "Buy Sacrifice fragments / Horned Scarab of Awakening with Chaos when one runs out (needs Faustus UI calibration).")]
    public ToggleNode AutoRestock { get; set; } = new(false);
    [Menu("Scarab restock target", "Horned Scarab of Awakening count after a restock.")]
    public RangeNode<int> ScarabTarget { get; set; } = new(5, 1, 60);
    [Menu("Sacrifice restock target (each)", "Each Sacrifice at Dawn/Noon/Dusk/Midnight count after a restock.")]
    public RangeNode<int> SacrificeTarget { get; set; } = new(20, 1, 200);
    [Menu("Stop when Chaos cannot pay a restock")]
    public ToggleNode StopWhenOutOfChaos { get; set; } = new(true);

    [Menu("Sell Chisels/Writ when Chaos is short", "Sell Maven's Chisels and The Maven's Writ at the best bid to fund a restock.")]
    public ToggleNode SellWhenChaosShort { get; set; } = new(true);
    [Menu("List Chisels/Writ below this Chaos", "When held Chaos drops below this, list them at (lowest ask - undercut).")]
    public RangeNode<int> ListBelowChaos { get; set; } = new(400, 0, 10000);
    [Menu("Listing undercut (chaos per unit)")]
    public RangeNode<float> ListUndercutChaos { get; set; } = new(1, 0, 100);

    [Menu("Auto buy maps", "When no acceptable T16 map is left, buy maps (needs market UI calibration).")]
    public ToggleNode AutoBuyMaps { get; set; } = new(false);
    [Menu("Maps to buy per restock")]
    public RangeNode<int> MapBuyCount { get; set; } = new(20, 1, 100);
    [Menu("Bank maps in Tmp", "Before F3, store acceptable T16 maps (dropped or bought) in the Tmp tab so the next map does not need a market trip.")]
    public ToggleNode BankMapsInTmp { get; set; } = new(true);
    [Menu("Max price per map (chaos)")]
    public RangeNode<float> MapMaxUnitChaos { get; set; } = new(50, 1, 1000);
    [Menu("Seller follow-up: max over first price (chaos)", "Buy more maps from the same seller tab if within this much of the first price.")]
    public RangeNode<float> MapFollowUpOverChaos { get; set; } = new(1, 0, 50);
    [Menu("Map minimum item quantity (%)")]
    public RangeNode<int> MapMinQuantity { get; set; } = new(115, 0, 300);
    [Menu("Map minimum pack size (%)")]
    public RangeNode<int> MapMinPackSize { get; set; } = new(40, 0, 200);
    [Menu("Map minimum prefixes + suffixes", "4+4 = eight-mod corrupted maps.")]
    public RangeNode<int> MapMinAffixesEach { get; set; } = new(4, 0, 4);
}

/// <summary>
/// Invest = materials and maps consumed (actual purchase price when bought, market price otherwise).
/// Profit = sales (realized) and looted value (market price). Economy chaos/hour is kept apart from the run statistic.
/// Everything is appended to economy.jsonl; one line per attempt goes to runs.jsonl for mod analysis.
/// </summary>
public sealed class AwakeningLedger
{
    private readonly string _economyFile, _runsFile;
    private readonly object _gate = new();
    public DateTime SessionStart { get; private set; } = DateTime.UtcNow;
    public double Invest { get; private set; }
    public double LootValue { get; private set; }
    public double Realized { get; private set; }
    public int Maps { get; private set; }
    public double OperatingSeconds { get; private set; }
    public string Error { get; private set; } = "";
    // Loot is valued (poe.ninja) when it is picked up; selling it later only converts it to Chaos, so Realized is
    // reported separately and NOT added again (13:50: 576c of Chisel sales had doubled the hourly rate to 5556c/h).
    public double? ChaosPerHour => OperatingSeconds > 60 ? (LootValue - Invest) * 3600 / OperatingSeconds : null;

    public AwakeningLedger(string directory)
    {
        _economyFile = Path.Combine(directory, "economy.jsonl");
        _runsFile = Path.Combine(directory, "runs.jsonl");
    }
    public void ResetSession() { lock (_gate) { SessionStart = DateTime.UtcNow; Invest = LootValue = Realized = OperatingSeconds = 0; Maps = 0; } }
    public object Summary() => new { sessionStartUtc = SessionStart, maps = Maps, invest = Math.Round(Invest, 1), lootValue = Math.Round(LootValue, 1),
        realized = Math.Round(Realized, 1), operatingMinutes = Math.Round(OperatingSeconds / 60, 1), chaosPerHour = ChaosPerHour is { } v ? Math.Round(v) : (double?)null, error = Error };

    public void Price(string item, double unitChaos, string source) => Append(_economyFile, new { utc = DateTime.UtcNow, type = "price", item, unitChaos, source });
    public void AddInvest(string item, int quantity, double unitChaos, string source)
    {
        lock (_gate) Invest += quantity * unitChaos;
        Append(_economyFile, new { utc = DateTime.UtcNow, type = "invest", item, quantity, unitChaos, total = quantity * unitChaos, source });
    }
    // Purchases are price history (what supplies really cost); Invest is booked when a map consumes them.
    public void Purchase(string item, int quantity, double unitChaos, string source) =>
        Append(_economyFile, new { utc = DateTime.UtcNow, type = "purchase", item, quantity, unitChaos, total = quantity * unitChaos, source });
    // A listing is not income until it fills; it is recorded so fills can be matched later (Profit is booked on sale).
    public void Listing(string item, int quantity, double unitChaos, string source) =>
        Append(_economyFile, new { utc = DateTime.UtcNow, type = "listing", item, quantity, unitChaos, total = quantity * unitChaos, source });
    public void AddLoot(string item, int quantity, double stackChaos)
    {
        lock (_gate) LootValue += stackChaos;
        Append(_economyFile, new { utc = DateTime.UtcNow, type = "loot", item, quantity, total = stackChaos });
    }
    public void AddSale(string item, int quantity, double unitChaos, string source)
    {
        lock (_gate) Realized += quantity * unitChaos;
        Append(_economyFile, new { utc = DateTime.UtcNow, type = "sale", item, quantity, unitChaos, total = quantity * unitChaos, source });
    }
    public void AddTime(double operatingSeconds, bool mapFinished) { lock (_gate) { if (mapFinished) Maps++; if (double.IsFinite(operatingSeconds) && operatingSeconds > 0) OperatingSeconds += operatingSeconds; } }
    public void Run(object record) => Append(_runsFile, record);

    private void Append(string file, object value)
    {
        try
        {
            var line = JsonSerializer.Serialize(value, AwakeningJson.Options) + Environment.NewLine;
            lock (_gate) File.AppendAllText(file, line);
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}

/// <summary>Dumps visible UI element trees and drives single UI actions so new panels (Faustus exchange, market) can be calibrated without screenshots.</summary>
public static class AwakeningUiInspector
{
    public static string Dump(GameController gc, string directory, string label, string? rootPath = null, int maxDepth = 9, bool includeHidden = false)
    {
        _includeHidden = includeHidden;
        var root = gc.IngameState.IngameUi;
        var nodes = new List<object>();
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var start = FindByPath(root, rootPath);
            if (start != null) nodes.Add(Node(start, rootPath, 0, maxDepth));
        }
        else
        {
            var children = root.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child?.IsVisible != true) continue;
                nodes.Add(Node(child, i.ToString(), 0, maxDepth));
            }
        }
        object? exchange = null;
        try { var panel = gc.IngameState.IngameUi.CurrencyExchangePanel; if (rootPath == null && panel?.IsVisible == true) exchange = Node(panel, "CurrencyExchangePanel", 0, maxDepth + 3); } catch { }
        var file = Path.Combine(directory, $"ui-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, label, window = gc.Window.GetWindowRectangle().ToString(), visibleRoots = nodes, exchange },
            new JsonSerializerOptions { WriteIndented = false }));
        return file;
    }
    /// <summary>Dumps the items under a UI grid (e.g. a seller's "Select Items To Buy" tab): entity, map read-out,
    /// listed price and every scalar property of the inventory element (to find the "highlighted" flag).</summary>
    public static string DumpItems(GameController gc, string directory, string label, string rootPath)
    {
        var start = FindByPath(gc.IngameState.IngameUi, rootPath);
        var items = new List<object>();
        if (start != null)
        {
            var kids = start.Children;
            for (var i = 0; i < kids.Count; i++)
            {
                var el = kids[i];
                if (el == null) continue;
                object? row = null;
                try
                {
                    var inv = el.AsObject<ExileCore.PoEMemory.Elements.InventoryElements.NormalInventoryItem>();
                    var entity = inv?.Item;
                    var props = new Dictionary<string, object?>();
                    if (inv != null)
                        foreach (var pr in inv.GetType().GetProperties())
                        {
                            if (pr.GetIndexParameters().Length > 0) continue;
                            var t = pr.PropertyType;
                            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;
                            try { props[pr.Name] = pr.GetValue(inv)?.ToString(); } catch { }
                        }
                    object? price = null;
                    try
                    {
                        var baseComp = entity?.GetComponent<ExileCore.PoEMemory.Components.Base>();
                        price = AwakeningGameReader.Property(baseComp, "PublicPrice");
                    }
                    catch { }
                    var map = entity != null ? AwakeningGameReader.ReadMap(gc, entity) : null;
                    row = new
                    {
                        p = rootPath + "," + i, r = el.GetClientRect().ToString(), visible = el.IsVisible,
                        path = entity?.Path, name = entity != null ? AwakeningGameReader.Name(gc, entity) : null,
                        price = price?.ToString(), tier = map?.Tier, quantity = map?.Quantity,
                        rejections = map != null ? AwakeningMapPolicy.Rejections(map) : null,
                        mods = map?.Mods.Select(m => m.Text).ToArray(), props
                    };
                }
                catch (Exception ex) { row = new { p = rootPath + "," + i, error = ex.Message }; }
                items.Add(row);
            }
        }
        var file = Path.Combine(directory, $"items-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, label, rootPath, found = start != null, items }));
        return file;
    }
    /// <summary>"50,2,3" = IngameUi child 50 → child 2 → child 3.</summary>
    public static Element? FindByPath(Element root, string path)
    {
        Element? e = root;
        foreach (var part in path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (e == null || !int.TryParse(part, out var i)) return null;
            try { e = e.GetChildAtIndex(i); } catch { return null; }
        }
        return e;
    }
    /// <summary>First visible element whose text equals (or, with a trailing *, starts with) the given text.</summary>
    public static Element? FindByText(Element root, string text, string? underPath = null)
    {
        var start = underPath != null ? FindByPath(root, underPath) : root;
        if (start == null) return null;
        var prefix = text.EndsWith('*'); var needle = prefix ? text[..^1] : text;
        var stack = new Stack<(Element, int)>(); stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (e, d) = stack.Pop();
            IList<Element>? kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || d > 14) continue;
            for (var i = kids.Count - 1; i >= 0; i--)
            {
                var k = kids[i];
                if (k == null || !k.IsVisible) continue;
                string t = ""; try { t = k.Text ?? ""; } catch { }
                if (prefix ? t.Trim().StartsWith(needle, StringComparison.OrdinalIgnoreCase) : t.Trim().Equals(needle, StringComparison.OrdinalIgnoreCase)) return k;
                stack.Push((k, d + 1));
            }
        }
        return null;
    }
    private static bool _includeHidden;
    private static object Node(Element e, string path, int depth, int maxDepth)
    {
        string? text = null; string rect = ""; int count = 0;
        try { text = e.Text; } catch { }
        try { rect = e.GetClientRect().ToString(); } catch { }
        try { count = Convert.ToInt32(e.ChildCount); } catch { }
        var kids = new List<object>();
        if (depth < maxDepth)
            try
            {
                var list = e.Children;
                for (var i = 0; i < list.Count && i < 150; i++)
                    if (list[i] != null && (_includeHidden || list[i].IsVisible)) kids.Add(Node(list[i], path + "," + i, depth + 1, maxDepth));
            }
            catch { }
        bool? hidden = null; if (_includeHidden) try { hidden = !e.IsVisible ? true : null; } catch { }
        return new { p = path, t = string.IsNullOrEmpty(text) ? null : text, r = rect, n = count, h = hidden, c = kids.Count > 0 ? kids : null };
    }
}
