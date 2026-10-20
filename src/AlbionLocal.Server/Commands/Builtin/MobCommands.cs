using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Mob commands (stage 5a/5d, analysis/work/stage5/SPEC.md §6): .mob spawn, .mob kill, .mob respawnscale, .mob ai.</summary>
public static class MobCommands
{
    /// <summary>Most mobs one .mob spawn makes.</summary>
    public const int MaxCount = 50;

    /// <summary>How far from the player .mob spawn puts its mobs (m), on a ring around it.</summary>
    public const float SpawnDistance = 3;

    /// <summary>Farthest <c>dist</c> of .mob spawn (m).</summary>
    public const float MaxDistance = 60;

    /// <summary>
    /// <paramref name="count"/> GM mobs of <paramref name="mob"/> (a mobs.xml uniquename, ignoring case). Without <paramref name="dist"/>: on a
    /// ring of <see cref="SpawnDistance"/> m around the player, facing it. With it (0-60 m, the checker's setups): <paramref name="dist"/> m in
    /// front of the player along its facing, more than one on a 2 m ring around that point, each on the nearest walkable cell of the
    /// cluster's walk grid within 6 m (the bots' grid; the point itself while it is being built or nothing is walkable there), facing
    /// the player. NewMob to everyone in view at once. They belong to no spawn point, so they never respawn.
    /// </summary>
    [Command("mob spawn", Level = AccountLevel.GameMaster,
        Help = "puts <count> mobs of a mobs.xml uniquename around the player, or [dist] m (0-60) in front of it on walkable ground (no respawn)")]
    public static void Spawn(CommandContext ctx, MobTemplate mob, int count = 1, float? dist = null)
    {
        var player = ctx.RequirePlayer();
        if (count is < 1 or > MaxCount) throw new CommandError($"count: {count} is not 1..{MaxCount}");
        if (dist is < 0 or > MaxDistance) throw new CommandError($"dist: {dist} is not 0..{MaxDistance}");
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var ids = new List<long>();
        if (dist is not { } d)
        {
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * i / count;
                var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * SpawnDistance;
                // Facing the player: the yaw of the way back (x = sin, z = cos, the convention of TileMath.Rotate).
                var facing = (float)(Math.Atan2(-offset.X, -offset.Y) * 180 / Math.PI);
                ids.Add(SpawnManager.SpawnAt(map, mob, at + offset, facing, ctx.World).Id);
            }
        }
        else
        {
            var (dx, dz) = TileMath.Rotate(0, d, player.Facing);
            var centre = at + new Vector2((float)dx, (float)dz);
            var nav = ctx.World.Bots.NavOf(map);
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * i / count;
                var spot = count == 1 ? centre : centre + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 2;
                if (nav?.Nearest(spot, 6) is { } walkable) spot = walkable;
                ids.Add(SpawnManager.SpawnAt(map, mob, spot, AI.MotionMaster.Facing(spot, at), ctx.World).Id);
            }
        }
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{mob.Name} (type {mob.Id}, T{mob.Tier}, {mob.HitPointsMax:0} hp): {count} spawned{(dist is { } m ? $" {m:0.#} m ahead" : "")}, object{(count == 1 ? "" : "s")} {string.Join(", ", ids)}"));
    }

    /// <summary>
    /// Studio phase 3: <paramref name="count"/> of a content pack's mob variant (mods/&lt;pack&gt;/server/mobs.json of a pack in Mods:Packs)
    /// around the player: its template mob with the variant's health, damage and fame factors and its loot lists. No respawn.
    /// </summary>
    [Command("mob variant", Level = AccountLevel.GameMaster, Help = "puts <count> of a content pack's mob variant (server/mobs.json) around the player")]
    public static void Variant(CommandContext ctx, string id, int count = 1)
    {
        var player = ctx.RequirePlayer();
        if (count is < 1 or > MaxCount) throw new CommandError($"count: {count} is not 1..{MaxCount}");
        var variants = ctx.World.ModDrops.Variants;
        if (!variants.TryGetValue(id, out var v))
            throw new CommandError(variants.Count == 0 ? "no mob variants: add a pack with server/mobs.json to Mods:Packs and restart"
                : $"unknown variant {id} (known: {string.Join(", ", variants.Keys.Take(20))})");
        if (ctx.World.Clusters?.MobsIfLoaded?.Find(v.Template) is not { } template) throw new CommandError($"{v.Id}: its template {v.Template} is not in mobs.xml");
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var angle = 2 * Math.PI * i / count;
            var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * SpawnDistance;
            var facing = (float)(Math.Atan2(-offset.X, -offset.Y) * 180 / Math.PI);
            ids.Add(SpawnManager.SpawnAt(map, template, at + offset, facing, ctx.World, healthFactor: v.HealthFactor, damageFactor: v.DamageFactor, fameFactor: v.FameFactor, variant: v.Id).Id);
        }
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{v.Id} ({v.NameEn ?? v.Template}, pack {v.Pack}: {template.Name} x{v.HealthFactor:0.##} health, x{v.DamageFactor:0.##} damage): {count} spawned, object{(count == 1 ? "" : "s")} {string.Join(", ", ids)}"));
    }

    /// <summary>
    /// Takes the mob nearest to the player in its view off the map (<paramref name="which"/> "all": every mob in view): a
    /// Leave to every client that knows it, and its spawn point respawns after its time (× the respawn scale). A despawn,
    /// not a kill: no corpse, no rewards, no loot (a kill is the combat's, <see cref="Systems.Combat"/>).
    /// </summary>
    [Command("mob kill", Level = AccountLevel.GameMaster, Help = "despawns the nearest mob in view (or all in view); its spawn point respawns it later")]
    public static void Kill(CommandContext ctx, string which = "nearest")
    {
        var player = ctx.RequirePlayer();
        var all = which.ToLowerInvariant() switch
        {
            "nearest" => false,
            "all" => true,
            _ => throw new CommandError($"which: '{which}' is not nearest or all"),
        };
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var inView = map.Grid!.Around(map.Grid.CellOf(at), ctx.World.Config.Visibility.EnterCells).OfType<Mob>()
            .OrderBy(m => Vector2.Distance(m.Position, at)).ToList();
        if (inView.Count == 0) throw new CommandError("no mob in view");
        var victims = all ? inView : inView.Take(1).ToList();
        foreach (var m in victims) SpawnManager.Despawn(m, ctx.World);
        var respawns = victims.Where(m => m.Slot?.RespawnAt is not null).Select(m => (m.Slot!.RespawnAt!.Value - ctx.World.Now).TotalSeconds).ToList();
        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        ctx.Reply(victims.Count == 1
            ? Inv($"{victims[0]} despawned{(respawns.Count == 1 ? Inv($", its spawn point respawns in {respawns[0]:0} s") : ", no respawn (a GM mob)")}")
            : Inv($"{victims.Count} mobs despawned{(respawns.Count > 0 ? Inv($", {respawns.Count} spawn points respawn in {respawns.Min():0}-{respawns.Max():0} s") : "")}"));
    }

    /// <summary>
    /// The factor on every mob respawn time, the old form of Rates:Respawn (= 1 / it; <see cref="World.MobRespawnScale"/>):
    /// shown, or set to <paramref name="scale"/> (0-100; 0.01 for quick tests). Kept beside .rates respawn.
    /// </summary>
    [Command("mob respawnscale", Level = AccountLevel.GameMaster, Help = "shows or sets the factor on mob respawn times (0.01 = a hundred times faster; = 1 / .rates respawn)")]
    public static void RespawnScale(CommandContext ctx, float scale = -1)
    {
        if (scale < 0)
        {
            ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"mob respawn scale {ctx.World.MobRespawnScale:0.####} (Rates:Respawn {ctx.World.Rates.Respawn:0.####})"));
            return;
        }
        if (scale > 100) throw new CommandError($"scale: {scale} is not 0..100");
        ctx.World.MobRespawnScale = scale;
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"mob respawn scale {scale:0.####} (for the respawns scheduled from now on)"));
    }

    /// <summary>
    /// The mob AI (stage 5d): <paramref name="mode"/> "on" / "off" switches it for the whole world (World:MobAi); without
    /// one, whether it is on and what the mob nearest to the player in view is doing (its AI, state, threat, target, walk,
    /// home and last reset).
    /// </summary>
    [Command("mob ai", Level = AccountLevel.GameMaster, Help = "on|off switches the mob AI; without an argument: the nearest mob's AI state")]
    public static void Ai(CommandContext ctx, string mode = "")
    {
        switch (mode.ToLowerInvariant())
        {
            case "on" or "off":
                ctx.World.MobAi = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
                ctx.Reply($"mob AI {(ctx.World.MobAi ? "on" : "off")}");
                return;
            case "":
                break;
            default:
                throw new CommandError($"mode: '{mode}' is not on or off");
        }
        var player = ctx.RequirePlayer();
        var map = MapOf(player);
        var at = player.PositionAt(DateTime.UtcNow);
        var mob = map.Grid!.Around(map.Grid.CellOf(at), ctx.World.Config.Visibility.EnterCells).OfType<Mob>().Where(m => !m.Dead)
            .MinBy(m => Vector2.Distance(m.Position, at)) ?? throw new CommandError("no living mob in view");
        ctx.Reply($"mob AI {(ctx.World.MobAi ? "on" : "off")}; {mob}: {mob.AI.Describe()}");
    }

    /// <summary>
    /// The mob spells (stageD mobspells): <paramref name="mode"/> "on" / "off" switches the &lt;AI&gt; rotations for the whole world
    /// (combat.json MobSpells; off: the mobs fight as in 5d, a cast under way still ends); without one, whether they are on.
    /// </summary>
    [Command("mob spells", Level = AccountLevel.GameMaster, Help = "on|off switches the mobs' <AI> spells; without an argument: whether they are on")]
    public static void Spells(CommandContext ctx, string mode = "")
    {
        switch (mode.ToLowerInvariant())
        {
            case "on" or "off":
                ctx.World.MobSpells = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
                break;
            case "":
                break;
            default:
                throw new CommandError($"mode: '{mode}' is not on or off");
        }
        ctx.Reply($"mob spells {(ctx.World.MobSpells ? "on" : "off")}");
    }

    private static Map MapOf(Player player) => player.Map is { Grid: not null } map ? map
        : throw new CommandError($"cluster {player.Map?.Cluster ?? player.Character.Cluster} has no world objects (yet)");
}
