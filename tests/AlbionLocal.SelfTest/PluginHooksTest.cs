using System.Numerics;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The plugin API v2 hooks (server/plugins/README.md "Hooks"), light: a refused cast and a changed hit, a scripted spell with an entity data
/// bag and timer, the mount hooks. In-process plugins on the combat arena (T4 axe, a ghoul at 1.5 m).
/// </summary>
public static class PluginHooksTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("plugins v2: Spells.OnCheckCast(\"RENDINGSTRIKE\") refuses the cast (CastStart rc + the reason line, no cast), OnDamageEarly(\"AXESMASH\") × 1.5 on its HealthUpdate", CancelAndModify),
        ("plugins v2: a scripted AXESMASH (ReplaceEffects): OnCast + OnHit, Game.ApplyBuff puts a 3 s buff on the caster, no spells.xml damage; a data bag and a 2-shot entity timer", ScriptedSpell),
        ("plugins v2: Items.OnEquip at login; Mounts.OnCheckMount refuses once (Mount rc), OnMounted fires and its GallopBuff = null takes the gallop away", MountHooks),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static (Arena Arena, WorldSession Session, FakePeer Peer, Player Me, Mob Ghoul) Fight()
    {
        var arena = new Arena();
        arena.Setup = c => c.Equipment[3] = new InventoryItem("T4_ARMOR_PLATE_SET3");
        var (session, peer, me) = arena.Join("Hooked", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        arena.Setup = null;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        return (arena, session, peer, me, ghoul);
    }

    private sealed class SpellRules : PluginBase
    {
        public double Seen;
        public int Cancels;
        public override string Name => "spell-rules";

        protected override void OnLoad()
        {
            Hooks.Spells.OnCheckCast("RENDINGSTRIKE", a => a.Cancel("Rending Strike is forbidden here"));
            Hooks.Spells.OnCancel(_ => Cancels++);
            Hooks.Spells.OnDamageEarly("AXESMASH", a =>
            {
                if (a.IsHeal) return;
                Seen = a.Amount.Value;
                a.Amount.Set(a.Amount.Value * 1.5);
            });
        }
    }

    private static void CancelAndModify()
    {
        var (arena, session, peer, me, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var rules = new SpellRules();
        Check(world.Plugins.Load(rules) is null, "the in-process plugin loads");
        var spells = Client.Spells!;
        short rending = spells.IdOf("RENDINGSTRIKE"), axesmash = spells.IdOf("AXESMASH");

        var (responses, mark) = (peer.Responses.Count, peer.Events.Count);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = rending, d1 = ghoul.Id, d4 = 1 });
        var lines = Since(peer, mark).Where(e => e.Code == EventCodes.SystemMessage).Select(e => e.P[0] as string).ToList();
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == WorldSession.CastRefused && me.Combat.Cast is null
              && lines.Any(l => l?.Contains("forbidden", StringComparison.Ordinal) == true) && rules.Cancels == 0,
              $"RENDINGSTRIKE refused by the plugin: the CastRefused response, the reason as a system line, nothing under way (lines: {string.Join(" | ", lines)})");

        mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 2 });
        arena.Run(0.5);
        var hit = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id && Convert.ToInt16(e.P[7]) == axesmash).P;
        var expected = (float)Math.Round(rules.Seen * 1.5);
        Check(rules.Seen > 150 && hit is not null && F(hit, 2) == -expected && Math.Abs(ghoul.Health.At(world.GameNow) - (before - expected)) < 0.5,
              $"AXESMASH's hit {rules.Seen:0.#} × 1.5 = {expected} in the HealthUpdate and on the ghoul (got {(hit is null ? "none" : F(hit, 2))})");
        world.Plugins.Shutdown();
    }

    private sealed class Counter
    {
        public int N;
    }

    private sealed class Scripted : PluginBase
    {
        public int Casts, Hits, Applied, TimerRuns;
        public string? Target;
        public override string Name => "scripted";

        protected override void OnLoad()
        {
            Hooks.Spells.Register("AXESMASH", new SpellScript
            {
                OnCast = c =>
                {
                    Casts++;
                    c.Caster.GetData("casts", () => new Counter()).N++;
                    c.Caster.AddTimer(TimeSpan.FromMilliseconds(500), 2, _ => TimerRuns++, "echo");
                },
                OnHit = c =>
                {
                    Hits++;
                    Target = c.Target?.Name;
                    c.Game.ApplyBuff(c.Caster, "AXESMASH_EFFECT", c.Caster, seconds: 3);
                },
            });
            Hooks.Buffs.OnApply("AXESMASH_EFFECT", a => Applied++);
        }
    }

    private static void ScriptedSpell()
    {
        var (arena, session, peer, me, ghoul) = Fight();
        using var _ = arena;
        var world = arena.World;
        var script = new Scripted();
        Check(world.Plugins.Load(script) is null, "the in-process plugin loads");
        var axesmash = Client.Spells!.IdOf("AXESMASH");
        var mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 1 });
        arena.Run(0.5);
        var events = Since(peer, mark);
        var list = events.LastOrDefault(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id).P;
        Check(script.Casts == 1 && script.Hits == 1 && script.Applied == 1 && script.Target == ghoul.Template.Name
              && events.Any(e => e.Code == EventCodes.CastFinished) && ghoul.Health.At(world.GameNow) == before
              && !events.Any(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id),
              $"OnCast and OnHit ran once at the ghoul, CastFinished still went out, the spells.xml damage did not (casts {script.Casts}, hits {script.Hits}, applied {script.Applied}, target {script.Target})");
        Check(list is not null && list[8] is int[] durations && durations.Length > 0 && durations.All(d => d == 3000)
              && me.Effects.Spells.Any(s => s.Spell.Name == "AXESMASH_EFFECT" && s.Caster == me.Id),
              $"ApplyBuff: AXESMASH_EFFECT on the caster with its durations 3000 ms in the list (got {(list is null ? "no list" : Show([(EventCodes.ActiveSpellEffectsUpdate, list)]))})");
        arena.Run(1.6);
        Check(script.TimerRuns == 2, $"the entity timer ran its 2 shots (ran {script.TimerRuns})");
        arena.Run(4);
        Check(!me.Effects.Spells.Any(s => s.Spell.Name == "AXESMASH_EFFECT" && s.Start < world.GameNow - SpellCasts.Seconds(4)), "the 3 s buff has run out");
        world.Plugins.Shutdown();
    }

    private sealed class MountRules : PluginBase
    {
        public int Refusals, Mounted, LoginEquips;
        public string? Ridden;
        public override string Name => "mount-rules";

        protected override void OnLoad()
        {
            Hooks.Items.OnEquip("T5_MOUNT_OX", a =>
            {
                if (a.AtLogin && a.Slot == Mounts.MountSlot) LoginEquips++;
            });
            Hooks.Mounts.OnCheckMount(a =>
            {
                if (Refusals++ == 0) a.Cancel("Not yet");
            });
            Hooks.Mounts.OnMounted("T5_MOUNT_OX", a =>
            {
                Mounted++;
                Ridden = a.Mount;
                a.GallopBuff.Set(null);
            });
        }
    }

    private static void MountHooks()
    {
        using var arena = new Arena();
        var world = arena.World;
        var rules = new MountRules();
        Check(world.Plugins.Load(rules) is null, "the in-process plugin loads");
        arena.Setup = c => c.Equipment[Mounts.MountSlot] = new InventoryItem("T5_MOUNT_OX");
        var (session, peer, me) = arena.Join("Rider", new InventoryItem("T4_MAIN_SWORD"));
        arena.Setup = null;
        Check(rules.LoginEquips == 1, $"Items.OnEquip(\"T5_MOUNT_OX\") ran at the login for slot 7 (got {rules.LoginEquips})");
        var responses = peer.Responses.Count;
        Send(session, new MountRequest { wf = world.GameNow, v3 = Mounts.ItemOf(me)!.Id, v4 = true });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == Mounts.Refused && me.Mount.State == MountState.None,
              "the first mount is refused by the plugin: the Mount refusal code, no mount bar");
        Send(session, new MountRequest { wf = world.GameNow, v3 = Mounts.ItemOf(me)!.Id, v4 = true });
        arena.Run(3.1);
        Check(me.IsMounted && rules.Mounted == 1 && rules.Ridden == "T5_MOUNT_OX" && me.Mount.Speed is null,
              $"the second mounts: OnMounted for the ox, and its GallopBuff null leaves no gallop (mounted {me.IsMounted}, hooks {rules.Mounted}, speed {me.Mount.Speed?.Spell.Name ?? "none"})");
        world.Plugins.Shutdown();
    }
}
