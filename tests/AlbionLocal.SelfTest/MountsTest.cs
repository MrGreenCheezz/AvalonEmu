using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageD mounts (analysis/work/stageD/mounts/SPEC.md §6) with a T5 ox in slot 7 and a watcher beside the rider: the full 3 s mount
/// (no answer, the mount buffs, Mounted, the observer's MountStart), the dismount's mount object on the 145° side, the 0.5 s remount
/// and the full mount farther away, the cancels, the refusals and the locked slot, the unequipped mount, the mount bar under mob
/// hits and its death with the cooldown, the dismount before a harvest, and the Join and NewCharacter catch-ups. Light: the
/// numbers the SPEC lists, the rest only as far as it shows the rule.
/// </summary>
public static class MountsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mounts: health damage cancels full mount and remount, shield absorption preserves the cast, and cancelled timers cannot mount later", DamageCancelsMount),
        ("mounts: full mount (no answer, 30 key 3, 211 to the observer), 1252 + 1253 and Mounted {2, 4 1262, 5 19.13, 7, 9, 10 -1} at 3 s; dismount (311 at 1.5 m on the 145° side, 30, 1445, Mounted {7, 8, 10}); remount 0.5 s {7, 8} + Leave; key 3 far away: 3 s", MountAndDismount),
        ("mounts: a walk-off and op 200 cancel (212 to both, no Mounted); wrong key 1 / no mount: rc only; slot 7 locked while mounted; the unequipped mount takes its object and 1253, the next mount casts both", CancelsAndRefusals),
        ("mounts: mob hits go to the mount bar (96 without 5/15, 312 {3 1262 - dmg}, no 6); its death: FORCE_DISMOUNTED, Mounted {9, 10 -1}, no 311, 313 t + 30 s, Mount refused", MountBar),
        ("mounts: a mounted HarvestStart dismounts first; a mounted rejoin (Join, 11, 30 key 3, Mounted {2, 7, 9, 11}, 313, JoinFinished), the observer's NewCharacter then Mounted; a dismounted rejoin still 313", HarvestAndJoin),
        ("mounts: the battle swamp dragon's <mountspell>s fill slots 0 / 2 / 5 while mounted; FIREBOLT / DRAGONBREATH keep the rider mounted (debuff area, cone damage), cooldown; a sword cast dismounts and clears the slots", MountSpells),
        ("mounts: the battle mammoth's spells end with the mount: BIGCLEAVE under way -> CastCancel {1 true}; the PROTECTIONAURA channel -> ChannelingEnded; SPRINT_MAMMOTH off by TerminateToggleSpell, on again then gone with the dismount", MountSpellsEndWithMount),
        ("mounts: carry load as the client (stageJ carry-load): the reported kit with the T8 fire drake is 535.44 / 1252.98 kg = 43 % mounted, half-mounted and on foot; SWORD_SPIN dashes; overloaded it does not", CarryLoad),
        ("mounts: the mount object despawns beyond halfmountrange 18 m (Leave to owner and observer, then the list without 1253, max load back), the next mount is full; within 18 m it stays and the remount is quick", ObjectDespawn),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private const string Ox = "T5_MOUNT_OX";

    private static void DamageCancelsMount()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = Join(arena, "MountInterrupted");
        var (_, observer, _) = Join(arena, "MountObserver", ox: false);
        var mob = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(2, 0));
        Ride(session, me);
        arena.Run(0.1);
        var mark = peer.Events.Count;
        var observed = observer.Events.Count;
        Combat.DamagePlayer(mob, me, 10, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(me.Mount is { State: MountState.None, Pending: null }
            && Since(peer, mark).Count(e => e.Code == EventCodes.MountCancel) == 1
            && Since(observer, observed).Count(e => e.Code == EventCodes.MountCancel) == 1,
            "damage reaching health cancels mounting once for the rider and observer");
        arena.Run(3.1);
        Check(!me.IsMounted && !Since(peer, mark).Any(e => e.Code == EventCodes.Mounted),
            "the cancelled full mount timer never completes");

        Ride(session, me);
        arena.Run(3.1);
        Off(session, me);
        Ride(session, me, remount: true);
        Check(me.Mount.Pending is { Remount: true }, "the next cast is a quick remount");
        Combat.DamagePlayer(mob, me, 10, EffectType.Magic, HitOrigin.OverTime, -1, world.GameNow);
        arena.Run(0.6);
        Check(!me.IsMounted && me.Mount.Pending is null, "a damage-over-time tick cancels a quick remount too");

        var shield = world.Spells!.Template("ENERGY_BARRIER")!;
        var now = world.GameNow;
        SpellCasts.Execute(new SpellCasts.SpellContext(me, me.Map!, shield, 0, 100, null,
            me.PositionAt(DateTime.UtcNow), now), shield);
        Ride(session, me);
        mark = peer.Events.Count;
        var health = me.Health.At(now);
        Combat.DamagePlayer(mob, me, 100, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, now);
        Check(me.Mount.State == MountState.Mounting && me.Health.At(now) == health
            && !Since(peer, mark).Any(e => e.Code == EventCodes.MountCancel),
            "a fully absorbed hit preserves the mount cast");
        Combat.DamagePlayer(mob, me, 1000, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, now);
        Check(me.Mount is { State: MountState.None, Pending: null }
            && Since(peer, mark).Count(e => e.Code == EventCodes.MountCancel) == 1,
            "damage spilling through the shield cancels the cast");
        arena.Run(3.1);
        Check(!me.IsMounted, "the shield-break cancellation also invalidates its completion timer");
    }

    /// <summary>
    /// A starter character on the arena's stand (or in <paramref name="cluster"/> at <paramref name="at"/>) with a sword and, unless not, the
    /// ox (or <paramref name="mount"/>), joined.
    /// </summary>
    private static (WorldSession Session, FakePeer Peer, Player Player) Join(Arena a, string name, bool ox = true, string cluster = "0204", Vector2? at = null,
        string mount = Ox)
    {
        var c = StarterKit.Default.NewCharacter(name);
        c.Equipment[0] = new InventoryItem("T4_MAIN_SWORD");
        if (ox) c.Equipment[Mounts.MountSlot] = new InventoryItem(mount);
        c.Cluster = cluster;
        (c.PositionX, c.PositionY) = ((at ?? a.Stand).X, (at ?? a.Stand).Y);
        c.Progression = new Progression();
        Check(a.Characters.Create(c), $"character {name} created");
        return Rejoin(a, c.Id);
    }

    private static (WorldSession Session, FakePeer Peer, Player Player) Rejoin(Arena a, Guid id)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, a.World);
        peer.Disconnected = session.OnDisconnected;
        session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
        return (session, peer, session.Player!);
    }

    private static readonly HashSet<EventCodes> Mount =
    [
        EventCodes.NewEquipmentItem, EventCodes.MountStart, EventCodes.Mounted, EventCodes.MountCancel, EventCodes.NewMountObject, EventCodes.CastSpell,
        EventCodes.Leave, EventCodes.ActiveSpellEffectsUpdate, EventCodes.MountHealthUpdate, EventCodes.MountCooldownUpdate, EventCodes.HealthUpdate,
        EventCodes.RegenerationPlayerComboChanged, EventCodes.InCombatStateUpdate, EventCodes.CastHit,
    ];

    private static List<(EventCodes Code, Dictionary<byte, object?> P)> Of(FakePeer peer, int mark) => [.. Since(peer, mark).Where(e => Mount.Contains(e.Code))];

    private static short Id(string spell) => Client.Spells!.Template(spell)!.Id;

    private static void Ride(WorldSession s, Player p, bool remount = false) =>
        Send(s, new MountRequest { wf = p.World.GameNow, v3 = Mounts.ItemOf(p)!.Id, v4 = true, v5 = remount });

    private static void Off(WorldSession s, Player p) => Send(s, new MountRequest { wf = p.World.GameNow });

    private static void Stop(WorldSession s, Vector2 at, float facing = 0) => Send(s, new MoveRequest { wf = GameTime.Now, v6 = [at.X, at.Y], v8 = [at.X, at.Y], v7 = facing });

    private static bool Has(Dictionary<byte, object?> p, params byte[] keys) => keys.All(p.ContainsKey);

    private static bool Lacks(Dictionary<byte, object?> p, params byte[] keys) => !keys.Any(p.ContainsKey);

    private static Vector2 V(Dictionary<byte, object?> p, byte k) => p[k] is float[] and [var x, var y] ? new Vector2(x, y) : new Vector2(float.NaN);

    private static void MountAndDismount()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = Join(arena, "Rider");
        var (_, watcher, _) = Join(arena, "Watcher", ox: false);
        var item = Mounts.ItemOf(me)!;
        Check(item.Template.Index == 3040 && item.Template.Mount is { MountTime: 3, RemountTime: 0.5, RemountDistance: 2, ForcedDismountCooldown: 30 }
              && me.MountHealth.Max == 1262 && Math.Abs(me.MountHealth.RegenPerSecond - 19.13f) < 1e-3,
              $"T5_MOUNT_OX is item 3040 (mounttime 3, remounttime 0.5, remountdistance 2, forceddismountcooldown 30), mount bar 1262 / 19.13 (got {item.Template.Index}, {me.MountHealth.Max})");
        Check(Mounts.ObjectPosition(Vector2.Zero, 0) is var a0 && Vector2.Distance(a0, new Vector2(-1.5f, 0)) < 0.01f
              && Vector2.Distance(Mounts.ObjectPosition(Vector2.Zero, 180), new Vector2(-1.5f, 0)) < 0.01f
              && Vector2.Distance(Mounts.ObjectPosition(Vector2.Zero, 90), new Vector2(0, 1.5f)) < 0.01f,
              "the mount object's side (SPEC §4.3): facing 0 → left (-1.5, 0), 180 → right (-1.5, 0), 90 → left (0, 1.5)");

        // 1. The full mount: no response, the mount item with key 3 at R and nothing else until R + 3 s; the observer's MountStart.
        var (responses, mark, wmark) = (peer.Responses.Count, peer.Events.Count, watcher.Events.Count);
        Ride(session, me);
        var atStart = Of(peer, mark);
        var seen = Of(watcher, wmark);
        Check(peer.Responses.Count == responses && atStart is [(EventCodes.NewEquipmentItem, var nei)] && L(nei, 0) == item.Id && nei[3] is true,
              $"Mount: no response, NewEquipmentItem {{0 the ox, 3 true}} only (got {Show(atStart)})");
        Check(seen is [(EventCodes.MountStart, var start)] && L(start, 0) == me.Id && L(start, 2) == 3040 && Lacks(start, 3),
              $"the observer: MountStart {{0 rider, 2 3040, no key 3}} (got {Show(seen)})");
        arena.Run(2.9);
        Check(Of(peer, mark).Count == 1, "nothing more before R + 3 s");
        arena.Run(0.15);
        var done = Of(peer, mark).Skip(1).ToList();
        Check(done.Select(e => e.Code).SequenceEqual([EventCodes.CastSpell, EventCodes.CastSpell, EventCodes.Mounted, EventCodes.ActiveSpellEffectsUpdate])
              && L(done[0].P, 3) == 1252 && L(done[1].P, 3) == 1253 && L(done[0].P, 1) == me.Id,
              $"at R + 3 s: CastSpell 1252, CastSpell 1253, Mounted, ActiveSpellEffectsUpdate (got {Show(done)})");
        var m = done[2].P;
        Check(L(m, 2) == 3040 && F(m, 4) == 1262 && Math.Abs(F(m, 5) - 19.13f) < 1e-3 && m[7] is true && m[9] is true && F(m, 10) == -1 && Lacks(m, 3, 8, 11),
              $"Mounted {{2 3040, 4 1262, 5 19.13, 7, 9, 10 -1}} (got {Show([done[2]])})");
        var list = done[3].P;
        Check(list[1] is short[] and [1252, 1253] && list[2] is float[] and [var ap, _] && Math.Abs(ap - 201.90387f) < 0.01f && list[3] is float[] and [var cc, _]
              && Math.Abs(cc - 155.10541f) < 0.01f && list[7] is byte[] bits && bits.Length >= 4 && bits[0] == 0x49 && bits[1] == 0x82 && bits[2] == 0x24 && bits[3] == 0x49,
              $"the list [1252, 1253], key 2 201.90387, key 3 155.10541, key 7 49 82 24 49 … as captured (got {Show([done[3]])})");
        var obs = Of(watcher, wmark).Skip(1).ToList();
        Check(obs.Take(3).Select(e => e.Code).SequenceEqual([EventCodes.CastSpell, EventCodes.CastSpell, EventCodes.Mounted]) && me.IsMounted && me.Character.Mounted,
              $"the observer gets the same CastSpells and Mounted; the rider is mounted and saved so (got {Show(obs)})");

        // 2. The dismount facing 90: the mount object at (0, 1.5) beside it.
        lock (world.Sync) me.Facing = 90;
        (mark, wmark) = (peer.Events.Count, watcher.Events.Count);
        Off(session, me);
        var off = Of(peer, mark);
        Check(off.Take(4).Select(e => e.Code).SequenceEqual([EventCodes.NewMountObject, EventCodes.NewEquipmentItem, EventCodes.CastSpell, EventCodes.Mounted]),
              $"dismount: NewMountObject, NewEquipmentItem, CastSpell, Mounted (got {Show(off)})");
        var o = off[0].P;
        Check(L(o, 1) == 3040 && L(o, 2) == -1 && Vector2.Distance(V(o, 3), arena.Stand + new Vector2(0, 1.5f)) < 0.01f && F(o, 4) == 90
              && o[5] is byte[] owner && new Guid(owner) == me.Character.Id && L(o, 7) == 3 && Enumerable.Range(11, 5).All(k => L(o, (byte)k) == -1) && F(o, 17) == 1,
              $"NewMountObject {{1 3040, 2 -1, 3 stand + (0, 1.5), 4 90, 5 own Guid, 7 3, 11-15 -1, 17 1}} (got {Show([off[0]])})");
        var dm = off[3].P;
        Check(Lacks(off[1].P, 3) && L(off[2].P, 3) == 1445 && Lacks(dm, 2, 9) && dm[7] is true && dm[8] is true && F(dm, 10) == 90 && !me.Character.Mounted,
              $"NewEquipmentItem without key 3, CastSpell 1445, Mounted {{no 2, 7, 8, 10 90}}, saved dismounted (got {Show(off.Skip(1).Take(3))})");
        Check(off.Last() is (EventCodes.ActiveSpellEffectsUpdate, var after) && after[1] is short[] and [1253, 1445] && after[8] is int[] and [5000],
              $"the list [1253, 1445] with DISMOUNTED's 5000 (got {Show(off.Skip(4))})");
        var wOff = Of(watcher, wmark);
        Check(wOff.Take(3).Select(e => e.Code).SequenceEqual([EventCodes.NewMountObject, EventCodes.CastSpell, EventCodes.Mounted]) && wOff.All(e => e.Code != EventCodes.MountStart),
              $"the observer: NewMountObject, CastSpell, Mounted, no MountStart (got {Show(wOff)})");
        var objectId = L(o, 0);

        // 3. The remount at 1.5 m: Mounted {7, 8} at R + 0.5 s, only 1252, then the Leave of the mount object.
        mark = peer.Events.Count;
        Ride(session, me, remount: true);
        arena.Run(0.45);
        Check(!Of(peer, mark).Any(e => e.Code == EventCodes.Mounted), "remount: no Mounted before 0.5 s");
        arena.Run(0.1);
        var re = Of(peer, mark).Skip(1).ToList();
        Check(re.Take(3).Select(e => e.Code).SequenceEqual([EventCodes.CastSpell, EventCodes.Mounted, EventCodes.Leave]) && L(re[0].P, 3) == 1252
              && re[1].P[7] is true && re[1].P[8] is true && Lacks(re[1].P, 9) && L(re[2].P, 0) == objectId && me.Mount.Object is null,
              $"remount: CastSpell 1252 only, Mounted {{7, 8}}, Leave of the mount object (got {Show(re)})");

        // 4. Key 3 but 5 m away from the mount object: a full 3 s mount.
        Off(session, me);
        Stop(session, arena.Stand + new Vector2(5, 0));
        mark = peer.Events.Count;
        Ride(session, me, remount: true);
        arena.Run(0.6);
        Check(!Of(peer, mark).Any(e => e.Code == EventCodes.Mounted), "key 3 at 5 m: no Mounted at 0.6 s");
        arena.Run(2.5);
        Check(Of(peer, mark).FirstOrDefault(e => e.Code == EventCodes.Mounted) is (EventCodes.Mounted, var full) && full.ContainsKey(9) && !full.ContainsKey(8),
              "key 3 at 5 m: a full mount, Mounted {9} at 3 s");
    }

    private static void CancelsAndRefusals()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = Join(arena, "Canceller");
        var (_, watcher, _) = Join(arena, "Onlooker", ox: false);
        var item = Mounts.ItemOf(me)!;

        // 5. A walk-off during the bar, then op 200: MountCancel to both, no Mounted later.
        var (mark, wmark) = (peer.Events.Count, watcher.Events.Count);
        Ride(session, me);
        Send(session, new MoveRequest { wf = GameTime.Now, v6 = [arena.Stand.X, arena.Stand.Y], v8 = [arena.Stand.X + 10, arena.Stand.Y], v9 = 5.5f });
        arena.Run(3.5);
        var mine = Of(peer, mark);
        Check(mine.Any(e => e.Code == EventCodes.MountCancel && L(e.P, 0) == me.Id) && Of(watcher, wmark).Any(e => e.Code == EventCodes.MountCancel)
              && mine.Count(e => e.Code == EventCodes.NewEquipmentItem) == 2 && mine.Last(e => e.Code == EventCodes.NewEquipmentItem).P.ContainsKey(3) == false
              && !mine.Any(e => e.Code == EventCodes.Mounted) && me.Mount.State == MountState.None,
              $"a walk-off: MountCancel to the rider and the observer, NewEquipmentItem without key 3, no Mounted (got {Show(mine)})");
        Stop(session, arena.Stand);
        mark = peer.Events.Count;
        Ride(session, me);
        Send(session, new MountCancelRequest { wf = world.GameNow });
        arena.Run(3.5);
        Check(Of(peer, mark).Any(e => e.Code == EventCodes.MountCancel) && !Of(peer, mark).Any(e => e.Code == EventCodes.Mounted), "op 200: MountCancel, no Mounted");

        // 10. A wrong key 1, and a player without a mount: a refusing response only.
        mark = peer.Events.Count;
        Send(session, new MountRequest { wf = world.GameNow, v3 = item.Id + 12345, v4 = true });
        Check(peer.Responses[^1].ReturnCode == Mounts.Refused && peer.Events.Count == mark, "a wrong key 1: rc != 0, no event");
        var (bare, barePeer, nobody) = Join(arena, "Walker", ox: false);
        var bareMark = barePeer.Events.Count;
        Send(bare, new MountRequest { wf = world.GameNow, v3 = 1, v4 = true });
        Check(barePeer.Responses[^1].ReturnCode == Mounts.Refused && barePeer.Events.Count == bareMark && nobody.Mount.State == MountState.None, "no mount in slot 7: rc != 0, no event");

        // §4.9: slot 7 does not move while mounted; it is put back.
        Ride(session, me);
        arena.Run(3.1);
        var eq = me.Inventory.Equipment;
        var bag = me.Inventory.Bag;
        var free = bag.FreeSlot();
        mark = peer.Events.Count;
        Send(session, new InventoryMoveItemRequest { ru = Mounts.MountSlot, rv = eq.Id, rw = item.Id, rx = free, ry = bag.Id });
        Check(me.IsMounted && eq[Mounts.MountSlot] == item && Since(peer, mark) is [(EventCodes.InventoryPutItem, var back)] && L(back, 1) == Mounts.MountSlot,
              "moving the mount while mounted: put back into slot 7, still mounted");

        // 11. Unequipped while dismounted with the mount object out: Leave, the list without 1253; the next mount is a full one casting both.
        Off(session, me);
        var objectId = me.Mount.Object!.Id;
        mark = peer.Events.Count;
        Send(session, new InventoryMoveItemRequest { ru = Mounts.MountSlot, rv = eq.Id, rw = item.Id, rx = free, ry = bag.Id });
        var unequip = Since(peer, mark);
        var leave = unequip.FindIndex(e => e.Code == EventCodes.Leave && L(e.P, 0) == objectId);
        Check(leave > unequip.FindIndex(e => e.Code == EventCodes.CharacterEquipmentChanged) && unequip.Skip(leave).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate
              && e.P[1] is short[] ids && !ids.Contains((short)1253)) && me.Mount.Object is null,
              $"unequipped: CharacterEquipmentChanged, then the Leave of the object and a list without 1253 (got {Show(unequip)})");
        Send(session, new InventoryMoveItemRequest { ru = free, rv = bag.Id, rw = item.Id, rx = Mounts.MountSlot, ry = eq.Id });
        Check(me.Mount.Object is null && eq[Mounts.MountSlot] == item, "re-equipped: no new mount object");
        mark = peer.Events.Count;
        Ride(session, me, remount: true);
        arena.Run(3.1);
        var casts = Of(peer, mark).Where(e => e.Code == EventCodes.CastSpell).Select(e => L(e.P, 3)).ToList();
        Check(me.IsMounted && casts.SequenceEqual([1252L, 1253L]), $"the next mount: full, CastSpell 1252 and 1253 (got {string.Join(", ", casts)})");

        // The mount destroyed while mounted: dismounted, no mount object, the mount buffs off.
        Send(session, new InventoryDestroyItemRequest { ri = item.Id });
        Check(!me.IsMounted && me.Mount.Object is null && eq[Mounts.MountSlot] is null && me.Effects.Find(1252, me.Id) is null && me.Effects.Find(1253, me.Id) is null,
              "the mount destroyed while mounted: dismounted, no mount object, 1252 and 1253 off");
    }

    private static void MountBar()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = Join(arena, "Ox rider");
        var (_, watcher, _) = Join(arena, "Bystander", ox: false);
        var mob = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(3, 0));
        Ride(session, me);
        arena.Run(3.1);
        var health = me.Health.At(world.GameNow);

        // 6. A mob's hit: 96 without keys 5/15 first, then the hit on the mount bar; no HealthUpdate.
        var (mark, wmark) = (peer.Events.Count, watcher.Events.Count);
        lock (world.Sync) Combat.DamagePlayer(mob, me, 100, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        var hit = Of(peer, mark);
        Check(hit.Select(e => e.Code).SequenceEqual([EventCodes.RegenerationPlayerComboChanged, EventCodes.InCombatStateUpdate, EventCodes.MountHealthUpdate])
              && Lacks(hit[0].P, 5, 15) && F(hit[0].P, 12) == 1262 && F(hit[2].P, 2) == -100 && F(hit[2].P, 3) == 1162 && L(hit[2].P, 6) == mob.Id
              && me.Health.At(world.GameNow) == health,
              $"a hit: 96 without 5/15, InCombatStateUpdate, MountHealthUpdate {{2 -100, 3 1162, 6 the mob}}, the health unchanged (got {Show(hit)})");
        Check(Of(watcher, wmark).Any(e => e.Code == EventCodes.MountHealthUpdate), "the observer gets the MountHealthUpdate too");

        // 7. The mount dies: FORCE_DISMOUNTED, Mounted {9, 10 -1}, no mount object, MountCooldownUpdate t + 30 s; a Mount then is refused.
        mark = peer.Events.Count;
        var t = world.GameNow;
        lock (world.Sync) Combat.DamagePlayer(mob, me, 5000, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, t);
        var death = Of(peer, mark);
        var bar = death.First(e => e.Code == EventCodes.MountHealthUpdate).P;
        var cast = death.First(e => e.Code == EventCodes.CastSpell).P;
        var mounted = death.First(e => e.Code == EventCodes.Mounted).P;
        var cooldown = death.First(e => e.Code == EventCodes.MountCooldownUpdate).P;
        Check(Lacks(bar, 3) && L(cast, 3) == Id("FORCE_DISMOUNTED") && mounted[9] is true && F(mounted, 10) == -1 && Lacks(mounted, 2, 8)
              && !death.Any(e => e.Code is EventCodes.NewMountObject or EventCodes.HealthUpdate) && L(cooldown, 1) == t + SpellCasts.Seconds(30)
              && !me.IsMounted && me.Mount.Object is null && me.Character.MountCooldownUntil == t + SpellCasts.Seconds(30),
              $"the mount's death: 312 without key 3, CastSpell FORCE_DISMOUNTED, Mounted {{9, 10 -1}}, no 311, 313 {{1 t + 30 s}} (got {Show(death)})");
        arena.Run(1);
        Ride(session, me);
        Check(peer.Responses[^1].ReturnCode == Mounts.Refused && me.Mount.State == MountState.None, "Mount within the 30 s: rc != 0");
    }

    private static void HarvestAndJoin()
    {
        using var arena = new Arena();
        var world = arena.World;
        var tree = Client.Clusters!.Get("0000").Harvestables.OrderBy(h => Vector2.Distance(h.Position, new Vector2(44.5f, 3.5f))).First();
        var stand = tree.Position - new Vector2(2, 0);
        var (session, peer, me) = Join(arena, "Gatherer", cluster: "0000", at: stand);
        Ride(session, me);
        arena.Run(3.1);
        Check(me.IsMounted, "mounted at the tree");

        // 8. HarvestStart while mounted: the dismount events, then the HarvestStart event and its answer.
        var node = me.Map!.Objects.Values.OfType<Harvestable>().Single(h => h.Key == tree.Key);
        var mark = peer.Sequence.Count;
        session.OnRequest(Request(new HarvestStartRequest { Start = GameTime.Now + TimeSpan.TicksPerSecond, HarvestableId = node.Id, ToolObjectId = -1 }, 7), encrypted: false);
        var seq = peer.Sequence.Skip(mark).Select(x => x is EventData e ? ((EventCodes)Code(e)).ToString() : $"rc {((OperationResponse)x).ReturnCode}").ToList();
        var mounted = seq.IndexOf(nameof(EventCodes.Mounted));
        var start = seq.IndexOf(nameof(EventCodes.HarvestStart));
        Check(!me.IsMounted && seq.IndexOf(nameof(EventCodes.NewMountObject)) is >= 0 and var o && o < mounted && mounted < start && seq.IndexOf("rc 0") > start,
              $"HarvestStart mounted: NewMountObject … Mounted, then the HarvestStart event and rc 0 (got {string.Join(", ", seq)})");
        arena.Run(3);

        // 9. A mounted rejoin, with a watcher already there; then a dismounted one.
        Ride(session, me);
        arena.Run(3.1);
        var (_, watcher, _) = Join(arena, "Witness", ox: false, cluster: "0000", at: stand);
        var id = me.Character.Id;
        peer.Disconnect();
        var wmark = watcher.Events.Count;
        (session, peer, me) = Rejoin(arena, id);
        List<string> AfterJoin(FakePeer p) => [.. p.Sequence.SkipWhile(x => x is not OperationResponse).Select(x => x is EventData e ? ((EventCodes)Code(e)).ToString() : "Join")
            .Where(n => n is "Join" or nameof(EventCodes.ActiveSpellEffectsUpdate) or nameof(EventCodes.NewEquipmentItem) or nameof(EventCodes.Mounted)
                or nameof(EventCodes.MountCooldownUpdate) or nameof(EventCodes.JoinFinished))];
        var order = AfterJoin(peer);
        Check(me.IsMounted && order.SequenceEqual(["Join", "ActiveSpellEffectsUpdate", "NewEquipmentItem", "Mounted", "MountCooldownUpdate", "JoinFinished"]),
              $"a mounted rejoin: Join, ASEU, NewEquipmentItem, Mounted, MountCooldownUpdate, JoinFinished (got {string.Join(", ", order)})");
        var all = Since(peer, 0);
        var catchUp = all.First(e => e.Code == EventCodes.Mounted).P;
        var key3 = all.Last(e => e.Code == EventCodes.NewEquipmentItem).P;
        Check(catchUp[7] is true && catchUp[9] is true && catchUp[11] is true && L(catchUp, 2) == 3040 && Lacks(catchUp, 8) && key3[3] is true
              && all.First(e => e.Code == EventCodes.ActiveSpellEffectsUpdate).P[1] is short[] l && l.Contains((short)1252) && l.Contains((short)1253),
              $"Mounted {{2 3040, 7, 9, 11}}, NewEquipmentItem key 3 true, the list with 1252 and 1253 (got {Show([(EventCodes.Mounted, catchUp)])})");
        var w = Since(watcher, wmark);
        var nc = w.FindIndex(e => e.Code == EventCodes.NewCharacter && L(e.P, 0) == me.Id);
        Check(nc >= 0 && w.FindIndex(e => e.Code == EventCodes.Mounted && L(e.P, 0) == me.Id) > nc, $"the watcher: NewCharacter, then Mounted (got {Show(w)})");
        Off(session, me);
        peer.Disconnect();
        (_, peer, me) = Rejoin(arena, id);
        order = AfterJoin(peer);
        Check(!me.IsMounted && order.Contains("MountCooldownUpdate") && order.IndexOf("MountCooldownUpdate") < order.IndexOf("JoinFinished") && !order.Contains("Mounted"),
              $"a dismounted rejoin: MountCooldownUpdate before JoinFinished, no Mounted (got {string.Join(", ", order)})");
    }

    /// <summary>
    /// A mount with abilities (T7_MOUNT_SWAMPDRAGON_BATTLE: FIREBOLT mainhand1, DRAGONBREATH offhandormainhand3, SPRINT_LIZARD shoes): its
    /// spells take the slots only while mounted; a cast of one keeps the rider mounted and runs its effects (the 10 m fire area on the
    /// mob), with the spell's 2 s cooldown; the sword's spell in the same slot dismounts first, and the mount slots go with the mount.
    /// </summary>
    private static void MountSpells()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (spells, items) = (Client.Spells!, Client.Items!);
        var (fire, breath, sprint) = (Id("MOUNTSPELL_FIREBOLT"), Id("MOUNTSPELL_DRAGONBREATH"), Id("MOUNTSPELL_SPRINT_LIZARD"));
        var dragon = items.Find("T7_MOUNT_SWAMPDRAGON_BATTLE")!.Mount!;
        Check(dragon.Spells is ["MOUNTSPELL_FIREBOLT", null, "MOUNTSPELL_DRAGONBREATH", null, null, "MOUNTSPELL_SPRINT_LIZARD", ..] && dragon.Spells.Count == 14
              && items.Find(Ox)!.Mount!.Spells.All(n => n is null),
              $"items.xml <mountspell>: the dragon's three spells by spellslot, the ox none (got {string.Join(", ", dragon.Spells)})");
        var (session, peer, me) = Join(arena, "Rider", mount: "T7_MOUNT_SWAMPDRAGON_BATTLE");
        var (_, watcher, _) = Join(arena, "Watcher", ox: false);
        var mob = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(6, 0));
        var near = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(-2.5f, 0)); // in the sword's reach
        Check(Mounts.SpellSlots(me) is null, "dismounted: no mount slots");
        Ride(session, me);
        arena.Run(10.1); // mounttime 10
        int[] expected = [fire, -1, breath, -1, -1, sprint, -1, -1, -1, -1, -1, -1, -1, -1];
        Check(me.IsMounted && Mounts.SpellSlots(me) is { } slots && slots.SequenceEqual(expected),
              $"mounted: slots 0 / 2 / 5 = FIREBOLT / DRAGONBREATH / SPRINT_LIZARD, the rest empty (got {string.Join(", ", Mounts.SpellSlots(me) ?? [])})");

        // The mount's FIREBOLT (ground, 1.2 s): no answer, still mounted, the observer's CastStart names the mount item; its 10 m area
        // puts MOUNTSPELL_FIREBOLT_EFFECT (-15 % defense) on the mob.
        var (responses, mark, wmark) = (peer.Responses.Count, peer.Events.Count, watcher.Events.Count);
        var start = world.GameNow;
        Send(session, new CastStartRequest { wf = start, dx = 0, d0 = fire, d2 = mob.Position, d4 = 1 });
        Check(peer.Responses.Count == responses && me.IsMounted && me.Combat.Cast is { MountSpell: true } && !Since(peer, mark).Any(e => e.Code == EventCodes.NewMountObject)
              && Since(watcher, wmark).Single(e => e.Code == EventCodes.CastStart).P is var cs && L(cs, 5) == fire && L(cs, 6) == Mounts.ItemOf(me)!.Template.Index,
              "CastStart of the mount spell: no answer, no dismount; the observer's CastStart {5 FIREBOLT, 6 the mount item}");
        arena.Run(4);
        Check(me.IsMounted && Since(peer, mark).Any(e => e.Code == EventCodes.CastFinished && L(e.P, 2) == fire)
              && mob.Effects.Find(Id("MOUNTSPELL_FIREBOLT_EFFECT"), me.Id) is not null && me.Combat.Cooldowns.TryGetValue(fire, out var ready) && Math.Abs(ready - start - SpellCasts.Seconds(1.2 + 2)) <= SpellCasts.Seconds(0.25),
              "at its end: CastFinished, the fire area's debuff on the mob, cooldown 2 s from the end (~3.2 s); still mounted");
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = fire, d2 = mob.Position, d4 = 2 });
        arena.Run(1.5); // finished at 1.2 s: on cooldown until 3.2 s
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = fire, d2 = mob.Position, d4 = 3 });
        Check(peer.Responses.Count == responses + 1 && me.IsMounted, "a recast within the cooldown is refused (one answer), the rider still mounted");

        // DRAGONBREATH (slot 2, a 45° cone 3-20 m from the rider): its area's -100 magic damage on the mob.
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 2, d0 = breath, d2 = mob.Position, d4 = 4 });
        arena.Run(3);
        Check(me.IsMounted && mob.Health.At(world.GameNow) < mob.Health.Max && near.Health.At(world.GameNow) == near.Health.Max,
              $"DRAGONBREATH: the mob in the cone is hurt ({mob.Health.At(world.GameNow)} of {mob.Health.Max}), the one behind not; still mounted");

        // The sword's spell in slot 0: not the mount's, so the server dismounts first; the mount slots are gone and FIREBOLT is refused.
        arena.Run(3);
        var sword = spells.SlotsOf("T4_MAIN_SWORD")!.Active[0][0]; // the first candidate of its slot 0, chosen on the sword
        lock (world.Sync) me.Inventory.Equipment[0]!.Active[0] = sword;
        mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = sword, d1 = near.Id, d4 = 5 });
        Check(!me.IsMounted && Since(peer, mark).Any(e => e.Code == EventCodes.NewMountObject) && Mounts.SpellSlots(me) is null,
              $"the sword's {spells.Name(sword)} while mounted dismounts first (NewMountObject) and the mount slots go");
        arena.Run(3);
        responses = peer.Responses.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = fire, d2 = mob.Position, d4 = 6 });
        Check(peer.Responses.Count == responses + 1, "dismounted, FIREBOLT is no slot's spell any more: refused");
    }

    /// <summary>
    /// What a dismount ends of the mount spells (T8_MOUNT_MAMMOTH_BATTLE: PROTECTIONAURA mainhand1, BIGCLEAVE armor, SPRINT_MAMMOTH
    /// shoes): a cast under way (interrupted), the aura's channel and the sprint toggle; TerminateToggleSpell finds the toggle in the
    /// mounted layout.
    /// </summary>
    private static void MountSpellsEndWithMount()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (aura, cleave, sprint) = (Id("MOUNTSPELL_PROTECTIONAURA"), Id("MOUNTSPELL_BIGCLEAVE"), Id("MOUNTSPELL_SPRINT_MAMMOTH"));
        var (session, peer, me) = Join(arena, "Rider", mount: "T8_MOUNT_MAMMOTH_BATTLE");
        void Mount(string when)
        {
            Ride(session, me);
            arena.Run(10.5); // mounttime 10
            Check(me.IsMounted && Mounts.SpellSlots(me) is { } slots && slots[0] == aura && slots[3] == cleave && slots[5] == sprint,
                  $"{when}: mounted, slots 0 / 3 / 5 = PROTECTIONAURA / BIGCLEAVE / SPRINT_MAMMOTH");
        }

        // BIGCLEAVE (1.5 s) under way: the dismount interrupts it (the caster's CastCancel {1 true}).
        Mount("first");
        var mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 3, d0 = cleave, d2 = arena.Stand + new Vector2(5, 0), d4 = 1 });
        arena.Run(0.5);
        Check(me.Combat.Cast is { MountSpell: true }, "BIGCLEAVE under way while mounted");
        Off(session, me);
        Check(!me.IsMounted && me.Combat.Cast is null && Since(peer, mark).Any(e => e.Code == EventCodes.CastCancel && e.P.GetValueOrDefault((byte)1) is true),
              "a dismount midway: the cast is gone, CastCancel {1 true} to the caster");

        // PROTECTIONAURA (instant, a channel): the dismount ends the channel.
        Mount("second");
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = aura, d1 = me.Id, d2 = arena.Stand, d4 = 2 });
        arena.Run(1);
        Check(me.IsMounted && me.Combat.Channel is not null, "PROTECTIONAURA channelling, still mounted");
        mark = peer.Events.Count;
        Off(session, me);
        Check(me.Combat.Channel is null && Since(peer, mark).Any(e => e.Code == EventCodes.ChannelingEnded), "the dismount ends the aura's channel (ChannelingEnded)");

        // SPRINT_MAMMOTH (slot 5): on, off by TerminateToggleSpell, on again, gone with the dismount.
        Mount("third");
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 5, d0 = sprint, d1 = me.Id, d2 = arena.Stand, d4 = 3 });
        arena.Run(0.5);
        Check(me.IsMounted && me.Combat.Toggles.ContainsKey(5), "SPRINT_MAMMOTH on in slot 5, still mounted");
        Send(session, new TerminateToggleSpellRequest { wf = world.GameNow, agt = 5 });
        Check(!me.Combat.Toggles.ContainsKey(5) && me.IsMounted, "TerminateToggleSpell switches it off, still mounted");
        arena.Run(0.5);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 5, d0 = sprint, d1 = me.Id, d2 = arena.Stand, d4 = 4 });
        arena.Run(0.5);
        Check(me.Combat.Toggles.ContainsKey(5), "SPRINT_MAMMOTH on again");
        Off(session, me);
        Check(!me.IsMounted && me.Combat.Toggles.Count == 0 && Mounts.SpellSlots(me) is null, "the dismount ends the toggle and clears the slots");
    }

    /// <summary>The kit of the stageJ carry-load report: equipment slots 0-9 and the bag, all quality 1, no enchantment.</summary>
    private static void ReportedKit(Character c)
    {
        c.Equipment[0] = new InventoryItem("T4_MAIN_SWORD") { Active = ["CLEAVE", "SWORD_SPIN", "MIGHTYBLOW"], Passive = ["PASSIVE_BLEEDCHANCE"] };
        c.Equipment[1] = null;
        c.Equipment[2] = new InventoryItem("T4_HEAD_PLATE_SET1");
        c.Equipment[3] = new InventoryItem("T4_ARMOR_PLATE_SET3");
        c.Equipment[4] = new InventoryItem("T4_SHOES_PLATE_SET1");
        c.Equipment[5] = new InventoryItem("T4_BAG") { Passive = ["PASSIVE_MAXLOAD"] };
        c.Equipment[6] = new InventoryItem("T4_CAPE");
        c.Equipment[7] = new InventoryItem("T8_MOUNT_DRAKE_FIRE") { Passive = ["PASSIVE_MAXLOAD_DRAKE", "MOUNTSPELL_GALLOP_FAKESPELL"] };
        c.Equipment[8] = new InventoryItem("T6_POTION_HEAL", 9);
        c.Equipment[9] = new InventoryItem("T8_MEAL_STEW", 9);
        c.Inventory =
        [
            new InventoryItem("T5_MOUNT_OX"), new InventoryItem("T6_MOUNT_DIREWOLF"), new InventoryItem("T6_MOUNT_FROSTRAM_ADC"),
            new InventoryItem("T5_MEAL_SOUP", 10), new InventoryItem("T3_MEAL_SOUP", 10), new InventoryItem("T1_MEAL_SOUP", 9), new InventoryItem("T7_MEAL_PIE", 10),
            new InventoryItem("T3_MEAL_OMELETTE", 10), new InventoryItem("T6_MEAL_STEW", 9), new InventoryItem("T4_MEAL_STEW", 10), new InventoryItem("T8_MEAL_SANDWICH", 10),
            new InventoryItem("T4_MEAL_SANDWICH", 10), new InventoryItem("T3_POTION_STONESKIN", 9), new InventoryItem("T4_POTION_HEAL", 10), new InventoryItem("T2_POTION_HEAL", 9),
            new InventoryItem("T6_POTION_ENERGY", 10), new InventoryItem("T4_POTION_ENERGY", 10), new InventoryItem("T2_POTION_ENERGY", 10), new InventoryItem("T5_POTION_REVIVE", 10),
            new InventoryItem("T7_POTION_STONESKIN", 10), new InventoryItem("T5_POTION_STONESKIN", 10), new InventoryItem("T6_POTION_COOLDOWN", 10), new InventoryItem("T5_POTION_ACID", 10),
            new InventoryItem("T6_POTION_BERSERK", 10), new InventoryItem("T3_SHOES_PLATE_SET1"),
        ];
    }

    /// <summary>
    /// stageJ carry-load SPEC §2: the client weighs the bag only (535.44 kg; the equipment weighs 0) and counts the equipped drake's
    /// PASSIVE_MAXLOAD_DRAKE in every mount state (50 + bag 75.57 + drake 1127.41 = 1252.98 kg): 42.73 %, shown 43. SWORD_SPIN's dash
    /// (IfOwnLoadPercent less 150) runs; with the bag overloaded it does not.
    /// </summary>
    private static void CarryLoad()
    {
        using var arena = new Arena();
        var world = arena.World;
        var c = StarterKit.Default.NewCharacter("Loaded");
        ReportedKit(c);
        c.Cluster = "0204";
        (c.PositionX, c.PositionY) = (arena.Stand.X, arena.Stand.Y);
        c.Progression = new Progression();
        Check(arena.Characters.Create(c), "character Loaded created");
        var (session, peer, me) = Rejoin(arena, c.Id);
        double load = 0, max = 0, percent = 0;
        void Read() { lock (world.Sync) (load, max, percent) = (Passives.Load(me), Passives.MaxLoad(me), Passives.LoadPercent(me)); }
        Read();
        Check(Math.Abs(load - 535.44) < 0.05 && Math.Abs(max - 1252.98) < 0.5 && Math.Round(percent) == 43,
              $"on foot: bag 535.44 kg / 50 + 75.57 + 1127.41 = 1252.98 kg -> 43 % as the client's bar (got {load:0.##} / {max:0.##} = {percent:0.##} %)");
        Ride(session, me);
        arena.Run(3.2);
        Read();
        Check(me.IsMounted && Math.Round(percent) == 43, $"mounted on the drake: still 43 % (its halfmountedbuff has no load; got {percent:0.##} %)");
        Off(session, me);
        Read();
        Check(me.Mount.Object is not null && Math.Round(percent) == 43, $"half-mounted beside the drake: 43 % (got {percent:0.##} %)");

        var spin = Client.Spells!.Template("SWORD_SPIN")!;
        bool Dashes()
        {
            var mark = peer.Events.Count;
            lock (world.Sync)
            {
                var now = world.GameNow;
                SpellCasts.Execute(new SpellCasts.SpellContext(me, world.Maps.Find("0204")!, spin, 1, 100, null, SpellMovement.PositionOf(me, now) + new Vector2(6, 0), now), spin);
            }
            var dashed = Since(peer, mark).Any(e => e.Code == EventCodes.ForcedMovement && L(e.P, 0) == me.Id);
            arena.Run(1);
            return dashed;
        }
        Check(Dashes(), "SWORD_SPIN at 43 %: the dash runs (ForcedMovement to the caster)");
        lock (world.Sync) me.Inventory.Bag[3]!.Count = 999; // 999 T5 soups
        Read();
        Check(percent > 150 && !Dashes(), $"overloaded ({percent:0} %): IfOwnLoadPercent less 150 fails, no dash");
    }

    /// <summary>
    /// stageJ carry-load SPEC §3.1 (stageD mounts Q2 [infer]): the ox's object stands while its dismounted owner stays within its
    /// halfmountrange (18 m), with the halfmounted buff 1253 and its 1901 kg; walked back to it the remount is the quick one. Beyond 18 m
    /// it despawns: the Leave to the owner and the observer, then the owner's list without 1253; the load bonus ends and the next mount,
    /// key 3 or not, is a full one that casts 1253 again.
    /// </summary>
    private static void ObjectDespawn()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = Join(arena, "Walker");
        var (_, watcher, _) = Join(arena, "Bystander", ox: false);
        var item = Mounts.ItemOf(me)!;
        Check(item.Template.Mount!.HalfMountRange == 18, $"T5_MOUNT_OX halfmountrange 18 (got {item.Template.Mount.HalfMountRange})");
        double Max() { lock (world.Sync) return Passives.MaxLoad(me); }
        // The Move's position runs on the wall clock, the ticks on the manual one: the walk ends with a stop there, then the ticks.
        void Walk(Vector2 from, Vector2 to, double seconds)
        {
            Send(session, new MoveRequest { wf = GameTime.Now, v6 = [from.X, from.Y], v8 = [to.X, to.Y], v9 = 5.5f });
            Stop(session, to);
            arena.Run(seconds);
        }
        Ride(session, me);
        arena.Run(3.2);
        Off(session, me);
        const short half = 1253; // T5_MOUNT_OX_HALFMOUNTED
        var withHalf = Max();
        Check(me.Mount.Object is not null && me.Effects.Find(half, me.Id) is not null && withHalf > 1900, $"dismounted: the object stands, 1253 on, max load {withHalf:0} kg");

        // Within 18 m: the object stays; back at it the remount is the 0.5 s one.
        var stand = arena.Stand;
        var mark = peer.Events.Count;
        Walk(stand, stand + new Vector2(10, 0), 2.2);
        Check(me.Mount.Object is not null && !Of(peer, mark).Any(e => e.Code == EventCodes.Leave) && Math.Abs(Max() - withHalf) < 1e-6, "at 10 m: the object and 1253 stay");
        Walk(stand + new Vector2(10, 0), stand, 2.2);
        mark = peer.Events.Count;
        Ride(session, me, remount: true);
        arena.Run(0.6);
        Check(Of(peer, mark).FirstOrDefault(e => e.Code == EventCodes.Mounted) is (EventCodes.Mounted, var quick) && quick.ContainsKey(8) && me.IsMounted,
              "back within remountdistance: the quick remount, Mounted {8} at 0.5 s");

        // Beyond 18 m: the Leave to both, then the list without 1253.
        Off(session, me);
        var objectId = me.Mount.Object!.Id;
        var wmark = watcher.Events.Count;
        mark = peer.Events.Count;
        Walk(stand, stand + new Vector2(25, 0), 5);
        var own = Of(peer, mark);
        var leave = own.FindIndex(e => e.Code == EventCodes.Leave && L(e.P, 0) == objectId);
        var list = own.FindIndex(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id && !(e.P.GetValueOrDefault((byte)1) is short[] ids && ids.Contains(half)));
        Check(me.Mount.Object is null && leave >= 0 && list > leave && me.Effects.Find(half, me.Id) is null && Max() < withHalf - 1800,
              $"at 25 m: Leave of the object, then the list without 1253, max load {Max():0} kg (got {Show(own)})");
        Check(Of(watcher, wmark).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == objectId), "the observer gets the object's Leave");

        // Back at the old spot: no object, key 3 is a full 3 s mount with 1253 cast again.
        Walk(stand + new Vector2(25, 0), stand, 5);
        mark = peer.Events.Count;
        Ride(session, me, remount: true);
        arena.Run(0.6);
        Check(!Of(peer, mark).Any(e => e.Code == EventCodes.Mounted), "key 3 without the object: no Mounted at 0.6 s");
        arena.Run(2.6);
        var full = Of(peer, mark);
        Check(full.FirstOrDefault(e => e.Code == EventCodes.Mounted) is (EventCodes.Mounted, var m) && m.ContainsKey(9) && !m.ContainsKey(8)
              && full.Any(e => e.Code == EventCodes.CastSpell && L(e.P, 3) == half),
              $"a full mount at 3 s: Mounted {{9}}, CastSpell 1253 again (got {Show(full)})");
    }
}
