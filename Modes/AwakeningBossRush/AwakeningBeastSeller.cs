using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;

namespace AutoExile.Modes.AwakeningBossRush;

/// <summary>
/// Captured red beasts worth >= the threshold (user, 2026-09-23): "/menagerie" → Ctrl+click Einhar → Bestiary /
/// Captured Beasts → filter by the beast's name → Ctrl+left-click the entry (itemised into the inventory) → "/hideout".
/// UI layout calibrated 2026-09-23 (ui-bestiary2): the Captured Beasts page holds a "Filter Beasts" box and, under
/// its sibling list, 12 family groups whose visible children are the captured beasts (name in child(1).child(0)).
/// One gated input per tick, like the market and exchange drivers.
/// </summary>
public sealed class AwakeningBeastSeller
{
    public const string ItemisedPath = "Metadata/Items/Currency/CurrencyItemisedCapturedMonster";
    private enum Step { Idle, GoMenagerie, WalkToEinhar, OpenBestiary, OpenCaptured, Filter, Scan, Itemise, Verify, GoHome,
        OpenShop, PickItem, SetPrice, CheckCurrency, PickCurrency, ClickList, ListVerify, Earnings, CollectEarnings, CloseShop, Done, Failed }
    private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
    private Step _step = Step.Idle;
    private DateTime _stepAt, _startedAt, _actionAt, _keyAt;
    private readonly Action<string, object> _log;
    private readonly Queue<Keys> _keys = new();
    private List<(string Name, double Chaos)> _targets = new();
    private int _targetIndex, _sidebarTry, _itemisedBefore, _chatRetries, _noEffect;
    private string _homeArea = "";
    private DateTime _einharClickAt; private int _filterRetries;
    private Element? _entry;
    public int Itemised { get; private set; }
    public double ItemisedChaos { get; private set; }
    public string Status { get; private set; } = "";
    public string FailReason { get; private set; } = "";
    public bool Busy => _step is not (Step.Idle or Step.Done or Step.Failed);
    public bool Finished => _step is Step.Done or Step.Failed;
    public Func<BotContext, Vector2, bool>? Navigate { get; set; }

    public AwakeningBeastSeller(Action<string, object> log) { _log = log; }

    public void Start(GameController gc, IEnumerable<(string Name, double Chaos)> valuable)
    {
        _targets = valuable.OrderByDescending(v => v.Chaos).ToList(); _targetIndex = 0; _keys.Clear();
        Itemised = 0; ItemisedChaos = 0; FailReason = ""; _noEffect = 0; _chatRetries = 0;
        _homeArea = gc.Area?.CurrentArea?.Name ?? "";
        _startedAt = DateTime.UtcNow;
        // 2026-09-25 (verified live): Einhar stands in the Hideout and Ctrl+clicking him opens the same Bestiary /
        // Captured Beasts pages. Skip the two loading screens of /menagerie + /hideout when he is here.
        // 2026-09-25 (user): itemising only works with Einhar in the Menagerie; the Hideout Einhar's Bestiary shows the
        // list but Ctrl+click does nothing (confirmed: "itemise_click_had_no_effect" twice in the Hideout). Always travel.
        _stayedHome = false;
        _log("beast.itemise_started", new { targets = _targets.Select(t => t.Name + ":" + t.Chaos).ToArray(), einharInHideout = _stayedHome });
        if (_stayedHome) { Set(Step.WalkToEinhar, "walking to Einhar (Hideout)"); return; }
        Chat(gc, "menagerie"); Set(Step.GoMenagerie, "travelling to the Menagerie");
    }
    public void Reset() { _step = Step.Idle; _keys.Clear(); }

    // ── Listing at Faustus' Merchant (Manage Shop) ─────────────────────────────────────────
    // Calibrated 2026-09-23 (ui-shop1 / ui-price1..6): Faustus dialog line "Manage Shop" opens the "Merchant" panel
    // (Shop / Earnings) beside the inventory. Ctrl+click on an inventory item opens "Set Item Price": the price field is
    // focused on open (row child 0 shows the typed digits), the currency picker is row child 1, "list item" row child 2.
    private Func<Entity, double>? _priceOf;
    private readonly HashSet<long> _listTried = new();
    private int _listPrice; private long _listItemId; private int _itemsBefore;
    public int Listed { get; private set; }
    public double ListedChaos { get; private set; }
    public void StartList(Func<Entity, double> priceOf)
    {
        _priceOf = priceOf; _listTried.Clear(); Listed = 0; ListedChaos = 0; FailReason = ""; _keys.Clear(); _collectOnly = false; EarningsCollected = 0;
        _startedAt = DateTime.UtcNow; Set(Step.OpenShop, "opening Faustus' shop");
        _log("shop.list_started", new { });
    }

    public void Tick(BotContext ctx)
    {
        if (!Busy) return;
        var gc = ctx.Game;
        if (IsListStep(_step))
        {
            if ((DateTime.UtcNow - _startedAt).TotalMinutes > 4 || (DateTime.UtcNow - _stepAt).TotalSeconds > (_step == Step.OpenShop ? 50 : 25))
            { if (_step == Step.CloseShop) { Fail("close_shop_timeout"); return; } CloseShop("timeout:" + _step); return; }
            if (_keys.Count > 0) { PumpKeys(gc); return; }
            if ((DateTime.UtcNow - _actionAt).TotalMilliseconds < 450 || !BotInput.CanAct) return;
            Dispatch(ctx); return;
        }
        if ((DateTime.UtcNow - _startedAt).TotalMinutes > 4 && _step != Step.GoHome) { GoHome(gc, "overall_timeout:" + _step); return; }
        if ((DateTime.UtcNow - _startedAt).TotalMinutes > 7) { Fail("home_timeout"); return; }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > (_step is Step.GoMenagerie or Step.GoHome ? 70 : _step == Step.WalkToEinhar ? 45 : 25))
        { if (_step == Step.GoHome) { Fail("step_timeout:GoHome"); return; } GoHome(gc, "step_timeout:" + _step); return; }
        if (_keys.Count > 0) { PumpKeys(gc); return; }
        if (gc.IsLoading || (DateTime.UtcNow - _actionAt).TotalMilliseconds < 450 || !BotInput.CanAct) return;
        Dispatch(ctx);
    }
    private void Dispatch(BotContext ctx)
    {
        var gc = ctx.Game;
        switch (_step)
        {
            case Step.GoMenagerie:
                if (gc.Area?.CurrentArea?.Name == "The Menagerie") { Set(Step.WalkToEinhar, "walking to Einhar"); return; }
                if ((DateTime.UtcNow - _stepAt).TotalSeconds > 15 && _chatRetries < 2) { _chatRetries++; Chat(gc, "menagerie"); }
                return;
            case Step.WalkToEinhar: TickWalk(ctx); return;
            case Step.OpenBestiary: TickOpenBestiary(gc); return;
            case Step.OpenCaptured: TickOpenCaptured(gc); return;
            case Step.Filter: TickFilter(gc); return;
            case Step.Scan: TickScan(gc); return;
            case Step.Verify: TickVerify(gc); return;
            case Step.OpenShop: TickOpenShop(ctx); return;
            case Step.PickItem: TickPickItem(gc); return;
            case Step.SetPrice: TickSetPrice(gc); return;
            case Step.CheckCurrency: TickCheckCurrency(gc); return;
            case Step.PickCurrency: TickPickCurrency(gc); return;
            case Step.ClickList: TickClickList(gc); return;
            case Step.ListVerify: TickListVerify(gc); return;
            case Step.Earnings: TickEarnings(gc); return;
            case Step.CollectEarnings: TickCollectEarnings(gc); return;
            case Step.CloseShop:
                if (ShopPanel(gc) == null && PriceDialog(gc) == null) { _log("shop.list_done", new { Listed, ListedChaos, EarningsCollected, reason = FailReason }); _step = Step.Done; Status = $"Shop: listed {Listed} ({ListedChaos:0}c)"; return; }
                if (BotInput.PressKey(Keys.Escape)) _actionAt = DateTime.UtcNow;
                return;
            case Step.GoHome:
                if (gc.Area?.CurrentArea?.IsHideout == true && gc.Area.CurrentArea.Name != "The Menagerie")
                {
                    _log("beast.itemise_done", new { Itemised, ItemisedChaos, reason = FailReason });
                    _step = FailReason.Length > 0 && Itemised == 0 ? Step.Failed : Step.Done; Status = $"Beasts: itemised {Itemised} ({ItemisedChaos:0}c)"; return;
                }
                if ((DateTime.UtcNow - _stepAt).TotalSeconds > 15 * (_chatRetries + 1) && _chatRetries < 3 && !gc.IsLoading) { _chatRetries++; Chat(gc, "hideout"); }
                return;
        }
    }

    private void TickWalk(BotContext ctx)
    {
        var gc = ctx.Game;
        var einhar = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => (e.RenderName ?? "").StartsWith("Einhar", StringComparison.OrdinalIgnoreCase)
            || (e.Path ?? "").Contains("Einhar", StringComparison.OrdinalIgnoreCase));
        var label = Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Einhar, Beastmaster");
        var rect = label?.GetClientRect();
        var w = gc.Window.GetWindowRectangle();
        bool onScreen = rect.HasValue && rect.Value.Width > 0 && rect.Value.Y > 40 && rect.Value.Y < w.Height - 200 && rect.Value.X > 40 && rect.Value.Right < w.Width - 40;
        // 2026-09-23 13:40: navigation in the Menagerie stalled 60 grid short of Einhar. Ctrl+clicking his name label
        // (even off-screen; the click is clamped to the window edge, verified twice) makes the character walk there itself.
        if (rect.HasValue && rect.Value.Width > 0)
        {
            if ((DateTime.UtcNow - _einharClickAt).TotalSeconds < 3) return;
            var target = rect.Value.Center; // same call the calibration "ui_click text:Einhar, Beastmaster|ctrl" used
            if (BotInput.CtrlClick(Abs(gc, target))) { _einharClickAt = DateTime.UtcNow; _actionAt = DateTime.UtcNow; Set(Step.OpenBestiary, "opening Einhar's Bestiary"); }
            return;
        }
        if (einhar != null && Navigate != null) { Navigate(ctx, einhar.GridPosNum); Status = $"Beasts: walking to Einhar ({einhar.DistancePlayer:0})"; return; }
        Status = "Beasts: looking for Einhar";
    }
    private Element? CapturedPanel(GameController gc)
    {
        var filter = Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Filter Beasts");
        return filter?.Parent?.Parent?.Parent; // [18,0,0,0] → [18]
    }
    private void TickOpenBestiary(GameController gc)
    {
        if (CapturedPanel(gc) != null) { Set(Step.Filter, "filtering"); return; }
        var tab = Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Bestiary" && e.GetClientRect().Y < 250);
        if (tab == null)
        {
            if ((DateTime.UtcNow - _stepAt).TotalSeconds > 8) { _actionAt = DateTime.UtcNow; Set(Step.WalkToEinhar, "retrying Einhar"); }
            return;
        }
        _sidebarTry = 0; Set(Step.OpenCaptured, "opening Captured Beasts");
    }
    private void TickOpenCaptured(GameController gc)
    {
        if (CapturedPanel(gc) != null) { Set(Step.Filter, "filtering"); return; }
        // The Bestiary page has a vertical button column (x ≈ 833, 6 buttons, 75x145); the Captured Beasts page is the
        // one whose content shows "Filter Beasts". Try the buttons one by one.
        var tab = Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Bestiary" && e.GetClientRect().Y < 250);
        var content = tab?.Parent?.Parent?.Parent?.Parent?.GetChildAtIndex(1); // [58,2,0,1,0,21,0,1] → [58,2,0,1] → child 1
        Element? column = null;
        try
        {
            var page = content?.Children?.FirstOrDefault(c => c != null && c.IsVisible)?.GetChildAtIndex(0);
            column = page?.Children?.LastOrDefault(c => c != null && c.IsVisible && c.ChildCount >= 5);
        }
        catch { }
        var buttons = column?.Children?.Where(c => c != null && c.IsVisible).OrderByDescending(c => c.GetClientRect().Y).ToList();
        if (buttons == null || buttons.Count == 0 || _sidebarTry >= buttons.Count + 1) { GoHome(gc, "captured_beasts_button_not_found"); return; }
        var b = buttons[_sidebarTry % buttons.Count]; _sidebarTry++;
        if (BotInput.Click(Abs(gc, b.GetClientRect().Center))) { _actionAt = DateTime.UtcNow.AddMilliseconds(500); _log("beast.sidebar_click", new { index = _sidebarTry - 1, rect = b.GetClientRect().ToString() }); }
    }
    private void TickFilter(GameController gc)
    {
        var panel = CapturedPanel(gc);
        if (panel == null) { Set(Step.OpenBestiary, "panel closed"); return; }
        if (_targetIndex >= _targets.Count) { GoHome(gc, ""); return; }
        if (FreeCells(gc) < 1) { GoHome(gc, "inventory_full"); return; }
        var input = panel.GetChildAtIndex(0)?.GetChildAtIndex(0)?.GetChildAtIndex(1)?.GetChildAtIndex(0);
        if (input == null) { GoHome(gc, "filter_box_not_found"); return; }
        var name = _targets[_targetIndex].Name;
        if (!SetClipboard(name)) { GoHome(gc, "clipboard_failed"); return; }
        if (BotInput.Click(Abs(gc, input.GetClientRect().Center)))
        {
            _actionAt = DateTime.UtcNow;
            _keys.Enqueue(Keys.A | Keys.Control); _keys.Enqueue(Keys.Back); _keys.Enqueue(Keys.V | Keys.Control); _keys.Enqueue(Keys.None);
            _hoverIdx = 0; _hovering = false;
            Set(Step.Scan, "looking for " + name);
        }
    }
    private void TickScan(GameController gc)
    {
        var panel = CapturedPanel(gc);
        if (panel == null) { Set(Step.OpenBestiary, "panel closed"); return; }
        if ((DateTime.UtcNow - _stepAt).TotalMilliseconds < 900) return;
        var target = _targets[_targetIndex];
        // 2026-09-23 13:57: the first paste was lost (filter empty → all 937 beasts visible → nothing matched). Check the
        // filter box really holds the name before scanning; re-paste up to 3 times.
        var box = panel.GetChildAtIndex(0)?.GetChildAtIndex(0)?.GetChildAtIndex(1)?.GetChildAtIndex(0);
        var typed = Normalize(Text(box));
        if (!string.Equals(typed, Normalize(target.Name), StringComparison.OrdinalIgnoreCase))
        {
            if (++_filterRetries <= 3) { _log("beast.filter_retry", new { target.Name, typed, retry = _filterRetries }); Set(Step.Filter, "re-typing the filter"); return; }
            _log("beast.filter_failed", new { target.Name, typed });
        }
        _filterRetries = 0;
        var list = panel.GetChildAtIndex(1); var lr = list?.GetClientRect();
        var entries = new List<Element>();
        try
        {
            foreach (var group in list?.GetChildAtIndex(0)?.Children ?? new List<Element>())
            {
                if (group == null || !group.IsVisible) continue;
                foreach (var e in group.GetChildAtIndex(1)?.Children ?? new List<Element>())
                    if (e != null && e.IsVisible && e.ChildCount >= 2) entries.Add(e);
            }
        }
        catch { }
        // Only entries fully inside the list viewport can be clicked. The entry shows the beast's own (rare) name, e.g.
        // "Painslasher"; its type ("- Primal Cystcaller -") is only in the hover tooltip (2026-09-25: an unverified
        // multi-word match picked a Painslasher for Primal Cystcaller). Hover each candidate and read the tooltip first.
        var inView = entries.Where(e => { var r = e.GetClientRect(); return lr.HasValue && r.Y >= lr.Value.Y - 2 && r.Bottom <= lr.Value.Bottom + 2; }).ToList();
        if (_hoverIdx >= inView.Count)
        {
            _log("beast.itemise_none", new { target.Name, visible = entries.Count, inView = inView.Count, hovered = _hoverIdx });
            _hoverIdx = 0; _hovering = false; _targetIndex++; Set(Step.Filter, "next beast"); return;
        }
        var cand = inView[_hoverIdx];
        var candPortrait = cand.GetChildAtIndex(0) ?? cand;
        if (!_hovering)
        {
            if (BotInput.MoveMouse(Abs(gc, candPortrait.GetClientRect().Center))) { _hovering = true; _hoverAt = DateTime.UtcNow; }
            return;
        }
        if ((DateTime.UtcNow - _hoverAt).TotalMilliseconds < 350) return;
        var tipName = HoverTypeName(gc, cand);
        _hovering = false;
        if (tipName.Length == 0 && (DateTime.UtcNow - _hoverAt).TotalMilliseconds < 1200) { _hovering = true; return; }
        if (!string.Equals(Normalize(tipName), Normalize(target.Name), StringComparison.OrdinalIgnoreCase))
        {
            _log("beast.hover_skip", new { target.Name, beast = Text(cand.GetChildAtIndex(1)?.GetChildAtIndex(0)), type = tipName });
            _hoverIdx++; return;
        }
        var clickable = new List<Element> { cand };
        _entry = clickable[0];
        _itemisedBefore = CountItemised(gc);
        // The beast tooltip reads "Ctrl + Left-click to Itemise Beast" (verified live 2026-09-23 on Black Mórrigan:
        // Ctrl+left-click on the portrait put the Imprinted Bestiary Orb into the inventory; right-clicks do nothing).
        var portrait = _entry.GetChildAtIndex(0) ?? _entry;
        if (BotInput.CtrlClick(Abs(gc, portrait.GetClientRect().Center)))
        {
            _actionAt = DateTime.UtcNow;
            _log("beast.itemise_click", new { target.Name, beast = Text(_entry.GetChildAtIndex(1)?.GetChildAtIndex(0)), before = _itemisedBefore, candidates = clickable.Count });
            Set(Step.Verify, "itemising " + target.Name);
        }
    }
    private void TickVerify(GameController gc)
    {
        var now = CountItemised(gc);
        var target = _targets[_targetIndex];
        if (now > _itemisedBefore)
        {
            Itemised++; ItemisedChaos += target.Chaos; _noEffect = 0;
            _log("beast.itemised", new { target.Name, target.Chaos, inventory = now });
            _hovering = false; Set(Step.Scan, "next " + target.Name); return; // same filter: take the next one of this type
        }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds < 2.5) return;
        _noEffect++;
        _log("beast.itemise_no_effect", new { target.Name, attempt = _noEffect, cursor = CursorHasItem(gc) });
        if (_noEffect >= 2) { GoHome(gc, "itemise_click_had_no_effect"); return; }
        _hovering = false; Set(Step.Scan, "retrying " + target.Name);
    }
    private int _hoverIdx; private bool _hovering; private DateTime _hoverAt;
    /// <summary>Beast type from the hover tooltip ("- Black Mórrigan -"): the entry's own tooltip, else any visible "- X -" text.</summary>
    private static string HoverTypeName(GameController gc, Element entry)
    {
        string Clean(string? t) => (t ?? "").Trim().Trim('-').Trim();
        try { var t = Clean(entry.Tooltip?.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text); if (t.Length > 0) return t; } catch { }
        try { var t = Clean(entry.GetChildAtIndex(0)?.Tooltip?.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text); if (t.Length > 0) return t; } catch { }
        var any = Find(gc.IngameState.IngameUi, e => { var x = Text(e); return e.IsVisible && x.Length > 4 && x.StartsWith("- ") && x.EndsWith(" -"); });
        return Clean(Text(any));
    }
    private static bool TypeMatches(Element entry, string name)
    {
        string? tip = null;
        try { tip = entry.Tooltip?.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text?.Replace("-", "").Trim(); } catch { }
        if (!string.IsNullOrEmpty(tip)) return string.Equals(Normalize(tip), Normalize(name), StringComparison.OrdinalIgnoreCase);
        return name.Contains(' '); // one-word names ("Parasite") also match whole families: require the tooltip
    }
    private static string Normalize(string s) => s.Replace("ó", "o").Replace("Ó", "O").Trim().ToLowerInvariant();
    private static bool IsListStep(Step s) => s is Step.OpenShop or Step.PickItem or Step.SetPrice or Step.CheckCurrency or Step.PickCurrency or Step.ClickList or Step.ListVerify or Step.Earnings or Step.CollectEarnings or Step.CloseShop;
    private int _earningClicks; private bool _collectOnly;
    public int EarningsCollected { get; private set; }
    /// <summary>Visit the shop only to empty the "Earnings (Remove-only)" tab (sold listings pay out there).</summary>
    public void StartCollect() { StartList(_ => 0); _collectOnly = true; }
    private void TickEarnings(GameController gc)
    {
        var tab = Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Earnings (Remove-only)");
        if (tab == null) { CloseShop("earnings_tab_missing"); return; }
        if (BotInput.Click(Abs(gc, tab.GetClientRect().Center))) { _actionAt = DateTime.UtcNow.AddMilliseconds(600); _earningClicks = 0; Set(Step.CollectEarnings, "collecting earnings"); }
    }
    private void TickCollectEarnings(GameController gc)
    {
        var panel = ShopPanel(gc)?.Parent;
        if (panel == null) { CloseShop("shop_closed"); return; }
        if (_earningClicks >= 30 || FreeCells(gc) < 2) { CloseShop(""); return; }
        var items = new List<Element>();
        void Walk(Element e, int d)
        {
            if (d > 14) return;
            IList<Element>? kids = null; try { kids = e.Children; } catch { }
            if (kids == null) return;
            foreach (var c in kids)
            {
                if (c == null || !c.IsVisible) continue;
                try
                {
                    var r = c.GetClientRect();
                    if (r.Width > 40 && r.Width < 200 && r.Height > 40 && r.Height < 200)
                    {
                        var inv = c.AsObject<ExileCore.PoEMemory.Elements.InventoryElements.NormalInventoryItem>();
                        if (inv?.Item != null && inv.Item.IsValid && !string.IsNullOrEmpty(inv.Item.Path)) { items.Add(c); continue; }
                    }
                }
                catch { }
                Walk(c, d + 1);
            }
        }
        Walk(panel, 0);
        if (items.Count == 0) { _log("shop.earnings_done", new { clicks = _earningClicks }); CloseShop(""); return; }
        if (BotInput.CtrlClick(Abs(gc, items[0].GetClientRect().Center))) { _actionAt = DateTime.UtcNow.AddMilliseconds(200); _earningClicks++; EarningsCollected++; }
    }
    private static Element? ShopPanel(GameController gc) => Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Merchant" && e.GetClientRect().Y < 200);
    private static Element? PriceDialog(GameController gc) => Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Set Item Price")?.Parent?.Parent;
    private static Element? PriceRow(Element dialog) => dialog.GetChildAtIndex(2)?.GetChildAtIndex(0);
    private void TickOpenShop(BotContext ctx)
    {
        var gc = ctx.Game;
        if (ShopPanel(gc) != null && gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true) { ctx.Interaction.Cancel(gc); Set(Step.PickItem, "choosing an item"); return; }
        var dialog = gc.IngameState.IngameUi.NpcDialog;
        if (dialog?.IsVisible == true)
        {
            var line = dialog.NpcLines?.FirstOrDefault(l => l?.Text?.Contains("Manage Shop", StringComparison.OrdinalIgnoreCase) == true);
            if (line?.Element != null && BotInput.Click(Abs(gc, line.Element.GetClientRect().Center))) _actionAt = DateTime.UtcNow.AddMilliseconds(600);
            Status = "Shop: choosing Manage Shop"; return;
        }
        if (ctx.Interaction.IsBusy) { ctx.Interaction.Tick(gc); Status = "Shop: walking to Faustus"; return; }
        var npc = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true);
        if (npc == null) { Status = "Shop: Faustus not found"; return; }
        ctx.Interaction.InteractWithEntity(npc, ctx.Navigation, requireProximity: true);
        _actionAt = DateTime.UtcNow;
    }
    private void TickPickItem(GameController gc)
    {
        if (PriceDialog(gc) != null) { Set(Step.SetPrice, "pricing"); return; }
        if (ShopPanel(gc) == null) { Set(Step.OpenShop, "shop closed, reopening"); return; }
        var grid = gc.IngameState.IngameUi.InventoryPanel?[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory];
        var item = grid?.VisibleInventoryItems?.FirstOrDefault(i => i?.Item?.Path == ItemisedPath && !_listTried.Contains(i.Item.Id));
        if (item?.Item == null || _collectOnly) { Set(Step.Earnings, "opening Earnings"); return; }
        var price = _priceOf?.Invoke(item.Item) ?? 0;
        _listTried.Add(item.Item.Id);
        if (price < 1) { _log("shop.no_price", new { name = MonsterName(item.Item) }); return; }
        _listPrice = (int)Math.Floor(price); _listItemId = item.Item.Id; _itemsBefore = CountItemised(gc);
        // User (2026-09-23): items are put up at Faustus' Merchant with Ctrl+RIGHT-click (Ctrl+left-click did nothing).
        if (BotInput.CtrlRightClick(Abs(gc, item.GetClientRect().Center)))
        { _actionAt = DateTime.UtcNow.AddMilliseconds(300); _log("shop.item_clicked", new { name = MonsterName(item.Item), price = _listPrice }); Set(Step.SetPrice, "waiting for the price dialog"); }
    }
    private void TickSetPrice(GameController gc)
    {
        var dialog = PriceDialog(gc);
        if (dialog == null) { if ((DateTime.UtcNow - _stepAt).TotalSeconds > 4) { _log("shop.price_dialog_missing", new { }); Set(Step.PickItem, "next item"); } return; }
        var field = PriceRow(dialog)?.GetChildAtIndex(0);
        var typed = Text(field).Trim();
        if (typed == _listPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Set(Step.CheckCurrency, "checking the currency"); return; }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > 10) { CancelDialog("price_not_accepted:" + typed); return; }
        // The field is focused when the dialog opens (verified: digits typed right away appeared in it). Click it
        // anyway, clear it and type the price.
        if (field != null && BotInput.Click(Abs(gc, field.GetClientRect().Center)))
        {
            _actionAt = DateTime.UtcNow;
            _keys.Enqueue(Keys.A | Keys.Control); _keys.Enqueue(Keys.End);
            for (var i = 0; i < 8; i++) _keys.Enqueue(Keys.Back);
            foreach (var ch in _listPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)) _keys.Enqueue(Keys.D0 + (ch - '0'));
        }
    }
    private static string CurrencyText(Element? picker)
    {
        // Texts and tooltips anywhere under the picker (visible or not): the selected currency's name.
        var texts = new List<string>();
        void Walk(Element? e, int d)
        {
            if (e == null || d > 6) return;
            var t = Text(e); if (t.Length > 0) texts.Add(t);
            try { var tip = e.Tooltip; if (tip != null) { var tt = Text(tip); if (tt.Length > 0) texts.Add(tt); foreach (var c in tip.Children) { var x = Text(c); if (x.Length > 0) texts.Add(x); } } } catch { }
            IList<Element>? kids = null; try { kids = e.Children; } catch { }
            if (kids != null) foreach (var c in kids) Walk(c, d + 1);
        }
        Walk(picker, 0);
        return string.Join("|", texts);
    }
    private void TickCheckCurrency(GameController gc)
    {
        var dialog = PriceDialog(gc);
        if (dialog == null) { Set(Step.PickItem, "dialog closed"); return; }
        var picker = PriceRow(dialog)?.GetChildAtIndex(1);
        var text = CurrencyText(picker);
        if (text.Contains("Chaos", StringComparison.OrdinalIgnoreCase)) { Set(Step.ClickList, "listing"); return; }
        _log("shop.currency_unknown", new { text, dump = DumpTree(dialog) });
        // Not provably Chaos: open the picker once and choose "Chaos Orb"; never list in an unverified currency.
        if (picker != null && BotInput.Click(Abs(gc, picker.GetClientRect().Center))) { _actionAt = DateTime.UtcNow.AddMilliseconds(400); Set(Step.PickCurrency, "choosing Chaos Orb"); }
    }
    private void TickPickCurrency(GameController gc)
    {
        var dialog = PriceDialog(gc);
        if (dialog == null) { Set(Step.PickItem, "dialog closed"); return; }
        var option = Find(gc.IngameState.IngameUi, e => e.IsVisible && (Text(e) == "Chaos Orb" || Text(e) == "Chaos Orbs"));
        if (option == null)
        {
            if ((DateTime.UtcNow - _stepAt).TotalSeconds > 3) { _log("shop.currency_option_missing", new { dump = DumpTree(PriceRow(dialog)) }); CancelDialog("chaos_option_not_found"); }
            return;
        }
        if (BotInput.Click(Abs(gc, option.GetClientRect().Center))) { _actionAt = DateTime.UtcNow.AddMilliseconds(300); _log("shop.currency_chosen", new { }); Set(Step.CheckCurrency, "re-checking the currency"); }
    }
    private void TickClickList(GameController gc)
    {
        var dialog = PriceDialog(gc);
        if (dialog == null) { Set(Step.PickItem, "dialog closed"); return; }
        var button = PriceRow(dialog)?.GetChildAtIndex(2);
        if (button == null) { CancelDialog("list_button_missing"); return; }
        if (BotInput.Click(Abs(gc, button.GetClientRect().Center))) { _actionAt = DateTime.UtcNow; Set(Step.ListVerify, "confirming the listing"); }
    }
    private void TickListVerify(GameController gc)
    {
        if (PriceDialog(gc) == null && CountItemised(gc) < _itemsBefore)
        {
            Listed++; ListedChaos += _listPrice;
            _log("shop.listed", new { price = _listPrice, currency = "chaos", Listed });
            Set(Step.PickItem, "next item"); return;
        }
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > 5) { _log("shop.list_unconfirmed", new { dialog = PriceDialog(gc) != null, items = CountItemised(gc), before = _itemsBefore }); CancelDialog("list_unconfirmed"); }
    }
    private void CancelDialog(string reason)
    {
        FailReason = reason; _log("shop.cancel_dialog", new { reason });
        _keys.Enqueue(Keys.Escape); Set(Step.PickItem, "skipping the item: " + reason);
    }
    private void CloseShop(string reason)
    {
        if (reason.Length > 0) FailReason = reason;
        _keys.Clear(); Set(Step.CloseShop, "closing the shop");
    }
    private static string MonsterName(Entity item)
    {
        try { return item.GetComponent<CapturedMonster>()?.MonsterVariety?.MonsterName ?? ""; } catch { return ""; }
    }
    public static string CapturedName(Entity item) => MonsterName(item);
    private static object DumpTree(Element? e, int depth = 0)
    {
        if (e == null || depth > 5) return new { };
        string t = Text(e); var r = e.GetClientRect();
        var kids = new List<object>();
        try { foreach (var c in e.Children) kids.Add(DumpTree(c, depth + 1)); } catch { }
        return new { t, r = $"{r.X:0},{r.Y:0},{r.Width:0},{r.Height:0}", v = e.IsVisible, c = kids };
    }
    private bool _stayedHome;
    private void GoHome(GameController gc, string reason)
    {
        if (reason.Length > 0) FailReason = reason;
        _log("beast.go_home", new { reason, Itemised, _stayedHome });
        _keys.Clear();
        if (CapturedPanel(gc) != null || Find(gc.IngameState.IngameUi, e => e.IsVisible && Text(e) == "Bestiary" && e.GetClientRect().Y < 250) != null) _keys.Enqueue(Keys.Escape);
        _chatRetries = 0;
        if (_stayedHome && gc.Area?.CurrentArea?.Name != "The Menagerie")
        {
            // Never left the Hideout: close the Bestiary and finish now (2026-09-25: waiting on the GoHome step timed out).
            _keys.Clear(); if (BotInput.CanAct) BotInput.PressKey(Keys.Escape);
            _log("beast.itemise_done", new { Itemised, ItemisedChaos, reason = FailReason });
            _step = FailReason.Length > 0 && Itemised == 0 ? Step.Failed : Step.Done; Status = $"Beasts: itemised {Itemised} ({ItemisedChaos:0}c)"; return;
        }
        Chat(gc, "hideout"); Set(Step.GoHome, "returning with /hideout");
    }
    private void Chat(GameController gc, string command)
    {
        _keys.Enqueue(Keys.Return); _keys.Enqueue(Keys.None); _keys.Enqueue(Keys.OemQuestion);
        foreach (var ch in command.ToUpperInvariant()) _keys.Enqueue((Keys)ch);
        _keys.Enqueue(Keys.Return);
    }
    private void PumpKeys(GameController gc)
    {
        var k = _keys.Peek();
        if (k == Keys.None) { if ((DateTime.UtcNow - _keyAt).TotalMilliseconds >= 700) { _keys.Dequeue(); _keyAt = DateTime.UtcNow; } return; }
        if (k == Keys.OemQuestion && !AwakeningGameReader.ChatOpen(gc))
        {
            if ((DateTime.UtcNow - _keyAt).TotalMilliseconds < 700) return;
            if (BotInput.CanAct && BotInput.PressKey(Keys.Return)) _keyAt = DateTime.UtcNow;
            return;
        }
        var sent = BotInput.CanAct && ((k & Keys.Control) != 0 ? BotInput.PressCtrlKey(k & Keys.KeyCode) : BotInput.PressKey(k));
        if (sent) { _keys.Dequeue(); _keyAt = DateTime.UtcNow; _actionAt = DateTime.UtcNow; }
    }
    public static int CountItemised(GameController gc)
    {
        try { return StashSystem.GetInventorySlotItems(gc)?.Count(i => i.Item?.Path == ItemisedPath) ?? 0; } catch { return 0; }
    }
    private static bool CursorHasItem(GameController gc)
    {
        try { return gc.IngameState.Data.ServerData.PlayerInventories.Select(h => h?.Inventory).Any(i => i != null && i.InventType.ToString() == "Cursor" && i.Items?.Count > 0); }
        catch { return false; }
    }
    private static int FreeCells(GameController gc)
    {
        try { var items = StashSystem.GetInventorySlotItems(gc); return 60 - (items?.Sum(i => Math.Max(1, i.SizeX * i.SizeY)) ?? 0); } catch { return 0; }
    }
    private static Element? Find(Element root, Func<Element, bool> match, int maxNodes = 6000)
    {
        var queue = new Queue<(Element E, int D)>(); queue.Enqueue((root, 0));
        for (var n = 0; queue.Count > 0 && n < maxNodes; n++)
        {
            var (e, d) = queue.Dequeue();
            try { if (match(e)) return e; } catch { }
            if (d >= 16) continue;
            IList<Element>? kids = null; try { kids = e.Children; } catch { }
            if (kids == null) continue;
            foreach (var c in kids) if (c != null && c.IsVisible) queue.Enqueue((c, d + 1));
        }
        return null;
    }
    private static string Text(Element? e) { try { return e?.Text ?? ""; } catch { return ""; } }
    private static Vector2 Abs(GameController gc, SharpDX.Vector2 p) { var w = gc.Window.GetWindowRectangle(); return new Vector2(w.X + p.X, w.Y + p.Y); }
    private static bool SetClipboard(string text)
    {
        var ok = false;
        var t = new Thread(() => { for (var i = 0; i < 5 && !ok; i++) { try { Clipboard.SetText(text); ok = true; } catch { Thread.Sleep(30); } } });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); t.Join(800);
        return ok;
    }
    private void Set(Step s, string status) { _step = s; _stepAt = DateTime.UtcNow; Status = "Beasts: " + status; }
    private void Fail(string reason) { FailReason = reason; _log("beast.itemise_failed", new { reason, step = _step.ToString(), Itemised }); _step = Step.Failed; Status = "Beasts failed: " + reason; }
}
