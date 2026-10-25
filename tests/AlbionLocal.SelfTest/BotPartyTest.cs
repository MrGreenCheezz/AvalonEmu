using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Bot-party (Bots:Party:*; <see cref="BotSocial.WantsParty"/>, <see cref="Companions.Recruit"/>): a human in Thetford invites GM city bots of
/// his tier. One accepts after the delay, joins his party as his companion (a Chat.Join line) and follows him; a second one is declined by
/// Bots:Party:MaxPerParty; kicked, the first leaves and goes. With the party switch, a kind switch off or a tier too far: declined as before.
/// </summary>
public static class BotPartyTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bot party: an invite to a city bot of the inviter's tier is accepted after the delay (in his party as a companion, a Chat.Join line), it follows him; a second bot is declined (MaxPerParty 1); kicked it leaves and goes", JoinFollowKick),
        ("bot party: Bots:Party:Enabled off, Bots:Party:City off or a tier beyond Bots:Party:TierRange: declined as before, no party; the presets' keys", Declined),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);

    private static Player City(Field f, int tier, Vector2 at)
    {
        lock (f.World.Sync) return f.World.Bots.Spawn(f.Map, BotRole.City, tier, at, 0)!;
    }

    private static void JoinFollowKick()
    {
        using var f = new Field(new BotOptions { Enabled = true, ThinkSeconds = (60, 60), PartyAcceptChance = 1, PartyMaxPerParty = 1 }, BotTest.NoPopulation);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, me) = f.Join("Lena", at);
        var w = f.World;
        var tier = Companions.TierOf(me);
        var bot = City(f, tier, at + new Vector2(3, 0));
        var other = City(f, tier, at + new Vector2(-3, 0));

        // The invite: the ordinary flow, then after the delay the accept.
        var mark = peer.Events.Count;
        short rc;
        lock (w.Sync) rc = w.Parties.Invite(me, bot.Character.Id, null, out _);
        f.Run(1.5);
        var early = w.Parties.Of(bot.Character.Id) is not null;
        f.Run(5.5);
        var party = w.Parties.Of(me.Character.Id);
        var answers = Of(peer, EventCodes.PartyInvitationAnswer, mark);
        var says = Of(peer, EventCodes.ChatSay, mark).Where(p => L(p, 4) == bot.Id).ToList();
        Check(rc == 0 && !early && party is { Members.Count: 2 } && party.Leader == me.Character.Id && w.Parties.Of(bot.Character.Id) == party
              && Of(peer, EventCodes.PartyJoined, mark).Count == 1 && answers is [var a] && a.GetValueOrDefault((byte)1) is true,
              $"invite RC {rc}, nothing within 1.5 s ({early}), then accepted ({answers.Count} answer): a party of {party?.Members.Count} he leads, PartyJoined to him");
        Check(bot.IsCompanion && w.Companions.RecruitOf(bot.Character.Id) == bot && bot.Bot is { Slot: null, Companion: { Recruited: true } c } && c.Owner == me.Character.Id
              && says is [var say] && w.BotSettings.JoinLines.Contains((string)say[1]!),
              $"it plays as his companion ({bot.Bot?.Role}, {bot.Bot?.Companion?.Kind}) and says a Chat.Join line ({says.Count})");

        // It follows him 10 m away.
        lock (w.Sync)
        {
            var far = at + new Vector2(10, 0);
            if (MobNav.Grid(w, f.Map) is { } grid) far = grid.NearestFree(far, 5) ?? far;
            me.Teleport(far, 0, DateTime.UtcNow);
        }
        f.Run(8);
        var d = Vector2.Distance(bot.PositionAt(w.Bots.Time), me.PositionAt(DateTime.UtcNow));
        Check(bot.Map == f.Map && d <= w.Config.Companions.FollowDistance + 2.5, $"he moved 10 m: it follows ({d:0.0} m from him)");

        // A second bot: declined, the party has its one world bot.
        mark = peer.Events.Count;
        lock (w.Sync) rc = w.Parties.Invite(me, other.Character.Id, null, out _);
        f.Run(7);
        answers = Of(peer, EventCodes.PartyInvitationAnswer, mark);
        Check(rc == 0 && answers is [var b] && b.GetValueOrDefault((byte)1) is null or false && w.Parties.Of(other.Character.Id) is null && !other.IsCompanion
              && party?.Members.Count == 2, $"a second bot (Bots:Party:MaxPerParty 1): RC {rc}, declined ({answers.Count} answer), the party still of 2");

        // Kicked: out of the party (of one: disbanded) and gone.
        var count = w.Bots.Count;
        lock (w.Sync) rc = w.Parties.Kick(me, bot.Character.Id, null, out _);
        Check(rc == 0 && bot.Map is null && !f.Map.Bots.ContainsKey(bot.Id) && w.Companions.RecruitOf(bot.Character.Id) is null && w.Bots.Count == count - 1
              && w.Parties.Of(me.Character.Id) is null && w.Parties.Of(bot.Character.Id) is null,
              $"kicked (RC {rc}): it is gone ({w.Bots.Count} of {count} bots), the party disbanded");
    }

    private static void Declined()
    {
        BotOptions Opt(BotOptions o) => o with { Enabled = true, ThinkSeconds = (60, 60), PartyAcceptChance = 1 };
        foreach (var (what, options, offset) in new[]
                 {
                     ("Bots:Party:Enabled false", Opt(new BotOptions { PartyEnabled = false }), 0),
                     ("Bots:Party:City false", Opt(new BotOptions { PartyCity = false }), 0),
                     ("2 tiers off with Bots:Party:TierRange 1", Opt(new BotOptions { PartyTierRange = 1 }), 2),
                 })
        {
            using var f = new Field(options, BotTest.NoPopulation);
            var at = f.Nav().Exits[0].Arrival;
            var (_, peer, me) = f.Join("Nils", at);
            var w = f.World;
            var tier = Companions.TierOf(me);
            var bot = City(f, tier + offset <= 8 ? tier + offset : tier - offset, at + new Vector2(3, 0));
            var mark = peer.Events.Count;
            short rc;
            lock (w.Sync) rc = w.Parties.Invite(me, bot.Character.Id, null, out _);
            f.Run(7);
            var answers = Of(peer, EventCodes.PartyInvitationAnswer, mark);
            Check(rc == 0 && answers is [var a] && a.GetValueOrDefault((byte)1) is null or false && w.Parties.Of(me.Character.Id) is null && !bot.IsCompanion
                  && bot.Bot?.Role == BotRole.City, $"{what}: invite RC {rc}, declined ({answers.Count} answer), no party, still a city bot");
        }
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, "{}");
        var official = LoadTestConfig(file, ["--Preset=official"]);
        Check(official.Bots is { Enabled: false, PartyEnabled: true } && ServerConfig.PveDefaults.Bots is
              { Enabled: true, PartyEnabled: true, PartyAcceptChance: 0.7, PartyTierRange: 1, PartyMaxPerParty: 2, PartyMaxMinutes: 120 },
              "official: off through Bots:Enabled false (Bots:Party:Enabled true under it); pve: on with AcceptChance 0.7, TierRange 1, MaxPerParty 2, MaxMinutes 120");
    }
}
