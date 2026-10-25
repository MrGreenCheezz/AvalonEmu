using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.8): what a bot and a player are to each other, the one branch of
/// <see cref="Pvp.Of(Player, Player)"/> for a pair with a bot (a = the one who would attack). Bots never fight bots, and a city bot is
/// Friendly to everyone in every zone. Otherwise by Bots:PvpRules: Official = the client's zone table both ways (bots have no party or
/// guild), Server = Pvp:Mode's relation both ways, Consent (the default) = a player may attack a bot as the zone table allows (attacking it
/// is the player's consent) while a bot starts a fight only where Pvp:Mode makes the pair Hostile (under the pve preset Red against Red),
/// and answers whoever the table lets it fight for Bots:Pvp:EngageSeconds (20) after that one's last hit on it or its patrol group
/// (<see cref="Engaged"/>). Bots:PvpRulesByZone sets the rule per zone (pvpwarningtype; none by default).
/// With Pvp:Mode Off nothing is Hostile but under Official. Bots:Pvp:Enabled false: a world bot is Hostile only to whoever it answers.
/// </summary>
public static class BotPvp
{
    public static Relation Of(Player a, Player b)
    {
        if (a.IsBot && b.IsBot) return Relation.Friendly;
        // StageN companions: a hired companion is never in a PvP fight, either way (it fights the mobs of its party only).
        if (a.IsCompanion || b.IsCompanion) return Relation.Friendly;
        if ((a.Bot ?? b.Bot)!.Role == BotRole.City) return Relation.Friendly;
        // StageL corrupted (SPEC §4.5): an invader is Hostile to the owner of the run it invades, both ways, while the invasion lasts;
        // Neutral to everyone else (a GM watching), whatever the zone or the flags.
        if ((a.Bot ?? b.Bot)!.Role == BotRole.Invader)
            return a.World.Corrupted.Opponents(a, b) ? Relation.Hostile : Relation.Neutral;
        // StageL hellgates (SPEC §5.2): a hellgate's enemy bot is Hostile to the instance's party, both ways, during objective 2 (choosing the
        // versus mode is the consent); Neutral to everyone else.
        if ((a.Bot ?? b.Bot)!.Role == BotRole.Hellgate)
            return a.World.Hellgates.Opponents(a, b) ? Relation.Hostile : Relation.Neutral;
        // Stage N arena (SPEC §4.3): an arena bot is Hostile to the other team's players during a fight; Neutral to everyone else.
        if ((a.Bot ?? b.Bot)!.Role == BotRole.Arena)
            return a.World.Arenas.Opponents(a.IsBot ? a : b, a.IsBot ? b : a) ? Relation.Hostile : Relation.Neutral;
        var world = a.World;
        var rules = Pvp.RulesOf(world, a.Map);
        var mode = world.Config.PvpMode;
        // stageL roads (SPEC §5.6): a road's zone class (Roads) before its warning type (black)
        var rule = world.Config.Bots.PvpRulesIn(rules.WarningType, a.Map?.Template?.Info.Type.StartsWith("TUNNEL_", StringComparison.Ordinal) == true ? "Roads" : null);
        if (mode == PvpMode.Off && rule != BotPvpRules.Official) return Relation.Neutral;
        var official = Pvp.Of(a.Flag, b.Flag, party: false, guild: false, rules);
        var server = Pvp.ModeOf(a.Flag, b.Flag, party: false, guild: false, rules, mode);
        var relation = rule switch
        {
            BotPvpRules.Official => official,
            BotPvpRules.Server => server,
            _ when !a.IsBot => official,
            _ => server == Relation.Hostile || (official == Relation.Hostile && Engaged(a, b)) ? Relation.Hostile : Relation.Neutral,
        };
        // Bots:Pvp:Enabled off: a world bot starts no fight; it still answers whoever hit it (or its group) within Bots:Pvp:EngageSeconds.
        if (a.IsBot && relation == Relation.Hostile && !world.Config.Bots.PvpEnabled && !Engaged(a, b)) return Relation.Neutral;
        return relation;
    }

    /// <summary>
    /// Whether a spell effect of <paramref name="bot"/> may reach human <paramref name="p"/>, on top of the server's PvP rules (stageJ
    /// pvp-spells SPEC §2.9: <see cref="Systems.SpellTargets.Of"/>), by the rules of its target choice (<see cref="BotCombat"/>): never a
    /// duelist; its target or one it answers (<see cref="Engaged"/>); for a patrol also one it would attack on sight: not spared
    /// (Bots:Patrol:SpareMinutes after a knockdown, a target given up at the leash), riding only with Bots:Patrol:AttackMounted, on the map
    /// for Bots:Patrol:JoinGraceSeconds. A gatherer reaches only the ones it answers.
    /// </summary>
    public static bool MayHit(Player bot, Player p)
    {
        if (bot.Bot is not { } info || p.IsBot || bot.World.Duels.OpponentOf(p) is not null) return false;
        if (info.Fight.TargetId == p.Id || Engaged(bot, p)) return true;
        if (info.Role != BotRole.Patrol || p.Map is not { } map || !bot.World.Config.Bots.PvpEnabled) return false; // Bots:Pvp:Enabled
        var world = bot.World;
        return !(info.Fight.Spared.TryGetValue(p.Character.Id, out var until) && until > world.GameNow)
            && (!p.IsMounted || world.Config.Bots.AttackMounted) && world.Bots.Settled(map, p);
    }

    /// <summary>
    /// Whether <paramref name="p"/> hit <paramref name="bot"/> or (a patrol) any member of its group, the defeated ones included
    /// (<see cref="BotGroup.Engaged"/>), within Bots:Pvp:EngageSeconds, or (Bots:Pvp:EngageParty) one of its party did: the bot may answer it.
    /// </summary>
    public static bool Engaged(Player bot, Player p)
    {
        if (bot.Bot is not { } info) return false;
        var options = bot.World.Config.Bots;
        var since = bot.World.GameNow - (long)(options.EngageSeconds * TimeSpan.TicksPerSecond);
        foreach (var (id, at) in info.Group?.Engaged ?? info.Fight.Engaged)
        {
            if (at < since) continue;
            if (id == p.Id) return true;
            if (options.EngageParty && bot.Map?.FindPlayer(id) is { IsBot: false } attacker && Pvp.SameParty(attacker, p)) return true;
        }
        return false;
    }
}
