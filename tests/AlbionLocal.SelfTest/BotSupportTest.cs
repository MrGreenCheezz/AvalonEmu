using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Bot support roles (Bots:Support:*, Bots:Patrol:Makeups; analysis/work/official-audit/audit-bots.md #7): a patrol healer heals a hurt
/// member of its group and never a hostile player or a bot of another group, a tank turns on its healer's attacker and taunts him, the
/// makeups are parsed and drawn by weight and handed out member by member, the switch off spawns today's dps, and a healer patrol in a
/// player's party (Bots:Party) heals him with the companion code. Thetford (0000) under a yellow override, a manual clock.
/// </summary>
public static class BotSupportTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bot support: a patrol healer (holy or nature staff, heals in its kit) heals a hurt member of its group (HealthUpdate caused by it)", HealsMember),
        ("bot support: in a fight a healer heals its mate, never the hurt hostile player (a heal at him refused NotFriend) nor targets a bot of another group; Pvp:FriendlySpells Group keeps the patrol group", NeverHostile),
        ("bot support: a tank (mace, hammer or shield; a taunt in its kit) taunts at once (its taunt the first thing it casts) and turns on whoever hits its healer", TankTaunts),
        ("bot support: Bots:Patrol:Makeups parsed per zone (bad entries refused), drawn by weight, handed out member by member; the presets' keys", Makeups),
        ("bot support: Bots:Support:Enabled false: a healer asked for is a dps of Gear.Weapons, no makeup, no heal of a mate", SwitchOff),
        ("bot support: a healer patrol that accepted a party invite (Bots:Party) heals the hurt inviter as his companion", PartyHealer),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly ClusterPvp Yellow = new(PvpRule.PvpAllowed, PlayersAreLethal: false, WarningType: "yellow");

    /// <summary>Fast bots that cast at once, dwelling at their stop, starting no fight of their own (they answer).</summary>
    private static BotOptions Quick => new()
    {
        Enabled = true, ThinkSeconds = (0.3, 0.3), CrimesAgainstBots = false, JoinGraceSeconds = 0, RouteDwellSeconds = (300, 300), RouteEndDwellSeconds = (300, 300),
        GearDropChance = 0, DeathGearDropChance = 0, SpellChance = 1, ReactionMs = 0, PvpEnabled = false,
    };

    private static BotSettings Plain => BotTest.NoPopulation with { PatrolExtra = [], GathererStartCarry = (0, 0), GathererWeaponShare = 0 };

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);

    private static Player Patrol(Field f, Vector2 at, PatrolPart? part, BotGroup? group = null)
    {
        lock (f.World.Sync)
        {
            var bot = f.World.Bots.Spawn(f.Map, BotRole.Patrol, 5, f.Nav().Grid.Nearest(at, 6) ?? at, 0, group: group, part: part)
                ?? throw new InvalidOperationException("no patrol spawned");
            if (bot.IsMounted) Mounts.Dismount(bot);
            return bot;
        }
    }

    private static void Hurt(Field f, Player p, double share)
    {
        lock (f.World.Sync) Knockdown.DamageSelf(p, (float)(p.Health.Max * share));
    }

    private static void Hit(Field f, Player attacker, Player target, double amount)
    {
        lock (f.World.Sync) Pvp.Damage(attacker, target, amount, EffectType.Physical, HitOrigin.Direct, -1, f.World.GameNow);
    }

    /// <summary>The positive HealthUpdates of <paramref name="target"/> caused by <paramref name="by"/> since <paramref name="mark"/>.</summary>
    private static int HealsOf(FakePeer peer, Player target, Player by, int mark) =>
        Of(peer, EventCodes.HealthUpdate, mark).Count(p => L(p, 0) == target.Id && L(p, 6) == by.Id && Convert.ToSingle(p[2]) > 0);

    private static List<SpellTemplate> Kit(Player bot) =>
        [.. Enumerable.Range(0, 5).Select(i => bot.Inventory.Equipment[i]).OfType<Item>().SelectMany(i => i.Active).Where(id => id >= 0).Select(id => Client.Spells!.Template(id)).OfType<SpellTemplate>()];

    private static string Weapon(Player bot) => bot.Inventory.Equipment[0]?.Template.Name ?? "-";

    private static (Player Healer, Player Mate) Pair(Field f, Vector2 at)
    {
        var healer = Patrol(f, at, PatrolPart.Healer);
        var mate = Patrol(f, at + new Vector2(3, 0), PatrolPart.Dps, healer.Bot!.Group);
        Check(healer.Bot.Part == PatrolPart.Healer && Weapon(healer) is var w && (w.Contains("HOLYSTAFF") || w.Contains("NATURESTAFF")) && Kit(healer).Any(Companions.Heals)
              && mate.Bot!.Group == healer.Bot.Group && mate.Bot.Part == PatrolPart.Dps,
              $"a healer with {Weapon(healer)} ({string.Join(", ", Kit(healer).Select(s => s.Name))}) and its dps mate of one group");
        return (healer, mate);
    }

    private static void HealsMember()
    {
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, _) = f.Join("Mira", at + new Vector2(6, 0));
        var (healer, mate) = Pair(f, at);
        Hurt(f, mate, 0.6);
        var mark = peer.Events.Count;
        f.Run(5);
        var casts = Of(peer, EventCodes.CastStart, mark).Where(p => L(p, 0) == healer.Id).Select(p => Client.Spells!.Name(Convert.ToInt32(p[5]))).ToList();
        Check(HealsOf(peer, mate, healer, mark) > 0, $"its hurt mate healed by it ({HealsOf(peer, mate, healer, mark)} HealthUpdates; casts {string.Join(", ", casts)}; {healer.Bot!.Brain.State})");
    }

    private static void NeverHostile()
    {
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, red) = f.Join("Rurik", at + new Vector2(5, 0), Pvp.Red);
        var (healer, mate) = Pair(f, at);
        var stranger = Patrol(f, at + new Vector2(-20, 0), PatrolPart.Dps); // a group of its own, beyond the reach of an area heal around its mate
        Hurt(f, red, 0.6);
        Hurt(f, mate, 0.6);
        Hurt(f, stranger, 0.6);
        var mark = peer.Events.Count;
        Hit(f, red, healer, 1); // the fight: the healer and its mate answer him
        f.Run(5);
        Check(healer.Bot!.Fight.Active && HealsOf(peer, mate, healer, mark) > 0 && HealsOf(peer, red, healer, mark) == 0 && HealsOf(peer, stranger, healer, mark) == 0,
              $"fighting him, it healed its mate ({HealsOf(peer, mate, healer, mark)}), never him ({HealsOf(peer, red, healer, mark)}) nor targets the other group's bot ({HealsOf(peer, stranger, healer, mark)})");
        var heal = Client.Spells!.Template("GENEROUSHEAL")!; // target="friendall"
        lock (f.World.Sync)
        {
            var (cast, why) = SpellCasts.Check(healer, [heal.Id], 0, heal.Id, red.Id, healer.PositionAt(f.World.Bots.Time), 1, f.World.GameNow);
            Check(cast is null && why == CastRefusal.NotFriend && SpellTargets.Friend(healer, mate, f.World.GameNow) && !SpellTargets.Friend(healer, red, f.World.GameNow),
                  $"a heal at the hostile player refused {why}; its mate a Friend, he not");
        }
        using var g = new Field(Quick, Plain, Yellow, config: c => c with { PvpFriendlySpells = FriendlySpells.Group });
        var at2 = g.Nav().Exits[0].Arrival;
        g.Join("Sven", at2 + new Vector2(6, 0));
        var (h2, m2) = Pair(g, at2);
        var s2 = Patrol(g, at2 + new Vector2(-3, 0), PatrolPart.Dps);
        Check(SpellTargets.Friend(h2, m2, g.World.GameNow) && !SpellTargets.Friend(h2, s2, g.World.GameNow),
              "Pvp:FriendlySpells Group: its group mate a Friend, a bot of another group not");
    }

    private static void TankTaunts()
    {
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, first) = f.Join("Ari", at + new Vector2(4, 0), Pvp.Red);
        var (_, _, second) = f.Join("Bodo", at + new Vector2(-4, 2), Pvp.Red);
        var tank = Patrol(f, at, PatrolPart.Tank);
        var healer = Patrol(f, at + new Vector2(2, 0), PatrolPart.Healer, tank.Bot!.Group);
        var main = Weapon(tank);
        var off = tank.Inventory.Equipment[1]?.Template.Name ?? "";
        Check(tank.Bot.Part == PatrolPart.Tank && (main.Contains("MACE") || main.Contains("HAMMER") || off.Contains("SHIELD")) && Kit(tank).Any(Companions.Taunts),
              $"a tank with {main} {off} and a taunt in its kit ({string.Join(", ", Kit(tank).Select(s => s.Name))})");
        var mark = peer.Events.Count;
        Hit(f, first, tank, 1);
        f.Run(1);
        var before = tank.Bot.Fight.TargetId;
        Hit(f, second, healer, 50);
        f.Run(0.5);
        var turned = tank.Bot.Fight.TargetId;
        f.Run(4);
        var casts = Of(peer, EventCodes.CastStart, mark).Where(p => L(p, 0) == tank.Id).Select(p => Client.Spells!.Template(Convert.ToInt32(p[5]))).OfType<SpellTemplate>().ToList();
        Check(before == first.Id && turned == second.Id && casts.Count > 0 && Companions.Taunts(casts[0]),
              $"it fought the first attacker ({before == first.Id}), turned on the healer's ({turned == second.Id}) and taunted ({string.Join(", ", casts.Select(s => s.Name))})");
    }

    private static void Makeups()
    {
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, "{}");
        var c = LoadTestConfig(file, ["--Bots:Patrol:Makeups:Red=[\"tank,dps,healer=3\",\"dps\"]", "--Bots:Patrol:Makeups=healer,dps=2;dps=0"]).Bots;
        var red = c.MakeupsOf("Red");
        Check(red is [{ Parts: [PatrolPart.Tank, PatrolPart.Dps, PatrolPart.Healer], Weight: 3 }, { Parts: [PatrolPart.Dps], Weight: 1 }]
              && c.MakeupsOf("Yellow") is [{ Parts: [PatrolPart.Healer, PatrolPart.Dps], Weight: 2 }, { Weight: 0 }] && c.MakeupsOf("Black").Count == 3,
              "Red's list from the file's JSON array, Default (Yellow) from a ';' list, Black keeps the preset's");
        static bool Refused(string file, string arg)
        {
            try { LoadTestConfig(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(Refused(file, "--Bots:Patrol:Makeups:Grey=dps") && Refused(file, "--Bots:Patrol:Makeups:Red=dps,mage") && Refused(file, "--Bots:Patrol:Makeups=dps=-1")
              && Refused(file, "--Bots:Support:HealBelow=2"), "an unknown zone, part or weight and a HealBelow above 1 refused");
        var random = new Random(7);
        var picks = Enumerable.Range(0, 20000).Select(_ => PatrolMakeup.Pick(red, random)).ToList();
        var share = picks.Count(p => p == red[0]) / 20000.0;
        Check(Math.Abs(share - 0.75) < 0.02 && PatrolMakeup.Pick(c.MakeupsOf("Yellow"), random) == c.MakeupsOf("Yellow")[0],
              $"drawn by weight: 3:1 gives {share:0.###}; weight 0 never");
        var official = LoadTestConfig(file, ["--Preset=official"]).Bots;
        Check(official is { Enabled: false, SupportEnabled: true, SupportHealBelow: 0.7, SupportShieldBelow: 0.9 } && ServerConfig.PveDefaults.Bots is { Enabled: true, SupportEnabled: true }
              && official.MakeupsOf("Safe").Select(m => m.Weight).SequenceEqual([90.0, 7, 3]) && official.MakeupsOf("Red").Select(m => m.Weight).SequenceEqual([80.0, 12, 8])
              && official.MakeupsOf("Safe")[0].Parts is [PatrolPart.Dps],
              "official: off through Bots:Enabled false (Support true under it); pve on; Default 90 dps / 7 healer / 3 tank+healer, red and black 80 / 12 / 8");

        // A group's makeup handed out member by member, the members beyond it dps.
        using var f = new Field(Quick, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        f.Join("Olga", at + new Vector2(6, 0));
        var lead = Patrol(f, at, null);
        var group = lead.Bot!.Group!;
        Check(lead.Bot.Part == PatrolPart.Dps, "a GM's patrol without a makeup: a dps");
        group.Makeup = [PatrolPart.Healer, PatrolPart.Tank];
        var second = Patrol(f, at + new Vector2(2, 0), null, group);
        var third = Patrol(f, at + new Vector2(-2, 0), null, group);
        var fourth = Patrol(f, at + new Vector2(0, 2), null, group);
        Check(second.Bot!.Part == PatrolPart.Healer && third.Bot!.Part == PatrolPart.Tank && fourth.Bot!.Part == PatrolPart.Dps,
              $"makeup healer,tank over a dps leader: {second.Bot!.Part}, {third.Bot!.Part}, {fourth.Bot!.Part}");
    }

    private static void SwitchOff()
    {
        using var f = new Field(Quick with { SupportEnabled = false }, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, _) = f.Join("Mira", at + new Vector2(6, 0));
        var bot = Patrol(f, at, PatrolPart.Healer);
        var group = bot.Bot!.Group!;
        group.Makeup = [PatrolPart.Healer, PatrolPart.Healer];
        var mate = Patrol(f, at + new Vector2(3, 0), null, group);
        Check(bot.Bot.Part == PatrolPart.Dps && mate.Bot!.Part == PatrolPart.Dps && Plain.Weapons.Any(w => Weapon(bot).EndsWith(w, StringComparison.Ordinal)),
              $"both dps ({bot.Bot.Part}, {mate.Bot!.Part}), a weapon of Gear.Weapons ({Weapon(bot)})");
        Hurt(f, mate, 0.6);
        var mark = peer.Events.Count;
        f.Run(4);
        Check(HealsOf(peer, mate, bot, mark) == 0, "no heal of its mate");
    }

    private static void PartyHealer()
    {
        using var f = new Field(Quick with { PartyAcceptChance = 1, PartyTierRange = 8 }, Plain, Yellow);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, me) = f.Join("Lena", at);
        var healer = Patrol(f, at + new Vector2(3, 0), PatrolPart.Healer);
        short rc;
        lock (f.World.Sync) rc = f.World.Parties.Invite(me, healer.Character.Id, null, out _);
        f.Run(7);
        Check(rc == 0 && healer.IsCompanion && healer.Bot!.Companion is { Kind: CompanionKind.Healer, Recruited: true } && f.World.Parties.Of(me.Character.Id) is { Members.Count: 2 },
              $"the healer patrol accepted (RC {rc}): his companion of kind {healer.Bot?.Companion?.Kind}");
        Hurt(f, me, 0.6);
        var mark = peer.Events.Count;
        f.Run(6);
        Check(HealsOf(peer, me, healer, mark) > 0, $"it healed him ({HealsOf(peer, me, healer, mark)} HealthUpdates caused by it; {healer.Bot!.Brain.State})");
    }
}
