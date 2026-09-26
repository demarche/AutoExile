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
    private enum Step { Idle, OpenMarket, Filters, Search, Read, Travel, Arrive, Buy, Home, Done, Failed }
    private Step _step = Step.Idle;
    private DateTime _stepAt, _startedAt, _actionAt, _clickAt;
    private readonly Action<string, object> _log;
    private readonly Queue<Keys> _keys = new();
    private DateTime _keyAt;
    private int _chatRetries;
    private static readonly Keys[] HideoutKeys = { Keys.Escape, Keys.None, Keys.Return, Keys.None, Keys.OemQuestion, Keys.H, Keys.I, Keys.D, Keys.E, Keys.O, Keys.U, Keys.T, Keys.Return };
    private MarketMapRequest? _request;
    private readonly HashSet<string> _skippedSellers = new();
    private string _seller = "", _homeArea = "";
    private long _homeHash, _sellerHash;
    private static readonly Dictionary<string, DateTime> DeadSellers = new();
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
        Bought = 0; Spent = 0; FailReason = ""; _searches = 0; _filterRounds = 0; _resetDone = false;
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
        // 2026-09-26 01:33: successful travels arrive in 6-11 s; each dead seller cost 20 s, and after the 6th search the
        // next one waited the full 60 s step timeout (3 maps in 3 min). Give up on a seller after 14 s, up to 12 of them.
        // 02:13-02:23: 19 of 21 travels "failed" at 14 s — the same four sellers again and again in every retry (each
        // retry forgot them), and one of them (TWoods) arrived in 18 s on a later try. 20 s, never while loading, and
        // remember dead sellers across market runs for 20 min.
        if (_step == Step.Arrive && _sellerHash == 0 && !gc.IsLoading && AreaHash(gc) == _homeHash && (DateTime.UtcNow - _stepAt).TotalSeconds > 20 && _skippedSellers.Count < 12)
        { _log("market.travel_failed", new { seller = _seller }); _skippedSellers.Add(_seller); DeadSellers[_seller] = DateTime.UtcNow; Set(Step.Search, "travel failed, next seller"); return; }
        var limit = _step switch { Step.Arrive => 30, Step.Home => 60, Step.Buy => 180, _ => 25 };
        if ((DateTime.UtcNow - _stepAt).TotalSeconds > limit) { GoHomeOrFail(gc, "step_timeout:" + _step); return; }
        if (_keys.Count > 0)
        {
            // Keys.None = pause 700 ms (the chat box needs a moment to open before "/" is typed, otherwise "/" opens
            // the market and the letters act as hotkeys, e.g. hideout edit mode).
            if (_keys.Peek() == Keys.None) { if ((DateTime.UtcNow - _keyAt).TotalMilliseconds >= 700) { _keys.Dequeue(); _keyAt = DateTime.UtcNow; } return; }
            // Keys.Pause = 250 ms gap between typed characters (market filter fields drop keystrokes typed too fast).
            if (_keys.Peek() == Keys.Pause) { if ((DateTime.UtcNow - _keyAt).TotalMilliseconds >= 250) { _keys.Dequeue(); _keyAt = DateTime.UtcNow; } return; }
            // "/hideout" is only typed into an open chat box; otherwise "/" opens the market and the letters are hotkeys.
            if (_keys.Peek() == Keys.OemQuestion && _chatRetries < 2 && !AwakeningGameReader.ChatOpen(ctx.Game))
            {
                if ((DateTime.UtcNow - _keyAt).TotalMilliseconds < 700) return;
                _chatRetries++; _log("market.chat_not_open", new { retries = _chatRetries });
                if (BotInput.CanAct && BotInput.PressKey(Keys.Return)) _keyAt = DateTime.UtcNow;
                return;
            }
            var next = _keys.Peek();
            var sent = (next & Keys.Control) != 0 ? BotInput.CanAct && BotInput.PressCtrlKey(next & Keys.KeyCode) : BotInput.CanAct && BotInput.PressKey(next);
            if (sent) { _keys.Dequeue(); _keyAt = DateTime.UtcNow; }
            return;
        }
        if (gc.IsLoading || (DateTime.UtcNow - _actionAt).TotalMilliseconds < 400 || !BotInput.CanAct) return;
        switch (_step)
        {
            case Step.OpenMarket: TickOpen(gc); break;
            case Step.Filters: TickFilters(gc); break;
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
        // 2026-09-22: a PoE restart cleared the manually saved filters (category Any → gems listed). Fill them in.
        if (!_filtersSet && _filterRounds < 3) { _filterRounds++; PlanFilters(market); Set(Step.Filters, "setting the search filters"); return; }
        _searches++;
        if (_searches > 14) { Fail("too_many_searches"); return; } // 2026-09-26: 6 → 14 (one search per seller; 3 maps per trip was too few)
        Click(gc, button);
        Set(Step.Read, "waiting for results");
    }
    // ── Search filters (paths relative to the market root; calibrated 2026-09-22 from ui-mkt3/mkt4/pk/nt) ──
    // A PoE restart clears the market filters. The bot sets: Category = Map, Map Tier 16–16, IIQ >= min, Pack >= min,
    // and a "Not" stat group holding the NG map mods. Every value is read back (Element.Text) and re-entered if a
    // keystroke was lost (the game shows a busy/hourglass cursor for a moment after a filter changes).
    private static bool _filtersSet;
    private int _filterRounds, _fStage, _fTries;
    private DateTime _fActedAt;
    private int _fieldIdx = -1;
    private DateTime _blurredAt;
    private readonly Dictionary<string, int> _ruleTries = new();
    private string? _typingRule; private int _typeTries;
    private readonly Dictionary<string, int> _removeTries = new();
    private static readonly int[] FCategory = { 2, 3, 1, 0, 0, 1, 2, 0, 0, 1, 0, 0, 1, 0, 3 };
    private static readonly int[] FMapHeader = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 0, 0 };
    private static readonly int[] FTypeHeader = { 2, 3, 1, 0, 0, 1, 2, 0, 0, 0, 0 };
    private static readonly int[] FTierLabel = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 0, 0, 0, 0 };
    private readonly HashSet<int> _typedFields = new(); private bool _needBlur;
    private static readonly int[] FTierRow = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 0, 0 };
    private static readonly int[] FTierMin = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 0, 0, 1, 0, 0, 0 };
    private static readonly int[] FTierMax = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 0, 0, 1, 0, 1, 0 };
    private static readonly int[] FPackMin = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 0, 1, 1, 0, 0, 0 };
    private static readonly int[] FQuantityMin = { 2, 3, 1, 0, 0, 1, 2, 0, 5, 1, 1, 0, 1, 0, 0, 0 };
    private static readonly int[] FViewport = { 2, 3, 1 };
    // Broom icon right of "Search" (user, 2026-09-22): resets every filter to default.
    private static readonly int[] FResetAll = { 2, 3, 1, 0, 0, 2, 1, 0 };
    private bool _resetFilters, _resetDone;
    private static readonly int[] FGroupTitle = { 2, 3, 1, 0, 0, 1, 2, 1, 0, 0, 0, 0 };
    private static readonly int[] FGroupEdit = { 2, 3, 1, 0, 0, 1, 2, 1, 0, 0, 1, 0 };
    private static readonly int[] FGroupRows = { 2, 3, 1, 0, 0, 1, 2, 1, 0, 1 };
    // Query typed into "+ Add Stat Filter" (letters and spaces only), and the lower-case text the picked row must contain.
    private static readonly (string Id, string Query, string Key)[] NotRules =
    {
        ("no_regen", "PLAYERS CANNOT REGENERATE LIFE", "players cannot regenerate"),
        ("less_recovery", "LESS RECOVERY RATE OF LIFE AND ENERGY SHIELD", "less recovery rate"),
        ("physical_thorns", "PHYSICAL THORNS", "physical thorns"),
        ("elemental_thorns", "ELEMENTAL THORNS", "elemental thorns"),
        ("shaper_touched", "MONSTERS IN AREA ARE SHAPER", "shaper-touched"),
        ("max_resists", "PLAYERS HAVE TO ALL MAXIMUM RESISTANCES", "maximum resistances"),
        ("defences", "PLAYERS HAVE MORE DEFENCES", "defences"),
        ("shaper_influence", "AREA IS INFLUENCED BY THE SHAPER", "influenced by the shaper"),
        ("eradicator", "OCCUPIED BY THE ERADICATOR", "eradicator"),
        ("purifier", "OCCUPIED BY THE PURIFIER", "purifier"),
        ("constrictor", "OCCUPIED BY THE CONSTRICTOR", "constrictor"),
        ("enslaver", "OCCUPIED BY THE ENSLAVER", "enslaver"),
        ("no_leech", "MONSTERS CANNOT BE LEECHED FROM", "cannot be leeched"),
        ("flask_charges", "PLAYERS GAIN REDUCED FLASK CHARGES", "reduced flask charges"),
        ("elemental_weakness", "PLAYERS ARE CURSED WITH ELEMENTAL WEAKNESS", "elemental weakness"),
        ("extra_lightning", "MONSTERS DEAL EXTRA PHYSICAL DAMAGE AS LIGHTNING", "damage as lightning|damage as extra lightning"),
        ("spell_suppress", "MONSTERS HAVE CHANCE TO SUPPRESS SPELL DAMAGE", "suppress spell damage"),
        // 2026-09-22 (user decision): ground-patch maps killed 2 of 2 runs. Other patch kinds are still rejected by the policy.
        ("ground_shocked", "AREA HAS PATCHES OF SHOCKED GROUND", "patches of shocked ground"),
        ("ground_burning", "AREA HAS PATCHES OF BURNING GROUND", "patches of burning ground"),
        ("ground_chilled", "AREA HAS PATCHES OF CHILLED GROUND", "patches of chilled ground"),
    };
    private void PlanFilters(Element market)
    {
        _fStage = _resetFilters && !_resetDone ? -1 : 0; _fTries = 0; _fActedAt = DateTime.MinValue; _ruleTries.Clear(); _typingRule = null; _removeTries.Clear(); _typedFields.Clear(); _needBlur = false; _fieldIdx = -1; _blurredAt = DateTime.MinValue;
        _log("market.filters_planned", new { round = _filterRounds, _request!.MinQuantity, _request.MinPackSize });
    }
    private static string FieldValue(GameController gc, Element f)
    {
        var t = Lower(f); if (t.Length > 0) return t;
        var i = AwakeningGameReader.InputText(gc, f); return (i ?? "").Trim().ToLowerInvariant();
    }
    private static string Lower(Element? e) => Regex.Replace(Text(e), @"<[^>]*>|[{}]", "").Trim().ToLowerInvariant();
    private bool Act(string what)
    {
        _fActedAt = DateTime.UtcNow; _stepAt = DateTime.UtcNow;
        if (++_fTries > 12) { _log("market.filter_gave_up", new { stage = _fStage, what }); _fStage++; _fTries = 0; return false; }
        return true;
    }
    // Scrolls the filter panel until the element is inside it; returns false while scrolling.
    private bool InView(GameController gc, Element market, Element e)
    {
        var view = Child(market, FViewport)?.GetClientRect(); var r = e.GetClientRect();
        if (view == null || (r.Center.Y > view.Value.Top + 20 && r.Center.Y < view.Value.Bottom - 20)) return true;
        var w = gc.Window.GetWindowRectangle();
        if (BotInput.Wheel(new Vector2(w.X + view.Value.Center.X, w.Y + view.Value.Center.Y), r.Center.Y < view.Value.Top ? 3 : -3))
        { _fActedAt = DateTime.UtcNow; _stepAt = DateTime.UtcNow; }
        return false;
    }
    private void TypeInto(GameController gc, Element field, string text, params Keys[] after)
    {
        Click(gc, field);
        _keys.Enqueue(Keys.None);
        _keys.Enqueue(Keys.End);
        for (var i = 0; i < 50; i++) _keys.Enqueue(Keys.Back);
        foreach (var ch in text.ToUpperInvariant()) { _keys.Enqueue(ch == ' ' ? Keys.Space : (Keys)ch); _keys.Enqueue(Keys.Pause); }
        if (after.Length > 0) { _keys.Enqueue(Keys.None); foreach (var k in after) _keys.Enqueue(k); }
    }
    // 2026-09-22 (user): typing each NG query one key at a time with 250 ms gaps took ~15 s per rule. Paste it instead:
    // the text goes to the Windows clipboard (STA thread) and is inserted with Ctrl+V.
    private static bool SetClipboard(string text)
    {
        var ok = false;
        var t = new Thread(() => { for (var i = 0; i < 5 && !ok; i++) { try { Clipboard.SetText(text); ok = true; } catch { Thread.Sleep(30); } } });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); t.Join(800);
        return ok;
    }
    private void PasteInto(GameController gc, Element field, string text, params Keys[] after)
    {
        if (!SetClipboard(text)) { TypeInto(gc, field, text, after); return; }
        Click(gc, field);
        _keys.Enqueue(Keys.None);
        // The add-stat box is empty after each pick ("typed": "" in every log line); Ctrl+A + Back clears any leftover.
        _keys.Enqueue(Keys.A | Keys.Control);
        _keys.Enqueue(Keys.Back);
        _keys.Enqueue(Keys.V | Keys.Control);
        _keys.Enqueue(Keys.Pause);
        if (after.Length > 0) { _keys.Enqueue(Keys.None); foreach (var k in after) _keys.Enqueue(k); }
    }
    private void TickFilters(GameController gc)
    {
        var market = MarketRoot(gc);
        if (market == null) { Set(Step.OpenMarket, "market closed, reopening"); return; }
        if ((DateTime.UtcNow - _fActedAt).TotalMilliseconds < 1300) return;
        switch (_fStage)
        {
            case -1: // start from a clean slate (results were wrong or a stale stat row could not be removed)
            {
                var broom = Child(market, FResetAll);
                _resetDone = true; _resetFilters = false; _fStage = 0; _fTries = 0;
                if (broom != null) { Click(gc, broom); _fActedAt = DateTime.UtcNow; _log("market.filters_reset", new { round = _filterRounds }); }
                return;
            }
            case 0: // Item Category = Map
            {
                var field = Child(market, FCategory);
                if (field == null || !field.IsVisible)
                {
                    // A reset (or a fresh client) collapses "Type Filters": expand it; never mark unset filters as set.
                    var typeHeader = Child(market, FTypeHeader);
                    if (typeHeader != null && InView(gc, market, typeHeader) && Act("expand_type")) { Click(gc, typeHeader); _log("market.filter_expand", new { section = "type" }); }
                    else if (typeHeader == null) { _log("market.filter_field_missing", new { stage = 0 }); _filterRounds = 99; Set(Step.Search, "filter panel not found"); }
                    return;
                }
                if (Lower(field) == "map") { _fStage = 1; _fTries = 0; return; }
                if (!InView(gc, market, field) || !Act("category")) return;
                PasteInto(gc, field, "MAP", Keys.Down, Keys.Return);
                return;
            }
            case 1: // Map Tier 16-16, IIQ, Pack
            {
                var header = Child(market, FMapHeader);
                if (Child(market, FTierRow)?.IsVisible != true)
                { if (header != null && InView(gc, market, header) && Act("expand_map")) Click(gc, header); return; }
                var want = new (int[] Path, string Value)[] { (FTierMin, "16"), (FTierMax, "16"),
                    (FQuantityMin, _request!.MinQuantity.ToString(CultureInfo.InvariantCulture)), (FPackMin, _request.MinPackSize.ToString(CultureInfo.InvariantCulture)) };
                // One field at a time: type → click the neutral "Map Tier" label (blur; a focused field only shows its
                // new text after losing focus) → verify via InputText/Text → next field. Up to 3 tries per field.
                for (var i = 0; i < want.Length; i++)
                {
                    var (path, value) = want[i];
                    var f = Child(market, path);
                    if (f == null) continue;
                    if (FieldValue(gc, f) == value) { if (_needBlur && _fieldIdx == i) _needBlur = false; continue; }
                    if (_needBlur && _fieldIdx == i)
                    {
                        var label = Child(market, FTierLabel);
                        if (label != null && InView(gc, market, label)) { Click(gc, label); _fActedAt = DateTime.UtcNow; _needBlur = false; _blurredAt = DateTime.UtcNow; }
                        return;
                    }
                    if (_fieldIdx == i && (DateTime.UtcNow - _blurredAt).TotalMilliseconds < 2500) return; // let the blur settle
                    if (_fieldIdx == i) _log("market.filter_retype", new { field = value, text = Text(f), input = AwakeningGameReader.InputText(gc, f) });
                    if (!InView(gc, market, f) || !Act("field:" + value)) return;
                    // 2026-09-23 (user): the IIQ box stayed empty — single keystrokes are lost while the client shows the
                    // busy cursor. One Ctrl+V is far more reliable than typing the digits.
                    PasteInto(gc, f, value); _fieldIdx = i; _needBlur = true; _blurredAt = DateTime.MinValue;
                    return;
                }
                // All values verified. The section must stay expanded: a collapsed filter section is not applied
                // (2026-09-22: Tier 2 maps listed while "Map Tier 16-16" sat in the collapsed section).
                _fStage = 2; _fTries = 0; return;
            }
            case 2: // Stat group type = Not
            {
                var title = Child(market, FGroupTitle);
                if (title == null) { _log("market.filter_field_missing", new { stage = 2 }); _fStage = 99; return; }
                if (Lower(title) == "not") { _fStage = 3; _fTries = 0; return; }
                var edit = Child(market, FGroupEdit);
                if (edit == null || !InView(gc, market, edit) || !Act("group_not")) return;
                // Options: Stat Filters, Not, If, Count, ... → "Not" is the second entry.
                Click(gc, edit);
                _keys.Enqueue(Keys.None); _keys.Enqueue(Keys.Down); if (_fTries % 2 == 1) _keys.Enqueue(Keys.Down); _keys.Enqueue(Keys.Return);
                return;
            }
            case 3: // NG mods inside the Not group
            {
                // Group body: child 0 = the stat rows (row,0,0,0 = text, row,1,4 = remove), child 1 = "+ Add Stat Filter" (,3 = input).
                var group = Child(market, FGroupRows);
                if (group == null) { _fStage = 99; return; }
                var input = Child(group, 1, 3);
                var kids = Child(group, 0)?.Children.Where(k => k != null).ToList() ?? new();
                var entries = kids.Select(k => (Row: k, Text: Lower(Child(k, 0, 0, 0)))).ToList();
                bool Matches(string text, string key) => key.Split('|').Any(x => text.Contains(x));
                // Wrong picks are removed: not an NG mod, a "fractured" variant, or a duplicate of an earlier row.
                var seen = new HashSet<string>();
                (Element Row, string Text) wrong = default;
                foreach (var e in entries)
                {
                    var rule = NotRules.FirstOrDefault(r => Matches(e.Text, r.Key));
                    // Fractured variants and duplicates are harmless inside a Not group; only foreign stats are removed
                    // (removing re-lays out the list and a stale × click repeated forever on 2026-09-22).
                    if (e.Text.Length > 0 && rule.Id == null && _removeTries.GetValueOrDefault(e.Text) < 3) { wrong = e; break; }
                    if (e.Text.Length > 0 && rule.Id == null && !_resetDone) { _resetFilters = true; PlanFilters(market); return; }
                }
                if (wrong.Row != null)
                {
                    _removeTries[wrong.Text] = _removeTries.GetValueOrDefault(wrong.Text) + 1; _stepAt = DateTime.UtcNow;
                    var remove = Child(wrong.Row, 1, 4);
                    _log("market.not_filter_removed", new { text = wrong.Text });
                    if (remove != null && InView(gc, market, remove)) { Click(gc, remove); _fActedAt = DateTime.UtcNow; }
                    return;
                }
                foreach (var rule in NotRules)
                {
                    if (entries.Any(e => Matches(e.Text, rule.Key) && !e.Text.StartsWith("fractured", StringComparison.Ordinal))) { if (_typingRule == rule.Id) _typingRule = null; continue; }
                    var tries = _ruleTries.GetValueOrDefault(rule.Id);
                    if (tries >= 3) continue;
                    if (input == null || !InView(gc, market, input)) return;
                    _fActedAt = DateTime.UtcNow; _stepAt = DateTime.UtcNow;
                    var typed = Lower(input);
                    if (_typingRule == rule.Id && typed == rule.Query.ToLowerInvariant())
                    {
                        // The query is in the box: pick the (tries)th option (a retry takes the next one down).
                        _ruleTries[rule.Id] = tries + 1; _typingRule = null;
                        for (var i = 0; i <= tries; i++) _keys.Enqueue(Keys.Down);
                        _keys.Enqueue(Keys.Return);
                        _log("market.not_filter_pick", new { rule.Id, option = tries + 1 });
                        return;
                    }
                    if (_typingRule == rule.Id && ++_typeTries > 3) { _ruleTries[rule.Id] = 3; _typingRule = null; _log("market.not_filter_type_failed", new { rule.Id, typed }); return; }
                    if (_typingRule != rule.Id) { _typingRule = rule.Id; _typeTries = 0; }
                    PasteInto(gc, input, rule.Query);
                    _log("market.not_filter_type", new { rule.Id, typed, paste = true });
                    return;
                }
                _log("market.not_filters", new { present = entries.Select(e => e.Text).ToArray(),
                    missing = NotRules.Where(r => !entries.Any(e => Matches(e.Text, r.Key) && !e.Text.StartsWith("fractured", StringComparison.Ordinal))).Select(r => r.Id).ToArray() });
                _fStage = 99; return;
            }
            default:
                _filtersSet = true; _log("market.filters_set", new { round = _filterRounds }); Set(Step.Search, "filters set");
                return;
        }
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
        if (rows.Count > 0 && rows.All(r => r.Reject?.StartsWith("not_T16", StringComparison.Ordinal) == true) && _filterRounds < 3)
        { _filtersSet = false; _resetFilters = true; Set(Step.Search, "results are not T16 maps: resetting the filters"); return; }
        foreach (var k in DeadSellers.Where(x => (DateTime.UtcNow - x.Value).TotalMinutes > 20).Select(x => x.Key).ToList()) DeadSellers.Remove(k);
        var pick = rows.FirstOrDefault(r => r.Reject == null && !_skippedSellers.Contains(r.Seller) && !DeadSellers.ContainsKey(r.Seller));
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
        _sellerHash = AreaHash(gc); DeadSellers.Remove(_seller);
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
        if (Bought < r.Count && _searches < 14) { _skippedSellers.Add(_seller); GoHome(gc, "seller_exhausted", research: true); return; }
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
        _leaveHash = AreaHash(gc);
        _log("market.go_home", new { reason, bought = Bought, spent = Spent, research, leaveHash = _leaveHash, homeHash = _homeHash });
        _chatRetries = 0;
        foreach (var k in HideoutKeys) _keys.Enqueue(k);
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
            if (AreaHash(gc) == _homeHash && SellerGrid(gc) != null && (DateTime.UtcNow - _actionAt).TotalSeconds > 2 && _keys.Count == 0)
            { _keys.Enqueue(Keys.Escape); _actionAt = DateTime.UtcNow; Status = "Market: closing the seller window at home"; return; }
            // Retry the chat command once if the first one was eaten (e.g. by a still-open window).
            if ((DateTime.UtcNow - _stepAt).TotalSeconds > 20 && (DateTime.UtcNow - _actionAt).TotalSeconds > 15)
            { _chatRetries = 0; foreach (var k in HideoutKeys) _keys.Enqueue(k); _actionAt = DateTime.UtcNow; }
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
    private long _leaveHash;
    private bool IsHome(GameController gc)
    {
        var area = gc.Area?.CurrentArea;
        if (gc.IsLoading || area?.IsHideout != true) return false;
        var hash = AreaHash(gc);
        // 2026-09-21: a seller whose hideout is also "Luxurious Hideout" left the bot waiting 60 s at home. After
        // "/hideout" the area simply has to change from the one we left and still be a hideout.
        if (_step == Step.Home && _leaveHash != 0 && hash != _leaveHash && SellerGrid(gc) == null) return true;
        // 2026-09-22 18:09: the seller window opened without a travel (still in our hideout); "/hideout" then never
        // changed the area and the bot waited forever. Never having left home counts as home.
        if (_step == Step.Home && _leaveHash == _homeHash && hash == _homeHash && SellerGrid(gc) == null && (DateTime.UtcNow - _stepAt).TotalSeconds > 3) return true;
        return _sellerHash == 0 ? hash == _homeHash || area.Name == _homeArea : hash != _sellerHash && (area.Name == _homeArea || hash == _homeHash);
    }

    // ── UI helpers ───────────────────────────────────────────────────
    /// <summary>True while the market or its results pane is still open (the caller closes it with Escape before using the map device).</summary>
    public static bool MarketOpen(GameController gc) => MarketRoot(gc) != null || ResultsPane(gc) != null;
    /// <summary>Market search, results or a seller's "Select Items To Buy" window is visible.</summary>
    public static bool AnyWindowOpen(GameController gc) =>
        MarketOpen(gc) || Roots(gc).Any(r => Text(Child(r, 3)) == "Select Items To Buy");
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
