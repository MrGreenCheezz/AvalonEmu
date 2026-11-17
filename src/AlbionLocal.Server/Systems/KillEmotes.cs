using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR victory emotes ("kill emotes", analysis/work/stageR/kill-vanity/SPEC.md §1). An emote is owned as charges (the client's
/// VanityConsumableType KillEmote): GetVanityConsumables 467 {0 ids, 1 charges} lists them, ConsumableVanityChargesAdded 517 {0 id, 1 amount}
/// adds to the client's count (bh.c 0x1807450F0 adds, it does not set). Charges come from the charge items (consumespell
/// T1_KILL_EMOTE_ADD_CHARGES_*: addvanityconsumablecharges, <see cref="Consumables.Award"/>), from the cosmetic drops
/// (Vanity:KillEmoteDropShare, <see cref="Vanity.Drop"/>) and from BuyVanityConsumableCharges 470, priced in gold as the client sends it
/// (killemotes.xml goldprice per bundle of bundledchargesamount; gold is an in-game currency here). EquipKillEmote 468 picks one (0 = none),
/// ChangeKillEmotePlayOnKnockdownSetting 469 whether it also plays on a knockdown; Join keys 119/120 carry both (cvr.d(ahi) 0x1809D2300).
/// At a player kill (and at a PvP knockdown with that setting) within maxtriggerdistance the killer's emote plays: NewVisualEventObject 512
/// {0 killer, 1 victim, 2 a new object id, 3 the victim's spot, 4 its facing, 5 the emote's visual index, 6 0 KILL_EMOTE, 7/8 start/end} to the
/// victim's observers and the killer, a Leave at the end [infer], and one charge goes (517 with -1). Under World.Sync.
/// </summary>
public sealed class KillEmotes(World world)
{
    /// <summary>The RC of a refused EquipKillEmote / BuyVanityConsumableCharges (the client shows its generic failure).</summary>
    public const short Refused = 1;

    /// <summary>VanityConsumableType.KillEmote, the only value of this build.</summary>
    public const byte KillEmoteType = 0;

    /// <summary>VisualEventType.KILL_EMOTE (NewVisualEventObject key 6).</summary>
    public const byte KillEmoteVisual = 0;

    /// <summary>The most bundles one purchase may ask for per emote (a guard against an overflow, not a client limit).</summary>
    public const int MaxBundles = 1000;

    private KillVanityData? _data;

    /// <summary>The drop share's random, apart from <see cref="Vanity.Random"/> so the skin drops roll as before.</summary>
    public Random Random { get; set; } = new();

    /// <summary>killemotes.xml and killtrophy.xml, read at the first use; <see cref="KillVanityData.Empty"/> without client data (logged once).</summary>
    public KillVanityData Data
    {
        get
        {
            if (_data is not null) return _data;
            try
            {
                if (world.Clusters is not { } store) throw new InvalidDataException("no client data");
                _data = KillVanityData.Load(store.Folder);
                Log.Info("data", $"kill vanity: {_data.Emotes.Count} victory emotes, {_data.Trophies.Count} kill trophy tables");
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _data = KillVanityData.Empty;
                Log.Warn("data", $"kill vanity data: {e.Message}; no victory emotes or kill trophies");
            }
            return _data;
        }
    }

    public static int Charges(Character c, KillEmote e) => c.Vanity?.KillEmoteCharges.GetValueOrDefault(e.Name) ?? 0;

    /// <summary>GetVanityConsumables keys 0/1: the emotes with charges, by id, and their charges.</summary>
    public (short[] Ids, int[] Charges) List(Character c)
    {
        var owned = Data.Emotes.Select(e => (e.Id, Count: Charges(c, e))).Where(x => x.Count > 0).ToList();
        return ([.. owned.Select(x => x.Id)], [.. owned.Select(x => x.Count)]);
    }

    /// <summary>Join key 119: the equipped emote's id, 0 = none (as the official Join without one).</summary>
    public short Equipped(Character c) => c.Vanity?.KillEmote is { } name && Data.Emote(name) is { } e ? e.Id : (short)0;

    /// <summary>
    /// EquipKillEmote (op 468, acm {0 short id}): 0 takes it off, else a known emote [assume: one without charges may be equipped too; it plays
    /// only while it has some]. Saved. Null = refused.
    /// </summary>
    public short? Equip(Player p, short id)
    {
        if (id != 0 && Data.Emote(id) is null) return null;
        Vanity.LogOf(p.Character).KillEmote = id == 0 ? null : Data.Emote(id)!.Name;
        world.Saves.MarkChanged(p.Character);
        Log.Info("vanity", $"'{p.Character.Name}' victory emote: {(id == 0 ? "none" : Data.Emote(id)!.Name)}");
        return id;
    }

    /// <summary>ChangeKillEmotePlayOnKnockdownSetting (op 469, acl {0 bool}): kept, saved.</summary>
    public void SetOnKnockdown(Player p, bool on)
    {
        Vanity.LogOf(p.Character).KillEmoteOnKnockdown = on;
        world.Saves.MarkChanged(p.Character);
    }

    /// <summary><paramref name="amount"/> charges of <paramref name="e"/> (negative = used) for <paramref name="p"/>: kept, saved, event 517 {0 id, 1 amount}.</summary>
    public void AddCharges(Player p, KillEmote e, int amount, string why)
    {
        if (amount == 0) return;
        var charges = Vanity.LogOf(p.Character).KillEmoteCharges;
        var now = Math.Max(0, checked(charges.GetValueOrDefault(e.Name) + amount));
        if (now == 0) charges.Remove(e.Name);
        else charges[e.Name] = now;
        world.Saves.MarkChanged(p.Character);
        if (!p.IsBot) p.Session.Send(new ConsumableVanityChargesAddedEvent { d6g = e.Id, d6h = amount });
        Log.Info("vanity", $"'{p.Character.Name}' {amount:+#;-#} {e.Name} charge(s) ({why}): {now}");
    }

    /// <summary>
    /// BuyVanityConsumableCharges (op 470, ack {0 type, 1 emote ids, 2 bundles per id, 3 a7m price}; VanityPreviewGui.DoBuyAllVanityItems
    /// 0x1815F8FA0 sends Σ goldprice × bundles as the price, GetBundledChargesAmount the charges per bundle): every id a buyable emote (goldprice
    /// &gt; 0, the client's LockedState Buyable), the price the client saw equal to the server's, the gold there; then the gold goes and each emote
    /// gets bundles × bundledchargesamount charges, in one step. Null, or why it was refused (nothing changed).
    /// </summary>
    public string? Buy(Player p, byte type, short[]? ids, int[]? bundles, long price)
    {
        if (type != KillEmoteType) return $"consumable type {type}";
        if (ids is not { Length: > 0 } || bundles is null || bundles.Length != ids.Length || ids.Distinct().Count() != ids.Length)
            return $"{ids?.Length ?? 0} ids, {bundles?.Length ?? 0} amounts";
        var lines = new List<(KillEmote Emote, int Bundles)>();
        long cost = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            if (Data.Emote(ids[i]) is not { GoldPrice: > 0 } e) return $"emote {ids[i]} is not for sale";
            if (bundles[i] is <= 0 or > MaxBundles) return $"{bundles[i]} bundles of {e.Name}";
            lines.Add((e, bundles[i]));
            cost = checked(cost + e.GoldPrice * 10000L * bundles[i]);
        }
        if (cost != price) return string.Create(CultureInfo.InvariantCulture, $"price {price / 10000.0:0.####} gold, the server's {cost / 10000.0:0.####}");
        if (!Wallet.TrySpendGold(p, cost, "victory emote charges")) return "not enough gold";
        foreach (var (e, n) in lines) AddCharges(p, e, n * e.BundledCharges, $"bought {n} bundle(s) for gold");
        world.Saves.SaveNow(p.Character);
        return null;
    }

    /// <summary>
    /// A cosmetic drop's victory emote share (Vanity:KillEmoteDropShare): one bundle of a random emote's charges for <paramref name="p"/>, with
    /// a chat line. Null when this drop is no emote bundle (the share's roll, or no emote data).
    /// </summary>
    public KillEmote? DropBundle(Player p, string source)
    {
        var share = world.Config.Vanity.KillEmoteDropShare;
        if (share <= 0 || Data.Emotes.Count == 0 || Random.NextDouble() >= share) return null;
        var e = Data.Emotes[Random.Next(Data.Emotes.Count)];
        var n = Math.Max(1, e.BundledCharges);
        AddCharges(p, e, n, source);
        Expeditions.Tell(p, $"Victory emote charges: {n} x {e.Name} ({source}). Equip it in the vanity window (victory emotes).");
        return e;
    }

    /// <summary>
    /// <paramref name="killer"/> killed (<paramref name="knockdown"/> false) or knocked down <paramref name="victim"/>: its emote plays when it
    /// has one equipped with a charge left, a knockdown only with its knockdown setting, the two within killemotes.xml maxtriggerdistance
    /// [infer: the distance the setting's name gives]. Returns whether it played.
    /// </summary>
    public bool Played(Player killer, Player victim, bool knockdown)
    {
        if (!world.Config.Vanity.KillEmotes || killer.IsBot || killer == victim || victim.Map is not { } map || killer.Map != map) return false;
        if (killer.Character.Vanity is not { KillEmote: { } name } log || Data.Emote(name) is not { } e || Charges(killer.Character, e) <= 0) return false;
        if (knockdown && !log.KillEmoteOnKnockdown) return false;
        var now = DateTime.UtcNow;
        var at = victim.PositionAt(now);
        if (Vector2.Distance(killer.PositionAt(now), at) > Data.MaxTriggerDistance) return false;
        var start = world.GameNow;
        var span = TimeSpan.FromSeconds(e.Duration);
        var id = world.ObjectIds.Next();
        var shown = new NewVisualEventObjectEvent
        {
            d6u = killer.Id, d6v = victim.Id, d6w = id, d6x = at, d6y = victim.Facing, d6z = e.Visual, d60 = KillEmoteVisual,
            d61 = start, d62 = start + SpellCasts.Seconds(e.Duration),
        };
        var audience = new HashSet<Player>(victim.Observers) { victim, killer };
        foreach (var o in audience) if (!o.IsBot) o.Session.Send(shown);
        map.Timers.At(world.Clock.Now + span, () =>
        {
            var leave = new LeaveEvent { ObjectId = id };
            foreach (var o in audience) if (o.Map == map && !o.IsBot) o.Session.Send(leave);
        });
        AddCharges(killer, e, -1, knockdown ? "played on a knockdown" : "played on a kill");
        return true;
    }
}
