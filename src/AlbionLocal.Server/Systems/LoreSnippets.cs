using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The lore snippets (analysis/work/stageR/lore/SPEC.md, "SPEC" here). Every criticalscenemember type="loresnippet" of a map's templates whose
/// loresnippetid is in lore.xml stands as a <see cref="LoreSnippetObject"/> (673 + the viewer's 674). The claim (SPEC §3) is the client's simple
/// action ClaimLoreSnippet (r0.SimpleAction 11; handler cwm, can-start check cwm.ai6 0x1809E2B70): DoSimpleActionStart (op 445 {1 counter,
/// 2 action, 3 the object}) is answered rc 0 with {0 counter, 1 action, 2 end}, DoSimpleActionStart (492) goes to the player and its observers,
/// and after the snippet's claimduration (5 s) DoSimpleActionFinished (494 {0 player, 1 counter, 2 action, 3 object, 4 the request's key 4})
/// makes the client play the claim (cwm.ai8 → ddr.aqp); then the claim is kept per character (<see cref="GoalLog.Lore"/>), the claimer gets
/// LoreSnippedClaimed (675 {0 index}: the client adds it to its lore list and shows the notice, ctp 0x1809973C0) and 674 {claimed}, and the
/// journal counts it (<see cref="AlbionJournal.LoreClaimed"/>: the LORE category's loreachievement of the snippet and the claimloresnippet
/// missions). RequestLoreSnippetStates (op 542) answers the claimed flags. LoreSnippets:Enabled false: no objects, no claims, all unclaimed.
/// Under <see cref="World.Sync"/>.
/// </summary>
public sealed class LoreSnippets(World world)
{
    /// <summary>r0.SimpleAction.ClaimLoreSnippet.</summary>
    public const byte ClaimAction = 11;

    /// <summary>
    /// The l0 codes cwm.ai6 sets for a refused start (a byte enum of 17800 + n; their texts are not traced): 17800 the player's state or a
    /// claimed snippet, 17807 the target is no lore snippet, 17808 out of reach, 17811 feature switch 182 LoreSnippets off.
    /// </summary>
    public const short Refused = 17800, NotALoreSnippet = 17807, TooFar = 17808, Disabled = 17811;

    /// <summary>How far from the snippet its claimer may stand (m) [assume: the client's own check is the local player's interaction range, cpx.ara].</summary>
    public const float Reach = 5 + SpellCasts.RangeTolerance;

    /// <summary>A claim under way: the object, the client's counter, the request's key 4, when it ends.</summary>
    private sealed record Pending(LoreSnippetObject Snippet, byte Counter, bool Flag, TimeSpan Due);

    private readonly Dictionary<Player, Pending> _pending = [];

    private bool Enabled => world.Config.LoreSnippetsEnabled;

    /// <summary>lore.xml, null without cluster data or when it cannot be read.</summary>
    public LoreData? Data
    {
        get
        {
            if (world.Clusters is not { } store) return null;
            try { return store.Lore; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Combat.LogOnce("lore.bin", $"lore.bin cannot be read ({e.Message}): no lore snippets");
                return null;
            }
        }
    }

    /// <summary>Whether <paramref name="c"/> claimed snippet <paramref name="name"/>.</summary>
    public static bool IsClaimed(Game.Character c, string name) => c.Goals?.Lore?.Contains(name) == true;

    /// <summary>The lore snippets of <paramref name="map"/>.</summary>
    public static IEnumerable<LoreSnippetObject> Of(Map map) => map.Objects.Values.OfType<LoreSnippetObject>();

    /// <summary>
    /// The lore snippet members of <paramref name="template"/> for <paramref name="map"/> (none when off): only those world.xml lists in the
    /// cluster's criticalObjects (a Guid) [infer: the templates carry more spots than world.xml names, e.g. 0201 has four, world.xml one
    /// (instanceslot_13, KEEPER_18), and the same snippet twice; world.xml's 77 used loresnippet entries are the placed ones]. A loresnippetid not in
    /// lore.xml is skipped.
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Enabled) yield break;
        var members = template.Critical.Where(m => m.Type == "loresnippet" && m.Guid is not null).ToList();
        if (members.Count == 0 || Data is not { } data) yield break;
        foreach (var m in members)
        {
            var id = m.Components.FirstOrDefault(c => c.Name.LocalName == "loresnippet")?.Attribute("loresnippetid")?.Value ?? m.Attr("loresnippetid");
            if (data.Find(id) is { } snippet) yield return new LoreSnippetObject(world.ObjectIds.Next(), m, snippet);
            else Combat.LogOnce("lore:" + id, $"{map.Cluster} {m.Path}: lore snippet '{id}' is not in lore.xml: left out");
        }
    }

    /// <summary>A lore snippet of <paramref name="spawn"/> put on <paramref name="map"/> after it was filled (the self-test), shown to its players.</summary>
    public LoreSnippetObject Place(Map map, ClusterObject spawn, LoreData.Snippet snippet)
    {
        var o = new LoreSnippetObject(world.ObjectIds.Next(), spawn, snippet);
        map.AddObject(o);
        return o;
    }

    /// <summary>
    /// RequestLoreSnippetStates (op 542, z8 {0 int[] indices}; sent by c32.j 0x180A77B20 in game): 0 the indices, 1 claimed, parallel
    /// [infer: aiq {0 int[], 1 bool[]}]; no indices = every snippet of lore.xml [assume].
    /// </summary>
    public RequestLoreSnippetStatesResponse States(Game.Character c, int[]? indices)
    {
        var data = Data;
        var asked = indices is { Length: > 0 } ? indices : [.. Enumerable.Range(0, data?.All.Count ?? 0)];
        return new RequestLoreSnippetStatesResponse
        {
            bk2 = asked,
            bk3 = [.. asked.Select(i => Enabled && data?.At(i) is { } s && IsClaimed(c, s.Name))],
        };
    }

    /// <summary>
    /// DoSimpleActionStart with ClaimLoreSnippet (SPEC §3.2): an unclaimed snippet on the player's map within <see cref="Reach"/>, the player
    /// up and not already claiming: rc 0, 492 to it and its observers, the claim due after the snippet's claimduration. Returns the response.
    /// </summary>
    public DoSimpleActionStartResponse Start(Player p, DoSimpleActionStartRequest req)
    {
        var answer = new DoSimpleActionStartResponse { aq0 = req.jt, aq1 = req.ju, Extra = { [0] = req.jt } }; // counter 0 goes out too
        var why = Check(p, req.jv, out var code, out var snippet);
        if (why is not null)
        {
            Log.Info("world", $"'{p.Character.Name}' lore claim of #{req.jv} refused ({code}): {why}");
            answer.ReturnCode = code;
            return answer;
        }
        var now = world.GameNow;
        var span = snippet!.Snippet.ClaimDuration;
        var end = now + SpellCasts.Seconds(span.TotalSeconds);
        _pending[p] = new Pending(snippet, req.jt, req.jw, world.Now + span);
        answer.aq2 = end;
        p.SendAround(new DoSimpleActionStartEvent { ddn = p.Id, b3o = now, b3p = end, b3q = ClaimAction, b3r = req.jt, b3s = snippet.Id, Extra = new() { [4] = req.jt } });
        Log.Info("world", $"'{p.Character.Name}' claims {snippet} ({span.TotalSeconds:0.#} s)");
        return answer;
    }

    /// <summary>Why <paramref name="p"/> cannot claim object <paramref name="id"/> now (null: it can), with the code and the snippet.</summary>
    private string? Check(Player p, long id, out short code, out LoreSnippetObject? snippet)
    {
        snippet = null;
        code = Refused;
        if (!Enabled)
        {
            code = Disabled;
            return "lore snippets are off (LoreSnippets:Enabled)";
        }
        if (p.Map is not { } map || !map.Objects.TryGetValue(id, out var o) || o is not LoreSnippetObject s)
        {
            code = NotALoreSnippet;
            return $"no lore snippet #{id} here";
        }
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (_pending.ContainsKey(p)) return "a claim is under way";
        if (IsClaimed(p.Character, s.Snippet.Name)) return $"{s.Snippet.Name} is claimed already";
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, s.Position) > Reach)
        {
            code = TooFar;
            return string.Create(CultureInfo.InvariantCulture, $"{s} is {Vector2.Distance(at, s.Position):0.#} m away (reach {Reach:0.#} m)");
        }
        snippet = s;
        return null;
    }

    /// <summary>
    /// DoSimpleActionCancel (op 446 {1 counter}) of the player's claim: dropped, 493 {0 player, 1 counter} to it and its observers. False when no
    /// claim of that counter is under way (the caller answers as before).
    /// </summary>
    public bool Cancel(Player p, byte counter)
    {
        if (!_pending.TryGetValue(p, out var pending) || pending.Counter != counter) return false;
        Drop(p, pending, "cancelled");
        return true;
    }

    private void Drop(Player p, Pending pending, string why)
    {
        _pending.Remove(p);
        p.SendAround(new DoSimpleActionCancelEvent { ddn = p.Id, b3j = pending.Counter, Extra = new() { [1] = pending.Counter } });
        Log.Info("world", $"'{p.Character.Name}' lore claim of {pending.Snippet} {why}");
    }

    /// <summary>Every tick: the claims whose time is up end (<see cref="Finish"/>); a player gone, moved off, or down drops its claim (493).</summary>
    public void Update()
    {
        if (_pending.Count == 0) return;
        foreach (var (p, pending) in _pending.ToList())
        {
            var s = pending.Snippet;
            if (p.Map is not { } map || s.Map != map || !map.Objects.ContainsKey(s.Id) || !map.Players.Values.Contains(p))
            {
                _pending.Remove(p);
                continue;
            }
            if (p.Combat.Down is not null) Drop(p, pending, "broken (knocked down)");
            else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), s.Position) > Reach + 2) Drop(p, pending, "broken (moved away)"); // [assume]
            else if (world.Now >= pending.Due)
            {
                _pending.Remove(p);
                p.SendAround(new DoSimpleActionFinishedEvent { ddn = p.Id, b3k = pending.Counter, b3l = ClaimAction, b3m = s.Id, b3n = pending.Flag, Extra = new() { [1] = pending.Counter } });
                Claim(p, s.Snippet);
            }
        }
    }

    /// <summary>
    /// <paramref name="snippet"/> claimed by <paramref name="p"/> (the end of a claim, a consumed UNIQUE_UNLOCK_LORE item, a GM's grant): kept, 675 {0 index},
    /// 674 {claimed} for the objects of it the player sees, the journal counts it. False when it was claimed already.
    /// </summary>
    public bool Claim(Player p, LoreData.Snippet snippet)
    {
        var goals = p.Character.Goals ??= new Game.GoalLog();
        goals.Lore ??= new(StringComparer.Ordinal);
        if (!goals.Lore.Add(snippet.Name)) return false;
        world.Saves.MarkChanged(p.Character);
        p.Session.Send(new LoreSnippedClaimedEvent { d8f = snippet.Index, Extra = new() { [0] = snippet.Index } });
        SendStates(p, snippet.Name, true);
        AlbionJournal.LoreClaimed(p, snippet.Name);
        Log.Info("world", $"'{p.Character.Name}' claimed lore snippet {snippet.Name} (#{snippet.Index})");
        return true;
    }

    /// <summary>A GM's reset: <paramref name="snippet"/> unclaimed for <paramref name="p"/> (674 false for the objects it sees, then 676). The journal stays as it is.</summary>
    public bool Reset(Player p, LoreData.Snippet snippet)
    {
        if (p.Character.Goals?.Lore is not { } lore || !lore.Remove(snippet.Name)) return false;
        world.Saves.MarkChanged(p.Character);
        SendStates(p, snippet.Name, false);
        return true;
    }

    /// <summary>LoreSnippetStatesChangedByCheat (676, no keys) after a GM change [infer: the client then asks op 542 again].</summary>
    public static void ChangedByCheat(Player p) => p.Session.Send(new LoreSnippetStatesChangedByCheatEvent());

    private static void SendStates(Player p, string name, bool claimed)
    {
        if (p.Map is not { } map) return;
        foreach (var o in Of(map).Where(o => o.Snippet.Name == name && p.Visibility.Knows(o))) p.Session.Send(o.BuildState(claimed));
    }
}
