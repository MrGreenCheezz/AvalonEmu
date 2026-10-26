using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageP PvP details (analysis/work/stageP/league-pvp/SPEC.md §2, phases P1-P3): the death recap (op 492), healing sickness and
/// resilience, the spawn buffs and spell cooldowns on entering a cluster. Client data; one focus each.
/// </summary>
public static class PvpDetailsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("pvp details P1: a ghoul hits 3× in the 2 s window (one older hit dropped) and knocks down: op 492 lists the victim and the ghoul, bucket {-1, kind 1, ×3, sum}", Recap),
        ("pvp details P2: healing sickness: two healers give HEALINGSICKNESS_2 (heal × 0.647), a third the 3; it runs out after 8 s, the 10 s window too", Sickness),
        ("pvp details P2: resilience: the 2nd and 3rd distinct attacker hit × (1 − 0.143) and × (1 − 0.259); off with Pvp:Resilience false", Resilience),
        ("pvp details P3: arriving in a red zone gives SPAWN_INVULNERABILITY, SPAWN_SPEED, SPAWN_BUFFIMMUNITY and the 15 s spell cooldowns; an attack breaks the shield; off by the keys", ZoneEntry),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>
    /// DeathRecap:Seconds 2: a ghoul's hit, 3 s later three more, the third knocking the player down; the recap (frozen at the
    /// knockdown) has entry 0 the victim (CauserType 1) and entry 1 the ghoul as captured (template "", key 3 -1, flag 0x80, CauserType 3)
    /// with one auto-attack bucket {spell -1, kind 1 melee, count 3, the three HealthUpdates' sum, key 16 = 17 = the first of them}.
    /// </summary>
    private static void Recap()
    {
        using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true, DeathRecapSeconds = 2 });
        var world = arena.World;
        var (session, peer, me) = arena.Join("Victim", new InventoryItem("T4_MAIN_SWORD"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        void Hit()
        {
            lock (world.Sync) Combat.DamagePlayer(ghoul, me, 60, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        }
        Hit();
        arena.Run(3);
        lock (world.Sync) me.Health.Set(me.Health.Max, world.GameNow);
        var mark = peer.Events.Count;
        Hit();
        arena.Run(0.2);
        Hit();
        arena.Run(0.2);
        lock (world.Sync) me.Health.Set(1, world.GameNow);
        Hit();
        var hits = Since(peer, mark).Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 6) == ghoul.Id).Select(e => e.P).ToList();
        Check(me.Combat.Down is not null && hits.Count == 3, $"three hits, the last knocks down (got {hits.Count}, down {me.Combat.Down is not null})");
        var sum = hits.Sum(h => F(h, 2));
        var first = L(hits[0], 1);

        DeathRecapResponse r;
        lock (world.Sync) r = world.Recaps.Response(me);
        Check(r.apj is ["Victim", "T4_MOB_ROAMING_UNDEAD_GHOUL"] && r.apt is [DeathRecaps.CauserLocalPlayer, DeathRecaps.CauserMob]
              && r.apk is ["HUMAN_MALE" or "HUMAN_FEMALE", ""] && r.apl is [0, -1] && r.apo is [_, DeathRecaps.MobFlag] && r.app![1] == 0 && r.apv![1] == 0
              && r.apn![1].StartsWith("RING_MOB", StringComparison.Ordinal) && r.apm![1].Length > 0 && r.api > 0,
              $"the rows: the victim (1) then the ghoul as captured (got {string.Join(",", r.apj ?? [])} / {string.Join(",", r.apn ?? [])} / {string.Join(",", r.apm ?? [])})");
        Check(r.apw![1] is [-1] && r.ap0![1] is [DeathRecaps.KindMelee] && r.ap1![1] is [3] && Math.Abs(r.apx![1][0] - sum) < 0.01
              && r.apy![1] is [var at] && at == first && r.apz![1] is [var end] && end == first && r.apw[0].Length == 0,
              $"the ghoul's bucket: auto-attack, melee, 3 hits (the one 3 s earlier is outside the 2 s window), {sum} in all, first hit {first} (got {string.Join(",", r.ap1![1])} / {r.apx![1].FirstOrDefault()} / {r.apy![1].FirstOrDefault()})");

        Send(session, new DeathRecapRequest { je = 1 });
        Check(peer.Responses[^1].Parameters.GetValueOrDefault((byte)1) is string[] { Length: 2 } && peer.Responses[^1].ReturnCode == 0,
              "op 492 answers the same recap (2 causers)");
    }

    /// <summary>A yellow zone (PvpAllowed) without lethal hits.</summary>
    private static readonly AlbionLocal.Server.Data.ClusterPvp Yellow = new(AlbionLocal.Server.Data.PvpRule.PvpAllowed, PlayersAreLethal: false);

    /// <summary>Runs <paramref name="spell"/> as <paramref name="me"/>'s hit now on <paramref name="target"/> at ability power 200.</summary>
    private static void Hit(World world, Player me, string spell, IUnit target)
    {
        var t = Client.Spells!.Template(spell)!;
        lock (world.Sync)
            SpellCasts.Execute(new SpellCasts.SpellContext(me, me.Map!, t, 1, 200, target, SpellMovement.PositionOf(target, world.GameNow), world.GameNow), t);
    }

    /// <summary>
    /// HOLYTOUCH_EFFECT on B from A, then from C 1 s later: B gets HEALINGSICKNESS_2 before C's heal, which comes out × (1 − 0.353);
    /// D's heal next makes it HEALINGSICKNESS_3 (the 2 gone). 8 s later the sickness has run out; 11 s after the heals A alone heals in full.
    /// </summary>
    private static void Sickness()
    {
        using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true }, pvp: Yellow);
        var world = arena.World;
        var (_, _, a) = arena.Join("HealerA", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, c) = arena.Join("HealerC", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, d) = arena.Join("HealerD", new InventoryItem("T4_MAIN_SWORD"));
        var (_, bPeer, b) = arena.Join("Patient", new InventoryItem("T4_MAIN_SWORD"));
        float Heal(Player healer)
        {
            lock (world.Sync) b.Health.Set(1, Math.Max(world.GameNow, b.Health.Stamp));
            var mark = bPeer.Events.Count;
            Hit(world, healer, "HOLYTOUCH_EFFECT", b);
            return Since(bPeer, mark).Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == b.Id && L(e.P, 6) == healer.Id).Sum(e => F(e.P, 2));
        }
        bool Has(string name) => b.Effects.Spells.Any(s => s.Spell.Name == name && s.Live(world.GameNow));
        var full = Heal(a);
        arena.Run(1);
        var second = Heal(c);
        Check(full > 0 && !Has("HEALINGSICKNESS_3") && Has("HEALINGSICKNESS_2") && Math.Abs(second - Combat.Rounded(full * (1 - 0.353))) <= 1,
              $"two healers: HEALINGSICKNESS_2 and the second heal × 0.647 (got {full} then {second}, want {Combat.Rounded(full * (1 - 0.353))})");
        Heal(d);
        Check(Has("HEALINGSICKNESS_3") && !Has("HEALINGSICKNESS_2"), "a third healer: HEALINGSICKNESS_3 replaces the 2");
        arena.Run(8.5);
        Check(!b.Effects.Spells.Any(s => s.Spell.Category == PvpDetails.SicknessCategory && s.Live(world.GameNow)), "8 s later the sickness has run out");
        arena.Run(2.5);
        var again = Heal(a);
        Check(Math.Abs(again - full) <= 1 && !Has("HEALINGSICKNESS_2"), $"11 s after the heals one healer heals in full again, no sickness (got {again}, want {full})");
    }

    /// <summary>
    /// Red players in a yellow zone: A, C and D (swords, melee) hit Red B in turn with 100 each: A's hit whole, C's × (1 − 0.143), D's
    /// × (1 − 0.259) (PlayerFocusFire rows 2 and 3, meleemodifier); with Pvp:Resilience false D's hit is whole.
    /// </summary>
    private static void Resilience()
    {
        foreach (var on in new[] { true, false })
        {
            using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true, PvpResilience = on }, pvp: Yellow);
            arena.Setup = ch => ch.Flag = Pvp.Red;
            var world = arena.World;
            var (_, _, a) = arena.Join("AttackerA", new InventoryItem("T4_MAIN_SWORD"));
            var (_, _, c) = arena.Join("AttackerC", new InventoryItem("T4_MAIN_SWORD"));
            var (_, _, d) = arena.Join("AttackerD", new InventoryItem("T4_MAIN_SWORD"));
            var (_, bPeer, b) = arena.Join("Target", new InventoryItem("T4_MAIN_SWORD"));
            float Strike(Player attacker)
            {
                var mark = bPeer.Events.Count;
                lock (world.Sync) Pvp.Damage(attacker, b, 100, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
                arena.Run(0.2);
                return -Since(bPeer, mark).Where(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == b.Id && L(e.P, 6) == attacker.Id).Sum(e => F(e.P, 2));
            }
            var (first, second, third) = (Strike(a), Strike(c), Strike(d));
            if (on)
                Check(first > 0 && Math.Abs(second - first * (1 - 0.143)) <= 1 && Math.Abs(third - first * (1 - 0.259)) <= 1,
                      $"resilience: 1, 2, 3 attackers → × 1, 0.857, 0.741 (got {first}, {second}, {third})");
            else
                Check(first > 0 && second == first && third == first, $"Pvp:Resilience false: every hit whole (got {first}, {second}, {third})");
        }
    }

    /// <summary>
    /// From 0204 (SAFEAREA) a GM's .tele to an OPENPVP_RED cluster and the arrival's Join: SPAWN_INVULNERABILITY (not the SHORT one: 0204
    /// is no dungeon), SPAWN_SPEED and SPAWN_BUFFIMMUNITY are on, the gear spells on the 15 s cooldown of spellcooldownsonenteringseconds
    /// (SpellCooldownUpdate); hitting a mob there breaks the invulnerability. With Pvp:SpawnBuffs and Pvp:EnterSpellCooldowns false: neither.
    /// </summary>
    private static void ZoneEntry()
    {
        var red = Client.Clusters!.World.Clusters.First(c => c.Type == "OPENPVP_RED").Id;
        foreach (var on in new[] { true, false })
        {
            using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true, PvpSpawnBuffs = on, PvpEnterSpellCooldowns = on });
            var world = arena.World;
            var (old, _, me) = arena.Join("Zoner", new InventoryItem("T4_MAIN_SWORD"));
            lock (world.Sync) world.Commands.Execute(new AlbionLocal.Server.Commands.CommandContext(world, me, AlbionLocal.Server.Persistence.AccountLevel.Admin, (_, _) => { }), $".tele {red}");
            for (var waited = 0; old.Status != AlbionLocal.Server.Network.SessionStatus.Transferring && waited < 30000; waited += 10)
            {
                Thread.Sleep(10);
                lock (world.Sync) world.Update();
            }
            var peer = new AlbionLocal.SelfTest.FakePeer();
            var session = new AlbionLocal.Server.Network.WorldSession(peer, world);
            lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = me.Character.Id }, 1), encrypted: false);
            var p = session.Player!;
            var now = world.GameNow;
            bool Has(string name) => p.Effects.Spells.Any(s => s.Spell.Name == name && s.Live(world.GameNow));
            var cooldowns = Since(peer, 0).Where(e => e.Code == EventCodes.SpellCooldownUpdate).Select(e => e.P).ToList();
            var ends = cooldowns.SelectMany(c => c.GetValueOrDefault((byte)1) is long[] e ? e : []).ToList();
            var cd = ends.Count > 0 ? (ends.Max() - now) / (double)TimeSpan.TicksPerSecond : 0;
            if (!on)
            {
                Check(p.Map?.Cluster == red && !Has("SPAWN_INVULNERABILITY") && !Has("SPAWN_SPEED") && cd <= 0,
                      $"Pvp:SpawnBuffs / EnterSpellCooldowns false: no spawn buff, no cooldown in {red} (cooldown {cd:0.#} s)");
                continue;
            }
            Check(p.Map?.Cluster == red && Has("SPAWN_INVULNERABILITY") && !Has("SPAWN_INVULNERABILITY_SHORT") && Has("SPAWN_SPEED") && Has("SPAWN_BUFFIMMUNITY")
                  && !Has("SPAWN_BUFFIMMUNITY_ONDEATH"),
                  $"arrived in {red}: SPAWN_INVULNERABILITY, SPAWN_SPEED, SPAWN_BUFFIMMUNITY (got {string.Join(", ", p.Effects.Spells.Select(x => x.Spell.Name))})");
            Check(Math.Abs(cd - 15) < 0.1 && cooldowns.Count > 0, $"the gear spells on the 15 s entering cooldown, SpellCooldownUpdate sent (got {cd:0.##} s in {ends.Count} slot(s))");
            var map = world.Maps.Find(red)!;
            var ghoul = SpawnManager.SpawnAt(map, Client.Clusters!.Mobs.Find("T4_MOB_ROAMING_UNDEAD_GHOUL")!, p.PositionAt(DateTime.UtcNow) + new Vector2(1.5f, 0), 0, world);
            lock (world.Sync) Combat.Damage(p, ghoul, 10, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
            Check(!Has("SPAWN_INVULNERABILITY"), "attacking a mob ends SPAWN_INVULNERABILITY (attackingbreakseffect)");
        }
    }
}
