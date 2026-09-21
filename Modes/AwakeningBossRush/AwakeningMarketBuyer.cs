using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

/// <summary>What to buy: the market filters themselves (Map Tier 16, IIQ, Pack Size, NOT-group of NG mods, category Map)
/// are saved in the game's market UI; the bot re-checks every candidate against its own rules before paying.</summary>
public sealed record MarketMapRequest(int Count, double MaxUnitChaos, double FollowUpOverChaos, int MinQuantity, int MinPackSize, int MinAffixesEach);

/// <summary>
/// Buys T16 maps through the in-game market ("/"): search → pick the cheapest acceptable listing → travel to the
/// seller's hideout → Ctrl+click acceptable maps in the open "Select Items To Buy" tab (first price + follow-up margin)
/// → "/hideout". Every step is one gated input per tick; every purchase is logged and handed back for the ledger.
/// UI layout verified live on 2026-09-21 (3.29): market root child 1 = "The Market", results root child 0,0 = "Search Results",
/// result list = results[0,3,1,0], travel button = entry[0,1,2,0,4,0], seller window child 3 = "Select Items To Buy",
/// seller grid = window[8,1,(visible tab),0]. Ctrl+click buys directly when the price is unchanged.
/// </summary>
public sealed class AwakeningMarketBuyer
{
    private enum Step { Idle, OpenMarket, Search, Read, Travel, Arrive, Buy, Home, Done, Failed }
    private Step _step = Step.Idle;
    private DateTime _stepAt, _startedAt, _actionAt, _clickAt;
    private readonly Action<string, object> _log;
    private readonly Queue<Keys> _keys = new();
    private MarketMapRequest? _request;
    private readonly HashSet<string> _skippedSellers = new();
    private string _seller = "", _homeArea = "";
    private long _homeHash, _sellerHash;
    private double _firstPrice;
    private int _gridCountBefore = -1, _pendingIndex = -1, _failedClicks, _searches, _mapsBefore;
    private double _pendingPrice;
    private string _pendingName = "";

    public string Status { get; private set; } = "";
    public string FailReason { get; private set; } = "";
    public bool Busy => _step is not (Step.Idle or Step.Done or Step.Failed);
    public bool Succeeded => _step == Step.Done;
    public int Bought { get; private set; }
    public double Spent { get; private set; }
    public List<(string Seller, double Price, string[] Mods)> Purchases { get; } = new();

    public AwakeningMarketBuyer(Action<string, object> log) { _log = log; }

    public void Start(GameController gc, MarketMapRequest request)
    {
        _request = request; _keys.Clear(); _skippedSellers.Clear(); Purchases.Clear();
        Bought = 0; Spent = 0; FailReason = ""; _searches = 0;
        _homeArea = gc.Area?.CurrentArea?.Name ?? ""; _homeHash = AreaHash(gc);
        _startedAt = DateTime.UtcNow; Set(Step.OpenMarket, "opening the market");
        _log("market.started", request);
    }
    public void Cancel() { _keys.Clear(); if (Busy) Fail("cancelled"); }

    public void Tick(BotContext ctx)
    {
        if (!Busy || _request == null) return;
        var gc = ctx.Game;
        if ((DateTime.UtcNow - _startedAt).TotalMinutes > 15) { GoHomeOrFail(gc, "overall_timeout:" + _step); return; }
        // A travel click that never leaves the hideout (seller offline / listing gone): try the next seller.
        if (_step == Step.Arrive && _sellerHash == 0 && AreaHash(gc) == _homeHash && (DateTime.UtcNow - _stepAt).TotalSeconds > 20 && _searches < 6)
        { _log("market.travel_failed", new { seller = _seller }); _skippedSellers.Add(_seller); Set(Step.Search, "travel failed, next seller"); return; }
        var limit = _step switch { Step.Arrive or Step.Home => 60, Step.Buy => 180, _ => 25 };
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > limit) { GoHomeOrFail(gc, "step_timeout:" + _step); return; }
        if (_keys.Count > 0) { if (BotInput.CanAct && BotInput.PressKey(_keys.Peek())) _keys.Dequeue(); return; }
        if (gc.IsLoading || (DateTime.UtcNow - _actionAt).TotalMilliseconds < 400 || !BotInput.CanAct) return;
        switch (_step)
        {
            case Step.OpenMarket: TickOpen(gc); break;
            case Step.Search: TickSearch(gc); break;
            case Step.Read: TickRead(gc); break;
            case Step.Travel: break;
            case Step.Arrive: TickArrive(gc); break;
            case Step.Buy: TickBuy(gc); break;
            case Step.Home: TickHome(gc); break;
        }
    }

    // ── Market search ────────────────────────────────────────────────
    private void TickOpen(GameController gc)
    {
        if (MarketRoot(gc) != null) { Set(Step.Search, "searching"); return; }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds < 0.3) return;
        if (BotInput.PressKey(Keys.OemQuestion)) { _actionAt = DateTime.UtcNow; Status = "Market: pressed / to open"; }
    }
    private void TickSearch(GameController gc)
    {
        var market = MarketRoot(gc);
        if (market == null) { Set(Step.OpenMarket, "market closed, reopening"); return; }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds < 1.5) return; // a click right after "/" is ignored by the freshly opened panel
        // Two elements read "search": the tab at the top and the button at the bottom — the button is the lower one.
        var button = Descendants(market).Where(e => Text(e).Trim().Equals("search", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.GetClientRect().Y).FirstOrDefault();
        if (button == null) { Status = "Market: search button not found"; return; }
        _searches++;
        if (_searches > 6) { Fail("too_many_searches"); return; }
        Click(gc, button);
        Set(Step.Read, "waiting for results");
    }
    private void TickRead(GameController gc)
    {
        if ((DateTime.UtcNow - _stepAt).TotalSeconds < 2.5) return; // results replace the previous list asynchronously
        var list = ResultList(gc);
        var rows = list?.Children.Where(x => x != null && x.IsVisible).Select(ParseResult).ToList() ?? new();
        if (rows.Count == 0 && !NoResultsShown(gc))
        {
            // The results pane opens (or refreshes) asynchronously; search again if it never showed up.
            if ((DateTime.UtcNow - _stepAt).TotalSeconds > 8) Set(Step.Search, "no results pane, searching again");
            else Status = "Market: waiting for the results list";
            return;
        }
        _log("market.results", new { count = rows.Count, rows = rows.Select(r => new { r.Name, r.Price, r.Currency, r.Seller, r.Quantity, r.Pack, r.Prefix, r.Suffix, reject = r.Reject }) });
        var pick = rows.FirstOrDefault(r => r.Reject == null && !_skippedSellers.Contains(r.Seller));
        if (pick == null) { GoHomeOrFail(gc, rows.Count == 0 ? "no_results" : "no_acceptable_listing"); return; }
        var travel = Child(pick.Entry, 0, 1, 2, 0, 4, 0);
        if (travel == null) { Fail("travel_button_not_found"); return; }
        _seller = pick.Seller; _firstPrice = pick.Price; _sellerHash = 0;
        Click(gc, travel);
        _log("market.travel", new { seller = _seller, price = _firstPrice, name = pick.Name });
        Set(Step.Arrive, $"travelling to {_seller} ({_firstPrice:0.#}c)");
    }
    private sealed record ResultRow(Element Entry, string Name, double Price, string Currency, string Seller, int Quantity, int Pack, int Prefix, int Suffix, string[] Mods, string? Reject);
    private ResultRow ParseResult(Element entry)
    {
        var texts = Descendants(entry, 16).Select(Text).Where(t => t.Length > 0).ToList();
        var name = texts.FirstOrDefault(t => t.Contains('\n')) ?? texts.FirstOrDefault(t => t.StartsWith("Map (", StringComparison.Ordinal)) ?? "";
        var priceText = texts.FirstOrDefault(t => t.StartsWith("~", StringComparison.Ordinal)) ?? "";
        var pm = Regex.Match(priceText, @"~(?:b/o|price)\s+([\d.]+)\s+(\w+)");
        var price = pm.Success ? double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        var currency = pm.Success ? pm.Groups[2].Value : "";
        var seller = texts.LastOrDefault(t => Regex.IsMatch(t, @"#\d{3,5}$")) ?? "";
        int Percent(string label) { var t = texts.FirstOrDefault(x => x.StartsWith(label, StringComparison.Ordinal)) ?? ""; var m = Regex.Match(t, @"\+(\d+)%"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }
        var quantity = Percent("Item Quantity"); var pack = Percent("Monster Pack Size");
        var prefix = texts.Count(t => Regex.IsMatch(t, @"^P\d$")); var suffix = texts.Count(t => Regex.IsMatch(t, @"^S\d$"));
        var mods = texts.Where(t => !t.Contains('\n') && !Regex.IsMatch(t, @"^([PS]\d|\d+x?|~.*|Item |Monster Pack|Monster Level|listed .*|Negotiable.*|Fee:|Chaos Orb|Corrupted)") && t != seller).ToArray();
        var joined = string.Join("\n", mods);
        string? reject = null;
        var r = _request!;
        if (!name.Contains("Map (Tier 16)")) reject = "not_T16:" + name.Replace('\n', ' ');
        else if (texts.Any(t => t == "Unidentified")) reject = "unidentified";
        else if (!currency.Equals("chaos", StringComparison.OrdinalIgnoreCase) || double.IsNaN(price)) reject = "price_not_chaos:" + priceText;
        else if (price > r.MaxUnitChaos) reject = $"price_over_cap:{price}";
        else if (quantity < r.MinQuantity) reject = $"iiq:{quantity}";
        else if (pack < r.MinPackSize) reject = $"pack:{pack}";
        else if (prefix < r.MinAffixesEach || suffix < r.MinAffixesEach) reject = $"affixes:{prefix}P/{suffix}S";
        else
        {
            var ng = AwakeningMapPolicy.Rules.FirstOrDefault(x => Regex.IsMatch(joined, x.Pattern, RegexOptions.IgnoreCase));
            if (ng.Id != null) reject = "ng:" + ng.Id;
        }
        return new ResultRow(entry, name.Replace('\n', ' '), price, currency, seller, quantity, pack, prefix, suffix, mods, reject);
    }

    // ── Seller's hideout ─────────────────────────────────────────────
    private void TickArrive(GameController gc)
    {
        var grid = SellerGrid(gc);
        if (grid == null) { Status = $"Market: waiting for {_seller}'s stash"; return; }
        _sellerHash = AreaHash(gc);
        _gridCountBefore = -1; _pendingIndex = -1; _failedClicks = 0;
        _log("market.arrived", new { seller = _seller, area = gc.Area?.CurrentArea?.Name, items = grid.Children.Count });
        Set(Step.Buy, $"buying from {_seller}");
    }
    private void TickBuy(GameController gc)
    {
        var r = _request!;
        var grid = SellerGrid(gc);
        if (grid == null) { GoHomeOrFail(gc, "seller_window_closed"); return; }
        var items = grid.Children.Where(x => x != null && x.IsVisible).ToList();
        if (_pendingIndex >= 0)
        {
            // Verify the previous Ctrl+click by our own inventory (another buyer can empty a slot of the seller's grid at the same time).
            if (MapsInInventory(gc) > _mapsBefore)
            {
                Bought++; Spent += _pendingPrice; _failedClicks = 0;
                var mods = _pendingName.Split('\n');
                Purchases.Add((_seller, _pendingPrice, mods));
                _log("market.bought", new { seller = _seller, price = _pendingPrice, bought = Bought, spent = Spent, mods });
                _pendingIndex = -1;
            }
            else if ((DateTime.UtcNow - _clickAt).TotalSeconds < 3) return;
            else
            {
                _failedClicks++; _log("market.buy_not_confirmed", new { seller = _seller, price = _pendingPrice, attempt = _failedClicks });
                _pendingIndex = -1;
                if (_failedClicks >= 2) { GoHomeOrFail(gc, "purchase_not_confirmed(out_of_chaos_or_price_changed)"); return; }
            }
        }
        if (Bought >= r.Count) { GoHome(gc, "bought_target"); return; }
        if (InventoryFree(gc) < 2) { GoHome(gc, "inventory_full"); return; }
        var cap = Math.Min(r.MaxUnitChaos, _firstPrice + r.FollowUpOverChaos);
        for (var i = 0; i < items.Count; i++)
        {
            var check = CheckSellerItem(gc, items[i], r, cap);
            if (check.Reject != null) continue;
            _gridCountBefore = items.Count; _pendingIndex = i; _mapsBefore = MapsInInventory(gc); _pendingPrice = check.Price; _pendingName = string.Join("\n", check.Mods);
            if (BotInput.CtrlClick(Abs(gc, items[i].GetClientRect().Center))) { _actionAt = _clickAt = DateTime.UtcNow; Status = $"Market: Ctrl+click {check.Price:0.#}c ({Bought}/{r.Count})"; }
            else _pendingIndex = -1;
            return;
        }
        // Nothing left in this tab within the margin: next cheapest seller if more maps are needed.
        _log("market.seller_done", new { seller = _seller, bought = Bought });
        if (Bought < r.Count && _searches < 6) { _skippedSellers.Add(_seller); GoHome(gc, "seller_exhausted", research: true); return; }
        GoHome(gc, "seller_exhausted");
    }
    private (double Price, string[] Mods, string? Reject) CheckSellerItem(GameController gc, Element element, MarketMapRequest r, double cap)
    {
        try
        {
            var entity = element.AsObject<NormalInventoryItem>()?.Item;
            if (entity == null || !entity.IsValid) return (0, [], "no_entity");
            var priceText = AwakeningGameReader.Property(entity.GetComponent<Base>(), "PublicPrice")?.ToString() ?? "";
            var pm = Regex.Match(priceText, @"~(?:b/o|price)\s+([\d.]+)\s+(\w+)");
            if (!pm.Success || !pm.Groups[2].Value.Equals("chaos", StringComparison.OrdinalIgnoreCase)) return (0, [], "price:" + priceText);
            var price = double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
            if (price > cap + 1e-6) return (price, [], $"over_margin:{price}>{cap}");
            var map = AwakeningGameReader.ReadMap(gc, entity);
            var rejections = AwakeningMapPolicy.Rejections(map).Where(x => x != "requires_Dunes").ToList(); // layout comes from the Atlas
            if (rejections.Count > 0) return (price, [], string.Join(",", rejections));
            int Sum(string stat) => map.Mods.Sum(m => m.Stats.Select((s, i) => (s, i)).Where(x => x.s.Contains(stat)).Sum(x => x.i < m.Values.Length ? m.Values[x.i] : 0));
            var quantity = Sum("map_item_drop_quantity"); var pack = Sum("map_pack_size");
            if (quantity < r.MinQuantity) return (price, [], $"iiq:{quantity}");
            if (pack < r.MinPackSize) return (price, [], $"pack:{pack}");
            var explicitMods = map.Mods.Count(m => !m.Implicit);
            if (explicitMods < r.MinAffixesEach * 2) return (price, [], $"mods:{explicitMods}");
            return (price, map.Mods.Select(m => m.Text).ToArray(), null);
        }
        catch (Exception ex) { return (0, [], "error:" + ex.Message); }
    }

    // ── Back home ────────────────────────────────────────────────────
    private bool _research;
    private void GoHome(GameController gc, string reason, bool research = false)
    {
        _research = research;
        _log("market.go_home", new { reason, bought = Bought, spent = Spent, research });
        foreach (var k in new[] { Keys.Escape, Keys.Return, Keys.OemQuestion, Keys.H, Keys.I, Keys.D, Keys.E, Keys.O, Keys.U, Keys.T, Keys.Return }) _keys.Enqueue(k);
        Set(Step.Home, "returning with /hideout (" + reason + ")");
    }
    private void GoHomeOrFail(GameController gc, string reason)
    {
        FailReason = reason;
        if (IsHome(gc)) { Finish(gc, reason); return; }
        GoHome(gc, reason);
    }
    private void TickHome(GameController gc)
    {
        if (!IsHome(gc))
        {
            // Retry the chat command once if the first one was eaten (e.g. by a still-open window).
            if ((DateTime.UtcNow - _stepAt).TotalSeconds > 20 && (DateTime.UtcNow - _actionAt).TotalSeconds > 15)
            { foreach (var k in new[] { Keys.Escape, Keys.Return, Keys.OemQuestion, Keys.H, Keys.I, Keys.D, Keys.E, Keys.O, Keys.U, Keys.T, Keys.Return }) _keys.Enqueue(k); _actionAt = DateTime.UtcNow; }
            return;
        }
        if (_research) { _research = false; Set(Step.OpenMarket, "next seller"); return; }
        Finish(gc, FailReason.Length > 0 ? FailReason : "done");
    }
    private void Finish(GameController gc, string reason)
    {
        _log("market.finished", new { reason, bought = Bought, spent = Spent, purchases = Purchases.Select(p => new { p.Seller, p.Price }) });
        if (Bought > 0) { _step = Step.Done; _stepAt = DateTime.UtcNow; Status = $"Market: bought {Bought} map(s) for {Spent:0.#}c ({reason})"; }
        else Fail(reason);
    }
    private bool IsHome(GameController gc)
    {
        var area = gc.Area?.CurrentArea;
        if (gc.IsLoading || area?.IsHideout != true) return false;
        var hash = AreaHash(gc);
        return _sellerHash == 0 ? hash == _homeHash || area.Name == _homeArea : hash != _sellerHash && (area.Name == _homeArea || hash == _homeHash);
    }

    // ── UI helpers ───────────────────────────────────────────────────
    /// <summary>True while the market or its results pane is still open (the caller closes it with Escape before using the map device).</summary>
    public static bool MarketOpen(GameController gc) => MarketRoot(gc) != null || ResultsPane(gc) != null;
    private static Element? MarketRoot(GameController gc) =>
        Roots(gc).FirstOrDefault(r => Text(Child(r, 1)) == "The Market");
    // The results pane stays in the tree after it is closed, so its visibility (child 0) must be checked.
    private static Element? ResultsPane(GameController gc) =>
        Roots(gc).Select(r => Child(r, 0)).FirstOrDefault(p => p?.IsVisible == true && Text(Child(p, 0)) == "Search Results");
    private static Element? ResultList(GameController gc) => Child(ResultsPane(gc), 3, 1, 0);
    private static bool NoResultsShown(GameController gc) => Text(Child(ResultsPane(gc), 2)) == "No results found";
    private static Element? SellerGrid(GameController gc)
    {
        var window = Roots(gc).FirstOrDefault(r => Text(Child(r, 3)) == "Select Items To Buy");
        var pages = Child(window, 8, 1);
        if (pages == null) return null;
        var page = pages.Children.FirstOrDefault(p => p != null && p.IsVisible);
        return Child(page, 0);
    }
    private static IEnumerable<Element> Roots(GameController gc)
    {
        IList<Element>? kids = null;
        try { kids = gc.IngameState.IngameUi.Children; } catch { }
        return kids?.Where(k => k != null && k.IsVisible) ?? [];
    }
    private static int InventoryFree(GameController gc)
    {
        try { var items = StashSystem.GetInventorySlotItems(gc); return 60 - (items?.Sum(i => Math.Max(1, i.SizeX * i.SizeY)) ?? 0); }
        catch { return 60; }
    }
    private static int MapsInInventory(GameController gc) { try { return StashSystem.CountInventoryItems(gc, "Metadata/Items/Maps/MapKeyTier16"); } catch { return 0; } }
    private static long AreaHash(GameController gc) { try { return (long)gc.IngameState.Data.CurrentAreaHash; } catch { return 0; } }
    private void Set(Step step, string status) { _step = step; _stepAt = DateTime.UtcNow; Status = "Market: " + status; }
    private void Fail(string reason) { FailReason = reason; _log("market.failed", new { reason, step = _step.ToString(), bought = Bought }); _step = Step.Failed; Status = "Market failed: " + reason; }
    private void Click(GameController gc, Element e) { if (BotInput.Click(Abs(gc, e.GetClientRect().Center))) _actionAt = DateTime.UtcNow; }
    private static Vector2 Abs(GameController gc, SharpDX.Vector2 p) { var w = gc.Window.GetWindowRectangle(); return new Vector2(w.X + p.X, w.Y + p.Y); }
    private static string Text(Element? e) { try { return e?.Text ?? ""; } catch { return ""; } }
    private static Element? Child(Element? e, params int[] path)
    {
        foreach (var i in path) { if (e == null) return null; try { e = e.GetChildAtIndex(i); } catch { return null; } }
        return e;
    }
    private static IEnumerable<Element> Descendants(Element root, int maxDepth = 12)
    {
        var stack = new Stack<(Element E, int Depth)>(); stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (e, d) = stack.Pop();
            IList<Element>? kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || d > maxDepth) continue;
            for (var i = kids.Count - 1; i >= 0; i--) { var k = kids[i]; if (k != null && k.IsVisible) { yield return k; stack.Push((k, d + 1)); } }
        }
    }
}
