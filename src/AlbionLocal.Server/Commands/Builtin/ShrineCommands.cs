using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Buff shrines for a GM (analysis/work/stageN/shrines/SPEC.md; the checker's RD-04): .shrine spawn, .shrine reset.</summary>
public static class ShrineCommands
{
    /// <summary>The tile a GM's shrine stands on (NewShrine key 4: the client builds its collider from it), the solo dungeon rooms' one.</summary>
    public const string Tile = "SHRINE_NON_COMBAT_BUFF";

    /// <summary>How far .shrine reset looks for a shrine (m).</summary>
    public const float ResetRange = 20;

    /// <summary>
    /// A buff shrine of buffshrines.xml <paramref name="name"/> (a plain buff shrine: <see cref="BuffShrineData.Definition.Supported"/>) 3 m in
    /// front of the player, Ready, that never respawns once spent (.shrine reset readies it again); NewShrine to the players in view.
    /// It lasts as long as the map.
    /// </summary>
    [Command("shrine spawn", Level = AccountLevel.GameMaster, Help = "puts a buff shrine of buffshrines.xml 3 m in front of you (ready, never respawns)")]
    public static void Spawn(CommandContext ctx, string name = "GENERAL_SHRINE_COMBAT_BUFF")
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { Grid: not null } map) throw new CommandError("the player is on no loaded map");
        if (world.Clusters is not { } store) throw new CommandError("this server runs without cluster data");
        var data = store.BuffShrines;
        var def = data.Find(name.ToUpperInvariant()) ?? throw new CommandError(
            $"{name} is not in buffshrines.xml (buff shrines: {string.Join(", ", data.All.Where(d => d.Supported).Select(d => d.Name).Take(8))}…)");
        if (!def.Supported) throw new CommandError($"{def.Name} is a {def.Type} shrine of mode {def.Mode}: only plain buff shrines run here");
        var at = player.PositionAt(DateTime.UtcNow);
        var (dx, dz) = TileMath.Rotate(0, 3, player.Facing);
        var spawn = new BuffShrineSpawn
        {
            Key = new SpawnKey(map.Cluster, "gmshrine", map.CustomSpawns++), Tile = Tile, Position = at + new Vector2((float)dx, (float)dz),
            Direction = player.Facing, Component = new XElement("shrine", new XAttribute("shrinetype", Tile)), ShrineType = Tile,
            Definition = def with { Respawns = false },
        };
        var shrine = world.Shrines.Place(map, spawn);
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"{def.Name} (object {shrine.Id}) at ({spawn.Position.X:0.#}, {spawn.Position.Y:0.#}): {def.UseSpell} -> {def.EffectSpell}, state {shrine.State}"));
    }

    /// <summary>The shrine nearest to the player within <see cref="ResetRange"/> m Ready again with full charges (UpdateShrine 397).</summary>
    [Command("shrine reset", Level = AccountLevel.GameMaster, Help = "makes the nearest buff shrine within 20 m ready again")]
    public static void Reset(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var at = player.PositionAt(DateTime.UtcNow);
        var shrine = ctx.World.Shrines.Of(map).Where(s => Vector2.Distance(s.Position, at) <= ResetRange).MinBy(s => Vector2.Distance(s.Position, at))
            ?? throw new CommandError($"no buff shrine within {ResetRange:0} m");
        ctx.World.Shrines.Reset(shrine);
        ctx.Reply($"{shrine.Definition.Name} (object {shrine.Id}) is ready again");
    }
}
