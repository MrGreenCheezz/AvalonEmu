using AlbionLocal.Modules;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Modules;

// The dispatch of the mob scripting hooks and of the player, map and loot hooks that came with them (the game's systems call these; each is one check when nobody listens).
public sealed partial class ModuleHost
{
    /// <summary>
    /// Mobs.OnBeforeThink, before <paramref name="m"/>'s engine AI runs: true = no AI this update (cancelled, or a handler has ended the mob). The engine's AI also moves
    /// the walk on, so that part is done here for a cancelled one: a module's <see cref="IMob.MoveTo"/> arrives and is seen.
    /// </summary>
    internal bool BeforeThink(Mob m, Map map)
    {
        if (!Hooks.Think.Wants(m.Template.Name)) return false;
        var args = new BeforeThinkArgs(Wrap(m), World.Delta.TotalSeconds);
        Hooks.Think.Raise(m.Template.Name, args);
        if (m.Dead || m.Removed || m.Map != map) return true;
        if (!args.IsCancelled) return false;
        (m.AI as AI.AggressiveAI)?.HoldLeash(World); // the time it does not act for its module does not count against it when the AI takes over again
        m.Motion.Update(World.Combat.MobMoveResend);
        return true;
    }

    /// <summary>Mobs.OnBeforeAggro for the player an idle mob picked: true = it stays idle. A handler's actions on the mob wait for the next tick, as in a hit.</summary>
    internal bool BeforeAggro(Mob m, Player p)
    {
        if (!Hooks.BeforeAggro.Wants(m.Template.Name)) return false;
        var args = new BeforeAggroArgs(Wrap(m), Wrap(p));
        _combatHooks++;
        Hooks.BeforeAggro.Raise(m.Template.Name, args);
        _combatHooks--;
        return args.IsCancelled;
    }

    /// <summary>Mobs.OnAggro: the fight has started, often in the middle of the hit that started it, so a handler's actions wait for the next tick.</summary>
    internal void MobAggro(Mob m, Player target)
    {
        if (!Hooks.MobAggro.Wants(m.Template.Name)) return;
        // At the next tick: a fight also starts inside the AI's walk over the map's objects (a fellow's alert), where a handler's summon would break that walk.
        World.Post(() =>
        {
            if (!m.Dead && !m.Removed) Hooks.MobAggro.Raise(m.Template.Name, new MobAggroArgs(Wrap(m), Wrap(target)));
        });
    }

    /// <summary>A module's cast on a mob without an AI rotation has no AI to end it: its death or reset does it here (<see cref="MobFacade.EndOwnCast"/>).</summary>
    internal void EndModuleCast(Mob m, bool death)
    {
        if (_mobs.TryGetValue(m, out var facade)) facade.EndOwnCast(death);
    }

    /// <summary>Mobs.OnLeaveCombat, in the middle of the reset: a handler's actions wait for the next tick.</summary>
    internal void MobLeftCombat(Mob m, string reason)
    {
        EndModuleCast(m, death: false);
        if (!Hooks.MobLeftCombat.Wants(m.Template.Name)) return;
        _combatHooks++;
        Hooks.MobLeftCombat.Raise(m.Template.Name, new MobLeaveCombatArgs(Wrap(m), ModuleTypes.LeftCombat(reason), reason));
        _combatHooks--;
    }

    /// <summary>Mobs.OnBeforeRespawn for the mob made for <paramref name="slot"/>, not on the map yet: true = dropped, and the point waits for another respawn time.</summary>
    internal bool BeforeRespawn(Map map, MobSpawnSlot slot, Mob mob, TimeSpan now)
    {
        var name = mob.Template.Name;
        if (!Hooks.Respawn.Wants(name)) return false;
        var at = mob.Position;
        var args = new BeforeRespawnArgs(Types.Of(mob.Template), map.Cluster, at.X, at.Y);
        Hooks.Respawn.Raise(name, args);
        if (!args.IsCancelled) return false;
        slot.Mob = null;
        slot.RespawnAt = now + SpawnManager.RespawnDelay(map, slot, mob.Template, World);
        return true;
    }

    /// <summary>Players.OnBeforeCommand: true = the command does not run (the reason is its error).</summary>
    internal bool BeforeCommand(CommandContext c, string line)
    {
        if (!Hooks.BeforeCommand.Any || c.Player is { IsBot: true }) return false;
        var args = new BeforeCommandArgs(!c.Console && c.Player is { } p ? Wrap(p) : null, line, (PlayerLevel)(int)c.Level);
        Hooks.BeforeCommand.Raise(null, args);
        if (!args.IsCancelled) return false;
        c.Error(args.CancelReason is { Length: > 0 } why ? why : "a module refused that command");
        return true;
    }

    /// <summary>Players.OnFameGained: <paramref name="gain"/> in the server's ×10000 units.</summary>
    internal void FameGained(Player p, long gain, FameContext context, ItemTemplate? item, bool pvp)
    {
        if (Hooks.FameGained.Any && gain > 0 && !p.IsBot) Hooks.FameGained.Raise(null, new FameGainedArgs(Wrap(p), gain / 10000.0, ModuleTypes.Fame(context.Source.ToString()), Types.OfOrNull(item), pvp));
    }

    /// <summary>Players.OnSilverChanged: <paramref name="delta"/> in the server's ×10000 units, the change already made.</summary>
    internal void SilverChanged(Player p, long delta, string reason)
    {
        if (Hooks.SilverChanged.Any && delta != 0 && !p.IsBot) Hooks.SilverChanged.Raise(null, new SilverChangedArgs(Wrap(p), delta / 10000.0, p.Character.Silver / 10000.0, reason));
    }

    internal void MapLoaded(Map map)
    {
        if (Hooks.MapLoaded.Wants(map.Cluster)) Hooks.MapLoaded.Raise(map.Cluster, new MapArgs(Types.Zone(map), map.InstanceKey));
    }

    /// <summary>Maps.OnUnloaded; a map that never got its cluster's data was never told as loaded either.</summary>
    internal void MapUnloaded(Map map)
    {
        if (map.Template is not null && Hooks.MapUnloaded.Wants(map.Cluster)) Hooks.MapUnloaded.Raise(map.Cluster, new MapArgs(Types.Zone(map), map.InstanceKey));
    }

    /// <summary>Items.OnBeforeLootTake when <paramref name="from"/> is the loot container <paramref name="p"/> has open (a vault move comes through the same code): true = the item stays (the reason told to the player).</summary>
    internal bool BeforeLootTake(Player p, Container from, Item item, int count)
    {
        var name = item.Template.Name;
        if (count <= 0 || !Hooks.LootTake.Wants(name) || p.IsBot || p.OpenBag is not { } bag || bag.ItemsFor(p) != from) return false;
        var args = new BeforeLootTakeArgs(Wrap(p), Types.Info(item), count, ModuleTypes.Loot(bag.Kind));
        Hooks.LootTake.Raise(name, args);
        if (!args.IsCancelled) return false;
        if (args.CancelReason is { Length: > 0 } why) p.Session.Send(new SystemMessageEvent { d0p = why, d0q = 1 });
        return true;
    }

    /// <summary>Items.OnChestLootRolled: the loot as the handlers leave it (<paramref name="drops"/> itself when none listens).</summary>
    internal IReadOnlyList<(string Item, int Amount)> ChestLootRolled(Player p, LootChest chest, IReadOnlyList<(string Item, int Amount)> drops)
    {
        var name = chest.Template.Name;
        if (!Hooks.ChestLoot.Wants(name) || p.IsBot) return drops;
        var args = new ChestLootArgs(Wrap(p), name, chest.Map?.Cluster ?? "", Loot(drops));
        Hooks.ChestLoot.Raise(name, args);
        return Loot(args.Items);
    }
}
