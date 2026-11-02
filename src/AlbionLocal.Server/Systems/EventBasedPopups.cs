using System.Runtime.CompilerServices;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Persistence for notices the original client actually displayed. All calls run under World.Sync.
/// The additive account JSON retains both account-wide and character-specific native condition semantics.</summary>
public static class EventBasedPopups
{
    private static readonly ConditionalWeakTable<World, HashSet<string>> Catalogs = new();

    /// <summary>345319: A5BFE0→7B9E70 loads five parallel arrays into bwm. A5BF06–55 sums key 95,
    /// then adds one for the current login; key 95 is not indexed by CreateCharacterRecord (16D5060).
    /// Save the new ordinal before returning the previous total, so logins without notices count too.</summary>
    public static void FillLogin(World world, Guid account, LoginResponse response)
    {
        if (world.Characters is not IShopStore store) return;
        var snapshot = world.Saves.Commit(() =>
        {
            var state = store.LoadShop(account) ?? new ShopAccount { Account = account };
            var previous = state.EventBasedPopups.LoginCount;
            state.EventBasedPopups.LoginCount = checked(previous + 1);
            if (!store.SaveShop([state], [])) throw new IOException("could not store popup login state");
            return (History: state.EventBasedPopups, Previous: previous);
        });
        var groups = snapshot.History.Seen.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        response.bfl = groups.Select(x => x.Key).ToArray();
        response.bfm = groups.Select(x => x.Value.Select(v => v.At).ToArray()).ToArray();
        response.bfn = groups.Select(x => x.Value.Select(v => v.Character).ToArray()).ToArray();
        response.bfo = groups.Select(x => x.Value.Select(v => v.Login).ToArray()).ToArray();
        response.bfp = groups.Select(x => x.Value.Select(v => v.Queue).ToArray()).ToArray();
        response.bfr = [snapshot.Previous];
    }

    /// <summary>345319 ghs.b C73840 sends sd via A68A80, then immediately adds the same view locally
    /// (C73B97→7B9490). No success response or replacement popup event is needed. Only the authenticated
    /// character supplies the owner; request timestamps/counters cannot create another account's history.</summary>
    public static void AddSeen(World world, Character character, EventBasedPopupAddSeenRequest request)
    {
        if (character.Transient || character.Account == Guid.Empty || world.Characters is not IShopStore store)
            throw new InvalidOperationException("popup history requires a persistent account");
        var ids = Catalogs.GetValue(world, LoadIds);
        if (request.kk is not { Length: > 0 and <= 256 } id)
            throw new InvalidDataException("invalid event-based popup id");
        if (!ids.Contains(id))
            throw new InvalidDataException($"unknown event-based popup id {System.Text.Json.JsonSerializer.Serialize(id)} (type {request.kl})");
        // PopupType in the installed 345319 metadata is None=0 through News=23.
        // The view timestamp uses the already-synchronized game clock; don't accept future history.
        if (request.kl is < 0 or > 23 || request.km <= 0 || request.km > world.GameNow + TimeSpan.FromMinutes(5).Ticks
            || request.ko <= 0 || request.kp < 0)
            throw new InvalidDataException("invalid event-based popup view");
        world.Saves.Commit(() =>
        {
            var state = store.LoadShop(character.Account) ?? throw new InvalidDataException("popup login state is missing");
            var history = state.EventBasedPopups;
            if (request.ko != history.LoginCount)
                throw new InvalidDataException("popup view belongs to a different login");
            if (!history.Seen.TryGetValue(id, out var entries)) history.Seen[id] = entries = [];
            // Retransmission is not another view. Keep genuinely repeated views, because native frequency
            // rules count them, including views of the same id by different characters on this account.
            if (entries.Any(v => v.Character == character.Id && v.At == request.km
                && v.Login == request.ko && v.Queue == request.kp)) return false;
            entries.Add(new(request.km, character.Id, request.ko, request.kp, request.kl, request.kn));
            if (!store.SaveShop([state], [])) throw new IOException("could not store event-based popup view");
            return true;
        });
    }

    private static HashSet<string> LoadIds(World world)
    {
        if (world.Clusters is not { } clusters) throw new InvalidOperationException("popup client data is unavailable");
        // Current345319 bwj.abp 7B7A40 dispatches all four XML definitions and registers each
        // via7B7CA5 ->7B73E0. Restricting the catalogue to <popup> drops six installed entries.
        return GameDataFile.Open(clusters.Folder, "eventbasedpopups.bin").Root()
            .Element("popups")?.Elements()
            .Where(e => e.Name.LocalName is "popup" or "lootchestpopup" or "genericadvertisement" or "survey")
            .Select(e => (string?)e.Attribute("uniquename"))
            .OfType<string>().Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal) ?? [];
    }
}
