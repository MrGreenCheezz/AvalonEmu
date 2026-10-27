using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Mounts (analysis/work/stageD/mounts/SPEC.md): the mount item in equipment slot 7, mounted with op 199 Mount (key 2 true) over
/// mounttime (remounttime at the own mount object within remountdistance), dismounted with op 199 without key 2 or by the server
/// before an action (an attack, a cast, a harvest, a station, loot), cancelled while it runs by op 200, a walk-off, an action
/// or the knockdown. Mounted, the player wears the mount's mountedbuff (speed, resistances, immunities, the speed buff of its
/// castspellaftermoving) and halfmountedbuff (load; it stays while the mount object stands, which despawns once the owner is beyond
/// its halfmountrange, <see cref="UpdateObject"/>), mob hits go to the mount bar
/// (MountHealthUpdate 312) instead of the health, and at 0 the mount dies: a forced dismount with its cooldown. A dismount leaves
/// a <see cref="MountObject"/> beside the player. The official server answers an accepted op 199 never (any answer aborts the
/// client's mount, cyk.ai7 0x180A01C70), so a refusal is a response with rc != 0. Mounted and the cooldown are saved with the
/// character; a mounted Join comes back mounted (§4.8). A mount with abilities (&lt;mountspell&gt;, <see cref="MountData.Spells"/>) puts
/// them in the spell slots while ridden (<see cref="SpellSlots"/>): their casts keep the rider mounted, every other cast dismounts.
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Mounts
{
    /// <summary>EquipmentSlot.Mount.</summary>
    public const int MountSlot = 7;

    /// <summary>The ReturnCode of a refused Mount (op 199): any value but 0 does, the client only aborts (SPEC §1); RcGeneric.InvalidState.</summary>
    public const short Refused = -501;

    /// <summary>The tolerance on top of remountdistance for a remount at the own mount object (SPEC §4.1).</summary>
    public const float RemountTolerance = 0.5f;

    /// <summary>How far beside the player a dismount leaves the mount object (1.50 in all 117 captured dismounts, SPEC §4.3).</summary>
    public const float ObjectOffset = 1.5f;

    /// <summary>The world direction the mount object's side follows (~145°, 107 of 117 captured dismounts, SPEC §4.3).</summary>
    private static readonly Vector2 SideDirection = new(-0.82f, 0.57f);

    /// <summary>MountCooldownUpdate key 2 as captured in every Join (35333329 ticks, 3.5 s after 0001-01-01: "never", Q4), and key 1 without a cooldown.</summary>
    public const long Never = 35333329;

    private sealed record MountSpawn : ClusterObject;

    private static int _spawns;

    /// <summary>The mount item in slot 7; null when the slot holds none (or something that is no mount).</summary>
    public static Item? ItemOf(Player p) => p.Inventory.Equipment[MountSlot] is { Template.Mount: not null } item ? item : null;

    private static SpellTemplate? Spell(Player p, string? name) => name is null ? null : p.World.Spells?.Template(name);

    /// <summary>
    /// The player rides <paramref name="item"/> from now on: <see cref="PlayerMount.Ridden"/>, and the mounted buff's castspellaftermoving
    /// (the speed buff, SPEC §4.7) read once into <see cref="PlayerMount.Speed"/> so the world tick parses nothing.
    /// </summary>
    private static void Ride(Player p, ItemTemplate item, SpellTemplate mounted)
    {
        p.Mount.Ridden = item;
        var node = mounted.Effects.FirstOrDefault(e => e.Name.LocalName == "castspellaftermoving");
        p.Mount.Speed = node is not null && Spell(p, (string?)node.Attribute("spell")) is { } speed
            ? new MountSpeedBuff(speed, SpellTemplate.Float(node, "minmovespeed"), SpellCasts.Seconds(SpellTemplate.Float(node, "movetimetocast")),
                SpellCasts.Seconds(SpellTemplate.Float(node, "cooldownafterdamage")), SpellCasts.Seconds(SpellTemplate.Float(node, "standtimetoendspell")))
            : null;
    }

    /// <summary>
    /// The player rides nothing any more (a dismount, the mount's death, the knockdown): its mount spells leave the slots with it, so a
    /// mount spell cast under way is interrupted (CastCancel {1 true}) and a mount spell's channel (the mammoth's auras) ends.
    /// </summary>
    private static void Unride(Player p)
    {
        if (p.Combat.Cast is { MountSpell: true } cast) SpellCasts.Cancel(p, cast, interrupted: true);
        if (p.Combat.Channel is { } channel && p.Mount.Ridden?.Mount?.Spells.Contains(channel.Parent.Name) == true)
            SpellCasts.EndChannel(p, SpellCasts.ChannelEnd.Interrupted);
        p.Mount.State = MountState.None;
        p.Mount.SpeedBuff = false;
        p.Mount.Ridden = null;
        p.Mount.Speed = null;
    }

    // ---- the mount spells ----

    /// <summary>
    /// The spell slots of a mounted player: per CharacterSpellSlot (0-13, as WorldSession.SpellSlots) the mount spell's id from
    /// <see cref="MountData.Spells"/>, -1 elsewhere (all of them on a mount without abilities, the ox); null when not mounted. Mirrors the client's own layout:
    /// on Mounted (cyk.aft 0x180A01310) it rebuilds its slot table (ct4.i 0x1809B17B0) and, while player+0x220 holds a mount item
    /// (cpx.au6), ct4.k 0x1809B1F70 fills slots 0-13 (CharacterSpellSlot.MainHand1..Food) from that item's bdq+0x4d0 spells with the mount item as their source and clears
    /// the rest (weapons, armor, potion, food); dismounted, ct4.j puts the equipment's spells back. The client builds it from its
    /// items.xml, not from CharacterEquipmentChanged key 7, so nothing is sent for it and key 7 stays the equipment's.
    /// </summary>
    public static int[]? SpellSlots(Player p)
    {
        if (!p.IsMounted || p.Mount.Ridden?.Mount is not { } data) return null;
        var slots = Enumerable.Repeat(-1, MountData.SpellSlotCount).ToArray();
        for (var i = 0; i < slots.Length && i < data.Spells.Count; i++)
            if (p.World.Spells?.Template(data.Spells[i]) is { } spell) slots[i] = spell.Id;
        return slots;
    }

    /// <summary>Whether a CastStart of <paramref name="spellId"/> in <paramref name="slot"/> casts a spell of the ridden mount (<see cref="SpellSlots"/>).</summary>
    public static bool IsMountSpell(Player p, byte slot, int spellId) =>
        spellId >= 0 && SpellSlots(p) is { } slots && slot < slots.Length && slots[slot] == spellId;

    /// <summary>
    /// An accepted mount spell cast: the rider stays mounted (no <see cref="BeforeAction"/>); like any cast it ends the mount's speed
    /// buff and restarts its movetimetocast (castingbreakseffect, SPEC §2).
    /// </summary>
    public static void CastsMountSpell(Player p)
    {
        if (p.Mount.MovingSince is not null) p.Mount.MovingSince = p.World.GameNow;
        if (p.Mount.SpeedBuff) EndSpeedBuff(p);
    }

    // ---- the request (SPEC §4.1) ----

    /// <summary>
    /// The server's checks of a Mount request with key 1 = <paramref name="itemId"/> (SPEC §4.1, the client's own in cyk.u): standing,
    /// neither mounting nor mounted, a mount item in slot 7 with that object id (it changes at every Join) and durability left, the
    /// cooldown after its death over, no harvest, cast or station action under way, and its mounted buff in spells.xml. Returns
    /// why it is refused; null = accepted.
    /// </summary>
    public static string? Check(Player p, long itemId)
    {
        if (p.Map is null || p.Combat.Down is not null) return "knocked down";
        if (p.Mount.State != MountState.None) return $"already {p.Mount.State}";
        if (ItemOf(p) is not { } item) return "no mount in slot 7";
        if (item.Id != itemId) return $"key 1 {itemId} is not the mount's object id {item.Id}";
        if (item.DurabilityRaw <= 0) return "the mount is broken";
        var now = p.World.GameNow;
        if (now < p.Character.MountCooldownUntil) return $"on cooldown for {(p.Character.MountCooldownUntil - now) / (double)TimeSpan.TicksPerSecond:0.#} s";
        if (p.Harvest is not null || p.Combat.Cast is not null || p.Action is { Over: false }) return "a harvest, cast or station action is under way";
        if (Spell(p, item.Template.Mount!.MountedBuff) is null) return $"no mounted buff {item.Template.Mount.MountedBuff} in spells.xml";
        return null;
    }

    /// <summary>
    /// An accepted mount at the server's receive time R (SPEC §4.1): a remount when <paramref name="remount"/> (key 3) and the own
    /// mount object stands within its item's remountdistance + <see cref="RemountTolerance"/>, else a full mount; D = remounttime or
    /// mounttime. At R: the mount item's NewEquipmentItem with key 3 true to the player, MountStart {0, 1 R, 2 item, 3 remount} to
    /// its observers only (never to itself: 0 of 18 captured); at R + D <see cref="Complete"/>.
    /// </summary>
    public static void Start(Player p, bool remount)
    {
        var world = p.World;
        var map = p.Map!;
        var item = ItemOf(p)!;
        var now = world.GameNow;
        var near = remount && p.Mount.Object is { } o
            && Vector2.Distance(SpellMovement.PositionOf(p, now), o.Position) <= o.Item.Mount!.RemountDistance + RemountTolerance;
        var data = item.Template.Mount!;
        var pending = new PendingMount(now, now + SpellCasts.Seconds(near ? data.RemountTime : data.MountTime), near, item);
        p.Mount.State = MountState.Mounting;
        p.Mount.Pending = pending;
        Flagging.Interrupt(p, "mounts", interrupted: false); // stageE pvp SPEC §3.1 [infer]: mounting ends a flag cast
        SpellEffects.Break(world, p, "mounting", now); // mountingbreakseffect (the invisibilities, stageD effects batch 2)
        p.Session.Send(Network.WorldSession.ItemObject(item, inUse: true));
        p.SendToObservers(new MountStartEvent { ddn = p.Id, csu = now, csv = item.Template.Index, csw = near });
        Log.Debug("mount", $"'{p.Character.Name}' mounts {item.Template.Name}{(near ? " (remount)" : "")}: {(pending.End - now) / TimeSpan.TicksPerMillisecond} ms");
        SpellCasts.At(world, map, pending.End, () =>
        {
            if (p.Mount.Pending == pending && p.Map == map) Complete(p, pending);
        });
    }

    /// <summary>
    /// The end of the mount bar at t = R + D (SPEC §4.1): CastSpell of the mounted buff, and of the halfmounted buff when it is not on
    /// (a full mount; a remount has it from its mount object), Mounted {0, 1 t, 2 item, 4 mount health, 5 its regeneration, 6 t,
    /// 7 true, 8 true (remount) / 9 true (full), 10 -1} to the player and its observers, the Leave of the mount object, then the
    /// list with the buffs (ActiveSpellEffectsUpdate; the mounted buff's removeactiveeffects movementbuff first). Saved as mounted.
    /// </summary>
    private static void Complete(Player p, PendingMount pending)
    {
        var world = p.World;
        var t = pending.End;
        var item = pending.Item;
        var data = item.Template.Mount!;
        p.Mount.Pending = null;
        if (ItemOf(p) != item)
        {
            p.Mount.State = MountState.None; // the item is gone meanwhile (a GM command): nothing to ride
            p.SendAround(new MountCancelEvent { ddn = p.Id });
            return;
        }
        p.Mount.State = MountState.Mounted;
        var at = SpellMovement.PositionOf(p, t);
        var mounted = Spell(p, data.MountedBuff)!;
        Ride(p, item.Template, mounted);
        var half = Spell(p, data.HalfMountedBuff);
        var halfOn = half is not null && p.Effects.Find(half.Id, p.Id) is not null;
        p.SendAround(CastSpell(p, mounted, at, t));
        if (half is not null && !halfOn) p.SendAround(CastSpell(p, half, at, t));
        SyncRegeneration(p, t);
        p.SendAround(Mounted(p, t, item, quick: pending.Remount, full: !pending.Remount, facing: -1));
        RemoveObject(p, dropHalf: false);
        SpellCasts.EndToggles(p, SpellCasts.ToggleEnd.Mount, send: false); // its toggles end with the mount (the list below)
        SpellEffects.Remove(world, p, s => s.Spell.Category == "movementbuff");
        Apply(p, mounted, item, t);
        if (half is not null && !halfOn) Apply(p, half, item, t);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
        p.Mount.MovingSince = p.Mount.StandingSince = null;
        p.Mount.SpeedBuff = false;
        p.Character.MountedSince = t;
        world.Saves.MarkChanged(p.Character);
        Log.Info("mount", $"'{p.Character.Name}' is mounted on {item.Template.Name}");
    }

    /// <summary>
    /// The mount bar under way ends without a mount (op 200, a walk-off, an action, a dismount request, logout, or damage reaching health):
    /// MountCancel {0 me} to the player and its observers, the mount item's NewEquipmentItem without key 3. Nothing when none runs.
    /// </summary>
    public static void Cancel(Player p, string why)
    {
        if (p.Mount is not { State: MountState.Mounting, Pending: { } pending }) return;
        p.Mount.State = MountState.None;
        p.Mount.Pending = null;
        p.SendAround(new MountCancelEvent { ddn = p.Id });
        p.Session.Send(Network.WorldSession.ItemObject(pending.Item, inUse: false));
        Log.Debug("mount", $"'{p.Character.Name}' mount cancelled: {why}");
    }

    /// <summary>
    /// Before an action of the player (an attack, a cast, a harvest, a station, loot; SPEC §4.2, §4.3): a mount under way is cancelled, a
    /// mounted player dismounted. A world interaction (all but the attack and the cast) also ends what has worldinteractionbreakseffect
    /// (<see cref="SpellEffects.Break"/>, stageD effects batch 2). A flag cast under way ends (<see cref="Flagging.Interrupt"/>, as the
    /// client's own: stageE pvp SPEC §3.1 [infer]).
    /// </summary>
    public static void BeforeAction(Player p, string what)
    {
        if (what != "fishes") Fishing.End(p, $"the player {what}"); // stageG fishing SPEC §4: any other action ends a session
        Flagging.Interrupt(p, what, interrupted: false);
        if (what is not ("attacks" or "casts")) SpellEffects.Break(p.World, p, "worldinteraction", p.World.GameNow);
        if (p.Mount.State == MountState.Mounting) Cancel(p, what);
        else if (p.IsMounted) Dismount(p);
    }

    // ---- the dismount (SPEC §4.3) ----

    /// <summary>
    /// A dismount at R (dismounttime 0: instant; the request without key 2, or the server's before an action): the mount object
    /// beside the player (NewMountObject to it and its observers, <see cref="ObjectPosition"/>), the mount item's NewEquipmentItem
    /// without key 3, CastSpell of the dismount buff (DISMOUNTED), Mounted {0, 1 R, 4, 5, 6 R, 7 true, 8 true, 10 the facing}, then
    /// the list without the mounted buff and the speed buff and with DISMOUNTED (5 s); the halfmounted buff stays while the object
    /// does. No MountStart to the observers (Q12: their handler would start a mount bar). Saved as dismounted.
    /// TODO (stageE pvp SPEC §3.3 [infer]): with a Hostile player within gamedata hostiledismountdetectionradius (40 m), the mount's
    /// hostiledismountbuff (DISMOUNTED_HOSTILE) and Mounted key 13 in place of dismountbuff.
    /// </summary>
    public static void Dismount(Player p)
    {
        if (!p.IsMounted || p.Map is not { } map) return;
        var world = p.World;
        var now = world.GameNow;
        var item = ItemOf(p);
        var data = (p.Mount.Ridden ?? item?.Template)?.Mount; // the ridden mount's buffs, also when its item has gone meanwhile
        var at = SpellMovement.PositionOf(p, now);
        var facing = p.Facing;
        p.Mount.State = MountState.None;
        p.Character.MountedSince = 0;
        world.Saves.MarkChanged(p.Character);
        if (item is not null) SpawnObject(p, map, item.Template, at, facing);
        if (item is not null) p.Session.Send(Network.WorldSession.ItemObject(item, inUse: false));
        var dismounted = Spell(p, data?.DismountBuff ?? "DISMOUNTED");
        if (dismounted is not null) p.SendAround(CastSpell(p, dismounted, at, now));
        SyncRegeneration(p, now);
        p.SendAround(Mounted(p, now, null, quick: true, full: false, facing: facing));
        TakeMountedBuffs(p, data, half: item is null); // no mount object without the item: the halfmounted buff goes too
        SpellCasts.EndToggles(p, SpellCasts.ToggleEnd.Mount, send: false); // a mount's toggle (MOUNTSPELL_SPRINT_*) and the rider's end
        Unride(p);
        if (dismounted is not null && item is not null) Apply(p, dismounted, item, now);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
        Log.Info("mount", $"'{p.Character.Name}' dismounts");
    }

    /// <summary>
    /// Where a dismount at <paramref name="at"/> facing <paramref name="facing"/> (degrees) leaves the mount: <see cref="ObjectOffset"/>
    /// to the side, left l = (-cos f, sin f) when it points towards <see cref="SideDirection"/> (dot ≥ 0), else right r = (cos f, -sin f)
    /// (SPEC §4.3; facing 0 → (-1.5, 0), 180 → (-1.5, 0), 90 → (0, 1.5)).
    /// </summary>
    public static Vector2 ObjectPosition(Vector2 at, float facing)
    {
        var f = facing * MathF.PI / 180;
        var left = new Vector2(-MathF.Cos(f), MathF.Sin(f));
        return at + ObjectOffset * (Vector2.Dot(left, SideDirection) >= 0 ? left : -left);
    }

    private static void SpawnObject(Player p, Map map, ItemTemplate item, Vector2 at, float facing)
    {
        RemoveObject(p, dropHalf: false);
        if (map.Grid is null) return; // no template yet: nowhere to stand (a map still loading)
        var pos = ObjectPosition(at, facing);
        var o = new MountObject(p.World.ObjectIds.Next(), new MountSpawn
        {
            Key = new SpawnKey(map.Cluster, "mount", _spawns++), Tile = "", Position = pos, Component = new XElement("mount"),
        }, p, item, pos, facing);
        p.Mount.Object = o;
        map.AddObject(o);
    }

    /// <summary>
    /// The mount object goes (SPEC §4.4): a Leave to every client that knows it. <paramref name="dropHalf"/> (the mount unequipped,
    /// the owner leaving, the owner beyond halfmountrange): the halfmounted buff goes with it (the caller sends the list); returns
    /// whether it did.
    /// </summary>
    public static bool RemoveObject(Player p, bool dropHalf)
    {
        var o = p.Mount.Object;
        if (o is not null)
        {
            p.Mount.Object = null;
            o.Map?.RemoveObject(o);
        }
        if (!dropHalf || p.IsMounted) return false;
        var names = new HashSet<string?>(StringComparer.Ordinal) { o?.Item.Mount?.HalfMountedBuff, ItemOf(p)?.Template.Mount?.HalfMountedBuff };
        return SpellEffects.Remove(p.World, p, s => s.Caster == p.Id && names.Contains(s.Spell.Name));
    }

    /// <summary>
    /// The slot-7 mount was taken off or swapped while dismounted (SPEC §4.4, §4.9; 113433: CharacterEquipmentChanged slot 7 = 0,
    /// then the Leave, then the list without the halfmounted buff). Re-equipping spawns no new object.
    /// </summary>
    public static void Unequipped(Player p)
    {
        if (p.Mount.Object is null) return;
        if (RemoveObject(p, dropHalf: true))
        {
            SpellEffects.Send(p.World, p);
            SpellEffects.Refresh(p.World, p);
        }
    }

    /// <summary>
    /// The slot-7 mount was destroyed (InventoryDestroyItem; the client has dropped the object already, so nothing about it is sent):
    /// a mount under way ends (MountCancel), a rider is dismounted without a mount object, a standing mount object goes.
    /// </summary>
    public static void Destroyed(Player p)
    {
        if (p.Mount.State == MountState.Mounting)
        {
            p.Mount.State = MountState.None;
            p.Mount.Pending = null;
            p.SendAround(new MountCancelEvent { ddn = p.Id });
            Log.Debug("mount", $"'{p.Character.Name}' mount cancelled: the mount was destroyed");
        }
        else if (p.IsMounted) Dismount(p);
        else Unequipped(p);
    }

    /// <summary>Takes the mounted buff and its speed buff (and with <paramref name="half"/> the halfmounted buff) off the player; the caller sends the list.</summary>
    private static void TakeMountedBuffs(Player p, MountData? data, bool half)
    {
        var names = new HashSet<string?>(StringComparer.Ordinal) { data?.MountedBuff, p.Mount.Speed?.Spell.Name, half ? data?.HalfMountedBuff : null };
        names.Remove(null);
        SpellEffects.Remove(p.World, p, s => s.Caster == p.Id && names.Contains(s.Spell.Name));
        p.Mount.SpeedBuff = false;
    }

    // ---- the mount bar (SPEC §4.5, §4.6) ----

    /// <summary>
    /// The armor or magic resistance share of a mounted player against <paramref name="type"/> (SPEC §4.5, Q1, provisional): the
    /// mounted buff's raw physicalarmor / magicresistance value × 100 as resistance points (ox 0.5 → 100 / 150 = 0.667; mule none: 1),
    /// on the raw hit, instead of the player's armor (captured: the mount took 0.62 × the dismounted player's hits).
    /// </summary>
    public static double Resistance(Player p, EffectType type)
    {
        var key = type == EffectType.Magic ? "magicresistance" : "physicalarmor";
        var node = Spell(p, (p.Mount.Ridden ?? ItemOf(p)?.Template)?.Mount?.MountedBuff)?.Effects
            .FirstOrDefault(e => e.Name.LocalName == "buffovertime" && (string?)e.Attribute("type") == key);
        return Combat.Resistance(node is null ? 0 : 100 * SpellTemplate.Float(node, "value"));
    }

    /// <summary>
    /// A hit of <paramref name="damage"/> (resisted and rounded) by mob or player <paramref name="attacker"/> on a mounted player at <paramref name="ticks"/> (SPEC §4.5):
    /// the mount bar, never the health: MountHealthUpdate {0 player, 1 ticks, 2 -damage, 3 left (absent at 0), 4 type, 5 origin,
    /// 6 the attacker, 7 spell} to the player and its observers (Q7); the speed buff ends and cannot come for its cooldownafterdamage. At 0
    /// the mount dies (<see cref="Died"/>; <paramref name="hostile"/>: by a Hostile player's hit).
    /// </summary>
    public static void Damage(Player p, long attacker, float damage, EffectType type, HitOrigin origin, short spell, long ticks, bool hostile = false)
    {
        var left = Math.Max(0, p.MountHealth.At(ticks) - damage);
        p.MountHealth.Set(left, Math.Max(ticks, p.MountHealth.Stamp));
        p.SendAround(new MountHealthUpdateEvent
        {
            ddn = p.Id, ch5 = ticks, ch6 = -damage, ch7 = left, ch8 = (byte)type, cia = attacker, cib = spell, Extra = new() { [5] = (byte)origin },
        });
        p.Mount.LastDamage = ticks;
        if (p.Mount.MovingSince is not null) p.Mount.MovingSince = ticks; // damageresetstimetocast
        if (p.Mount.SpeedBuff) EndSpeedBuff(p);
        if (left <= 0) Died(p, ticks, hostile);
    }

    /// <summary>
    /// The mount bar at 0 at <paramref name="t"/> (SPEC §4.6, not captured): CastSpell of forceddismountbuff (FORCE_DISMOUNTED),
    /// Mounted {0, 1 t, 4 0, 5 regen, 6 t, 7 true, 9 true, 10 -1} (no mount object: the mount despawns), the mount item's
    /// NewEquipmentItem without key 3, MountCooldownUpdate {0, 1 t + forceddismountcooldown, 2 <see cref="Never"/>} (saved; Mount is
    /// refused until then), the list without the mount buffs, then FORCE_DISMOUNTED runs (its cleanse and short immunities).
    /// <paramref name="hostile"/> (stageE pvp SPEC §3.3 [infer], a Hostile player's hit): the mount's forcedhostiledismountbuff
    /// (FORCE_DISMOUNTED_HOSTILE: the FORCE_DISMOUNTED cleanse and damage debuffs) and Mounted key 13 (the client's OnHostileDismount
    /// cooldowns) in place of forceddismountbuff.
    /// </summary>
    private static void Died(Player p, long t, bool hostile)
    {
        var world = p.World;
        var item = ItemOf(p);
        var data = (p.Mount.Ridden ?? item?.Template)?.Mount;
        var at = SpellMovement.PositionOf(p, t);
        p.Mount.State = MountState.None;
        var forced = Spell(p, hostile
            ? (p.Mount.Ridden ?? item?.Template)?.Attrs.GetValueOrDefault("forcedhostiledismountbuff") is { Length: > 0 } h ? h : "FORCE_DISMOUNTED_HOSTILE"
            : data?.ForcedDismountBuff ?? "FORCE_DISMOUNTED");
        if (forced is not null) p.SendAround(CastSpell(p, forced, at, t));
        SyncRegeneration(p, t);
        p.SendAround(Mounted(p, t, null, quick: false, full: true, facing: -1, hostile: hostile));
        if (item is not null) p.Session.Send(Network.WorldSession.ItemObject(item, inUse: false));
        var c = p.Character;
        c.MountedSince = 0;
        c.MountCooldownUntil = t + SpellCasts.Seconds(data?.ForcedDismountCooldown ?? 30);
        world.Saves.MarkChanged(c);
        p.Session.Send(CooldownUpdate(p));
        RemoveObject(p, dropHalf: false);
        TakeMountedBuffs(p, data, half: true);
        SpellCasts.EndToggles(p, SpellCasts.ToggleEnd.Mount, send: false);
        Unride(p);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
        if (forced is not null && p.Map is { } map && item is not null)
            SpellCasts.Execute(new SpellCasts.SpellContext(p, map, forced, 0, Combat.AbilityPowerOf(p, item), null, at, t)
                { CcAbilityPower = Combat.CcAbilityPowerOf(p, item) }, forced);
        Log.Info("mount", $"'{c.Name}''s mount died{(hostile ? " (hostile)" : "")}: no mount for {data?.ForcedDismountCooldown ?? 30} s");
    }

    /// <summary>
    /// The mount bar's regeneration follows the state at <paramref name="t"/>: 0 in combat (SPEC §4.5, like the health),
    /// else mounthitpointsregeneration (<see cref="Player.MountRegeneration"/>); the value regenerated so far is kept.
    /// </summary>
    public static void SyncRegeneration(Player p, long t)
    {
        var regen = p.MountRegeneration(p.ComputeStats().MountHealthRegen);
        if (p.MountHealth.RegenPerSecond == regen) return;
        p.MountHealth.Set(p.MountHealth.At(t), Math.Max(t, p.MountHealth.Stamp));
        p.MountHealth.RegenPerSecond = regen;
    }

    // ---- the knockdown and leaving (SPEC §4.2, §4.4) ----

    /// <summary>
    /// The player goes down (stage 5e; its buffs end with the knockdown's own list): a mount under way is cancelled, a mounted player
    /// thrown off (Mounted {7, 9, 10 -1}, no mount object), a mount object leaves in the same instant (002835: with the HealthUpdate to 0).
    /// </summary>
    public static void KnockedDown(Player p, long ticks)
    {
        Cancel(p, "knocked down");
        if (p.IsMounted)
        {
            Unride(p);
            p.Character.MountedSince = 0;
            p.World.Saves.MarkChanged(p.Character);
            SyncRegeneration(p, ticks);
            p.SendAround(Mounted(p, ticks, null, quick: false, full: true, facing: -1));
            if (ItemOf(p) is { } item) p.Session.Send(Network.WorldSession.ItemObject(item, inUse: false));
        }
        RemoveObject(p, dropHalf: false);
    }

    /// <summary>
    /// The player leaves its map (logout, cluster change, a new Join): a mount under way is dropped (not mounted), the mount object
    /// leaves (the halfmounted buff with it); a mounted player stays mounted in its character (SPEC §3: the next Join is mounted).
    /// </summary>
    public static void Leave(Player p)
    {
        if (p.Mount.State == MountState.Mounting) p.Mount.State = MountState.None;
        p.Mount.Pending = null;
        RemoveObject(p, dropHalf: true);
    }

    // ---- Join and the observers (SPEC §4.8) ----

    /// <summary>
    /// At Join, before the bars: a character saved mounted with its mount still in slot 7 (and its buff in spells.xml) is mounted
    /// again, its mounted and halfmounted buffs on with their old start; otherwise it is dismounted.
    /// </summary>
    public static void Restore(Player p)
    {
        var c = p.Character;
        if (!c.Mounted) return;
        if (ItemOf(p) is not { DurabilityRaw: > 0 } item || Spell(p, item.Template.Mount!.MountedBuff) is not { } mounted)
        {
            c.MountedSince = 0;
            p.World.Saves.MarkChanged(c);
            return;
        }
        p.Mount.State = MountState.Mounted;
        Ride(p, item.Template, mounted);
        Apply(p, mounted, item, c.MountedSince);
        if (Spell(p, item.Template.Mount.HalfMountedBuff) is { } half) Apply(p, half, item, c.MountedSince);
    }

    /// <summary>
    /// What a Join sends after its list (ActiveSpellEffectsUpdate) and before JoinFinished (SPEC §4.8, 113433 / 002835): mounted, the
    /// mount item's NewEquipmentItem with key 3 true and Mounted {0, 1 now, 2 item, 4, 5, 6 now, 7, 9, 11 true}; then, at every Join
    /// (14/14, mounted or not), MountCooldownUpdate.
    /// </summary>
    public static IEnumerable<AlbionEvent> JoinEvents(Player p)
    {
        if (p.IsMounted && ItemOf(p) is { } item)
        {
            yield return Network.WorldSession.ItemObject(item, inUse: true);
            var now = p.World.GameNow;
            yield return Mounted(p, now, item, quick: false, full: true, facing: 0, catchUp: true);
        }
        yield return CooldownUpdate(p);
    }

    /// <summary>The catch-up of a mounted player for a client that just got its NewCharacter and list: Mounted {7, 9, 11} (SPEC §4.8, Q6); null when not mounted.</summary>
    public static MountedEvent? ForNewObserver(Player p) =>
        p.IsMounted && ItemOf(p) is { } item ? Mounted(p, p.World.GameNow, item, quick: false, full: true, facing: 0, catchUp: true) : null;

    /// <summary>MountCooldownUpdate (313, asy): {0 me, 1 "can mount again at" (a past stamp when none), 2 <see cref="Never"/> (Q4)}.</summary>
    public static MountCooldownUpdateEvent CooldownUpdate(Player p) =>
        new() { ddn = p.Id, cse = p.Character.MountCooldownUntil > 0 ? p.Character.MountCooldownUntil : Never, csf = Never };

    // ---- the speed buff (SPEC §4.7) ----

    /// <summary>
    /// The world tick of a player: a dismounted owner beyond its mount object's halfmountrange loses it (<see cref="UpdateObject"/>);
    /// a mounted one runs the mounted buff's castspellaftermoving (ox: movetimetocast 4, standtimetoendspell 2, minmovespeed 5,
    /// cooldownafterdamage 4). Moving at minmovespeed or faster for movetimetocast, with no hit in the last cooldownafterdamage:
    /// CastHit {0 me, 1 me, 2 the speed buff, 3 1, 4 0} and the list with it; standing for standtimetoendspell: the list without it.
    /// </summary>
    public static void Update(Player p)
    {
        if (!p.IsMounted)
        {
            UpdateObject(p);
            return;
        }
        if (p.Map is null || p.Mount.Speed is not { } buff || ItemOf(p) is not { } item) return;
        var now = p.World.GameNow;
        var moving = p.Movement is { } m && m.Speed >= buff.MinMoveSpeed && m.PositionAt(DateTime.UtcNow) != m.Target;
        var mount = p.Mount;
        if (moving)
        {
            mount.StandingSince = null;
            mount.MovingSince ??= now;
        }
        else
        {
            mount.MovingSince = null;
            mount.StandingSince ??= now;
        }
        if (!mount.SpeedBuff && moving && now - mount.MovingSince >= buff.MoveTime && now - mount.LastDamage >= buff.DamageCooldown)
        {
            var speed = buff.Spell;
            mount.SpeedBuff = true;
            p.SendAround(Combat.CastHit(p.Id, p.Id, speed.Id, 0));
            Apply(p, speed, item, now);
            SpellEffects.Send(p.World, p);
            SpellEffects.Refresh(p.World, p);
        }
        else if (mount.SpeedBuff && !moving && now - mount.StandingSince >= buff.StandTime)
            EndSpeedBuff(p);
    }

    /// <summary>
    /// The half-mounted state ends with the distance (stageJ carry-load SPEC §3.1, stageD mounts SPEC Q2 [infer]): once the dismounted
    /// owner is farther than the object's halfmountrange (18 m on all 109 mounts, 2-D, the radius the client draws as a ring around
    /// its own object) from it, the object goes: its Leave to the owner and every observer (the client clears IsMountInRange and plays
    /// despawneffect), then the list without the halfmountedbuff (its load, ox 1901 kg). The client never measures this itself
    /// (MountObjectView.CreateHalfMountRangeRingObject is the only reader of halfmountrange); the next mount is a full one (mounttime).
    /// </summary>
    private static void UpdateObject(Player p)
    {
        if (p.Mount.Object is not { } o || p.Mount.State == MountState.Mounting || o.Item.Mount is not { } data || p.Map is null) return;
        var range = data.HalfMountRange > 0 ? data.HalfMountRange : 18;
        if (Vector2.DistanceSquared(SpellMovement.PositionOf(p, p.World.GameNow), o.Position) <= range * range) return;
        var half = RemoveObject(p, dropHalf: true);
        Log.Debug("mount", $"'{p.Character.Name}' left the mount object's {range:0.#} m: it despawns");
        if (!half) return;
        SpellEffects.Send(p.World, p);
        SpellEffects.Refresh(p.World, p);
    }

    /// <summary>The speed buff ends (standing, a hit): the list without it.</summary>
    private static void EndSpeedBuff(Player p)
    {
        p.Mount.SpeedBuff = false;
        var speed = p.Mount.Speed?.Spell;
        if (speed is null || !SpellEffects.Remove(p.World, p, s => s.Caster == p.Id && s.Spell.Id == speed.Id)) return;
        SpellEffects.Send(p.World, p);
        SpellEffects.Refresh(p.World, p);
    }

    // ---- events and buffs ----

    /// <summary>
    /// Mounted (210, asz): 0 me, 1 <paramref name="t"/>, 2 the mount item's index (absent = dismounted), 4 the mount bar at t, 5 its
    /// regeneration, 6 t (Int64 ticks), 7 true, 8 <paramref name="quick"/> (remount / dismount), 9 <paramref name="full"/> (full mount,
    /// forced dismount, catch-up), 10 <paramref name="facing"/> (-1 on a mount: ≥ 0 would turn the player), 11 <paramref name="catchUp"/>.
    /// Key 3: the mount skin number of the mount's category (stageN vanity SPEC §1.5), through Extra so that skin 0 goes out too; left out without one.
    /// </summary>
    private static MountedEvent Mounted(Player p, long t, Item? item, bool quick, bool full, float facing, bool catchUp = false, bool hostile = false)
    {
        var e = new MountedEvent
        {
            ddn = p.Id, csg = t, csh = item?.Template.Index ?? 0, csj = p.MountHealth.At(t), csk = p.MountHealth.RegenPerSecond, csl = t,
            csm = true, csn = quick, cso = full, csp = facing, csq = catchUp, css = hostile,
        };
        if (item is not null && p.World.Vanity.MountSkinFor(p.Character, item.Template) is var skin and >= 0) e.Extra = new() { [3] = skin };
        return e;
    }

    /// <summary>CastSpell (19) of a mount buff on the player: {0 me, 1 me, 2 its position, 3 the spell, 4 t, 5 t}.</summary>
    private static CastSpellEvent CastSpell(Player p, SpellTemplate spell, Vector2 at, long t) =>
        new() { ddn = p.Id, bvw = p.Id, bvx = at, bvy = spell.Id, bvz = t, bv0 = t };

    /// <summary>
    /// Puts <paramref name="spell"/> of the mount <paramref name="item"/> on the player from <paramref name="start"/>: each timed node as
    /// <see cref="SpellEffects.Make"/> makes it (buffs until removed, DISMOUNTED's 5 s), with the mount's ability power (ActiveSpellEffectsUpdate
    /// key 2 = 100 × abilitypowerprogression^(IP/100), 201.90387 for the T5 ox, key 3 155.10541). The caller sends the list.
    /// </summary>
    private static void Apply(Player p, SpellTemplate spell, Item item, long start)
    {
        var world = p.World;
        var ap = Combat.AbilityPowerOf(p, item);
        var cc = Combat.CcAbilityPowerOf(p, item);
        var now = world.GameNow;
        var subs = new SubEffect?[spell.Effects.Count];
        for (var i = 0; i < subs.Length; i++) subs[i] = SpellEffects.Make(world, p, ap, cc, spell.Effects[i], i, p, now);
        SpellEffects.Apply(world, p, spell, p.Id, ap, cc, start, subs);
        Log.Debug("mount", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' gets {spell} (AP {ap:0.###}, CC {cc:0.###})"));
    }
}
