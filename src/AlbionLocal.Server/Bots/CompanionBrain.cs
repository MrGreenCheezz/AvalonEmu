using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// StageN companions (analysis/work/stageN/companions/SPEC.md §3): what a hired companion does every Bots:Combat:TickMs. Never while it is
/// knocked down, casts, channels or revives. A healer first revives a bleeding-out party member (the players' revive, <see cref="Revive.Start"/>:
/// it walks to within the spell's range and casts PLAYER_BLEEDOUT_REVIVE), then heals the party member (itself and the other companions
/// included) with the lowest share of health under Companions:HealBelow with a heal of its kit (<see cref="Companions.Heals"/>). Then the
/// fight: only mobs that already fight the party (one of its members on their threat list or as their target) within Companions:AssistRange
/// of the owner, so it never pulls a camp on its own; the owner's own enemies first (assist), a tank the mob that hits someone else
/// (it taunts: a threat spell of its kit, else a threat match with Companions:TankThreatFactor); a spell with Companions:SpellChance per
/// second, else its auto-attacks in weapon range, a ranged one keeping its distance; the walk on the mobs' walk grid
/// (<see cref="MobNav"/>). A companion Companions:LeashDistance from its owner drops the fight; out of a fight it walks to its place
/// behind him beyond Companions:FollowDistance (<see cref="Companions.Update"/> puts it there when it is left behind or stuck).
/// </summary>
public sealed class CompanionBrain(BotManager bots, Companions companions) : IBotBrain
{
    /// <summary>How far a walk's goal may move before a new path (m), and how often at most (s).</summary>
    private const float Replan = 2;

    private const double ReplanSeconds = 0.75;

    /// <summary>A tank pulls a mob off another member at most this often (s), its threat spells aside.</summary>
    private const double TauntSeconds = 6;

    private string _state = "follows";
    private Vector2? _goal;
    private TimeSpan _plannedAt;
    private long _spellRollAt;
    private byte _counter;

    public string State => _state;

    public void Attacked(Player bot, Player by) { }

    private World WorldOf { get; set; } = null!;

    private CompanionOptions Options => WorldOf.Config.Companions;

    public void Think(Player bot, TimeSpan now)
    {
        WorldOf = bot.World;
        var info = bot.Bot!;
        if (bot.Map is not { } map || info.Companion is not { } me) return;
        if (bot.Combat.Down is not null)
        {
            info.Motion.Reset();
            _state = "knocked down";
            return;
        }
        if (companions.OwnerOf(bot) is not { } owner || owner.Map != map)
        {
            _state = "waits for its owner";
            return;
        }
        var ticks = WorldOf.GameNow;
        if (bot.Combat.Cast is not null || bot.Combat.Channel is not null || bot.PvpState.Revive is not null || SpellEffects.Stunned(bot, ticks)) return;
        var here = bot.PositionAt(bots.Time);
        var ownerAt = owner.PositionAt(DateTime.UtcNow);
        var party = Party(owner, map);
        if (me.Kind == CompanionKind.Healer && (Revive(bot, party, here, ticks) || Heal(bot, party, here, ticks))) return;
        var far = Vector2.Distance(here, ownerAt) > Options.LeashDistance;
        var target = far ? null : Target(bot, owner, party, map, me.Kind, ownerAt);
        if (target is null)
        {
            info.Fight.TargetId = 0;
            Follow(bot, owner, map, here, ownerAt, me);
            return;
        }
        info.Fight.TargetId = target.Id;
        Fight(bot, target, party, map, here, ticks, me);
    }

    /// <summary>The party members on <paramref name="map"/> mobs may fight: the owner's party's players and companions (the owner alone without one).</summary>
    private List<Player> Party(Player owner, Map map)
    {
        var world = owner.World;
        if (world.Parties.Of(owner.Character.Id) is not { } party) return [owner];
        var ids = party.Members.Select(m => m.Id).ToHashSet();
        return [.. map.Fighters.Where(p => ids.Contains(p.Character.Id))];
    }

    // ---- the healer ----

    /// <summary>A bleeding-out party member: the revive (walk into range, then <see cref="Revive.Start"/>). True when it acts on one.</summary>
    private bool Revive(Player bot, List<Player> party, Vector2 here, long ticks)
    {
        var down = party.Where(p => p != bot && p.Combat.Down is { BleedOutPoints: not null })
            .MinBy(p => Vector2.DistanceSquared(p.PositionAt(Clock(p)), here));
        if (down is null) return false;
        var world = bot.World;
        var name = (world.GameData?.BleedOut ?? BleedOutSettings.Default).ReviveSpell;
        var spell = world.Spells?.Template(name);
        var range = spell?.CastRange ?? 1.5f;
        var at = down.PositionAt(Clock(down));
        if (Pvp.EdgeDistance(bot, here, down) > range)
        {
            _state = $"runs to revive '{down.Character.Name}'";
            WalkTo(bot, at, 0.8f, here);
            return true;
        }
        bot.Bot!.Motion.Stop(bot, MotionMaster.Facing(here, at), bots);
        _counter = (byte)(_counter % 255 + 1);
        if (Systems.Revive.Start(bot, spell?.Id ?? 0, down.Id, _counter, ticks) is { } why)
        {
            Log.Debug("bots", $"companion '{bot.Character.Name}' cannot revive '{down.Character.Name}': {why}");
            return false;
        }
        _state = $"revives '{down.Character.Name}'";
        return true;
    }

    /// <summary>The party member with the lowest health share under Companions:HealBelow: a heal of its kit at it. True when one started.</summary>
    private bool Heal(Player bot, List<Player> party, Vector2 here, long ticks)
    {
        var hurt = party.Where(p => p.Combat.Down is null && p.Health.Max > 0 && p.Health.At(ticks) < p.Health.Max * Options.HealBelow)
            .MinBy(p => p.Health.At(ticks) / p.Health.Max);
        if (hurt is null || bot.World.Spells is not { } spells) return false;
        var slots = bot.Session.SpellSlots(bot.Inventory);
        var at = hurt.PositionAt(Clock(hurt));
        var inRange = false;
        foreach (var slot in Enumerable.Range(0, 6))
        {
            var id = slots[slot];
            if (id < 0 || spells.Template(id) is not { Kind: "activespell" } spell || !Companions.Heals(spell)) continue;
            var ground = SpellTargets.Parse(spell.Target) == SpellTargets.Cls.Ground;
            if (Pvp.EdgeDistance(bot, here, hurt) > spell.CastRange) continue;
            inRange = true;
            if (Cast(bot, slots, slot, spell, hurt == bot ? 0 : ground ? 0 : hurt.Id, ground ? at : here, at, here, ticks))
            {
                _state = $"heals '{hurt.Character.Name}'";
                return true;
            }
        }
        if (inRange || hurt == bot) return false; // its heals wait for their cooldowns: it fights meanwhile
        _state = $"runs to heal '{hurt.Character.Name}'";
        WalkTo(bot, at, 8, here);
        return true;
    }

    // ---- the fight ----

    /// <summary>
    /// The mob to fight: one that fights the party now (a member on its threat list or its target), alive and attackable, within
    /// Companions:AssistRange of the owner; a tank's first the one that targets another member, everyone's next the owner's own enemies, the
    /// current one kept, then the nearest. Null when none.
    /// </summary>
    private Mob? Target(Player bot, Player owner, List<Player> party, Map map, CompanionKind kind, Vector2 ownerAt)
    {
        var ids = party.Select(p => p.Id).ToHashSet();
        var range = Options.AssistRange;
        var here = bot.PositionAt(bots.Time);
        var fighting = map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && !m.Removed && m.Attackable && m.Map == map
            && (ids.Contains(m.TargetId) || m.Threat.Keys.Any(p => ids.Contains(p.Id)))
            && Vector2.Distance(m.Position, ownerAt) <= range).ToList();
        if (fighting.Count == 0) return null;
        if (kind == CompanionKind.Tank && fighting.Where(m => m.TargetId != 0 && m.TargetId != bot.Id).MinBy(m => Vector2.DistanceSquared(m.Position, here)) is { } loose)
            return loose;
        var current = bot.Bot!.Fight.TargetId;
        if (fighting.FirstOrDefault(m => m.Id == current) is { } kept && owner.Combat.Enemies.Contains(kept) == owner.Combat.Enemies.Count > 0) return kept;
        return fighting.Where(owner.Combat.Enemies.Contains).MinBy(m => Vector2.DistanceSquared(m.Position, ownerAt))
            ?? fighting.MinBy(m => Vector2.DistanceSquared(m.Position, here));
    }

    private void Fight(Player bot, Mob mob, List<Player> party, Map map, Vector2 here, long ticks, CompanionInfo me)
    {
        var world = bot.World;
        var info = bot.Bot!;
        _state = $"fights {mob.Template.Name}";
        var distance = Combat.EdgeDistance(bot, here, mob);
        // A tank takes a mob that hits someone else: a threat spell of its kit, else a threat match (every TauntSeconds).
        if (me.Kind == CompanionKind.Tank && mob.TargetId != bot.Id && mob.TargetId != 0)
        {
            if (Spell(bot, mob, here, ticks, distance, Companions.Taunts, always: true)) return;
            if (ticks >= me.NextTaunt && distance <= 8 && mob.Threat.Count > 0)
            {
                var top = mob.Threat.Values.Max();
                mob.AI.AddThreat(bot, Math.Max(0, top * 1.25 - mob.Threat.GetValueOrDefault(bot)) + 1);
                me.NextTaunt = ticks + (long)(TauntSeconds * TimeSpan.TicksPerSecond);
                Log.Debug("bots", $"companion '{bot.Character.Name}' taunts {mob}");
            }
        }
        if (Spell(bot, mob, here, ticks, distance, null, always: false)) return;
        var weapon = Combat.WeaponOf(bot);
        var reach = (float)weapon.Range + SpellCasts.RangeTolerance;
        if (distance <= reach)
        {
            // A ranged companion steps back from a mob at its side that targets it, while it has room on the grid.
            if (me.Kind is CompanionKind.Ranged or CompanionKind.Healer && weapon.Range >= 6 && mob.TargetId == bot.Id && distance < 2.5f && KiteFrom(bot, mob, here))
                return;
            var facing = MotionMaster.Facing(here, mob.Position);
            if (info.Motion.Busy || bot.Movement is { Speed: > 0 }) info.Motion.Stop(bot, facing, bots);
            var last = bot.Combat.LastSwing;
            if (last != 0 && ticks < last + SpellCasts.SwingInterval(bot) || SpellEffects.CannotAttack(bot, ticks)) return;
            Knockdown.Acted(bot, "attacks");
            SpellCasts.Swing(bot, mob, 0, ticks);
            return;
        }
        // Into reach: a melee to its weapon's reach, a ranged one to most of its range (it keeps its distance).
        var stand = me.Kind is CompanionKind.Ranged or CompanionKind.Healer ? Math.Max(1, (float)weapon.Range - 1.5f) : Math.Max(0.5f, (float)weapon.Range - 0.3f);
        WalkTo(bot, mob.Position, stand + mob.Template.CollisionRadius + world.CollisionRadius, here);
    }

    /// <summary>
    /// A spell of its kit at <paramref name="mob"/>: with Companions:SpellChance per second of the fight (<paramref name="always"/>: at once),
    /// those that pass <paramref name="filter"/>: an enemy one at the mob, a ground one at its position in range, an attack around itself with
    /// the mob within 5 m; never a heal, buff or friendly one (a healer heals above). True when a cast started.
    /// </summary>
    private bool Spell(Player bot, Mob mob, Vector2 here, long ticks, float distance, Func<SpellTemplate, bool>? filter, bool always)
    {
        if (bot.World.Spells is not { } spells) return false;
        if (!always)
        {
            var elapsed = _spellRollAt == 0 ? 0.25 : Math.Min(1, (ticks - _spellRollAt) / (double)TimeSpan.TicksPerSecond);
            _spellRollAt = ticks;
            if (bots.Random.NextDouble() >= 1 - Math.Pow(1 - Math.Clamp(Options.SpellChance, 0, 1), elapsed)) return false;
        }
        var slots = bot.Session.SpellSlots(bot.Inventory);
        foreach (var slot in Enumerable.Range(0, 6).OrderBy(_ => bots.Random.Next()))
        {
            var id = slots[slot];
            if (id < 0 || spells.Template(id) is not { Kind: "activespell", UsableInCombat: true } spell) continue;
            if (filter is not null && !filter(spell)) continue;
            var cls = SpellTargets.Parse(spell.Target);
            if (cls == SpellTargets.Cls.Ground)
            {
                if (distance > spell.CastRange || Companions.Heals(spell)) continue;
                if (Cast(bot, slots, slot, spell, 0, mob.Position, mob.Position, here, ticks)) return true;
            }
            else if (SpellCasts.Enemy(cls) || cls is SpellTargets.Cls.All or SpellTargets.Cls.Other or SpellTargets.Cls.AllNotPassive or SpellTargets.Cls.AllUnmounted)
            {
                if (distance > spell.CastRange) continue;
                if (Cast(bot, slots, slot, spell, mob.Id, here, mob.Position, here, ticks)) return true;
            }
            else if (cls == SpellTargets.Cls.Self && distance <= BotCombat.SelfSpellReach && (spell.Category is "damage" or "crowdcontrol" or "debuff" || spell.UiType is "damage" or "crowdcontrol" || filter is not null))
            {
                if (Cast(bot, slots, slot, spell, 0, here, mob.Position, here, ticks)) return true;
            }
        }
        return false;
    }

    /// <summary>The players' cast checks and start (<see cref="SpellCasts.Check"/>, <see cref="SpellCasts.Start"/>), standing and facing <paramref name="face"/>.</summary>
    private bool Cast(Player bot, int[] slots, int slot, SpellTemplate spell, long targetId, Vector2 aim, Vector2 face, Vector2 here, long ticks)
    {
        _counter = (byte)(_counter % 255 + 1);
        var (cast, _) = SpellCasts.Check(bot, slots, (byte)slot, spell.Id, targetId, aim, _counter, ticks);
        if (cast is null) return false;
        bot.Bot!.Motion.Stop(bot, MotionMaster.Facing(here, face), bots);
        Knockdown.Acted(bot, "casts");
        SpellCasts.Start(bot, cast);
        Log.Debug("bots", $"companion '{bot.Character.Name}' casts {spell.Name}");
        return true;
    }

    /// <summary>A ranged companion steps 5 m away from <paramref name="mob"/> when the grid has a free line there. True when it walks.</summary>
    private bool KiteFrom(Player bot, Mob mob, Vector2 here)
    {
        var away = here - mob.Position;
        if (away.LengthSquared() < 0.01f) away = new Vector2(1, 0);
        var spot = here + Vector2.Normalize(away) * 5;
        if (MobNav.Grid(bot.World, bot.Map!) is { } grid && !grid.ClearFree(here, spot)) return false;
        bots.Walk(bot, [here, spot], MotionMaster.Facing(spot, mob.Position));
        _goal = spot;
        _plannedAt = bot.World.Clock.Now;
        return true;
    }

    // ---- walks ----

    /// <summary>Out of a fight: to its place behind the owner when beyond Companions:FollowDistance of him; else it stands.</summary>
    private void Follow(Player bot, Player owner, Map map, Vector2 here, Vector2 ownerAt, CompanionInfo me)
    {
        var info = bot.Bot!;
        if (Vector2.Distance(here, ownerAt) <= Options.FollowDistance)
        {
            if (info.Motion.Busy && _goal is { } g && Vector2.Distance(g, ownerAt) > Options.FollowDistance + 2) info.Motion.Stop(bot, owner.Facing, bots);
            _state = "follows";
            return;
        }
        _state = $"follows '{owner.Character.Name}'";
        var (spot, _) = companions.SpotNear(owner, map, me.Index);
        WalkTo(bot, spot, 0, here, owner.Facing);
    }

    /// <summary>
    /// A walk to <paramref name="short"/> m short of <paramref name="to"/>: straight when the mobs' grid has a free line (or there is no grid),
    /// else along a <see cref="MobNav"/> path (to the nearest reachable point); a new plan only when the goal moved <see cref="Replan"/> m or
    /// after <see cref="ReplanSeconds"/>.
    /// </summary>
    private void WalkTo(Player bot, Vector2 to, float @short, Vector2 here, float? facing = null)
    {
        var world = bot.World;
        var info = bot.Bot!;
        var line = here - to;
        var goal = line.LengthSquared() > @short * @short && @short > 0 ? to + Vector2.Normalize(line) * @short : @short > 0 ? here : to;
        if (Vector2.Distance(goal, here) < 0.3f) return;
        if (info.Motion.Busy && _goal is { } g && Vector2.Distance(g, goal) < Replan && (world.Clock.Now - _plannedAt).TotalSeconds < ReplanSeconds) return;
        IReadOnlyList<Vector2>? path = [here, goal];
        if (MobNav.Grid(world, bot.Map!) is { } grid && !grid.ClearFree(here, goal))
        {
            path = MobNav.Path(world, grid, here, goal, partial: true, out var overBudget);
            if (overBudget) return;
        }
        if (path is null || path.Count < 2) return;
        _goal = goal;
        _plannedAt = world.Clock.Now;
        bots.Walk(bot, path, facing ?? MotionMaster.Facing(path[^2], to));
    }

    /// <summary>The clock a unit's samples are on: a bot's the game time, a human's the UTC time.</summary>
    private DateTime Clock(Player p) => p.IsBot ? bots.Time : DateTime.UtcNow;
}
