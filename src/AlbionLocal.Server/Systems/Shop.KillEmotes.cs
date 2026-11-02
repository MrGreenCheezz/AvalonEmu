using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    private KillEmoteCatalog? _killEmotes;
    public KillEmoteCatalog KillEmotes
    {
        get
        {
            if (_killEmotes is not null) return _killEmotes;
            try { return _killEmotes = world.Clusters is { } c ? KillEmoteCatalog.Load(c.Folder) : KillEmoteCatalog.Empty; }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Log.Warn("shop", $"kill emotes unavailable: {e.Message}");
                return _killEmotes = KillEmoteCatalog.Empty;
            }
        }
    }

    public (short[] Ids, int[] Charges) Consumables(Guid account)
    {
        var state = State(account);
        var entries = state.Consumables.Where(x => x.Value > 0 && KillEmotes.Find(x.Key) is not null).OrderBy(x => x.Key).ToArray();
        return ([.. entries.Select(x => x.Key)], [.. entries.Select(x => x.Value)]);
    }
    public short EquippedKillEmote(Character c) => c.Transient ? (short)0 : State(c.Account).KillEmotes.GetValueOrDefault(c.Id)?.ConsumableId ?? 0;
    public bool KillEmoteOnKnockdown(Character c) => !c.Transient && (State(c.Account).KillEmotes.GetValueOrDefault(c.Id)?.PlayOnKnockdown ?? true);

    /// <summary>The client has already confirmed its native gold dialog. Validate every bundle against GameData and record the
    /// purchase plus account wallet/stock in one transaction. A repeated request id replays its receipt without another event or debit.</summary>
    public ShopResult BuyConsumables(Player p, byte type, short[] ids, int[] bundles, long quotedPrice, string requestId)
    {
        if (type != 0 || ids.Length is < 1 or > 128 || ids.Length != bundles.Length || ids.Distinct().Count() != ids.Length || bundles.Any(n => n <= 0))
            return new(false, "Selección de consumibles inválida.");
        long price = 0;
        var charges = new Dictionary<short, int>();
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (KillEmotes.Find(ids[i]) is not { } emote) return new(false, "Consumible no disponible.");
                price = checked(price + emote.GoldPrice * bundles[i]);
                charges.Add(ids[i], checked(emote.BundleCharges * bundles[i]));
            }
        }
        catch (OverflowException) { return new(false, "Cantidad demasiado grande."); }
        if (quotedPrice != price) return new(false, "El precio ha cambiado.");
        var product = "native.kill-emotes." + string.Join("-", charges.OrderBy(x => x.Key).Select(x => $"{x.Key}x{x.Value}"));
        ShopAccount? committed = null;
        var result = Attempt(() =>
        {
            if (!Allowed(p.Character.Account) || p.Character.Transient || !ValidKey(requestId)) return new(false, "Cuenta o pedido inválido.");
            var state = State(p.Character.Account);
            if (state.Orders.FirstOrDefault(o => o.Id == requestId) is { } old)
                return old.ProductId == product && old.Character == p.Character.Id && old.Price == price && old.Status == "completed"
                    ? new(true, "Compra completada.", old) : new(false, "Este identificador corresponde a otra compra.");
            if (state.Gold < price) return new(false, "Oro insuficiente. No se ha cobrado.");
            foreach (var (id, amount) in charges) state.Consumables[id] = checked(state.Consumables.GetValueOrDefault(id) + amount);
            state.Gold -= price;
            var order = new ShopOrder
            {
                Id = requestId, Account = state.Account, Character = p.Character.Id, ProductId = product, Title = "Cargas de gestos de victoria",
                Currency = "gold", Price = price, Charges = new(charges), Status = "completed", CreatedAt = world.GameNow,
                ExpiresAt = world.GameNow, CompletedAt = world.GameNow,
            };
            state.Orders.Add(order);
            Write(state);
            committed = state;
            return new(true, "Compra completada.", order);
        });
        if (committed is not null)
        {
            Publish(committed, []);
            foreach (var (id, amount) in charges) SendConsumableDelta(committed.Account, id, amount);
        }
        return result;
    }

    public bool EquipKillEmote(Player p, short id)
    {
        if (id == -1) id = 0;
        return Attempt(() =>
        {
            if (!Allowed(p.Character.Account) || p.Character.Transient) return new(false, "Cuenta no disponible.");
            var state = State(p.Character.Account);
            if (id != 0 && (KillEmotes.Find(id) is null || state.Consumables.GetValueOrDefault(id) <= 0)) return new(false, "No tienes cargas.");
            var previous = state.KillEmotes.GetValueOrDefault(p.Character.Id) ?? new();
            state.KillEmotes[p.Character.Id] = previous with { ConsumableId = id };
            Write(state);
            return new(true, "Gesto equipado.");
        }).Success;
    }

    public bool SetKillEmoteOnKnockdown(Player p, bool enabled) => Attempt(() =>
    {
        if (!Allowed(p.Character.Account) || p.Character.Transient) return new(false, "Cuenta no disponible.");
        var state = State(p.Character.Account);
        state.KillEmotes[p.Character.Id] = (state.KillEmotes.GetValueOrDefault(p.Character.Id) ?? new()) with { PlayOnKnockdown = enabled };
        Write(state);
        return new(true, "Preferencia guardada.");
    }).Success;

    private void SendConsumableDelta(Guid account, short id, int amount)
    {
        foreach (var player in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => AccountId(p.Character.Account) == account))
            player.Session.Send(new ConsumableVanityChargesAddedEvent { d6g = id, d6h = amount });
    }

    /// <summary>Only a real supported PvP outcome consumes a charge. Persistence precedes the visual; an unavailable store,
    /// distant target, full local effect area or disabled KO option leaves the stock untouched. The normal map timer expires it.</summary>
    public bool PlayKillEmote(Player killer, Player victim, bool knockdown, long ticks)
    {
        if (!Enabled || killer.IsBot || killer.Character.Transient || killer == victim || killer.Map is not { Grid: not null } map || victim.Map != map)
            return false;
        var at = victim.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(killer.PositionAt(DateTime.UtcNow), at) > KillEmotes.MaxDistance) return false;
        var active = map.Objects.Values.OfType<KillEmoteVisual>().Where(v => v.End > ticks).ToArray();
        if (active.Any(v => v.Victim == victim.Id && v.Start == ticks) || active.Count(v => Vector2.Distance(v.Position, at) <= KillEmotes.AreaRadius) >= KillEmotes.MaxInArea)
            return false;
        KillEmoteCatalog.Emote? selected = null;
        var result = Attempt(() =>
        {
            var state = State(killer.Character.Account);
            var setting = state.KillEmotes.GetValueOrDefault(killer.Character.Id);
            if (setting is null || knockdown && !setting.PlayOnKnockdown || KillEmotes.Find(setting.ConsumableId) is not { } emote
                || state.Consumables.GetValueOrDefault(emote.Id) <= 0) return new(false, "Sin gesto disponible.");
            state.Consumables[emote.Id]--;
            Write(state);
            selected = emote;
            return new(true, "Gesto ejecutado.");
        });
        if (!result.Success || selected is null) return false;
        var rotation = selected.Rotation == "none" ? 0 : victim.MovementSample(DateTime.UtcNow).Direction;
        if (selected.Rotation == "defaultwithrandoffset") rotation += selected.MinRotation + (float)map.Random.NextDouble() * (selected.MaxRotation - selected.MinRotation);
        var end = checked(ticks + TimeSpan.FromSeconds(selected.Duration).Ticks);
        var visual = new KillEmoteVisual(world.ObjectIds.Next(), DeathSpot.At(map, at, rotation), selected, killer.Id, victim.Id, ticks, end);
        map.AddObject(visual);
        map.Timers.At(world.ClockAt(end), () => map.RemoveObject(visual));
        SendConsumableDelta(AccountId(killer.Character.Account), selected.Id, -1);
        return true;
    }
}
