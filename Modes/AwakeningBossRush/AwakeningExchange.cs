using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

public enum ExchangeKind { BuyAtAsk, SellAtBid, ListAtAskMinus, Quote, BuyAtBid }

/// <summary>One order through Faustus' Currency Exchange. Want/Have follow the in-game panel.</summary>
public sealed record ExchangeRequest(ExchangeKind Kind, string WantName, string HaveName, int Quantity, double MaxUnitChaos = 0, double UndercutChaos = 1);

/// <summary>
/// Drives the Currency Exchange panel tick by tick with BotInput (one gated input per tick):
/// open Faustus → collect finished orders → pick I Want / I Have → read the competing book →
/// type both amounts (verified by reading the fields back) → place order → wait → collect.
/// Book semantics were verified on the live panel (see TickQuote). Every quote is logged so prices can be audited.
/// </summary>
public sealed class AwakeningExchange
{
    private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
    private enum Step { Idle, Open, Collect, PickWant, PickHave, Quote, FillWant, FillHave, Place, AwaitFill, CollectResult, Done, Failed }
    private Step _step = Step.Idle;
    private DateTime _stepAt, _startedAt, _actionAt;
    private ExchangeRequest? _request;
    private readonly Action<string, object> _log;
    private readonly Queue<Keys> _keys = new();
    private int _fillAttempts, _collectClicks;
    private int _orderWant, _orderHave;
    private string _searched = "";
    private int _placeClicks, _slotsBefore;
    private DateTime _placeAt;

    public string Status { get; private set; } = "";
    public bool Busy => _step is not (Step.Idle or Step.Done or Step.Failed);
    public bool Succeeded => _step == Step.Done;
    public string FailReason { get; private set; } = "";
    public int FilledWant { get; private set; }
    public int PaidHave { get; private set; }
    public double UnitChaos { get; private set; }
    public object? LastQuote { get; private set; }
    public ExchangeRequest? Request => _request;

    public AwakeningExchange(Action<string, object> log) { _log = log; }

    public void Start(ExchangeRequest request)
    {
        _request = request; _fillAttempts = 0; _collectClicks = 0; _keys.Clear(); _searched = ""; _placeClicks = 0;
        FilledWant = 0; PaidHave = 0; UnitChaos = 0; FailReason = ""; LastQuote = null;
        _startedAt = DateTime.UtcNow; Set(Step.Open, "opening Faustus");
        _log("exchange.started", request);
    }
    public void Cancel() { _keys.Clear(); if (Busy) Fail("cancelled"); }

    public void Tick(BotContext ctx)
    {
        if (!Busy || _request == null) return;
        var gc = ctx.Game;
        if ((DateTime.UtcNow - _startedAt).TotalSeconds > 120) { Fail("overall_timeout:" + _step); return; }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > (_step == Step.AwaitFill ? 30 : 20)) { Fail("step_timeout:" + _step); return; }
        if (_keys.Count > 0) { if (BotInput.CanAct && BotInput.PressKey(_keys.Peek())) _keys.Dequeue(); return; }
        if ((DateTime.UtcNow - _actionAt).TotalMilliseconds < 350 || !BotInput.CanAct) return;
        var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
        var open = panel?.IsVisible == true;
        if (_step != Step.Open && !open) { Fail("panel_closed_during:" + _step); return; }
        switch (_step)
        {
            case Step.Open: TickOpen(ctx, open); break;
            case Step.Collect: if (!CollectFinished(gc, panel!)) Set(Step.PickWant, "picking I Want"); break;
            case Step.PickWant: if (Pick(gc, panel!, true)) Set(Step.PickHave, "picking I Have"); break;
            case Step.PickHave: if (Pick(gc, panel!, false)) Set(Step.Quote, "reading the book"); break;
            case Step.Quote: TickQuote(gc, panel!); break;
            case Step.FillWant: if (Fill(gc, panel!.WantedItemCountInput, _orderWant)) Set(Step.FillHave, "typing I Have"); break;
            case Step.FillHave: if (Fill(gc, panel!.OfferedItemCountInput, _orderHave)) Set(Step.Place, "placing order"); break;
            case Step.Place: TickPlace(gc, panel!); break;
            case Step.AwaitFill: TickAwait(gc, panel!); break;
            case Step.CollectResult:
                if (!CollectFinished(gc, panel!))
                {
                    _log("exchange.done", new { _request, FilledWant, PaidHave, UnitChaos });
                    Set(Step.Done, $"bought/sold {FilledWant} {_request.WantName} for {PaidHave} {_request.HaveName}");
                }
                break;
        }
    }

    private void TickOpen(BotContext ctx, bool open)
    {
        var gc = ctx.Game;
        if (open) { ctx.Interaction.Cancel(gc); Set(Step.Collect, "collecting finished orders"); return; }
        var dialog = gc.IngameState.IngameUi.NpcDialog;
        if (dialog?.IsVisible == true)
        {
            var line = dialog.NpcLines?.FirstOrDefault(l => l?.Text?.Contains("Currency Exchange", StringComparison.OrdinalIgnoreCase) == true)
                ?? dialog.NpcLines?.FirstOrDefault(l => l?.Text?.Contains("Continue", StringComparison.OrdinalIgnoreCase) == true);
            if (line?.Element != null) Click(gc, line.Element);
            Status = "choosing Currency Exchange"; return;
        }
        if (ctx.Interaction.IsBusy) { ctx.Interaction.Tick(gc); Status = "walking to Faustus: " + ctx.Interaction.Status; return; }
        var npc = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true);
        if (npc == null) { Status = "Faustus not found in this area"; return; }
        ctx.Interaction.InteractWithEntity(npc, ctx.Navigation, requireProximity: true);
        _actionAt = DateTime.UtcNow; Status = "interacting with Faustus";
    }

    // Finished orders show "Order Completed"; their slots still holding items (bought items / unspent currency) are collected by hover + Ctrl+right-click.
    private static List<(Element Slot, string Count)> FinishedSlots(Element panel)
    {
        var result = new List<(Element, string)>();
        foreach (var card in Descendants(panel).Where(e => Text(e).Contains("Order Completed", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var parent = card.Parent;
            if (parent == null) continue;
            foreach (var slot in parent.Children)
            {
                if (slot == null || !slot.IsVisible || slot.ChildCount < 2) continue;
                var count = slot.Children.Select(Text).FirstOrDefault(t => Regex.IsMatch(t, @"^\d+$"));
                if (count != null && count != "0") result.Add((slot, count));
            }
        }
        return result;
    }
    private bool CollectFinished(GameController gc, Element panel)
    {
        if (_collectClicks > 20) return false;
        var slots = FinishedSlots(panel);
        if (slots.Count == 0) return false;
        var (slot, count) = slots[0];
        if (BotInput.CtrlRightClick(Abs(gc, slot.GetClientRect().Center))) { _actionAt = DateTime.UtcNow; _collectClicks++; _log("exchange.collect_click", new { count, rect = slot.GetClientRect().ToString() }); }
        Status = "collecting a finished order"; return true;
    }

    private bool Pick(GameController gc, dynamic panel, bool want)
    {
        var name = want ? _request!.WantName : _request!.HaveName;
        string? current = null;
        try { current = want ? (string?)panel.WantedItemType?.BaseName : (string?)panel.OfferedItemType?.BaseName; } catch { }
        dynamic? picker = null;
        try { picker = panel.CurrencyPicker; } catch { }
        bool pickerOpen = false;
        try { pickerOpen = picker != null && picker.IsVisible; } catch { }
        if (!pickerOpen && string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (pickerOpen)
        {
            Element? target = null;
            try
            {
                foreach (var option in picker!.Options)
                {
                    string? baseName = null;
                    try { baseName = option?.ItemType?.BaseName; } catch { }
                    if (!string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    target = (Element)option; break;
                }
            }
            catch { }
            // Options live in a long scrolling list; only click one that is really on screen.
            if (target != null && target.IsVisible && BotInput.IsRectOnScreen(target.GetClientRect()))
            { Click(gc, target); Status = "selected " + name; return false; }
            // Otherwise filter the list with the picker's search box (bottom of the picker).
            var query = SearchQuery(name);
            Element? search = null;
            try { search = picker!.SearchInput; } catch { }
            search ??= FindChild(FindChild((Element)picker!, 4), 0);
            if (search != null && search.IsVisible && _searched != query)
            {
                Click(gc, search);
                for (var i = 0; i < 40; i++) _keys.Enqueue(Keys.Back);
                foreach (var ch in query.ToUpperInvariant()) if (char.IsLetterOrDigit(ch)) _keys.Enqueue((Keys)ch); else if (ch == ' ') _keys.Enqueue(Keys.Space);
                _searched = query; Status = "searching the picker for " + query; return false;
            }
            Status = "waiting for " + name + " in the picker"; return false;
        }
        var button = FindChild((Element)panel, want ? 7 : 10);
        if (button != null) { Click(gc, button); Status = "opening " + (want ? "I Want" : "I Have") + " picker"; }
        return false;
    }

    private void TickQuote(GameController gc, dynamic panel)
    {
        var request = _request!;
        // Verified on the live panel (Want=Horned Scarab, Have=Chaos): WantedItemStock = orders that fill ours
        // (Get = our Want amount, Give = our Have amount, Listed = Want units available; best ask 1:110 = Market Ratio);
        // OfferedItemStock = competing orders on our side (Get = our Have amount, Give = our Want amount).
        var counter = new List<(int Get, int Give, long Listed)>();
        try { foreach (var s in panel.WantedItemStock) { int get = s.Get, give = s.Give; long listed = s.ListedCount; if (get > 0 && give > 0) counter.Add((get, give, listed)); } } catch { }
        var rivals = new List<(int Get, int Give, long Listed)>();
        try { foreach (var s in panel.OfferedItemStock) { int get = s.Get, give = s.Give; long listed = s.ListedCount; if (get > 0 && give > 0) rivals.Add((get, give, listed)); } } catch { }
        string ratioText = "";
        try { ratioText = Descendants((Element)panel).Select(Text).FirstOrDefault(t => t.Contains("Market Ratio")) ?? ""; } catch { }
        // The book arrives from the server a moment after both items are picked.
        if ((ratioText.Contains("Retrieving", StringComparison.OrdinalIgnoreCase) || (counter.Count == 0 && rivals.Count == 0))
            && (DateTime.UtcNow - _stepAt).TotalSeconds < 6) { Status = "Faustus: waiting for market info"; return; }
        LastQuote = new { request.WantName, request.HaveName, counter = counter.Select(b => new { b.Get, b.Give, b.Listed }).ToArray(),
            rivals = rivals.Select(b => new { b.Get, b.Give, b.Listed }).ToArray(), marketRatio = Regex.Replace(ratioText, @"<[^>]*>|[{}]|\r|\n", " ").Trim() };
        _log("exchange.quote", LastQuote);
        if (request.Kind == ExchangeKind.Quote) { Set(Step.Done, "quoted"); return; }

        long want, have;
        if (request.Kind == ExchangeKind.BuyAtAsk)
        {
            // Asks: chaos per item = Give/Get. Take the cheapest levels that cover the quantity; order at the worst one needed.
            if (counter.Count == 0) { Fail("no_sellers"); return; }
            var levels = counter.OrderBy(b => (double)b.Give / b.Get).ToList();
            long covered = 0; var chosen = levels[0];
            foreach (var l in levels) { chosen = l; covered += Math.Max(1, l.Listed); if (covered >= request.Quantity) break; }
            var (rw, rh) = Reduce(chosen.Get, chosen.Give);
            // 2026-09-21: rounding up to the reduced lot (80:239) bought 80 Sacrifice at Noon for a request of 19.
            // Order exactly the requested quantity and round the Chaos up so the ratio still meets that ask.
            want = request.Quantity; have = (long)Math.Ceiling(request.Quantity * (double)rh / rw);
            if (have <= 0) { Fail("bad_amounts"); return; }
        }
        else if (request.Kind == ExchangeKind.BuyAtBid)
        {
            // 2026-09-22: Horned Scarab asks jumped to 178c (cap 162c) while bids sat at 110-120c. Place a buy order
            // one chaos above the best competing bid; it fills later and is collected on the next Faustus visit.
            // Rivals here are other buyers: Get = chaos they give, Give = items they want → chaos per item = Get/Give.
            if (rivals.Count == 0) { Fail("no_competing_bid"); return; }
            var bestBid = rivals.Max(b => (double)b.Get / b.Give);
            var unit = Math.Floor(bestBid) + request.UndercutChaos;
            if (counter.Count > 0) unit = Math.Min(unit, Math.Ceiling(counter.Min(b => (double)b.Give / b.Get)));
            want = request.Quantity; have = (long)Math.Ceiling(unit * request.Quantity);
        }
        else if (request.Kind == ExchangeKind.SellAtBid)
        {
            // Bids (we want chaos, have the item): chaos per item = Get/Give; take the best one.
            if (counter.Count == 0) { Fail("no_buyers"); return; }
            var best = counter.OrderByDescending(b => (double)b.Get / b.Give).First();
            var (rw, rh) = Reduce(best.Get, best.Give);
            have = request.Quantity / rh * rh; want = have / rh * rw;
            if (have <= 0) { Fail("quantity_below_lot:" + rh); return; }
        }
        else
        {
            // Listing: rivals sell the same item for chaos at Give/Get chaos each; undercut the lowest by a flat amount.
            // 2026-09-22: Maven's Chisel of Avarice had bids at 141c but no chaos asks, so it was never listed.
            // With no rival ask, list 20% above the best bid instead of skipping.
            if (rivals.Count == 0 && counter.Count == 0) { Fail("no_competing_listing"); return; }
            var ask = rivals.Count > 0 ? rivals.Min(b => (double)b.Give / b.Get)
                : Math.Floor(counter.Max(b => (double)b.Get / b.Give) * 1.2) + request.UndercutChaos;
            var unit = Math.Floor(ask - request.UndercutChaos);
            if (unit < 1) { Fail("undercut_below_1c"); return; }
            if (counter.Count > 0 && counter.Max(b => (double)b.Get / b.Give) >= unit) { Fail("would_fill_at_bid_use_sell"); return; }
            have = request.Quantity; want = (long)(unit * request.Quantity);
        }
        if (want <= 0 || have <= 0 || want > int.MaxValue || have > int.MaxValue) { Fail("bad_amounts"); return; }
        _orderWant = (int)want; _orderHave = (int)have;
        var chaosPerItem = request.Kind is ExchangeKind.BuyAtAsk or ExchangeKind.BuyAtBid ? (double)have / want : (double)want / have;
        UnitChaos = chaosPerItem;
        if (request.MaxUnitChaos > 0 && request.Kind is ExchangeKind.BuyAtAsk or ExchangeKind.BuyAtBid && chaosPerItem > request.MaxUnitChaos)
        { Fail($"price_above_cap:{chaosPerItem:F2}>{request.MaxUnitChaos:F2}"); return; }
        _log("exchange.order_planned", new { request, want, have, chaosPerItem });
        _fillAttempts = 0; Set(Step.FillWant, $"typing I Want {want}");
    }

    private bool Fill(GameController gc, Element? field, int value)
    {
        if (field == null) { Fail("input_field_missing"); return false; }
        // The fields show the market suggestion as placeholder text: a value only counts after we typed it ourselves.
        if (_fillAttempts > 0 && ReadNumber(field) == value) { _fillAttempts = 0; return true; }
        if (++_fillAttempts > 4) { Fail("field_not_verified:" + value); return false; }
        Click(gc, field);
        for (var i = 0; i < 10; i++) _keys.Enqueue(Keys.Back);
        foreach (var ch in value.ToString()) _keys.Enqueue((Keys)ch);
        Status = "typing " + value; return false;
    }

    // "n/10" order slots next to Place Order: a placed order raises n (or completes at once and shows a finished card).
    private static int? OrderSlots(Element panel)
    {
        var text = Descendants(panel).Select(Text).FirstOrDefault(t => Regex.IsMatch(t.Trim(), @"^\d+/\d+$"));
        return text != null && int.TryParse(text.Trim().Split('/')[0], out var n) ? n : null;
    }
    private void TickPlace(GameController gc, Element panel)
    {
        if (_placeClicks > 0)
        {
            var slots = OrderSlots(panel);
            if ((slots.HasValue && slots.Value > _slotsBefore) || FinishedSlots(panel).Count > 0)
            {
                _log("exchange.order_placed", new { _request, _orderWant, _orderHave, slots, clicks = _placeClicks });
                Set(Step.AwaitFill, "waiting for the order to fill"); return;
            }
            if ((DateTime.UtcNow - _placeAt).TotalSeconds < 2.5) { Status = "Faustus: confirming the order"; return; }
            if (_placeClicks >= 3) { Fail("order_not_registered"); return; }
        }
        else _slotsBefore = OrderSlots(panel) ?? 0;
        var label = Descendants(panel).FirstOrDefault(e => Text(e).Trim().Equals("place order", StringComparison.OrdinalIgnoreCase));
        var button = label?.Parent ?? label;
        if (button == null) { Status = "Faustus: place order button not found"; return; }
        if (BotInput.Click(Abs(gc, button.GetClientRect().Center))) { _actionAt = _placeAt = DateTime.UtcNow; _placeClicks++; Status = "Faustus: clicked place order"; }
    }

    private void TickAwait(GameController gc, Element panel)
    {
        if (_request!.Kind is ExchangeKind.ListAtAskMinus or ExchangeKind.BuyAtBid)
        { if ((DateTime.UtcNow - _stepAt).TotalSeconds > 2) Set(Step.Done, "order placed (fills later)"); return; }
        if (FinishedSlots(panel).Count == 0) { Status = "waiting for the order to fill"; return; }
        FilledWant = _orderWant; PaidHave = _orderHave;
        _collectClicks = 0; Set(Step.CollectResult, "collecting");
    }

    // ── helpers ──
    private void Set(Step step, string status) { _step = step; _stepAt = DateTime.UtcNow; Status = "Faustus: " + status; }
    private void Fail(string reason) { FailReason = reason; _log("exchange.failed", new { reason, step = _step.ToString(), _request }); _step = Step.Failed; Status = "Faustus failed: " + reason; }
    private void Click(GameController gc, Element e)
    {
        if (BotInput.Click(Abs(gc, e.GetClientRect().Center))) _actionAt = DateTime.UtcNow;
    }
    private static Vector2 Abs(GameController gc, SharpDX.Vector2 p) { var w = gc.Window.GetWindowRectangle(); return new Vector2(w.X + p.X, w.Y + p.Y); }
    private static string Text(Element? e) { try { return e?.Text ?? ""; } catch { return ""; } }
    // "Maven's Chisel of Avarice" → "Chisel of Avarice", "The Maven's Writ" → "Writ": the search box cannot take apostrophes.
    private static string SearchQuery(string name)
    {
        var words = name.Split(' ');
        var i = Array.FindLastIndex(words, w => w.Contains('\''));
        return i >= 0 && i < words.Length - 1 ? string.Join(' ', words.Skip(i + 1)) : name;
    }
    
    private static Element? FindChild(Element? e, int index) { try { return e?.GetChildAtIndex(index); } catch { return null; } }
    private static IEnumerable<Element> Descendants(Element root)
    {
        var stack = new Stack<(Element E, int Depth)>(); stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (e, d) = stack.Pop();
            IList<Element>? kids = null;
            try { kids = e.Children; } catch { }
            if (kids == null || d > 8) continue;
            foreach (var k in kids) if (k != null && k.IsVisible) { yield return k; stack.Push((k, d + 1)); }
        }
    }
    private static int? ReadNumber(Element field)
    {
        var raw = Text(field);
        if (string.IsNullOrWhiteSpace(raw)) raw = Descendants(field).Select(Text).FirstOrDefault(t => t.Any(char.IsDigit)) ?? "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : null;
    }
    private static (long Want, long Have) Reduce(long want, long have)
    {
        long a = want, b = have; while (b != 0) (a, b) = (b, a % b);
        return a > 0 ? (want / a, have / a) : (want, have);
    }

    /// <summary>Chaos (or any base item) held in inventory and stash, from the server-side inventories.</summary>
    /// <summary>Per server inventory (type/slot) count of an item, for diagnosing where currency is visible.</summary>
    public static List<object> HeldBreakdown(GameController gc, string baseName)
    {
        var rows = new List<object>();
        try
        {
            foreach (var holder in gc.IngameState.Data.ServerData.PlayerInventories)
            {
                var inv = holder?.Inventory;
                if (inv == null) continue;
                long n = 0;
                foreach (var item in inv.Items)
                {
                    if (item == null) continue;
                    if (!string.Equals(gc.Files.BaseItemTypes.Translate(item.Path)?.BaseName, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                    n += item.GetComponent<Stack>()?.Size ?? 1;
                }
                if (n > 0) rows.Add(new { type = inv.InventType.ToString(), slot = inv.InventSlot.ToString(), n });
            }
        }
        catch (Exception ex) { rows.Add(new { error = ex.Message }); }
        return rows;
    }
    public static int CountInMainInventory(GameController gc, string baseName)
    {
        long total = 0;
        try
        {
            foreach (var holder in gc.IngameState.Data.ServerData.PlayerInventories)
            {
                var inv = holder?.Inventory;
                if (inv == null || inv.InventType != InventoryTypeE.MainInventory) continue;
                foreach (var item in inv.Items)
                    if (item != null && string.Equals(gc.Files.BaseItemTypes.Translate(item.Path)?.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                        total += item.GetComponent<Stack>()?.Size ?? 1;
            }
        }
        catch { }
        return (int)Math.Min(total, int.MaxValue);
    }
    public static int CountHeld(GameController gc, string baseName)
    {
        long total = 0;
        try
        {
            foreach (var holder in gc.IngameState.Data.ServerData.PlayerInventories)
            {
                var inv = holder?.Inventory;
                if (inv == null || (inv.InventType != InventoryTypeE.MainInventory && inv.InventSlot != InventorySlotE.StashInventoryId)) continue;
                foreach (var item in inv.Items)
                {
                    if (item == null) continue;
                    var name = gc.Files.BaseItemTypes.Translate(item.Path)?.BaseName;
                    if (!string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                    total += item.GetComponent<Stack>()?.Size ?? 1;
                }
            }
        }
        catch { }
        return (int)Math.Min(total, int.MaxValue);
    }
}
