using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A player's silver and gold (stage 6 SPEC §0): a7m raw on its character (Join keys 33/34), changed only here, each change
/// sent as UpdateMoney (event 81) and saved with the delayed item save (<see cref="Persistence.SaveScheduler.ChangeDelay"/>).
/// Under <see cref="World.Sync"/>.
/// </summary>
public static class Wallet
{
    /// <summary>
    /// UpdateMoney {0 player object, 1 silver, 2 gold} (ctp.af(a1d) 0x18099A4C0): the client overwrites both values and reads a
    /// missing key as 0, so both always go out, a zero too (the official 103829 one had no key 2 with gold 0).
    /// </summary>
    public static UpdateMoneyEvent Event(Player p) => new()
    {
        d5g = p.Id,
        Extra = new() { [1] = p.Character.Silver, [2] = p.Character.Gold },
    };

    /// <summary>Sends <see cref="Event"/> to the player's client.</summary>
    public static void Send(Player p) => p.Session.Send(Event(p));

    /// <summary>Takes <paramref name="raw"/> silver (a7m) when the player has that much; false (nothing changed, nothing sent) otherwise.</summary>
    public static bool TrySpend(Player p, long raw, string why)
    {
        var c = p.Character;
        if (raw < 0 || c.Silver < raw) return false;
        if (raw == 0) return true;
        c.Silver -= raw;
        Changed(p, string.Create(CultureInfo.InvariantCulture, $"-{raw / 10000.0:0.####} silver ({why})"));
        p.World.Modules.SilverChanged(p, -raw, why); // the modules' Players.OnSilverChanged
        return true;
    }

    /// <summary>Takes <paramref name="raw"/> gold (a7m) when the player has that much; false (nothing changed, nothing sent) otherwise.</summary>
    public static bool TrySpendGold(Player p, long raw, string why)
    {
        var c = p.Character;
        if (raw < 0 || c.Gold < raw) return false;
        if (raw == 0) return true;
        c.Gold -= raw;
        Changed(p, string.Create(CultureInfo.InvariantCulture, $"-{raw / 10000.0:0.####} gold ({why})"));
        return true;
    }

    /// <summary>Gives <paramref name="raw"/> silver (a7m, ≥ 0) and sends UpdateMoney.</summary>
    public static void Credit(Player p, long raw, string why)
    {
        if (raw <= 0) return;
        p.Character.Silver = checked(p.Character.Silver + raw);
        Changed(p, string.Create(CultureInfo.InvariantCulture, $"+{raw / 10000.0:0.####} silver ({why})"));
        p.World.Modules.SilverChanged(p, raw, why); // the modules' Players.OnSilverChanged
    }

    /// <summary>
    /// Moves <paramref name="raw"/> gold (a7m) from <paramref name="from"/> to <paramref name="to"/> (a trade under Trade:AllowGold) when
    /// <paramref name="from"/> has that much, UpdateMoney to both; false (nothing changed) otherwise.
    /// </summary>
    public static bool TransferGold(Player from, Player to, long raw, string why)
    {
        if (raw < 0 || from.Character.Gold < raw) return false;
        if (raw == 0) return true;
        from.Character.Gold -= raw;
        to.Character.Gold = checked(to.Character.Gold + raw);
        Changed(from, string.Create(CultureInfo.InvariantCulture, $"-{raw / 10000.0:0.####} gold ({why})"));
        Changed(to, string.Create(CultureInfo.InvariantCulture, $"+{raw / 10000.0:0.####} gold ({why})"));
        return true;
    }

    /// <summary>Sets both values (GM .money) and sends UpdateMoney.</summary>
    public static void Set(Player p, long silver, long gold)
    {
        var silverBefore = p.Character.Silver;
        p.Character.Silver = Math.Max(0, silver);
        p.Character.Gold = Math.Max(0, gold);
        Changed(p, "set");
        p.World.Modules.SilverChanged(p, p.Character.Silver - silverBefore, "set"); // the modules' Players.OnSilverChanged
    }

    private static void Changed(Player p, string what)
    {
        Send(p);
        p.World.Saves.MarkChanged(p.Character);
        Log.Info("money", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' {what}: now {p.Character.Silver / 10000.0:0.####} silver, {p.Character.Gold / 10000.0:0.####} gold"));
    }
}
