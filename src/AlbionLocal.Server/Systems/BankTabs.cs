using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;

namespace AlbionLocal.Server.Systems;

/// <summary>Durable personal-tab metadata changes. The native handler owns bank access, the catalogue,
/// purchase price and action validation. All calls run under World.Sync, like the existing wallet operations.</summary>
public static class BankTabs
{
    /// <summary>The complete ordered metadata of one resolved vault location and its silver cost commit as
    /// one character save. The save barrier drains earlier snapshots first. No bank-item rows/keys are transformed.
    /// A network failure after commit must not turn a paid tab into a failed purchase or charge it again.</summary>
    public static bool TryCommit(Player player, string location, IReadOnlyList<BankTab> tabs, long silverCostA7m, out string error)
    {
        error = "";
        var character = player.Character;
        if (character.Transient || player.Session.Player != player || string.IsNullOrWhiteSpace(location) || location.Contains('|')
            || silverCostA7m < 0 || tabs is null
            || tabs.Any(t => t is null || t.Id == Guid.Empty || t.Name is null || t.Icon is null)
            || tabs.Select(t => t.Id).Distinct().Count() != tabs.Count)
        {
            error = "Los datos de las pestañas del banco no son válidos.";
            return false;
        }
        if (character.Silver < silverCostA7m)
        {
            error = "No tienes suficiente plata para esta pestaña.";
            return false;
        }
        // This route buys/edits/reorders tabs; dropping ownership would strand its existing bank_items.
        var ids = tabs.Select(t => t.Id).ToHashSet();
        if (character.BankTabs.TryGetValue(location, out var owned) && owned.Any(t => !ids.Contains(t.Id)))
        {
            error = "La actualización no puede eliminar una pestaña que ya posees.";
            return false;
        }
        var ownedIds = (owned ?? []).Select(t => t.Id).ToHashSet();
        ownedIds.Add(WorldSession.DefaultTab); // Always owned, including characters without metadata yet.
        if (silverCostA7m > 0 && !ids.Any(id => !ownedIds.Contains(id)))
        {
            error = "La pestaña ya está comprada. No se ha cobrado plata.";
            return false;
        }
        Character? committed = null;
        try
        {
            var saved = player.World.Saves.Commit(() =>
            {
                // Clone after the barrier's position refresh. Item handlers already mirror every move into
                // Character.Vaults; retain those records and overflow verbatim instead of rematerializing containers.
                if (character.Silver < silverCostA7m) return false;
                var snapshot = character.Clone();
                snapshot.Silver -= silverCostA7m;
                snapshot.BankTabs[location] = [.. tabs];
                if (!player.World.Characters.Save(snapshot)) return false;
                committed = snapshot;
                return true;
            });
            if (!saved || committed is null)
            {
                error = "No se pudo guardar la pestaña del banco. No se ha cobrado plata.";
                return false;
            }
        }
        catch (Exception e)
        {
            Log.Warn("bank", $"tab metadata of '{character.Name}' at {location} was not committed: {e.Message}");
            error = "No se pudo guardar la pestaña del banco. No se ha cobrado plata.";
            return false;
        }
        // Install durable ownership and balance before any presentation can fail. A retried request then sees
        // the purchased tab; the handler can reject/replay it without paying the purchase a second time.
        character.BankTabs = committed.BankTabs;
        character.Silver = committed.Silver;
        if (silverCostA7m != 0)
            try { Wallet.Send(player); }
            catch (Exception e) { Log.Warn("bank", $"tab purchase committed for '{character.Name}'; wallet notification deferred: {e.Message}"); }
        Log.Info("bank", $"'{character.Name}' saved {tabs.Count} tab(s) at {location}, paid {silverCostA7m} a7m silver");
        return true;
    }
}
