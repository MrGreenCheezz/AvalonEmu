using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Getting a knocked-down ally back up (stageN revive, analysis/work/stageN/revive/SPEC.md). Two client flows:
/// <list type="number">
/// <item><b>Bleed-out and the party revive</b> (Pve:PartyRevive, the pve preset's; officially the zones with enterbleedoutstateondeath):
/// a non-lethal knockdown (a mob's, a PvP one outside a duel) of a player with a party or guild mate on its map sends BleedOut (693)
/// {0 me, 1 ts, 2 causer, 3 its text, 4 me, 5 my name, 6 ts, 7 points ×10000, 8 change per second ×10000, 9 false} instead of KnockedDown:
/// the client's state 17 (cpx BleedOut), the bleed-out bar, and the ally's click on it (LocalInputHandler.DoActionAttack → czb.u: a
/// player, not Hostile, in state 17) casts the cluster's playerrevivespell (PLAYER_BLEEDOUT_REVIVE: 6 s, castrange 1.5,
/// damagecancelscasting) on CharacterSpellSlot 27. <see cref="Start"/> runs it as a server cast like the execute: CastStart {9 27} to the
/// observers, CastCancel on damage, a knockdown, its own CastCancel, another action or a walk; at its end (<see cref="Update"/>)
/// CastFinished and CastSpell, and the ally gets up (Respawn 88, <see cref="Knockdown.GetUp"/>) with revivehitpointspercentage (30 %)
/// of its health and onrevivebuff (BLEEDOUT_REVIVE_DEBUFF). The points (totalpoints 120) run down by 1/s with a standing ally within
/// closerange (43 m), 4/s without one, 30/s with every ally down (a new BleedOut when the rate changes); at 0 a lethal zone's
/// (Pvp:LethalDeath) player dies, elsewhere the bleed-out ends in the plain knockdown (KnockedDown 165) and its get-up timer. The give-up
/// (Suicide op 102) outside a lethal zone does the same. The get-up timers of the knockdown run on all the while (18 s after the fight).</item>
/// <item><b>Holy resurrection</b> (RESURRECTION, target knockeddownfriendplayer, &lt;resurrect restoration="0.3" time="10"/&gt;):
/// <see cref="Offer"/> sends ResurrectionOffer (218) {0 target, 1 caster, 2 its name, 3 restoration, 4 deadline}; the target answers
/// with ResurrectionReply (op 207) {0 ts, 1 accept} (<see cref="Reply"/>): it gets up with that share of its health; a refusal sends
/// the caster ResurrectionReply (219) {0 caster, 1 target, 2 1 = rejected} (2 = not possible, 3 = already in progress: a second offer).</item>
/// </list>
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Revive
{
    /// <summary>CharacterSpellSlot ReviveFromBleedOut.</summary>
    public const byte Slot = 27;

    /// <summary>ResurrectionReply (219) key 2: the client's texts (@RESURRECT_OFFER_REJECTED, _NOT_POSSIBLE, _ALREADY_IN_PROGRESS).</summary>
    public const int Rejected = 1, NotPossible = 2, InProgress = 3;

    private const double DefaultCastSeconds = 6, DefaultCastRange = 1.5;

    /// <summary>A revive under way: its target, spell id, the request's cast counter, its start and end GameTimeStamps.</summary>
    public sealed record ReviveCast(Player Target, short SpellId, byte Counter, long Start, long End);

    private static BleedOutSettings Settings(World world) => world.GameData?.BleedOut ?? BleedOutSettings.Default;

    /// <summary>Whether <paramref name="a"/> may revive <paramref name="b"/>: party or guild mates, not Hostile to each other.</summary>
    public static bool Allies(Player a, Player b) =>
        a != b && (!a.IsBot || a.IsCompanion) && (!b.IsBot || b.IsCompanion) && (Pvp.SameParty(a, b) || a.World.Guilds.Same(a.Character.Id, b.Character.Id)) && Pvp.Of(a, b) != Relation.Hostile;

    private static IEnumerable<Player> AlliesOn(Player p) =>
        p.Map is { } map ? map.Fighters.Where(q => q != p && Allies(q, p)) : []; // stageN: the party's companions too

    // ---- the bleed-out ----

    /// <summary>
    /// From <see cref="Knockdown.Start"/> (instead of KnockedDown): with Pve:PartyRevive, a <see cref="KnockdownKind.Pve"/> or
    /// <see cref="KnockdownKind.Pvp"/> knockdown of a player with an ally on its map bleeds out (BleedOut to it and its observers).
    /// Returns false for the plain knockdown.
    /// </summary>
    internal static bool Begin(Player p, KnockedDownState down, long ticks)
    {
        var world = p.World;
        if (!world.Config.PvePartyRevive || p.IsBot && !p.IsCompanion || down.Kind is not (KnockdownKind.Pve or KnockdownKind.Pvp) || !AlliesOn(p).Any()) return false;
        down.BleedOutPoints = Settings(world).TotalPoints;
        down.BleedOutStamp = world.Now;
        down.BleedOutRate = -Rate(p);
        Send(p, down, ticks);
        Log.Info("combat", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' bleeds out ({down.BleedOutPoints:0} points, {down.BleedOutRate:0.#}/s)"));
        return true;
    }

    /// <summary>The points lost per second now: 1 with a standing ally within closerange, 4 without one, 30 with every ally down.</summary>
    private static double Rate(Player p)
    {
        var s = Settings(p.World);
        var allies = AlliesOn(p).ToList();
        if (allies.Count == 0) return s.LossNoAllyClose;
        var standing = allies.Where(q => q.Combat.Down is null).ToList();
        if (standing.Count == 0) return s.LossAllDown;
        var at = p.PositionAt(DateTime.UtcNow);
        return standing.Any(q => System.Numerics.Vector2.Distance(q.PositionAt(DateTime.UtcNow), at) <= s.CloseRange) ? s.LossPerSecond : s.LossNoAllyClose;
    }

    private static double PointsAt(KnockedDownState down, TimeSpan now) =>
        Math.Max(0, (down.BleedOutPoints ?? 0) + down.BleedOutRate * (now - down.BleedOutStamp).TotalSeconds);

    private static void Send(Player p, KnockedDownState down, long ticks)
    {
        var e = new BleedOutEvent
        {
            ddn = p.Id, btq = ticks, btr = down.Killer, bts = down.KillerText, btt = p.Id, btu = p.Character.Name,
            btv = ticks, btw = (long)Math.Round((down.BleedOutPoints ?? 0) * 10000), btx = (long)Math.Round(down.BleedOutRate * 10000), bty = false,
        };
        p.Session.Send(e);
        p.SendToObservers(e);
    }

    /// <summary>
    /// The world tick of a bleeding-out player (<see cref="Knockdown.Update"/>): the loss rate follows its allies (a new BleedOut when it
    /// changes), an offer that ran out lapses; at 0 points <see cref="Expire"/>. Returns true when the knockdown is over (a death).
    /// </summary>
    internal static bool Update(Player p, KnockedDownState down, TimeSpan now)
    {
        if (down.Offer is { } offer && now >= offer.Until) EndOffer(p, down, "it ran out");
        if (down.BleedOutPoints is null) return false;
        var points = PointsAt(down, now);
        if (points <= 0) return Expire(p, down, "bled out");
        var rate = -Rate(p);
        if (rate != down.BleedOutRate)
        {
            down.BleedOutPoints = points;
            down.BleedOutStamp = now;
            down.BleedOutRate = rate;
            Send(p, down, Math.Max(p.World.GameNow, p.Health.Stamp));
        }
        return false;
    }

    /// <summary>
    /// The bleed-out ends without a revive: in a lethal zone (Pvp:LethalDeath and playersarelethal) the player dies (KnockedOutToDead),
    /// elsewhere it lies on in the plain knockdown (KnockedDown 165: the client's state 15, no more revive). Returns whether it died.
    /// </summary>
    private static bool Expire(Player p, KnockedDownState down, string why)
    {
        down.BleedOutPoints = null;
        Log.Info("combat", $"'{p.Character.Name}' {why}");
        if (Pvp.Lethal(p.World, p.Map) && !p.IsBot) // stageN: a companion never dies, it lies on until its get-up
        {
            Death.BledOut(p);
            return true;
        }
        var e = new KnockedDownEvent { ddn = p.Id, cmr = Math.Max(p.World.GameNow, p.Health.Stamp), cms = down.Killer, cmt = down.KillerText, cmu = p.Id, cmv = p.Character.Name };
        p.Session.Send(e);
        p.SendToObservers(e);
        return false;
    }

    /// <summary>The give-up (Suicide op 102) of a bleeding-out player outside a lethal zone: the plain knockdown. Returns false when not bleeding out.</summary>
    public static bool GiveUp(Player p)
    {
        if (p.Combat.Down is not { BleedOutPoints: not null } down || Pvp.Lethal(p.World, p.Map)) return false;
        Expire(p, down, "gives up the bleed-out");
        return true;
    }

    // ---- the revive cast (slot 27) ----

    /// <summary>Why <paramref name="p"/> may not revive <paramref name="target"/> now (null = it may).</summary>
    public static string? Refusal(Player p, Player target)
    {
        if (p.Combat.Down is not null) return "knocked down";
        if (p == target || p.Map is null || target.Map != p.Map) return "no target";
        if (target.Combat.Down is not { BleedOutPoints: not null }) return "the target is not bleeding out";
        if (!Allies(p, target)) return "not a party or guild mate";
        var range = p.World.Spells?.Template(Settings(p.World).ReviveSpell)?.CastRange ?? DefaultCastRange;
        if (Pvp.EdgeDistance(p, p.PositionAt(DateTime.UtcNow), target) > range + SpellCasts.RangeTolerance) return "out of range";
        return null;
    }

    /// <summary>
    /// CastStart on slot 27 (the client's revive of a bleeding-out ally): <paramref name="spellId"/> must be the playerrevivespell's;
    /// the checks, then a server cast of its castingtime: CastStart (14) {0 me, 1 start, 4 end, 5 spell, 6 -1, 7 target, 8 counter, 9 27}
    /// to the observers (the client casts its own). Returns why it was refused, or null.
    /// </summary>
    public static string? Start(Player p, int spellId, long targetId, byte counter, long start)
    {
        var world = p.World;
        var name = Settings(world).ReviveSpell;
        var spell = world.Spells?.Template(name);
        if (spell is not null && spellId != spell.Id) return $"spell {spellId} is not {name}";
        if (p.Map?.FindPlayer(targetId) is not { } target || target.IsBot && !target.IsCompanion || !p.Observers.Contains(target) && !(p.IsCompanion && target.Map == p.Map))
            return "no player in view";
        if (Refusal(p, target) is { } why) return why;
        if (p.PvpState.Revive is not null || p.PvpState.Execute is not null || p.PvpState.FlagCast is not null || p.Combat.Cast is not null) return "a cast is under way";
        Knockdown.Acted(p, "revives");
        Mounts.BeforeAction(p, "revives");
        var id = spell?.Id ?? (short)spellId;
        var cast = new ReviveCast(target, id, counter, start, start + SpellCasts.Seconds(spell?.CastingTime ?? DefaultCastSeconds));
        p.SendToObservers(new CastStartEvent
        {
            ddn = p.Id, bwb = cast.Start, bwe = cast.End, bwf = id, bwg = -1, bwh = target.Id, Extra = new() { [8] = counter, [9] = Slot },
        });
        p.PvpState.Revive = cast;
        Log.Info("combat", $"'{p.Character.Name}' revives '{target.Character.Name}'");
        return null;
    }

    /// <summary>
    /// The world tick: the revive that is due ends. Checked again (the target got up, died, stopped bleeding or went out of range →
    /// interrupted); then CastFinished (18) and CastSpell (19) to the reviver and its observers, and the target gets up (<see cref="Raise"/>).
    /// </summary>
    public static void Update(Player p)
    {
        if (p.PvpState.Revive is not { } cast || p.World.GameNow < cast.End) return;
        p.PvpState.Revive = null;
        var target = cast.Target;
        var now = p.World.GameNow;
        if (Refusal(p, target) is { } why)
        {
            Cancel(p, cast, interrupted: true);
            Log.Info("combat", $"'{p.Character.Name}''s revive fails: {why}");
            return;
        }
        var point = target.PositionAt(DateTime.UtcNow);
        p.SendAround(new CastFinishedEvent
        {
            ddn = p.Id, bux = target.Id, buy = cast.SpellId, bu1 = cast.End, bu2 = SpellCasts.CooldownFactorOf(p), Extra = new() { [3] = Slot, [4] = cast.Counter },
        });
        p.SendAround(new CastSpellEvent
        {
            ddn = p.Id, bvw = target.Id, bvx = point, bvy = cast.SpellId, bvz = cast.End, bv0 = now, Extra = new() { [6] = cast.Counter, [7] = (short)0 },
        });
        Raise(target, Settings(p.World).ReviveHealthPercent / 100, Settings(p.World).ReviveBuff, p);
    }

    /// <summary>
    /// The revive under way ends without its effect: <paramref name="interrupted"/> by the server (damage, a knockdown) tells the reviver
    /// as well, its own CastCancel or another action only the observers. A no-op without one (from <see cref="Death.Interrupt"/>).
    /// </summary>
    public static void Interrupt(Player p, string why, bool interrupted = true)
    {
        if (p.PvpState.Revive is not { } cast) return;
        p.PvpState.Revive = null;
        Cancel(p, cast, interrupted);
        Log.Info("combat", $"'{p.Character.Name}''s revive is cancelled: {why}");
    }

    private static void Cancel(Player p, ReviveCast cast, bool interrupted)
    {
        if (p.Map is null) return;
        p.SendToObservers(new CastCancelEvent { ddn = p.Id, buo = interrupted, bup = cast.Counter, Extra = new() { [9] = (byte)0 } });
        if (interrupted) p.Session.Send(new CastCancelEvent { ddn = p.Id, buo = true, bup = cast.Counter, buq = SpellCasts.CooldownFactorOf(p) });
    }

    /// <summary>
    /// <paramref name="p"/> gets up from its knockdown with <paramref name="share"/> of its maximum health (<see cref="Knockdown.GetUp"/>),
    /// then <paramref name="buff"/> runs on it (BLEEDOUT_REVIVE_DEBUFF; none without spell data).
    /// </summary>
    private static void Raise(Player p, double share, string? buff, Player by)
    {
        if (p.Combat.Down is not { Kind: KnockdownKind.Pve or KnockdownKind.Pvp }) return;
        Knockdown.GetUp(p, share);
        Log.Info("combat", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' is revived by '{by.Character.Name}' at {share:P0} health"));
        if (buff is null || p.World.Spells?.Template(buff) is not { } spell || p.Map is not { } map) return;
        var now = Math.Max(p.World.GameNow, p.Health.Stamp);
        SpellCasts.Execute(new SpellCasts.SpellContext(p, map, spell, 0, 100, null, p.PositionAt(DateTime.UtcNow), now), spell);
    }

    // ---- the holy resurrection (ResurrectionOffer / ResurrectionReply) ----

    /// <summary>
    /// The &lt;resurrect&gt; effect of <paramref name="caster"/>'s spell on knocked-down <paramref name="target"/>: ResurrectionOffer to it,
    /// valid <paramref name="seconds"/>. A second offer while one is open: ResurrectionReply {2 3} to the caster.
    /// </summary>
    public static void Offer(Player caster, Player target, double restoration, double seconds, string? waitingSpell, long ticks)
    {
        if (target.Combat.Down is not { Kind: KnockdownKind.Pve or KnockdownKind.Pvp } down || target.IsBot)
        {
            caster.Session.Send(new ResurrectionReplyEvent { ddn = caster.Id, dwc = target.Id, dwd = NotPossible });
            return;
        }
        if (down.Offer is not null)
        {
            caster.Session.Send(new ResurrectionReplyEvent { ddn = caster.Id, dwc = target.Id, dwd = InProgress });
            return;
        }
        var world = target.World;
        down.Offer = (caster, restoration, world.Now + TimeSpan.FromSeconds(seconds), waitingSpell);
        target.Session.Send(new ResurrectionOfferEvent
        {
            ddn = target.Id, dv8 = caster.Id, dv9 = caster.Character.Name, dwa = (float)restoration, dwb = ticks + SpellCasts.Seconds(seconds),
        });
        Log.Info("combat", string.Create(CultureInfo.InvariantCulture, $"'{caster.Character.Name}' offers '{target.Character.Name}' a resurrection ({restoration:P0}, {seconds:0} s)"));
    }

    /// <summary>ResurrectionReply (op 207) {0 ts, 1 accept} of <paramref name="p"/>: up with the offer's health share, or the caster is told of the refusal.</summary>
    public static string? Reply(Player p, bool accept)
    {
        if (p.Combat.Down is not { Offer: { } offer } down) return "no offer";
        EndOffer(p, down, accept ? "accepted" : "rejected");
        if (!accept)
        {
            if (offer.Caster.Map is not null) offer.Caster.Session.Send(new ResurrectionReplyEvent { ddn = offer.Caster.Id, dwc = p.Id, dwd = Rejected });
            return null;
        }
        Raise(p, offer.Restoration, null, offer.Caster);
        return null;
    }

    /// <summary>The offer ends (answered or run out): its waiting spell (removespellonresurrectanswer) leaves the target's list.</summary>
    private static void EndOffer(Player p, KnockedDownState down, string why)
    {
        if (down.Offer is not { } offer) return;
        down.Offer = null;
        if (offer.WaitingSpell is { } name && SpellEffects.Remove(p.World, p, s => s.Spell.Name == name))
        {
            SpellEffects.Send(p.World, p);
            SpellEffects.Refresh(p.World, p);
        }
        Log.Debug("combat", $"'{p.Character.Name}''s resurrection offer ends: {why}");
    }
}
