using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The hellgates, phase B (analysis/work/stageL/hellgates/SPEC.md §7, "SPEC" here): the chain (402 ActivateHellgateExit and a walk-in portal at
/// CHAIN_EXIT after the claim; the winners' next hellgate of the mode, its chests × bct.af(chain)), the restricted area (452 after
/// arearestrictiondelay in a fight against a team, the warning ring and the damage beyond), the cluster type's spawn buffs, the infamy of the
/// win (446 per InfamyType, the milestones) and the party-vs-party match (Hellgates:Matchmaking Players: a second party of the same versus mode
/// at start B, the two Hostile to each other during the second objective, the fallen party's state 6). Not here (SPEC §7, client checks first):
/// companions, open-world hellgates, the might of the weekly PvP challenge (no such system here), HellgateSuspense (456: an animator's values).
/// Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Hellgates
{
    /// <summary>The restricted area's damage a second (HELLGATE_DAMAGE_HIT, spells.xml: its unconditional health change): 1 % of the maximum health outside the radius.</summary>
    public const double WarningShare = 0.01;

    /// <summary>HELLGATE_DAMAGE_STACK's maxcharges: the damage beyond the ring grows to its 31st second outside.</summary>
    public const int MaxAreaStacks = 31;

    /// <summary>
    /// HELLGATE_DAMAGE_HIT's damage beyond the ring with <paramref name="stacks"/> seconds outside (its stack, 1-31), on top of the flat
    /// <see cref="WarningShare"/> it deals everywhere outside the radius: 2 % of the maximum health up to 10, then 0.25 % more each (2.25 % at
    /// 11, 7.25 % at 31); 3 % to 8.25 % a second in all.
    /// </summary>
    public static double DamageShare(int stacks) => stacks <= 10 ? 0.02 : 0.02 + 0.0025 * (Math.Min(stacks, MaxAreaStacks) - 10);

    /// <summary>
    /// After the Join in an instance (<see cref="Joined"/>): the chain exit's 402 to a winner, the restricted area under way (452), and to a
    /// member that <paramref name="arrived"/> (not a relog) the cluster type's spawn buffs under Hellgates:SpawnBuffs (isclusterchange true,
    /// ispvppossible in a versus mode, every other condition false: no hideout, no revive).
    /// </summary>
    private void JoinedB(Player p, HellgateInstance i, bool arrived)
    {
        if (i.ChainExit is { } chain && !i.LostBy(p.Character.Id)) p.Session.Send(new ActivateHellgateExitEvent { ddn = p.Id, brq = chain.Position });
        if (i.Area is { } area) p.Session.Send(AreaEvent(p, area));
        if (!arrived || !Options.SpawnBuffs || p.Map?.Template is not { } t) return;
        var given = SpawnBuffs.Give(p, t.SpawnBuffs, c => c switch { "isclusterchange" => true, "ispvppossible" => i.Versus, _ => false }, _ => true);
        _spawnBuffs.UnionWith(given);
        if (given.Count > 0) Log.Info("world", $"'{p.Character.Name}' in {i}: spawn buffs {string.Join(", ", given)}");
    }

    /// <summary>The cluster types' spawn buffs a hellgate gives: HELL_2V2_ENTER (also one saved before a restart), and any other given since the start.</summary>
    private readonly HashSet<string> _spawnBuffs = ["HELL_2V2_ENTER"];

    /// <summary>
    /// A Join outside any hellgate (<see cref="Joined"/>): a hellgate spawn buff that never ends by itself (an effect of time 0) ends unless an
    /// effect of it has persistsafterclusterchange (HELL_2V2_ENTER's -35 % healing and +10 % damage against players have it false; the server's
    /// effects do not read that attribute themselves: CorruptedDungeons.Joined does the same for CORRUPTED_DUNGEON_ENTER). Timed ones run out.
    /// </summary>
    private void LeftBuffs(Player p)
    {
        if (p.Effects.Count == 0 || !SpellEffects.Remove(world, p, s => _spawnBuffs.Contains(s.Spell.Name) && s.Subs.Any(sub => sub.DurationMs <= 0)
                && !s.Subs.Any(sub => SpellTemplate.Bool(sub.Node, "persistsafterclusterchange")))) return;
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
    }

    // ---- the match (SPEC §7 "party vs party") ----

    /// <summary>
    /// The hellgate a party's finished lock joins as the second party (Hellgates:Matchmaking Players): a live one of the same versus mode made
    /// for another party less than Hellgates:MatchSeconds ago, still in its first objective, without a second party and not a chain's; the
    /// oldest. Null otherwise (a new one: its bots come unless a party joins it in time).
    /// </summary>
    private HellgateInstance? MatchFor(Entry e) =>
        Options.Matchmaking != HellgateMatchmaking.Players || e.Party == 0 || !Options.VersusModes.Contains(e.Mode.Name) ? null
        : _instances.Values.Where(i => !i.Closed && i.Versus && i.Mode == e.Mode && i.Party != 0 && i.Party != e.Party && i.PartyB == 0 && i.Chain == 0
                && i.State == HellgateState.Progress && world.Now - i.StartedAt < TimeSpan.FromSeconds(Options.MatchSeconds) && e.Locked.Count <= i.Mode.TeamSize)
            .OrderBy(i => i.StartedAt).FirstOrDefault();

    /// <summary>The second objective of a matched hellgate is the fight between its two parties when both have a member inside (no bots); false otherwise.</summary>
    private bool StartMatch(HellgateInstance i)
    {
        if (i.PartyB == 0) return false;
        var inside = Inside(i).Where(p => i.Members.ContainsKey(p.Character.Id)).ToList();
        if (!inside.Any(p => i.TeamOf(p.Character.Id) == 1) || !inside.Any(p => i.TeamOf(p.Character.Id) == 2)) return false;
        i.Match = true;
        TellAll(i, "hg.match");
        Log.Info("world", $"{i}: party {i.Party} against party {i.PartyB}");
        return true;
    }

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are members of the two parties of a match while its second objective runs: Hostile
    /// both ways whatever the zone and Pvp:Mode (Pvp.Of; choosing the versus mode with Hellgates:Matchmaking Players is the consent).
    /// </summary>
    public bool Rivals(Player a, Player b) =>
        !a.IsBot && !b.IsBot && a.Map is { } map && b.Map == map && Find(map.Cluster) is { Match: true, State: HellgateState.Defeat } i
        && i.Members.ContainsKey(a.Character.Id) && i.Members.ContainsKey(b.Character.Id) && i.TeamOf(a.Character.Id) != i.TeamOf(b.Character.Id);

    /// <summary>
    /// Every tick of a match's second objective (<see cref="Update"/>): a party whose members inside are all down at once has lost
    /// (<see cref="TeamLost"/>); so has one with none inside (nor arriving) for Hellgates:EmptyCloseSeconds while the other has some (a relog
    /// comes back in time).
    /// </summary>
    private void MatchOver(HellgateInstance i, Map map, List<Player> inside)
    {
        var now = world.Now;
        bool Gone(int team)
        {
            if (inside.Any(p => i.TeamOf(p.Character.Id) == team) || i.Arriving.Keys.Any(id => i.TeamOf(id) == team))
            {
                i.TeamAbsentSince.Remove(team);
                return false;
            }
            if (!i.TeamAbsentSince.TryGetValue(team, out var since)) i.TeamAbsentSince[team] = since = now;
            return now - since >= TimeSpan.FromSeconds(Options.EmptyCloseSeconds);
        }
        bool Fallen(int team) => inside.Where(p => i.TeamOf(p.Character.Id) == team).ToList() is { Count: > 0 } members && members.All(p => p.Combat.Down is not null);
        bool Present(int team) => inside.Any(p => i.TeamOf(p.Character.Id) == team);
        if (Fallen(1)) TeamLost(i, map, 1, "fell");
        else if (Fallen(2)) TeamLost(i, map, 2, "fell");
        else if (Gone(1) && Present(2)) TeamLost(i, map, 1, "left");
        else if (Gone(2) && Present(1)) TeamLost(i, map, 2, "left");
    }

    /// <summary>
    /// Party <paramref name="team"/> (1 the first, 2 the second) of a match lost: its members inside get 455 {6} and the chat line (and keep state 6,
    /// <see cref="Status"/>), the other party wins (state 3: the pvp chest, its infamy; the chests are refused to the fallen, <see cref="ChestRefused"/>).
    /// The REWARD layer's BOSS chest opens as for a beaten bot team (TeamOver): the map's held-back boss never comes in a versus run.
    /// </summary>
    private void TeamLost(HellgateInstance i, Map map, int team, string why)
    {
        if (i.State != HellgateState.Defeat || i.LostTeam != 0) return;
        i.LostTeam = team;
        foreach (var p in Inside(i).Where(p => i.LostBy(p.Character.Id)))
        {
            p.Session.Send(new HellgateStatusUpdateEvent { ddn = p.Id, cix = (int)HellgateState.Lost });
            Expeditions.Tell(p, Settings.Text("hg.beaten"));
        }
        var lost = $"the {(team == 1 ? "first" : "second")} party {why}";
        UnlockRewards(i, map, boss: true, lost);
        Defeated(i, map, lost);
    }

    /// <summary>A chest of a match is refused to the party that lost it (RandomDungeons.OpenChest, LootHandler's RegisterToObject): why, with the chat line, or null.</summary>
    public string? ChestRefused(Player p, LootChest chest)
    {
        if (chest.Map is not { } map || Find(map.Cluster) is not { LostTeam: not 0 } i || !i.LostBy(p.Character.Id)) return null;
        const string why = "the other party won this hellgate's chests";
        Expeditions.Tell(p, Settings.Text("hg.refused", ("why", why)));
        return why;
    }

    // ---- the win: infamy (SPEC §3.8, §7 "infamy") ----

    /// <summary>
    /// Objective 2 is won (<see cref="Defeated"/>): the restricted area ends; each winner inside gets Hellgates:InfamyWin × the chain factor of the
    /// mode's InfamyType (<see cref="Credit"/>).
    /// </summary>
    private void Won(HellgateInstance i)
    {
        EndArea(i);
        var gain = (long)Math.Round(Options.InfamyWin * i.ChainFactor, MidpointRounding.AwayFromZero);
        if (gain <= 0 || i.Mode.InfamyType == 0) return;
        foreach (var p in Inside(i).Where(p => i.Members.ContainsKey(p.Character.Id) && !i.LostBy(p.Character.Id)).ToList()) Credit(p, i.Mode.InfamyType, gain);
    }

    /// <summary>
    /// <paramref name="amount"/> infamy of InfamyType <paramref name="type"/> (2 Hell2v2, 3 Hell5v5) for <paramref name="p"/>: the current total and
    /// the peak (below 2^31, the client's int), saved; UpdateInfamy (446 {0 me, 1 type, 2 the total, 3 the gain}; ctp.ajv) and the chat lines (the
    /// gain, each hellgate.xml milestone crossed). The corrupted dungeons' infamy (type 1) is CorruptedDungeons.Credit's.
    /// </summary>
    public void Credit(Player p, int type, long amount)
    {
        var log = p.Character.Infamy ??= new InfamyLog();
        var before = log.Current.GetValueOrDefault(type);
        var after = Math.Min(int.MaxValue, before + amount);
        log.Current[type] = after;
        log.Peak[type] = Math.Max(log.Peak.GetValueOrDefault(type), after);
        world.Saves.MarkChanged(p.Character);
        p.Session.Send(new UpdateInfamyEvent { d35 = p.Id, d36 = type, d37 = (int)after, d38 = (int)(after - before) });
        Expeditions.Tell(p, Settings.Text("hg.infamy", ("gain", (after - before).ToString("N0", CultureInfo.InvariantCulture)),
            ("total", after.ToString("N0", CultureInfo.InvariantCulture)), ("mode", InfamyName(type))));
        foreach (var m in Data?.Crossed(type, before, after) ?? [])
            Expeditions.Tell(p, Settings.Text("hg.milestone", ("value", m.ToString("N0", CultureInfo.InvariantCulture)), ("mode", InfamyName(type))));
        Log.Info("world", $"'{p.Character.Name}': +{after - before} hellgate infamy ({InfamyName(type)}), {after} now");
    }

    /// <summary>The InfamyType's name in the chat lines (2v2, 5v5, 10v10).</summary>
    public static string InfamyName(int type) => type switch { 2 => "2v2", 3 => "5v5", 4 => "10v10", _ => $"type {type}" };

    // ---- the chain (SPEC §7 "chaining") ----

    /// <summary>
    /// The claim of a won hellgate (Hellgates:Chain): the chain exit at the CHAIN_EXIT marker, a walk-in NewRandomDungeonExit (324 without keys
    /// 12/13: the plain RandomDungeonExit path that sends ChangeCluster {0: its id}, SPEC §3.8) with the mode's chainexitvisualization
    /// (HELLGATE_CHAIN_EXIT → SHARED_HELLGATE_EXIT_LETHAL, 7 x 7) and a CORRUPTED_SOLO_* RandomExit name as key 3 (activationtime 0, as the corrupted
    /// way out) [inference: client check]; 402 ActivateHellgateExit {1 its position} to each winner inside; the next map picked and read now.
    /// </summary>
    private void OpenChain(HellgateInstance i, Map map)
    {
        if (i.ChainExit is not null || i.State != HellgateState.Claimed || Data is not { } data) return;
        if (map.Template?.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ChainExit) is not { } marker
            || world.Clusters?.DungeonsIfLoaded is not { } dungeons || !dungeons.Visualizations.TryGetValue(i.Mode.ChainVisualization, out var vis)
            || vis.Fallback.FirstOrDefault() is not { } prefab)
        {
            Log.Warn("world", $"{i}: no chain exit (no CHAIN_EXIT marker, or randomdungeons.xml without {i.Mode.ChainVisualization})");
            return;
        }
        var size = TileMath.V2((string?)world.Clusters.Tiles.Components(prefab.Name).FirstOrDefault(c => c.Name.LocalName == "randomdungeonrandomexit")?.Attribute("size")) ?? Vector2.One;
        var name = i.Mode.Lethal ? "CORRUPTED_SOLO_LETHAL" : "CORRUPTED_SOLO_NONLETHAL";
        map.AddObject(i.ChainExit = new CorruptedExitPortal(world.ObjectIds.Next(), marker, name, prefab.Name, prefab.Index, size));
        i.NextWorld = PickMap(data, i.Mode, except: i.World);
        if (i.NextWorld is { } next)
        {
            world.PrefetchCluster(next);
            StartsFor(next); // its walk grid built now: the chain's entry needs its start points
        }
        var bonus = ((i.Mode.ChainFactor(i.Chain + 1) - 1) * 100).ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var p in Inside(i).Where(p => i.Members.ContainsKey(p.Character.Id) && !i.LostBy(p.Character.Id)))
        {
            p.Session.Send(new ActivateHellgateExitEvent { ddn = p.Id, brq = marker.Position });
            Expeditions.Tell(p, Settings.Text("hg.chain", ("chain", i.Chain + 1), ("bonus", bonus)));
        }
        Log.Info("world", $"{i}: the chain exit at ({marker.Position.X:0}, {marker.Position.Y:0}), the next on {i.NextWorld ?? "no map"}");
    }

    /// <summary>
    /// <paramref name="p"/> walks into <paramref name="i"/>'s chain exit (ChangeCluster {0: its id}, Handlers/World/HellgateHandler): a winner goes
    /// into the chain's next hellgate (made by the first one: the same mode on the map picked at the claim, chain + 1, for the winners inside),
    /// keeping its first way back; <paramref name="to"/> its arrival for the answer {0 id, 1 map}. Why not (the next map still being read: step in
    /// again), or null.
    /// </summary>
    public string? ChainInto(Player p, HellgateInstance i, out Arrival? to)
    {
        to = null;
        var id = p.Character.Id;
        if (i.ChainExit is null) return "the chain exit is not open";
        if (i.LostBy(id)) return "your party lost this hellgate";
        if (!i.Members.TryGetValue(id, out var back)) return "you are no member of this hellgate";
        // A next one already won, lost or claimed (lingering FinishedLingerSeconds) is not joined: a fresh one of the chain instead.
        if (i.Next is not { Closed: false, State: <= HellgateState.Defeat } next)
        {
            if (i.NextWorld is not { } map) return "no map for the next hellgate is known";
            if (world.CachedCluster(map) is null || StartsFor(map) is not { } starts)
            {
                world.PrefetchCluster(map);
                return "the next hellgate is being prepared; step in again in a moment";
            }
            var party = world.Parties.Of(id)?.Id ?? (i.TeamOf(id) == 2 ? i.PartyB : i.Party);
            var going = Inside(i).Count(m => i.Members.ContainsKey(m.Character.Id) && !i.LostBy(m.Character.Id));
            next = i.Next = Create(i.Mode, map, party, going, starts, i.Chain + 1);
        }
        i.Members.Remove(id);
        i.Arriving.Remove(id);
        to = Send(p, next, back, answer: true);
        Log.Info("world", $"'{p.Character.Name}' chains from {i} into {next}");
        return null;
    }

    // ---- the restricted area (SPEC §3.8, §7 "restricted area") ----

    /// <summary>
    /// Every tick (<see cref="Update"/>): the area starts arearestrictiondelay after the start of a fight against a team (Hellgates:RestrictedArea),
    /// its final phase finalarearestrictionbreak after the first reached its end radius; once a second each member inside that stands outside the
    /// radius takes the warning ring's damage, or beyond the ring the growing damage of its seconds outside (<see cref="DamageShare"/>); so does
    /// each bot of the enemy team still standing (the same rules for both sides).
    /// [inference: the ring is the warning area (HELLGATE_WARNING), beyond it the damage area (HELLGATE_DAMAGE); both pulse HELLGATE_DAMAGE_HIT]
    /// </summary>
    private void UpdateArea(HellgateInstance i, List<Player> inside)
    {
        var now = world.Now;
        if (i.AreaDue is { } due && now >= due)
        {
            i.AreaDue = null;
            if (i.State == HellgateState.Defeat) StartArea(i, final: false);
        }
        if (i.Area is not { } area) return;
        if (!area.Final && now >= area.Reached + TimeSpan.FromSeconds(i.Mode.AreaFinalBreak)) area = StartArea(i, final: true) ?? area;
        if (now < i.NextAreaHit) return;
        i.NextAreaHit = now + TimeSpan.FromSeconds(1);
        var radius = area.RadiusAt(now);
        var width = area.WidthAt(radius);
        var utc = DateTime.UtcNow;
        var ticks = world.GameNow;
        var spell = world.Spells?.IdOf("HELLGATE_DAMAGE_HIT") ?? -1;
        var bots = i.Enemies.Where(b => b.Map?.Cluster == i.Id && b.Bot is { Fight.Defeated: false } && !i.Beaten.Contains(b));
        foreach (var p in inside.Where(p => !p.IsBot).Concat(bots).Where(p => p.Combat.Down is null).ToList())
        {
            var distance = Vector2.Distance(p.PositionAt(utc), area.Center);
            var id = p.Character.Id;
            if (distance <= radius)
            {
                i.AreaStacks.Remove(id);
                continue;
            }
            var stacks = i.AreaStacks[id] = distance <= radius + width ? 0 : Math.Min(MaxAreaStacks, i.AreaStacks.GetValueOrDefault(id) + 1);
            AreaHit(p, stacks == 0 ? WarningShare : WarningShare + DamageShare(stacks), ticks, spell);
            if (i.Area is null) break; // the last enemy bot beaten by it: the win ended the area
        }
    }

    /// <summary>
    /// A phase of <paramref name="i"/>'s restricted area from its template's arearestrictionsettings (SPEC §2.4): the first from startradius to
    /// endradius at arearestrictionvelocity, the <paramref name="final"/> one from endradius to 0 at finalarearestrictionvelocity [inference];
    /// 452 to everyone inside and the chat line. Null (and no area) without the settings.
    /// </summary>
    public HellgateArea? StartArea(HellgateInstance i, bool final)
    {
        if (world.CachedCluster(i.World)?.HellgateAreas.FirstOrDefault() is not { } s)
        {
            Log.Warn("world", $"{i}: no arearestrictionsettings in its template, no restricted area");
            return null;
        }
        var area = final
            ? new HellgateArea(s.Center, s.EndRadius, 0, s.EndWidth, s.EndWidth, (float)i.Mode.AreaFinalVelocity, world.GameNow, world.Now, true)
            : new HellgateArea(s.Center, s.StartRadius, s.EndRadius, s.StartWidth, s.EndWidth, (float)i.Mode.AreaVelocity, world.GameNow, world.Now, false);
        (i.Area, i.AreaDue, i.NextAreaHit) = (area, null, world.Now + TimeSpan.FromSeconds(1));
        foreach (var p in Inside(i)) p.Session.Send(AreaEvent(p, area));
        TellAll(i, final ? "hg.areafinal" : "hg.area");
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{i}: the restricted area{(final ? " (final)" : "")} at ({s.Center.X:0}, {s.Center.Y:0}), {area.From:0.#} → {area.To:0.#} m at {area.Velocity} m/s"));
        return area;
    }

    /// <summary>
    /// The restricted area ends (the win, the loss): 452 {1 false} to everyone inside, with keys 2-8 filled (the centre, the first phase's start
    /// radius as key 4, the phase's widths and m/s): the client's HellgateFXManager animates the ring back out from where it stands to key 4 at
    /// key 8 and only then turns it off; with 0 in both a ring closed to 0 would stay drawn at the centre.
    /// </summary>
    private void EndArea(HellgateInstance i)
    {
        i.AreaDue = null;
        i.AreaStacks.Clear();
        if (i.Area is not { } area) return;
        i.Area = null;
        var start = world.CachedCluster(i.World)?.HellgateAreas.FirstOrDefault()?.StartRadius ?? area.From;
        foreach (var p in Inside(i))
            p.Session.Send(new HellgateRestrictedAreaUpdateEvent
            {
                ddn = p.Id, cin = world.GameNow, cio = area.Center, cip = start, ciq = area.To, cir = area.WidthFrom, cis = area.WidthTo, cit = area.Velocity,
                Extra = new() { [1] = false },
            });
    }

    /// <summary>HellgateRestrictedAreaUpdate (452 aqd {0 me, 1 true, 2 start, 3 centre, 4 start radius, 5 end radius, 6 start width, 7 end width, 8 m/s}).</summary>
    private static HellgateRestrictedAreaUpdateEvent AreaEvent(Player p, HellgateArea area) => new()
    {
        ddn = p.Id, cim = true, cin = area.Start, cio = area.Center, cip = area.From, ciq = area.To, cir = area.WidthFrom, cis = area.WidthTo, cit = area.Velocity,
        Extra = new() { [5] = area.To }, // a final phase's end radius 0 goes out too
    };

    /// <summary>
    /// One second of the area's damage on <paramref name="p"/>: <paramref name="share"/> of its maximum health (no armour, no invincible target),
    /// HealthUpdate (over time, the spell HELLGATE_DAMAGE_HIT) to it and its observers; at 0 a PvP knockdown (mobsarelethal false in every hell
    /// archetype; the fight's own get-up time, Hellgates:KnockdownSeconds, not the PvE combat-over shortcut that would bring it up at full health
    /// 18 s later). An enemy bot at 0 is beaten (BotCombat.Defeated) by its last human attacker on the map, else by the area alone (no rewards).
    /// </summary>
    private static void AreaHit(Player p, double share, long ticks, short spell)
    {
        if (p.Combat.Untouchable || SpellEffects.Invincible(p, ticks)) return;
        var health = p.Health.At(ticks);
        if (health <= 0) return;
        var damage = (float)Math.Min(health, p.Health.Max * share);
        var left = Math.Max(0, health - damage);
        p.Health.Set(left, Math.Max(ticks, p.Health.Stamp));
        var e = Combat.HealthUpdate(p.Id, ticks, -damage, left, EffectType.Magic, HitOrigin.OverTime, p.Id, spell);
        p.Session.Send(e);
        p.SendToObservers(e);
        if (left > 0) return;
        if (p.Bot is { } bot)
        {
            var killer = bot.Fight.Hits.Count > 0 && p.Map?.FindPlayer(bot.Fight.Hits[^1].Id) is { IsBot: false } last ? last : p;
            p.World.Bots.Defeated(p, killer, crime: false, ticks);
        }
        else Knockdown.Start(p, p.Id, "the hellgate", ticks, KnockdownKind.Pvp);
    }

    /// <summary>.hg infamy: <paramref name="c"/>'s hellgate infamy (2v2, 5v5: the current and the peak) and the next milestone of each.</summary>
    public string DescribeInfamy(Character c)
    {
        string Line(int type)
        {
            var current = c.Infamy?.Current.GetValueOrDefault(type) ?? 0;
            var peak = Math.Max(current, c.Infamy?.Peak.GetValueOrDefault(type) ?? 0);
            var next = (Data?.Milestones.GetValueOrDefault(type) ?? []).FirstOrDefault(m => m > current);
            return string.Create(CultureInfo.InvariantCulture, $"{InfamyName(type)} {current:N0} (peak {peak:N0}{(next > 0 ? $", next milestone {next:N0}" : "")})");
        }
        return $"hellgate infamy: {Line(2)}, {Line(3)}";
    }
}
