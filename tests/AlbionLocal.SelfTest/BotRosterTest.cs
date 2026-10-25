using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Bot-roster (audit-bots.md improvements 5 and 8; <see cref="BotRoster"/>, Bots:Schedule, Bots:Gear realism): Thetford's population drawn
/// from the roster of a seed (two worlds: the same names with the same Guids and gear); the hour's curve scales the counts and sends the
/// surplus away one by one; mixed-tier, enchanted and artifact pieces within their settings; an identity that left (a party kick, a turnover)
/// is "not online" to a whisper and comes back later with its Guid and gear.
/// </summary>
public static class BotRosterTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bot roster: the same seed gives two worlds the same roster; Thetford's bots are roster names, a name in both worlds has the same Guid and gear", SameSeed),
        ("bot schedule: a flat 0.5 halves Thetford's bots; Points interpolate; a falling hour sends the surplus away one by one and the slots stay empty", Schedule),
        ("bot gear: MixedShare 1 puts every piece one tier off (tools, bags, mounts not), EnchantChance 1 enchants them .1-.3, ArtifactChance 1 gives artifacts; the presets' keys", Gear),
        ("bot roster: a party bot kicked is 'not online' to a whisper and its identity free; after turnovers it comes back with its Guid and gear", Returns),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static string GearOf(Player bot) =>
        string.Join(",", bot.Inventory.Equipment.Items.Where(i => i is not null && i.Template.Slot is not ("potion" or "food")).Select(i => $"{i!.Template.Name}/{i.Quality}").Order());

    /// <summary>Thetford's bots after 40 s by name (its Guid and gear), the rolls shifted by <paramref name="skip"/> draws; only identities of a royal city's tiers (T4-T8: they wear their own tier).</summary>
    private static Dictionary<string, (Guid Id, string Gear)> Population(int skip, BotOptions options)
    {
        using var f = new Field(options);
        var at = f.Nav().Exits[0].Arrival;
        lock (f.World.Sync)
            for (var i = 0; i < skip; i++) f.World.Bots.Random.Next(); // another order of the rolls: other identities picked first
        f.Join("Ruth", at);
        f.Run(40);
        var roster = f.World.Bots.Roster!;
        var names = roster.All.ToDictionary(i => i.Name, i => i);
        Check(f.Map.Bots.Count == 24 && f.Map.Bots.Values.All(b => names.TryGetValue(b.Character.Name, out var id) && id.Id == b.Character.Id && b.Bot!.Slot!.Identity == id.Index),
              $"Thetford: {f.Map.Bots.Count} bots, each a roster identity of its Guid held by its slot");
        return f.Map.Bots.Values.Where(b => names[b.Character.Name].Tier >= 4).ToDictionary(b => b.Character.Name, b => (b.Character.Id, GearOf(b)));
    }

    private static void SameSeed()
    {
        var a = BotRoster.Build(BotSettings.Default, 7, 300);
        var b = BotRoster.Build(BotSettings.Default, 7, 300);
        var c = BotRoster.Build(BotSettings.Default, 8, 300);
        Check(a.All.Count == 300 && a.All.SequenceEqual(b.All) && a.All.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 300
              && a.All.All(i => BotNames.Shape().IsMatch(i.Name) && i.Tier is >= 1 and <= 8) && !a.All.Select(i => i.Name).SequenceEqual(c.All.Select(i => i.Name))
              && a.All.Count(i => i.Kind == BotKind.City) is > 90 and < 150,
              $"Build(seed 7, 300) twice: equal, 300 unique names of the shape ({a.All.Count(i => i.Kind == BotKind.City)} city); seed 8 other names");
        var options = new BotOptions { Enabled = true, RosterSize = 100, RosterSeed = 7 };
        var first = Population(0, options);
        var second = Population(13, options);
        var common = first.Keys.Intersect(second.Keys).ToList();
        Check(common.Count >= 10 && common.All(n => first[n] == second[n]),
              $"two worlds of seed 7: {common.Count} T4+ names in both, each with the same Guid and gear ({string.Join("; ", common.Take(2).Select(n => $"{n} {first[n].Gear}"))})");
    }

    private static void Schedule()
    {
        // A flat half: 12 of Thetford's 24.
        using (var f = new Field(new BotOptions { Enabled = true, ScheduleHours = [.. Enumerable.Repeat(0.5, 24)] }))
        {
            f.Join("Ivo", f.Nav().Exits[0].Arrival);
            f.Run(40);
            Check(f.Map.Bots.Count == 12 && f.World.Bots.ScheduleFactor == 0.5, $"Schedule 24 × 0.5: {f.Map.Bots.Count} of 24 bots in Thetford");
        }
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, """{ "Bots": { "Schedule": { "Points": { "0": 0.2, "12": 1.0, "18": 1.6 }, "UtcOffsetHours": 3 } } }""");
        var c = LoadTestConfig(file, ["--Preset=pve"]).Bots;
        var noon = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc); // 12:00 local
        Check(Math.Abs(c.ScheduleHours[6] - 0.6) < 1e-9 && Math.Abs(c.ScheduleHours[15] - 1.3) < 1e-9 && Math.Abs(c.ScheduleHours[21] - 0.9) < 1e-9 && c.ScheduleHours[0] == 0.2
              && c.ScheduleAt(noon) == 1.0 && Math.Abs(c.ScheduleAt(noon.AddMinutes(30)) - 1.05) < 1e-9 && c.ScheduleUtcOffsetHours == 3,
              $"Points 0 0.2, 12 1.0, 18 1.6: hour 6 {c.ScheduleHours[6]:0.##}, 15 {c.ScheduleHours[15]:0.##}, 21 {c.ScheduleHours[21]:0.##}; at 12:30 local {c.ScheduleAt(noon.AddMinutes(30)):0.###}");
        static bool Refused(string file, string arg)
        {
            try { LoadTestConfig(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(LoadTestConfig(file, ["--Bots:Schedule:Points:12=1", "--Bots:Schedule:Points:0=1", "--Bots:Schedule:Points:18=1"]).Bots.ScheduleHours.All(h => h == 1)
              && Refused(file, "--Bots:Schedule:Hours=1,1") && Refused(file, "--Bots:Schedule:Points:24=1") && Refused(file, "--Bots:Gear:EnchantChance:Grey=0.1")
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), [$"--Bots:Schedule:Hours={string.Join(",", Enumerable.Repeat("0.5", 24))}"]).Bots.ScheduleHours.All(h => h == 0.5),
              "Points override the file's; 2 hours, hour 24 or an unknown zone refused; 24 comma values a curve");

        // A falling hour: full at 10:00 local (hours 0-11 1.0), a quarter from 12:00 (6 of 24); 2 h on, the surplus leaves one per pass.
        var hours = Enumerable.Range(0, 24).Select(h => h < 12 ? 1.0 : 0.25).ToArray();
        var utcNow = new DateTime(GameTime.Now, DateTimeKind.Utc);
        var offset = 10 - utcNow.TimeOfDay.TotalHours; // local 10:00 now
        using var g = new Field(new BotOptions { Enabled = true, ScheduleHours = hours, ScheduleUtcOffsetHours = Math.Round(offset, 6) is var o && o < -12 ? o + 24 : o > 14 ? o - 24 : o });
        g.Join("Ines", g.Nav().Exits[0].Arrival);
        g.Run(40);
        var before = g.Map.Bots.Count;
        g.TimeShift = TimeSpan.FromHours(2).Ticks;
        g.Run(11); // two passes
        var w = g.World;
        var leaving = g.Map.Bots.Values.Count(b => b.Bot!.StayUntil <= w.Clock.Now);
        var gone = before - g.Map.Bots.Count;
        Check(before == 24 && Math.Abs(w.Bots.ScheduleFactor - 0.25) < 0.01 && leaving + gone is >= 1 and <= 3,
              $"at 10:00 local {before} bots; at 12:00 the factor {w.Bots.ScheduleFactor:0.##}: after two passes {leaving} leaving and {gone} gone (one per pass)");
        g.Run(240);
        var slots = w.Bots.SlotsOf(g.Map);
        Check(g.Map.Bots.Count <= 8 && slots.Where(s => s.Role == BotRole.City && s.Index >= 6).All(s => s.Bot is null || s.Bot.Bot!.StayUntil <= w.Clock.Now),
              $"4 min later {g.Map.Bots.Count} bots (target 6, the last on their way out); no slot above the target refilled");
    }

    private static void Gear()
    {
        using var f = new Field(new BotOptions { Enabled = true }, BotTest.NoPopulation);
        var factory = f.World.Bots.Factory;
        var random = new Random(5);
        GearStyle Style(double mixed, double enchant, double artifact) => new(random, 1, mixed, enchant, (1, 3), artifact);
        var plain = new HashSet<string>();
        var (offTier, pieces, kept) = (0, 0, true);
        for (var i = 0; i < 40; i++)
        {
            foreach (var item in factory.Gear(BotRole.Patrol, 5, random, style: Style(1, 0, 0)))
            {
                if (item.Slot is "mount") kept &= item.Tier == 5;
                else
                {
                    pieces++;
                    if (item.Tier is 4 or 6) offTier++;
                }
                plain.Add(item.Name);
            }
            foreach (var item in factory.Gear(BotRole.Gatherer, 5, random, "WOOD", Style(1, 0, 0)))
                kept &= item.Tier == 5;
        }
        Check(pieces > 0 && offTier == pieces && kept, $"MixedShare 1, spread 1: {offTier} of {pieces} T5 patrol pieces T4 or T6; mounts and the gatherer's tool, set, bag and mount T5 ({kept})");
        var (enchanted, slots, bad) = (0, 0, 0);
        var arts = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            foreach (var item in factory.Gear(BotRole.Patrol, 6, random, style: Style(0, 1, 0)))
            {
                if (item.Slot is not ("mainhand" or "offhand" or "head" or "armor" or "shoes")) continue;
                slots++;
                if (item.Enchantment is >= 1 and <= 3) enchanted++;
                if (item.Tier != 6) bad++;
            }
            foreach (var item in factory.Gear(BotRole.Patrol, 6, random, style: Style(0, 0, 1)))
                if (f.World.BotSettings.ArtifactSets.Any(a => item.Name.EndsWith("_" + a, StringComparison.Ordinal)) && item.Slot is "head" or "armor" or "shoes"
                    || f.World.BotSettings.ArtifactWeapons.Values.Any(l => l.Any(a => item.Name.EndsWith(a, StringComparison.Ordinal))))
                    arts.Add(item.Name);
        }
        var old = factory.Gear(BotRole.Patrol, 6, new Random(9));
        var again = factory.Gear(BotRole.Patrol, 6, new Random(9));
        Check(slots > 0 && enchanted == slots && bad == 0 && arts.Count >= 40 && arts.All(a => a.StartsWith("T6_", StringComparison.Ordinal))
              && old.Select(i => i.Name).SequenceEqual(again.Select(i => i.Name)) && old.All(i => i.Enchantment == 0 && i.Tier == 6 || i.Slot == "mount"),
              $"EnchantChance 1: {enchanted} of {slots} weapon and armour pieces .1-.3; ArtifactChance 1: {arts.Count} artifacts ({string.Join(", ", arts.Distinct().Take(4))}); without a style one tier, no enchantment");
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, "{}");
        var official = LoadTestConfig(file, ["--Preset=official"]).Bots;
        Check(official is { Enabled: false, RosterEnabled: true, RosterSeed: 1, RosterSize: 1500, GearMixedShare: 0.25, GearTierSpread: 1, GearEnchant: (1, 3), ScheduleUtcOffsetHours: 0, ScheduleLeavePerPass: 1 }
              && official.ScheduleHours.All(h => h == 1) && official.EnchantChance["Black"] == 0.12 && official.ArtifactChance["Black"] == 0.06 && !official.ArtifactChance.ContainsKey("Safe")
              && ServerConfig.PveDefaults.Bots is { Enabled: true, RosterEnabled: true, GearMixedShare: 0.25 } && ServerConfig.PveDefaults.Bots.ScheduleHours.All(h => h == 1)
              && LoadTestConfig(file, ["--Bots:Gear:ArtifactChance:black=0.5"]).Bots.ArtifactChance is { } a && a["Black"] == 0.5 && a["Red"] == 0.02,
              "official: bots off, the roster, a flat schedule and the gear defaults under it; pve the same; a zone's chance set alone");
        var shipped = BotSettings.Load(Path.Combine(DataDir(), "..", "content", "bots.json"));
        Check(shipped.ArtifactSets.SequenceEqual(BotSettings.Default.ArtifactSets) && shipped.ArtifactWeapons.Count == BotSettings.Default.ArtifactWeapons.Count
              && shipped.ArtifactWeapons.All(w => BotSettings.Default.ArtifactWeapons[w.Key].SequenceEqual(w.Value))
              && shipped.ArtifactWeapons.Values.SelectMany(l => l).All(a => f.World.Items.Find($"T6_{a}") is { IsEquipment: true }),
              "content/bots.json Gear.ArtifactSets and ArtifactWeapons: the built-in ones, every artifact weapon in items.xml at T6");
    }

    private static void Returns()
    {
        // A roster of two city identities (T4-T8, both wearable in Thetford) and a few others: Thetford's one city slot alternates them.
        var seed = Enumerable.Range(1, 5000).First(s => BotRoster.Build(BotSettings.Default, s, 6).All.Where(i => i.Kind == BotKind.City).ToList() is { Count: 2 } c
            && c.All(i => i.Tier >= 4));
        var one = BotSettings.Default with { Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 1 } } };
        using var f = new Field(new BotOptions
        {
            Enabled = true, RosterSeed = seed, RosterSize = 6, PopulateSeconds = 0.5, StayMinutes = (0.1, 0.1), ArrivalSeconds = (1, 1), ThinkSeconds = (0.5, 0.5),
            ActiveRange = 10000, PartyAcceptChance = 1, PartyTierRange = 8, ReplyDelaySeconds = (0.5, 0.5),
        }, one);
        var w = f.World;
        var at = f.Nav().Exits[0].Arrival;
        var (_, _, me) = f.Join("Pia", at);
        f.Run(1);
        var first = f.Map.Bots.Values.Single();
        var identity = w.Bots.Roster!.All.Single(i => i.Name == first.Character.Name);
        var (gear, slot) = (GearOf(first), first.Bot!.Slot!);
        lock (w.Sync) w.Parties.Invite(me, first.Character.Id, null, out _); // by its stable Guid (Bots:Party:TierRange 8: a starter invites a T4+ bot)
        f.Run(2);
        var recruited = first.IsCompanion;
        var heldWhileOnline = !w.Bots.IdentityFree(identity) && slot.Identity != identity.Index;
        short rc;
        lock (w.Sync) w.Parties.Kick(me, first.Character.Id, null, out _);
        lock (w.Sync) rc = w.Chat.Whisper(me.Character.Id, me.Character.Name, AccountLevel.Player, identity.Name, "still there?");
        Check(recruited && heldWhileOnline && first.Map is null && rc == ChatHub.PlayerNotOnline && w.Bots.IdentityFree(identity),
              $"'{identity.Name}' joined the party (its slot let it go, the name online), kicked it is gone: a whisper {rc} (not online), the identity free");
        Player? back = null;
        var seen = new List<string>();
        for (var i = 0; i < 600 && back is null; i++)
        {
            f.Run(0.5);
            foreach (var b in f.Map.Bots.Values)
            {
                if (seen.Count == 0 || seen[^1] != b.Character.Name) seen.Add(b.Character.Name);
                if (b.Character.Name == identity.Name) back = b;
            }
        }
        Check(back is not null && back != first && back.Character.Id == identity.Id && GearOf(back) == gear && seen.All(n => w.Bots.Roster!.All.Any(i => i.Name == n)),
              $"after {seen.Count} arrivals ({string.Join(", ", seen)}) '{identity.Name}' is back with its Guid and gear ({gear})");
    }
}
