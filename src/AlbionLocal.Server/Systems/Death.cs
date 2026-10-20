using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The death of a player in a lethal cluster (stageE pvp SPEC §4, sub-step E3, analysis/work/stageE/pvp/SPEC.md; no lethal PvP was
/// ever captured, so the order and most rules are [infer] from the client and the data):
/// <list type="number">
/// <item><b>When</b>: zero health from a Hostile player's hit where the archetype's playersarelethal is true (ORANGE, RED, black,
/// lethal arenas) kills at once (<see cref="Cause.AliveToDead"/>, from <see cref="Pvp.Damage"/>); a mob still knocks down. A knocked-down
/// player (by a mob, or a PvP knockdown elsewhere) dies by an execute (<see cref="Execute"/>: PLAYER_COUPDEGRACE on slot 26) or by
/// its own die button (Suicide op 102 after showdiebuttondelay, 3 s, only where playersarelethal; <see cref="Suicide"/>), both
/// <see cref="Cause.KnockedOutToDead"/>. An execute between enemy factions in a non-lethal cluster kills without any item loss
/// (<see cref="Cause.NoLoss"/>, SPEC §5 Q10 [infer]).</item>
/// <item><b>Messages</b> (<see cref="Kill"/>): the lethal HealthUpdate (the caller's), the knockdown clean-up without KnockedDown and
/// without regeneration (<see cref="Knockdown.Die"/>), Died (164) to every player of the map {0 pos, 1 victim, 2 its name, 5 its flag,
/// 9 killer, 10 killer name, 13 killer flag; the guild keys 3/11 not filled yet (guilds part 2)}, to the killer KilledPlayer (163) {0 killer, 1 victim, 2 name, 3 its fame}
/// then UpdateFame {5 false} or KillRewardedNoFame (615), and for a crime ReputationUpdate reason 6 (the victim level's killpenalty),
/// then the loot bag and the silver.</item>
/// <item><b>Kill fame</b> [infer, SPEC §4.3]: gamedata KillPlayerIv factor × the summed itemvalue of the victim's equipment, × Rates:Fame
/// × Rates:CombatFame, shared by the killer's party members on the map within pvpfamesharingradius (50 m) by their damage to the victim
/// in the combat timeout (a killer without damage of its own counts as their mean [infer]; alone it takes all).</item>
/// <item><b>Items</b> (gamedata &lt;ItemLoss&gt;, <see cref="PvpDeathSettings"/>): each item loses minfactor..maxfactor of its maximum
/// durability (× MultipleAttackerMultiplier by the attackers of the combat timeout, at least minimumremainingfactor left); the parts with a
/// stack loss drop (everything; in orange the inventory only): a share of each count is destroyed, rounded at random so a single item is
/// destroyed with that chance [infer], and the destroyed ones vanish (the tier's trash item is not made, SPEC §4.4 [infer]). The rest
/// goes into a <see cref="PlayerLootBag"/> at the spot (NewLoot type 1 + LootEquipmentChanged), the killer's and its party's for
/// pvpallowedprotection (120 s; pvpforcedprotection 90 s in PvpForced), then anyone's, gone after vanishtime (1800 s). The victim's
/// client gets InventoryState of its emptied containers [infer], the character is saved at once (Pvp:FullLoot false: nothing drops
/// or is destroyed, only the wear). With Pvp:DeathSilverDrop (off by
/// default [infer]) SilverDropFromPvp (0.75) of its silver lies next to it as a NewSilverObject for the killer.</item>
/// <item><b>Afterwards</b> (<see cref="Update"/>): playervisibilityafterdeath (3 s) later the others get its Leave and no NewCharacter of
/// it any more; the victim's client asks GetRespawnInfos (op 204: <see cref="RespawnInfos"/>) and sends Respawn (op 101,
/// <see cref="Respawn"/>), or autorespawntimerinsecs (600 s) pass: the character goes to the respawn city (Pvp:RespawnCluster, else its
/// last city, else 0000) through the stage-3 cluster change with full bars, and the Join there is followed by a Respawn (88) event
/// [infer]. A dead player who disconnects joins next time at that point (<see cref="Departed"/>); one knocked down in a lethal
/// cluster dies first (<see cref="Abandoned"/> [infer]). No exit or journey while knocked down or dead (ClusterHandler, JourneyHandler).</item>
/// </list>
/// Pvp:LethalDeath false (the pve preset, PVE-DESIGN.md §1): no death at all, <see cref="Pvp.Lethal"/> is false everywhere (the hit
/// knocks down, no execute, no die button, no death on leaving while down).
/// An arena (ARENA_*) or an unnamed archetype (<see cref="ClusterPvp.NoDeathLoss"/>) kills without item loss [infer: their teams
/// and matches are not modelled]. Not modelled (TODO): the guild keys (3/11, guilds part 2) and the kill feed's item power (Died 7/15), kill trophies, might/favor, the bleed-out state of
/// enterbleedoutstateondeath, resurrection by party members, the effect category 30 that forbids an execute. Touched only under
/// <see cref="World.Sync"/>.
/// </summary>
public static class Death
{
    /// <summary>How a death treats the items (SPEC §4.4): AliveToDead (killed standing), KnockedOutToDead (executed, the die button), none.</summary>
    public enum Cause : byte
    {
        AliveToDead,
        KnockedOutToDead,
        NoLoss,
    }

    /// <summary>CharacterSpellSlot CoupDeGrace (SPEC §0).</summary>
    public const byte ExecuteSlot = 26;

    /// <summary>gamedata DeathSettings playerexecutionspell.</summary>
    public const string ExecuteSpell = "PLAYER_COUPDEGRACE";

    /// <summary>PLAYER_COUPDEGRACE without spell data: id 1065 in 345319, castingtime 1.5 s, castrange 2 m.</summary>
    public const short DefaultExecuteId = 1065;

    private const double DefaultExecuteSeconds = 1.5, DefaultExecuteRange = 2;

    /// <summary>The archetype default showdiebuttondelay (3 s, SPEC §1.1): the die button comes this long after the knockdown.</summary>
    public const double DieButtonSeconds = 3;

    /// <summary>The <see cref="PendingTransfer.Via"/> of a respawn.</summary>
    public const string RespawnVia = "respawn";

    /// <summary>GetRespawnInfos key 0: one bool per respawn option (a0.b has 9 values, SPEC §4.3).</summary>
    public const int RespawnOptions = 9;

    /// <summary>The respawn cluster without Pvp:RespawnCluster and without a last city: Thetford, the starter cluster.</summary>
    public const string DefaultRespawnCluster = "0000";

    /// <summary>An execute under way: its target, spell id, the cast counter of the request, its start and end GameTimeStamps.</summary>
    public sealed record ExecuteCast(Player Target, short SpellId, byte Counter, long Start, long End);

    public static PvpDeathSettings Settings(World world) => world.GameData?.PvpDeath ?? PvpDeathSettings.Default;

    // ---- who hit whom ----

    /// <summary>
    /// <paramref name="attacker"/>'s <paramref name="damage"/> on <paramref name="target"/> (<see cref="Pvp.Damage"/>): kept per attacker
    /// for the death's attackers and fame shares.
    /// </summary>
    public static void Hit(Player attacker, Player target, double damage)
    {
        if (damage <= 0 || attacker == target) return;
        var hits = target.PvpState.HitBy;
        hits[attacker] = (hits.GetValueOrDefault(attacker).Damage + damage, target.World.Clock.Now);
    }

    /// <summary>
    /// The players who hit <paramref name="victim"/> within the combat timeout (gamedata InCombat activetimeoutseconds, 20 s [infer]) and
    /// are on its map, with their damage; the older ones are dropped. Stage I: never a bot (bots SPEC §1.9: no PvP credit, fame share or
    /// goal for a bot, and its damage counts neither in the assisters' total nor in their number).
    /// </summary>
    public static List<(Player Player, double Damage)> Attackers(Player victim)
    {
        var world = victim.World;
        var timeout = TimeSpan.FromSeconds(world.GameData?.InCombatActiveSeconds ?? 20);
        var hits = victim.PvpState.HitBy;
        foreach (var old in hits.Where(h => world.Clock.Now - h.Value.Last > timeout || h.Key.Map is null).Select(h => h.Key).ToList()) hits.Remove(old);
        return [.. hits.Where(h => h.Key.Map == victim.Map && !h.Key.IsBot).Select(h => (h.Key, h.Value.Damage))];
    }

    // ---- the death ----

    /// <summary>
    /// <paramref name="victim"/> dies at <paramref name="ticks"/> (see the class): <paramref name="killer"/> the player who killed it (null:
    /// a mob's knockdown and the die button), <paramref name="killerId"/> / <paramref name="killerText"/> Died keys 9/10,
    /// <paramref name="cause"/> the item loss, <paramref name="crime"/> whether the kill is a crime (judged before the hit). The caller has
    /// sent the lethal HealthUpdate. Stage I: a player-bot killer (Bots:Pvp:LethalToPlayers, or the execute or die button after a bot's
    /// knockdown) gets no reward and no goal, its victim's bag is anyone's at once and no silver drops for it; the victim's journal counts a
    /// death by a bot.
    /// </summary>
    public static void Kill(Player victim, Player? killer, long killerId, string killerText, Cause cause, bool crime, long ticks)
    {
        if (victim.Map is not { } map || victim.Combat.Down is { Kind: KnockdownKind.Dead }) return;
        if (victim.IsBot) return; // stage I: a bot's defeat is Bots/BotCombat's (Pvp.Damage), never a death with loss and rewards
        var world = victim.World;
        var settings = Settings(world);
        var rules = Pvp.RulesOf(world, map);
        var attackers = Attackers(victim);
        var fame = killer is null ? 0 : KillFame(victim);
        var at = victim.PositionAt(DateTime.UtcNow);
        var look = victim.Inventory.EquipmentTypeIds(); // the corpse's gear (LootEquipmentChanged), before anything drops
        Knockdown.Die(victim, killerId, killerText, ticks);
        // Died to every player of the map (a non-object event; its receivers were never captured).
        var died = new DiedEvent
        {
            b21 = at, b22 = victim.Id, b23 = victim.Character.Name, b3a = killerId, b3b = killerText,
            Extra = new() { [5] = victim.Flag, [13] = killer?.Flag ?? (byte)0 },
        };
        foreach (var p in map.Players.Values.ToList()) p.Session.Send(died);
        if (killer is { IsBot: true })
        {
            // Killed standing (Bots:Pvp:LethalToPlayers); a death after the bot's knockdown counted at the knockdown (BotCombat.KnockedDownPlayer).
            if (cause == Cause.AliveToDead) PvpGoals.OnPvpDeath(victim, map, byBot: true);
            killer = null; // the loot and the silver below: no owner
        }
        else if (killer is not null)
        {
            var shares = Reward(killer, victim, fame, attackers, crime, ticks);
            // Stage I (pvp-goals SPEC §2.1): the PvP goals of the kill, the victim's journal death.
            PvpGoals.OnPvpKill(world, new PvpKill(PvpActor.Of(killer), PvpActor.Of(victim), [.. attackers.Select(a => (PvpActor.Of(a.Player), a.Damage))], map,
                PvpOutcome.Death, crime, fame, ticks, Math.Max(1, attackers.Count)) { FameShares = shares });
            PvpGoals.OnPvpDeath(victim, map, byBot: false);
            world.Corrupted.Defeated(killer, victim); // stageL corrupted: one side of an invasion killed the other (the official preset's lethal ranks)
        }
        if (cause != Cause.NoLoss && rules.NoDeathLoss)
        {
            Log.Warn("pvp", $"'{victim.Character.Name}' died on {map.Cluster}, an arena or an archetype worldsettings.xml does not name: no item loss [infer]");
            cause = Cause.NoLoss;
        }
        if (cause != Cause.NoLoss)
        {
            var rule = cause == Cause.AliveToDead
                ? rules.Orange ? settings.AliveToDeadOrange : settings.AliveToDead
                : rules.Orange ? settings.KnockedOutToDeadOrange : settings.KnockedOutToDead;
            if (!world.Config.PvpFullLoot) rule = rule with { EquipmentStack = null, InventoryStack = null }; // the wear only, nothing drops
            Drop(victim, killer, rule, Math.Max(1, attackers.Count), rules, ticks, at, look);
        }
        victim.PvpState.HitBy.Clear();
        victim.PvpState.DownCredits = null;
        world.PrefetchCluster(RespawnCluster(victim)); // the respawn finds it resolved
        Log.Info("pvp", $"'{victim.Character.Name}' died on {map.Cluster} ({cause}) by {(killer is null ? killerText : $"'{killer.Character.Name}'")}, {attackers.Count} attacker(s)");
    }

    /// <summary>The kill fame of <paramref name="victim"/> (a7m, before Rates): KillPlayerIv factor × Σ itemvalue of its equipment [infer].</summary>
    public static long KillFame(Player victim)
    {
        var world = victim.World;
        var value = victim.Inventory.Equipment.Items.Sum(i => world.ItemValues.Of(i.Template));
        return (long)Math.Round(value * Settings(world).KillPlayerIvFactor * 10000);
    }

    /// <summary>
    /// The killer's rewards: the fame (× Rates:Fame × Rates:CombatFame) split over the killer's party members within the sharing radius
    /// by their damage; the killer gets KilledPlayer with its share (<paramref name="killMessage"/>: a death; stage I, a player-bot's
    /// knockdown gives none), then UpdateFame (key 5 false) or KillRewardedNoFame; the other sharers UpdateFame. A crime costs the victim
    /// level's killpenalty (reason 6; a player-bot's knockdown its knockdownpenalty, reason 5, as a player's knockdown) and implicates
    /// (<see cref="Pvp.Crime"/>). Returns the fame each sharer got (the PvP goals'
    /// killplayerfame). Also a defeated player-bot's (Bots/BotRewards, its fame already × Bots:Rewards:FameFactor).
    /// </summary>
    public static List<(Player Player, long Fame)> Reward(Player killer, Player victim, long fame, List<(Player Player, double Damage)> attackers, bool crime, long ticks,
        bool killMessage = true)
    {
        var world = killer.World;
        fame = Rates.ScaleFame(fame, world.Rates.Fame * world.Rates.CombatFame);
        var given = Share(killer, victim, fame, attackers, out var mine);
        if (killMessage) killer.Session.Send(new KilledPlayerEvent { ddn = killer.Id, cmo = victim.Id, cmp = victim.Character.Name, cmq = mine });
        if (mine <= 0) killer.Session.Send(new KillRewardedNoFameEvent { ddn = killer.Id }); // SPEC §4.3: no fame for it
        foreach (var (p, share) in given) Destiny.Award(p, share, Destiny.Kill(p, VictimTier(victim)), pvp: true);
        if (crime) Pvp.Crime(killer, victim, killMessage ? Reputation.LostKill : Reputation.LostKnockdown, ticks);
        Log.Info("pvp", string.Create(CultureInfo.InvariantCulture,
            $"'{killer.Character.Name}' {(killMessage ? "killed" : "knocked down")} '{victim.Character.Name}': fame {fame / 10000.0:0.####}{(given.Count > 1 ? $" shared by {given.Count}" : "")}"));
        return given;
    }

    private static int VictimTier(Player victim) => Math.Max(1, victim.Inventory.Equipment.Items.Select(i => i.Template.Tier).DefaultIfEmpty(1).Max());

    /// <summary>
    /// Stage I, Pvp:KnockdownFameFactor (pvp-goals SPEC §2.4): <paramref name="fame"/> (a7m, before Rates) of a non-lethal knockdown shared as
    /// a kill's (<see cref="Share"/>), each sharer's UpdateFame only: no KilledPlayer for someone still alive [infer, Q4]. Returns the
    /// shares; the killer's (<paramref name="mine"/>) 0 means the caller's KillRewardedNoFame. Only the sharers <paramref name="credited"/>
    /// takes get theirs (the PvP goals' refusals: an alt or a farmed victim gives no fame); the others' parts are not handed out.
    /// </summary>
    public static List<(Player Player, long Fame)> KnockdownFame(Player killer, Player victim, long fame, List<(Player Player, double Damage)> attackers,
        Func<Player, bool> credited, out long mine)
    {
        var world = killer.World;
        var given = Share(killer, victim, Rates.ScaleFame(fame, world.Rates.Fame * world.Rates.CombatFame), attackers, out mine).Where(g => credited(g.Player)).ToList();
        foreach (var (p, share) in given) Destiny.Award(p, share, Destiny.Kill(p, VictimTier(victim)), pvp: true);
        return given;
    }

    /// <summary>
    /// <paramref name="fame"/> (a7m, Rates applied) split over the killer's party members within the sharing radius by their damage (see
    /// <see cref="Reward"/>): the shares above 0, the killer's (<paramref name="mine"/>, 0 included) first.
    /// </summary>
    private static List<(Player Player, long Fame)> Share(Player killer, Player victim, long fame, List<(Player Player, double Damage)> attackers, out long mine)
    {
        var world = killer.World;
        var radius = Settings(world).FameSharingRadius;
        var party = world.Parties.Of(killer.Character.Id);
        var at = victim.PositionAt(DateTime.UtcNow);
        var sharers = attackers.Where(a => a.Player != killer && party is not null && world.Parties.Of(a.Player.Character.Id) == party
            && Vector2.Distance(a.Player.PositionAt(DateTime.UtcNow), at) <= radius && a.Damage > 0).ToList();
        // The killer always shares: by its damage, or without any (an execute after its party's hits) as much as the others' mean,
        // its finishing blow's share (alone: 1, all of it) [infer].
        var own = attackers.Where(a => a.Player == killer).Sum(a => a.Damage);
        sharers.Insert(0, (killer, own > 0 ? own : sharers.Count > 0 ? sharers.Average(s => s.Damage) : 1));
        var total = sharers.Sum(s => s.Damage);
        mine = (long)Math.Floor(fame * sharers[0].Damage / total);
        var given = new List<(Player, long)>();
        foreach (var (p, damage) in sharers)
            if ((long)Math.Floor(fame * damage / total) is var share and > 0) given.Add((p, share));
        return given;
    }

    /// <summary>
    /// The item loss of <paramref name="rule"/> on <paramref name="victim"/> (see the class), the loot bag and the silver at
    /// <paramref name="at"/>; the victim's items saved at once.
    /// </summary>
    private static void Drop(Player victim, Player? killer, ItemLossRule rule, int attackers, ClusterPvp rules, long ticks, Vector2 at, int[] look)
    {
        var world = victim.World;
        var map = victim.Map!;
        if (map.Grid is null)
        {
            Log.Warn("pvp", $"'{victim.Character.Name}' died on {map.Cluster}, whose objects are not there yet: it keeps its items");
            return;
        }
        var settings = Settings(world);
        var random = map.Random;
        var multiplier = settings.DurabilityMultiplier(attackers);
        var dropped = new List<Item>();
        var destroyed = new List<string>();
        var worn = new List<Item>();
        void Part(Container container, DurabilityLossRule? wear, StackLossRule? stack)
        {
            for (var slot = 0; slot < container.Size; slot++)
            {
                if (container[slot] is not { } item) continue;
                if (wear is not null && Wear(item, wear, multiplier, random))
                {
                    worn.Add(item);
                    if (item.DurabilityRaw <= 0) world.Plugins.Broke(victim, item, "death"); // Items.OnBreak
                }
                if (stack is null) continue;
                container.Take(slot);
                var lost = RandomRound(item.Count * stack.For(item.Template.Kind), random);
                if (lost >= item.Count)
                {
                    destroyed.Add($"{item.Template.Name} x{item.Count}");
                    continue;
                }
                if (lost > 0) destroyed.Add($"{item.Template.Name} x{lost}");
                item.Count -= lost;
                dropped.Add(item);
            }
        }
        var inventory = victim.Inventory;
        Part(inventory.Equipment, rule.EquipmentWear, rule.EquipmentStack);
        Part(inventory.Bag, rule.InventoryWear, rule.InventoryStack);
        foreach (var item in worn.Where(i => !dropped.Contains(i))) Network.WorldSession.SendItemTo(victim, item); // what stays, worn
        if (rule.EquipmentDrops) victim.Session.Send(new InventoryStateEvent { ck0 = inventory.Equipment.ObjectIds(), ck2 = inventory.Equipment.Id });
        if (rule.InventoryDrops) victim.Session.Send(new InventoryStateEvent { ck0 = inventory.Bag.ObjectIds(), ck2 = inventory.Bag.Id });
        victim.SaveItems();
        victim.RecalculateStats(Math.Max(ticks, victim.Health.Stamp), keep: true);
        var spot = DeathSpot.At(map, at, victim.Facing);
        if (dropped.Count > 0)
        {
            var container = new Container(Guid.NewGuid(), dropped.Count);
            for (var i = 0; i < dropped.Count; i++) container.Put(i, dropped[i]);
            Guid[] looters = killer is null ? [] : world.Parties.Of(killer.Character.Id) is { } party ? [.. party.Members.Select(m => m.Id)] : [killer.Character.Id];
            var protection = killer is null ? 0 : rules.Rule == PvpRule.PvpForced ? settings.ForcedProtectionSeconds : settings.AllowedProtectionSeconds;
            var bag = new PlayerLootBag(world.ObjectIds.Next(), spot, victim, container, looters, ticks, ticks + SpellCasts.Seconds(protection), look);
            map.AddObject(bag);
            map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(settings.VanishSeconds), () => Combat.RemoveBag(bag, "its time is up"));
        }
        if (world.Config.PvpDeathSilverDrop && killer is not null && (int)Math.Floor(victim.Character.Silver / 10000.0 * settings.SilverDropFactor) is var silver and > 0
            && Wallet.TrySpend(victim, silver * 10000L, "dropped at death"))
        {
            var pile = new SilverPile(world.ObjectIds.Next(), spot, at, victim.Id, victim.Character.Name, silver, 1, killer.Character.Id, ticks,
                world.Parties.Of(killer.Character.Id)?.Id ?? 0, SilverPile.FromPlayer);
            map.AddObject(pile);
            map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(settings.VanishSeconds), () => Combat.RemoveSilver(pile, "it decayed"));
        }
        world.Saves.SaveNow(victim.Character); // the drop is the source of truth (SPEC §4.4)
        Log.Info("pvp", $"'{victim.Character.Name}' drops {dropped.Count} item(s) (durability x{multiplier:0.##}){(destroyed.Count > 0 ? $", destroyed {string.Join(", ", destroyed)}" : "")}");
    }

    /// <summary>
    /// <paramref name="item"/> loses <paramref name="rule"/>'s share of its maximum durability (uniform min..max × <paramref name="multiplier"/>;
    /// at least minimumremainingfactor of the maximum left, an item below that keeps what it has). True when it changed. The PvP
    /// knockdown's wear too (<see cref="Pvp.KnockedDown"/>).
    /// </summary>
    public static bool Wear(Item item, DurabilityLossRule rule, double multiplier, Random random)
    {
        var next = Worn(item.Template, item.DurabilityRaw, rule, multiplier, random);
        if (next == item.DurabilityRaw) return false;
        item.DurabilityRaw = next;
        return true;
    }

    /// <summary>The durability (raw) an item of <paramref name="type"/> at <paramref name="raw"/> keeps after <paramref name="rule"/>'s wear (see <see cref="Wear"/>; also a saved item's, stageL roads).</summary>
    public static long Worn(ItemTemplate type, long raw, DurabilityLossRule rule, double multiplier, Random random)
    {
        if (type.MaxDurability <= 0) return raw;
        var (min, max, rest) = rule.For(type.Kind);
        var factor = (max > min ? min + (max - min) * random.NextDouble() : min) * multiplier;
        if (factor <= 0) return raw;
        var full = type.FullDurabilityRaw;
        return Math.Max(Math.Min(raw, (long)Math.Round(full * rest)), raw - (long)Math.Round(full * factor));
    }

    /// <summary><paramref name="x"/> rounded down or up at random by its fraction (0.33 → 1 with a chance of 0.33).</summary>
    public static int RandomRound(double x, Random random)
    {
        var whole = Math.Floor(x);
        return (int)whole + (random.NextDouble() < x - whole ? 1 : 0);
    }

    // ---- after the death ----

    /// <summary>The world tick of a dead player: the Leave for the others after playervisibilityafterdeath, the auto-respawn.</summary>
    public static void Update(Player p, KnockedDownState down, TimeSpan now)
    {
        var settings = Settings(p.World);
        if (!down.Hidden && now >= down.At + TimeSpan.FromSeconds(settings.VisibilityAfterDeathSeconds))
        {
            down.Hidden = true;
            foreach (var o in p.Observers) o.Session.Send(new LeaveEvent { ObjectId = p.Id });
        }
        if (now >= down.At + TimeSpan.FromSeconds(settings.AutoRespawnSeconds) && !down.Respawning)
        {
            down.Respawning = true;
            p.World.Post(() => Respawn(p, "auto-respawn")); // not inside the tick's walk over the map's players
        }
    }

    /// <summary>
    /// The cluster <paramref name="p"/> respawns in: Pvp:RespawnCluster, else its last city, else <see cref="DefaultRespawnCluster"/>
    /// (also once a respawn there failed to resolve).
    /// </summary>
    public static string RespawnCluster(Player p) =>
        p.Combat.Down is { RespawnFailed: true } ? DefaultRespawnCluster : RespawnCluster(p.World, p.Character);

    /// <summary>The respawn cluster of character <paramref name="c"/>: Pvp:RespawnCluster, else its last city, else <see cref="DefaultRespawnCluster"/>.</summary>
    public static string RespawnCluster(World world, Character c) =>
        world.Config.PvpRespawnCluster is { Length: > 0 } configured ? configured : c.LastCity is { Length: > 0 } city ? city : DefaultRespawnCluster;

    /// <summary>
    /// GetRespawnInfos (op 204): one bool per respawn option, only the first set [infer: which a0.b index is which is unknown (SPEC §5 Q7);
    /// every option leads to <see cref="RespawnCluster"/>].
    /// </summary>
    public static bool[] RespawnInfos() => [true, .. new bool[RespawnOptions - 1]];

    /// <summary>
    /// The respawn of dead <paramref name="p"/> (Respawn op 101, the auto-respawn): the cluster change to <see cref="RespawnCluster"/>'s
    /// respawn point (<see cref="Transfers.DefaultSpawn"/>), resolved first when it is not yet (the move follows); without cluster data
    /// at (0, 0). False when it is not dead.
    /// </summary>
    public static bool Respawn(Player p, string why)
    {
        if (p.Combat.Down is not { Kind: KnockdownKind.Dead } || p.Map is null || p.Session.Player != p) return false;
        var world = p.World;
        // StageG: a death inside a random dungeon respawns in front of its entrance in the open world (Dungeons:RespawnAtEntrance).
        if (world.Dungeons.RespawnOf(p.Map.Cluster) is { } outside)
        {
            Log.Info("pvp", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' respawns at the dungeon entrance in {outside.Cluster} ({outside.Position.X:0.##}, {outside.Position.Y:0.##}) ({why})"));
            p.Session.Teleport(outside, RespawnVia);
            return true;
        }
        // StageH: a death inside an expedition respawns at its active checkpoint (else its start) in the same instance (SPEC §6).
        if (world.Expeditions.RespawnOf(p) is { } inside)
        {
            Log.Info("pvp", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' respawns in {inside.To.Cluster} at ({inside.To.Position.X:0.##}, {inside.To.Position.Y:0.##}) ({why})"));
            p.Session.Teleport(inside.To, RespawnVia, inside.World);
            return true;
        }
        world.Expeditions.DiedOut(p); // Expeditions:CheckpointRespawn off: out of the instance for good (263 kind 3), the city respawn follows
        world.Corrupted.DiedOut(p); // stageL corrupted: out of the run (its owner's early leave), the city respawn follows
        world.Hellgates.DiedOut(p); // stageL hellgates: no member any more (the official preset's lethal modes)
        var cluster = world.Mists.RespawnOf(p) ?? RespawnCluster(p); // stageL mists: a death in a zone respawns in Mists:RespawnCluster (Brecilien)
        void Go(Vector2 spot)
        {
            if (p.Combat.Down is not { Kind: KnockdownKind.Dead } || p.Map is null || p.Session.Player != p) return;
            Log.Info("pvp", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' respawns in {cluster} at ({spot.X:0.##}, {spot.Y:0.##}) ({why})"));
            p.Session.Teleport(new Arrival(cluster, spot, 0), RespawnVia);
        }
        if (world.CachedCluster(cluster) is { } cached) Go(Transfers.DefaultSpawn(cached));
        else if (world.ResolveCluster(cluster) is { } load)
            load.ContinueWith(t => world.Post(() =>
            {
                if (t.IsCompletedSuccessfully) Go(Transfers.DefaultSpawn(t.Result));
                else
                {
                    // The auto-respawn tries again in a tick (Update) with the default cluster; when that fails too, at (0, 0) there.
                    Log.Warn("pvp", $"respawn cluster {cluster} of '{p.Character.Name}' not resolved: {t.Exception?.InnerException?.Message}");
                    if (p.Combat.Down is not { Kind: KnockdownKind.Dead } still) return;
                    if (still.RespawnFailed) Go(Vector2.Zero);
                    else (still.Respawning, still.RespawnFailed) = (false, true);
                }
            }), TaskScheduler.Default);
        else Go(Vector2.Zero);
        return true;
    }

    /// <summary>
    /// A dead player's connection ended (WorldSession.OnDisconnected, after the position was stored): its character is put at the
    /// respawn point, so the next Join is the respawn's [infer]. A respawn cluster not resolved yet (Kill prefetched it) is read here
    /// (rare: it holds up the world for that read); one that cannot be read falls back to <see cref="DefaultRespawnCluster"/>.
    /// </summary>
    public static void Departed(World world, Player p)
    {
        var c = p.Character;
        if (p.Map is { } map && world.Dungeons.RespawnOf(map.Cluster) is { } outside) // stageG: dead inside a dungeon, in front of its entrance
        {
            (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (outside.Cluster, outside.Position.X, outside.Position.Y, outside.Direction);
            Log.Info("pvp", $"'{c.Name}' left dead in a dungeon: it joins at its entrance in {outside.Cluster} next time");
            return;
        }
        if (world.Expeditions.RespawnOf(p) is { } inside) // stageH: dead inside an expedition, at its checkpoint while the instance lives
        {
            (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (inside.To.Cluster, inside.To.Position.X, inside.To.Position.Y, inside.To.Direction);
            Log.Info("pvp", $"'{c.Name}' left dead in an expedition: it joins at its checkpoint next time");
            return;
        }
        world.Expeditions.DiedOut(p); // CheckpointRespawn off: no member any more, it joins at the city respawn
        world.Corrupted.DiedOut(p); // stageL corrupted: the run left (a new entry at once), it joins at the city respawn
        world.Hellgates.DiedOut(p); // stageL hellgates: no member any more, it joins at the city respawn
        if (RespawnPoint(world, world.Mists.RespawnOf(p) ?? RespawnCluster(p), c.Name) is not { } point) // stageL mists: Mists:RespawnCluster, out of the mists
        {
            Log.Warn("pvp", $"'{c.Name}' left dead; no respawn cluster: it joins where it died");
            return;
        }
        var (cluster, at) = point;
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (cluster, at.X, at.Y, 0);
        Log.Info("pvp", $"'{c.Name}' left dead: it joins in {cluster} next time");
    }

    /// <summary>
    /// The respawn point of <paramref name="cluster"/> (<see cref="Transfers.DefaultSpawn"/>) for an offline character's next Join; a cluster not
    /// resolved yet is read here (rare: it holds up the world for that read), one that cannot be read falls back to
    /// <see cref="DefaultRespawnCluster"/>; without cluster data (0, 0). Null when neither can be read.
    /// </summary>
    public static (string Cluster, Vector2 At)? RespawnPoint(World world, string cluster, string who)
    {
        if (world.Clusters is not { } store) return (cluster, Vector2.Zero);
        ClusterTemplate? Read(string id)
        {
            try { return world.CachedCluster(id) ?? store.Get(id); }
            catch (Exception e) { Log.Warn("pvp", $"respawn cluster {id} of '{who}' not read: {e.Message}"); return null; }
        }
        if ((Read(cluster) ?? (cluster == DefaultRespawnCluster ? null : Read(cluster = DefaultRespawnCluster))) is not { } t) return null;
        return (cluster, Transfers.DefaultSpawn(t));
    }

    /// <summary>
    /// The Join of a respawn's cluster change (WorldSession, after JoinFinished): Respawn (88) {0 me, 1 now, 2 the spot, 3 facing, 4/5
    /// full health, 7 regeneration, 8 now, 9/10 full energy, 12 regeneration, 13 now} to the player [infer: the SPEC's "Respawn event at the
    /// destination"; without the get-up animation keys 14/15].
    /// </summary>
    public static void Arrived(Player p)
    {
        var now = Math.Max(p.World.GameNow, Math.Max(p.Health.Stamp, p.Energy.Stamp));
        p.Session.Send(new RespawnEvent
        {
            ddn = p.Id, dvt = now, dvu = p.PositionAt(DateTime.UtcNow), dvv = p.Facing,
            dvw = p.Health.Value, dvx = p.Health.Max, dvz = p.Health.RegenPerSecond, dv0 = now,
            dv1 = p.Energy.Value, dv2 = p.Energy.Max, dv4 = p.Energy.RegenPerSecond, dv5 = now,
        });
    }

    // ---- the die button ----

    /// <summary>
    /// Suicide (op 102, the die button): a player knocked down (not in a duel) in a playersarelethal cluster, at least
    /// <see cref="DieButtonSeconds"/> after the knockdown, dies (<see cref="Cause.KnockedOutToDead"/>; a player who knocked it down is
    /// the killer). Returns why not, or null.
    /// </summary>
    public static string? Suicide(Player p)
    {
        if (p.Combat.Down is not { Kind: KnockdownKind.Pve or KnockdownKind.Pvp } down) return "not knocked down";
        if (!Pvp.Lethal(p.World, p.Map)) return Revive.GiveUp(p) ? null : "not a lethal cluster"; // also with Pvp:LethalDeath false (stageN: a bleed-out ends)
        if (p.World.Now < down.At + TimeSpan.FromSeconds(DieButtonSeconds)) return "before the die button";
        Die(p, down);
        return null;
    }

    /// <summary>
    /// The connection of <paramref name="p"/> ends (a logout, a disconnect; WorldSession.OnDisconnected, before it leaves) while it is
    /// knocked down (not in a duel) in a playersarelethal cluster: it dies as by the die button, so leaving does not save its items
    /// from the execute [infer: not in the client; the official server's rule is unknown]. Then <see cref="Departed"/>.
    /// </summary>
    public static void Abandoned(Player p)
    {
        if (p.Combat.Down is not { Kind: KnockdownKind.Pve or KnockdownKind.Pvp } down || !Pvp.Lethal(p.World, p.Map)) return;
        Log.Info("pvp", $"'{p.Character.Name}' left knocked down in a lethal cluster: it dies");
        Die(p, down);
    }

    /// <summary>A bleed-out that ran out in a lethal zone (stageN revive): the death as by the die button.</summary>
    internal static void BledOut(Player p)
    {
        if (p.Combat.Down is { Kind: KnockdownKind.Pve or KnockdownKind.Pvp } down) Die(p, down);
    }

    /// <summary>The death of knocked-down <paramref name="p"/> by itself (KnockedOutToDead; a player who knocked it down is the killer).</summary>
    private static void Die(Player p, KnockedDownState down)
    {
        var killer = p.Map!.FindPlayer(down.Killer) is { } k && k != p ? k : null; // a bot's knockdown: Kill gives it nothing
        var now = Math.Max(p.World.GameNow, p.Health.Stamp);
        LethalUpdate(p, down.Killer, -1, now);
        Kill(p, killer, down.Killer, down.KillerText, Cause.KnockedOutToDead, crime: killer is not null && Pvp.IsCrime(killer, p, now), now);
    }

    /// <summary>The lethal HealthUpdate (no key 3) of a knocked-down player who dies now, to it and its observers.</summary>
    private static void LethalUpdate(Player p, long causer, short spell, long ticks)
    {
        var left = p.Health.At(ticks);
        p.Health.Set(0, ticks);
        p.SendAround(Combat.HealthUpdate(p.Id, ticks, -left, 0, EffectType.Physical, HitOrigin.Direct, causer, spell));
    }

    // ---- the execute ----

    /// <summary>
    /// Why <paramref name="p"/> may not execute <paramref name="target"/> now (null = it may; cwt.u 0x1809E68B0, SPEC §4.2 V8): standing
    /// itself, the target in a plain knockdown (not dead, not a duel's), not its duel opponent, Hostile to it, and either a lethal
    /// cluster (ctp.af_21) or enemy factions / two Caerleon players outside one party (cpx.af_3), within castrange (2 m).
    /// </summary>
    public static string? ExecuteRefusal(Player p, Player target, long now)
    {
        if (p.Combat.Down is not null) return "knocked down";
        if (!p.World.Config.PvpLethalDeath) return "no execute on this server (Pvp:LethalDeath false)";
        if (p == target || p.Map is null || target.Map != p.Map) return "no target";
        if (target.Combat.Down is not { Kind: KnockdownKind.Pve or KnockdownKind.Pvp }) return "the target is not knocked down";
        if (p.World.Duels.OpponentOf(p) == target) return "the duel opponent";
        if (p.World.Arenas.Find(p.Map.Cluster) is not null) return "no execute in the arena"; // stage N arena: a defeat is a knockdown there
        var relation = Pvp.Of(p, target);
        if (relation != Relation.Hostile) return "not hostile";
        var lethal = Pvp.RulesOf(p.World, p.Map).PlayersAreLethal;
        var factions = Pvp.IsFaction(p.Flag) && Pvp.IsFaction(target.Flag) && p.Flag != target.Flag
            || p.Flag == Pvp.Caerleon && target.Flag == Pvp.Caerleon && !Pvp.SameParty(p, target);
        if (!lethal && !factions) return "no execute in a non-lethal cluster";
        var range = p.World.Spells?.Template(ExecuteSpell)?.CastRange ?? DefaultExecuteRange;
        if (Pvp.EdgeDistance(p, p.PositionAt(DateTime.UtcNow), target) > range + SpellCasts.RangeTolerance) return "out of range";
        return null;
    }

    /// <summary>
    /// CastStart on slot 26 (PLAYER_COUPDEGRACE, the client's DoActionAttack on a knocked-down player): the checks, then a server cast
    /// of castingtime (1.5 s): CastStart (14) {0 me, 1 start, 4 end, 5 spell, 6 -1, 7 target, 8 counter, 9 26} to the observers (the
    /// client casts its own). Damage taken, a knockdown, its own CastCancel or another cast or attack end it (<see cref="Interrupt"/>,
    /// from CombatHandler). <paramref name="spellId"/> must be PLAYER_COUPDEGRACE's. Returns why it was refused, or null.
    /// </summary>
    public static string? Execute(Player p, int spellId, long targetId, byte counter, long start)
    {
        var spell = p.World.Spells?.Template(ExecuteSpell);
        if (spellId != (spell?.Id ?? DefaultExecuteId)) return $"spell {spellId} is not {ExecuteSpell}";
        if (p.Map?.Players.GetValueOrDefault(targetId) is not { } target || !p.Observers.Contains(target)) return "no player in view";
        if (ExecuteRefusal(p, target, start) is { } why) return why;
        if (p.PvpState.Execute is not null || p.PvpState.FlagCast is not null || p.Combat.Cast is not null) return "a cast is under way";
        Knockdown.Acted(p, "executes");
        Mounts.BeforeAction(p, "executes"); // before the cast: it ends a server cast under way
        var cast = new ExecuteCast(target, spell?.Id ?? DefaultExecuteId, counter, start, start + SpellCasts.Seconds(spell?.CastingTime ?? DefaultExecuteSeconds));
        p.SendToObservers(new CastStartEvent
        {
            ddn = p.Id, bwb = cast.Start, bwe = cast.End, bwf = cast.SpellId, bwg = -1, bwh = target.Id, Extra = new() { [8] = counter, [9] = ExecuteSlot },
        });
        p.PvpState.Execute = cast;
        Log.Info("pvp", $"'{p.Character.Name}' executes '{target.Character.Name}'");
        return null;
    }

    /// <summary>
    /// The world tick: the execute that is due ends. Checked again (<see cref="ExecuteRefusal"/>: the target got up, died or went out of
    /// range meanwhile → interrupted); then CastFinished (18) and CastSpell (19) to the executor and its observers, the target's lethal
    /// HealthUpdate and its death: KnockedOutToDead in a lethal cluster, no item loss for a faction execute elsewhere [infer].
    /// </summary>
    public static void UpdateExecute(Player p)
    {
        if (p.PvpState.Execute is not { } cast || p.World.GameNow < cast.End) return;
        p.PvpState.Execute = null;
        var target = cast.Target;
        var now = p.World.GameNow;
        if (ExecuteRefusal(p, target, now) is { } why)
        {
            Cancel(p, cast, interrupted: true);
            Log.Info("pvp", $"'{p.Character.Name}''s execute fails: {why}");
            return;
        }
        var point = target.PositionAt(DateTime.UtcNow);
        p.SendAround(new CastFinishedEvent
        {
            ddn = p.Id, bux = target.Id, buy = cast.SpellId, bu1 = cast.End, bu2 = SpellCasts.CooldownFactorOf(p),
            Extra = new() { [3] = ExecuteSlot, [4] = cast.Counter },
        });
        p.SendAround(new CastSpellEvent
        {
            ddn = p.Id, bvw = target.Id, bvx = point, bvy = cast.SpellId, bvz = cast.End, bv0 = now, Extra = new() { [6] = cast.Counter, [7] = (short)0 },
        });
        var lethal = Pvp.RulesOf(p.World, p.Map).PlayersAreLethal;
        var crime = Pvp.IsCrime(p, target, now);
        var ticks = Math.Max(now, target.Health.Stamp);
        LethalUpdate(target, p.Id, cast.SpellId, ticks);
        Kill(target, p, p.Id, p.Character.Name, lethal ? Cause.KnockedOutToDead : Cause.NoLoss, crime, ticks);
    }

    /// <summary>
    /// The execute under way ends without its kill: <paramref name="interrupted"/> by the server (damage, a knockdown) tells the executor as
    /// well, its own CastCancel only the observers. A no-op without one (called with every flag cast interrupt, <see cref="Flagging.Interrupt"/>).
    /// </summary>
    public static void Interrupt(Player p, string why, bool interrupted = true)
    {
        Revive.Interrupt(p, why, interrupted); // stageN: the revive of slot 27 is the same kind of server cast
        if (p.PvpState.Execute is not { } cast) return;
        p.PvpState.Execute = null;
        Cancel(p, cast, interrupted);
        Log.Info("pvp", $"'{p.Character.Name}''s execute is cancelled: {why}");
    }

    private static void Cancel(Player p, ExecuteCast cast, bool interrupted)
    {
        if (p.Map is null) return;
        p.SendToObservers(new CastCancelEvent { ddn = p.Id, buo = interrupted, bup = cast.Counter, Extra = new() { [9] = (byte)0 } });
        if (interrupted) p.Session.Send(new CastCancelEvent { ddn = p.Id, buo = true, bup = cast.Counter, buq = SpellCasts.CooldownFactorOf(p) });
    }
}
