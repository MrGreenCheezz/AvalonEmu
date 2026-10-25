using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>A player-bot as the victim of a PvP result (<see cref="PvpGoals.OnPvpKill"/>): its persona, name, flag, gear tier and object id at its defeat.</summary>
public sealed record BotVictim(string PersonaId, string Name, byte Flag, int Tier, long Id) : IPvpBot
{
    public static BotVictim Of(Player bot) => new(bot.Bot!.PersonaId, bot.Character.Name, bot.Flag, bot.Bot.Tier, bot.Id);
}

/// <summary>
/// What a defeated bot leaves (<see cref="BotRewards"/>): the items of its bag (moved out of its containers, or minted for a gatherer's
/// "gathered" resource and a patrol's extra), its silver, who may take them first and for how long (s), and its gear for the corpse's look.
/// </summary>
public sealed record BotDrop(List<Item> Items, int Silver, Guid Owner, int PartyId, Guid[] Looters, double ProtectionSeconds, int[] Look);

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.10, bots-combat SPEC §2.8): the rewards of a bot's defeat, all configurable.
/// <list type="bullet">
/// <item><b>Fame</b>: <see cref="Death.KillFame"/> (its gear's item value) × Bots:Rewards:FameFactor (0.5), shared as a player kill's
/// (<see cref="Death.Reward"/>: the killer's party within the sharing radius by damage, Rates:Fame × Rates:CombatFame, the season's PvP fame
/// through <see cref="Destiny.Award"/>); KilledPlayer only for a death, a knockdown gives UpdateFame alone.</item>
/// <item><b>PvP goals</b>: <see cref="PvpGoals.OnPvpKill"/> with the bot as the victim (<see cref="BotVictim"/>, its persona the anti-farming
/// key), the outcome what the client saw (a death in a lethal zone, else a knockdown); every rule of the bot victim is the goals'
/// (BotFactor, JournalCountsBots).</item>
/// <item><b>Silver</b>: content/bots.json Rewards.Silver by its tier × Rates:Silver, a pile of a player's kind (NewSilverObject key 18 = 3)
/// for the killer and its party.</item>
/// <item><b>Bag</b>: a player's loot bag (NewLoot type 1, the corpse wearing the bot's gear) with each worn combat item (mainhand, offhand,
/// head, armor, shoes, cape; not the bag, the mount or food) with Bots:Rewards:GearDropChance (0.2; a death in a lethal zone
/// Bots:Rewards:DeathGearDropChance, official 1.0, pve 0.5), at least Bots:Rewards:MinItems of them (pve 1), <b>moved</b> out of its equipment and
/// worn by AliveToDead's durability rule; a gatherer's "gathered" resources × Bots:Rewards:ResourceDropShare as one new stack per item, a
/// patrol's Rewards.PatrolExtra; each with a player's spell layout (<see cref="WorldSession.PlayerItem"/>: a bot's own gear has no
/// passives); the killer's party's for Bots:Rewards:LootProtectionSeconds (else gamedata's 120 s, 90 s in PvpForced), gone after
/// vanishtime. No bag when nothing dropped.</item>
/// <item><b>Farming caps</b> (the rewards only, the goals have theirs): one killer gets a bot persona's full reward once in
/// Bots:Rewards:SameBotMinutes (10), else <see cref="SameBotFameShare"/> of the fame and nothing else; and bags and silver piles of bots at
/// most Bots:Rewards:BagsPerHour (30) an hour, beyond it fame only. Kept in memory.</item>
/// </list>
/// A death's bag and silver lie there at once; a knockdown's at the despawn (<see cref="Place"/>, whatever despawns it but a map's
/// unload), so that no bag lies next to a "player" still on the ground. No duplicate is possible: a bot's items exist only from its spawn (and the one minted stack here), a defeat is
/// one-way and moves them, nothing of a bot is saved, and bags and silver live in memory (a crash loses them). Touched only under
/// <see cref="World.Sync"/>.
/// </summary>
public sealed class BotRewards(World world)
{
    /// <summary>The fame share of a bot persona its killer defeated again within Bots:Rewards:SameBotMinutes.</summary>
    public const double SameBotFameShare = 0.25;

    /// <summary>The worn items that may drop: mainhand, offhand, head, armor, shoes, cape (equipment slots).</summary>
    private static readonly int[] DropSlots = [0, 1, 2, 3, 4, 6];

    /// <summary>Killer character → GameTimeStamps of its bags and silver piles of bots in the last hour.</summary>
    private readonly Dictionary<Guid, List<long>> _drops = [];

    /// <summary>(killer character, bot persona) → GameTimeStamp of its last full reward.</summary>
    private readonly Dictionary<(Guid Killer, string Persona), long> _full = [];

    private BotOptions Options => world.Config.Bots;

    /// <summary>How many bags and silver piles of bots <paramref name="killer"/> got in the last hour (.bot info, the self-test).</summary>
    public int DropsOf(Player killer) => _drops.GetValueOrDefault(killer.Character.Id)?.Count(t => world.GameNow - t < TimeSpan.TicksPerHour) ?? 0;

    /// <summary>
    /// <paramref name="bot"/> defeated by <paramref name="killer"/> at <paramref name="ticks"/> (<see cref="BotCombat.Defeated"/>, after its
    /// show; <paramref name="attackers"/> those of the combat timeout, <paramref name="death"/> whether it died, <paramref name="crime"/>
    /// judged before the hit): the fame, the PvP goals, then (the caps willing) its bag and silver (see the class).
    /// </summary>
    public void Grant(Player bot, Player killer, List<(Player Player, double Damage)> attackers, bool death, bool crime, long ticks)
    {
        if (bot.Bot is not { } info || killer.IsBot || bot.Map is not { } map) return;
        var options = Options;
        if (!options.RewardsEnabled)
        {
            // Bots:Rewards:Enabled off: no fame, no PvP goal credit, no bag or silver; the kill message and a crime's reputation stay.
            Death.Reward(killer, bot, 0, attackers, crime, ticks, killMessage: death);
            Log.Info("bots", $"'{killer.Character.Name}' defeated {info.PersonaId}: no reward (Bots:Rewards:Enabled false)");
            return;
        }
        Prune(ticks);
        var key = (killer.Character.Id, info.PersonaId);
        var full = !_full.TryGetValue(key, out var last) || ticks - last >= (long)(options.SameBotMinutes * TimeSpan.TicksPerMinute);
        if (full) _full[key] = ticks;
        var raw = Death.KillFame(bot);
        var fame = (long)Math.Round(raw * options.FameFactor * (full ? 1 : SameBotFameShare));
        var shares = Death.Reward(killer, bot, fame, attackers, crime, ticks, killMessage: death);
        PvpGoals.OnPvpKill(world, new PvpKill(PvpActor.Of(killer), PvpActor.Of(BotVictim.Of(bot)), [.. attackers.Select(a => (PvpActor.Of(a.Player), a.Damage))], map,
            death ? PvpOutcome.Death : PvpOutcome.Knockdown, crime, raw, ticks, Math.Max(1, attackers.Count)) { FameShares = shares });
        if (!full)
        {
            Log.Info("bots", $"'{killer.Character.Name}' defeated {info.PersonaId} again within {options.SameBotMinutes:0.#} min: a quarter of the fame, no bag or silver");
            return;
        }
        if (!_drops.TryGetValue(killer.Character.Id, out var drops)) _drops[killer.Character.Id] = drops = [];
        if (drops.Count >= options.BagsPerHour)
        {
            Log.Info("bots", $"'{killer.Character.Name}' had {drops.Count} bot bags and piles this hour (Bots:Rewards:BagsPerHour): fame only");
            return;
        }
        var look = bot.Inventory.EquipmentTypeIds(); // the corpse's gear, before anything is taken
        var items = Roll(bot, info, map.Random, death);
        var silver = (int)Math.Round(world.BotSettings.Silver.GetValueOrDefault(info.Tier) * world.Rates.Silver * (info.Elite ? options.EliteSilverFactor : 1), MidpointRounding.AwayFromZero);
        if (items.Count == 0 && silver <= 0) return;
        if (items.Count > 0) drops.Add(ticks);
        if (silver > 0) drops.Add(ticks);
        var party = world.Parties.Of(killer.Character.Id);
        var settings = Death.Settings(world);
        var protection = options.LootProtectionSeconds
            ?? (Pvp.RulesOf(world, map).Rule == PvpRule.PvpForced ? settings.ForcedProtectionSeconds : settings.AllowedProtectionSeconds);
        var drop = new BotDrop(items, silver, killer.Character.Id, party?.Id ?? 0, party is null ? [killer.Character.Id] : [.. party.Members.Select(m => m.Id)], protection, look);
        if (death) Place(bot, drop, ticks);
        else info.Fight.Drop = drop; // at the despawn
    }

    /// <summary>
    /// The items of the bag (see the class): each worn combat item with Bots:Rewards:GearDropChance (a <paramref name="death"/>:
    /// Bots:Rewards:DeathGearDropChance) taken out of the equipment and worn, at least Bots:Rewards:MinItems of them, a
    /// gatherer's share of each item it "gathered" as one new stack, a patrol's Rewards.PatrolExtra by their chances; all with a player's
    /// spell layout.
    /// </summary>
    private List<Item> Roll(Player bot, BotInfo info, Random random, bool death)
    {
        var options = Options;
        var items = new List<Item>();
        var wear = Death.Settings(world).AliveToDead.EquipmentWear;
        var equipment = bot.Inventory.Equipment;
        var chance = death ? options.DeathGearDropChance : options.GearDropChance;
        void Move(int slot)
        {
            if (equipment.Take(slot) is not { } item) return;
            if (wear is not null) Death.Wear(item, wear, 1, random);
            items.Add(bot.Session.PlayerItem(item)); // the same object, now with passives a player may choose
        }
        foreach (var slot in DropSlots)
            if (equipment[slot] is not null && random.NextDouble() < chance) Move(slot);
        // Bots:Rewards:MinItems (stageM bots): random further worn items until the bag holds that many of its gear (moved, never copied).
        var least = options.MinItems + (info.Elite ? options.EliteExtraItems : 0); // stageN botpower: an elite's richer bag
        while (items.Count < least && DropSlots.Where(s => equipment[s] is not null).ToList() is { Count: > 0 } worn)
            Move(worn[random.Next(worn.Count)]);
        // Each item it gathered on its own (a band of tiers and rare states): the only items made outside the spawn.
        foreach (var (name, amount) in info.Fight.Carried)
            if (world.Items.Find(name) is { } resource && (int)Math.Floor(amount * options.ResourceDropShare) is var count and > 0)
                items.Add(Mint(bot, resource, Math.Min(count, Math.Max(1, resource.MaxStackSize))));
        if (BotManager.Patrols(info.Role))
            foreach (var (pattern, extraChance) in world.BotSettings.PatrolExtra)
                if (random.NextDouble() < extraChance && world.Bots.Factory.ResolveAny(pattern, info.Tier) is { } extra)
                    items.Add(Mint(bot, extra, 1));
        return items;
    }

    private Item Mint(Player bot, ItemTemplate type, int count) =>
        bot.Session.PlayerItem(new Item(world.ObjectIds.Next(), type, count, 1, type.FullDurabilityRaw, [], [], null));

    /// <summary>A knockdown's bag and silver, due at its despawn (<see cref="BotManager.Despawn"/>, before the Leave): placed now.</summary>
    public void Despawning(Player bot)
    {
        if (bot.Bot is not { Fight.Drop: { } drop } info) return;
        info.Fight.Drop = null;
        Place(bot, drop, world.GameNow);
    }

    /// <summary>
    /// <paramref name="drop"/> where <paramref name="bot"/> lies: its bag (<see cref="PlayerLootBag"/>, NewLoot type 1 + LootEquipmentChanged
    /// to the players in view) and its silver (<see cref="SilverPile"/> of kind <see cref="SilverPile.FromPlayer"/>), both gone after vanishtime.
    /// </summary>
    private void Place(Player bot, BotDrop drop, long ticks)
    {
        if (bot.Map is not { Grid: not null } map) return;
        var settings = Death.Settings(world);
        var at = bot.PositionAt(world.Bots.Time);
        var spot = DeathSpot.At(map, at, bot.Facing);
        var vanish = world.Clock.Now + TimeSpan.FromSeconds(settings.VanishSeconds);
        if (drop.Items.Count > 0)
        {
            var container = new Container(Guid.NewGuid(), drop.Items.Count);
            for (var i = 0; i < drop.Items.Count; i++) container.Put(i, drop.Items[i]);
            var bag = new PlayerLootBag(world.ObjectIds.Next(), spot, bot, container, drop.Looters, ticks, ticks + SpellCasts.Seconds(drop.ProtectionSeconds), drop.Look);
            map.AddObject(bag);
            map.Timers.At(vanish, () => Combat.RemoveBag(bag, "its time is up"));
        }
        if (drop.Silver > 0)
        {
            var pile = new SilverPile(world.ObjectIds.Next(), spot, at, bot.Id, bot.Character.Name, drop.Silver, 1, drop.Owner, ticks, drop.PartyId, SilverPile.FromPlayer);
            map.AddObject(pile);
            map.Timers.At(vanish, () => Combat.RemoveSilver(pile, "it decayed"));
        }
        Log.Info("bots", string.Create(CultureInfo.InvariantCulture,
            $"'{bot.Character.Name}' ({bot.Bot?.PersonaId}) leaves {drop.Items.Count} item(s) ({string.Join(", ", drop.Items.Select(i => i.Template.Name))}) and {drop.Silver} silver at ({at.X:0.#}, {at.Y:0.#})"));
    }

    /// <summary>The caps' memory: bags and piles older than an hour, full rewards older than Bots:Rewards:SameBotMinutes, dropped.</summary>
    private void Prune(long ticks)
    {
        foreach (var (killer, list) in _drops.ToList())
        {
            list.RemoveAll(t => ticks - t >= TimeSpan.TicksPerHour);
            if (list.Count == 0) _drops.Remove(killer);
        }
        var same = (long)(Options.SameBotMinutes * TimeSpan.TicksPerMinute);
        foreach (var gone in _full.Where(f => ticks - f.Value >= same).Select(f => f.Key).ToList()) _full.Remove(gone);
    }
}
