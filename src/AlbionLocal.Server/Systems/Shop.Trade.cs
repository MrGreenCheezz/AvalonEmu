using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

public sealed partial class Shop
{
    /// <summary>
    /// Both account wallets and both full character snapshots commit in one transaction. Preparation runs after
    /// the older save queue drains. Live inventory, money events and hooks are published only after success.
    /// </summary>
    internal bool TryCommitTrade(PlayerTrade trade, Func<(Character A, Character B)> prepare,
        Action<Character, Character> publish)
    {
        Character? a = null, b = null;
        ShopAccount[] accounts = [];
        var result = Attempt(() =>
        {
            if (!Enabled || trade.A.Player.Character.Transient || trade.B.Player.Character.Transient)
                return new(false, "El intercambio no está disponible.");
            var first = State(trade.A.Player.Character.Account);
            var sameAccount = first.Account == AccountId(trade.B.Player.Character.Account);
            var second = sameAccount ? first : State(trade.B.Player.Character.Account);
            if (trade.A.Gold < 0 || trade.B.Gold < 0 || trade.A.Silver < 0 || trade.B.Silver < 0
                || first.Gold < trade.A.Gold || second.Gold < trade.B.Gold
                || trade.A.Player.Character.Silver < trade.A.Silver || trade.B.Player.Character.Silver < trade.B.Silver)
                return new(false, "El saldo del intercambio cambió.");
            (a, b) = prepare();
            a.Silver = checked(a.Silver - trade.A.Silver + trade.B.Silver);
            b.Silver = checked(b.Silver - trade.B.Silver + trade.A.Silver);
            if (!sameAccount)
            {
                first.Gold = checked(first.Gold - trade.A.Gold + trade.B.Gold);
                second.Gold = checked(second.Gold - trade.B.Gold + trade.A.Gold);
            }
            a.Gold = first.Gold;
            b.Gold = second.Gold;
            accounts = sameAccount ? [first] : [first, second];
            if (!Store!.SaveShop(accounts, [a, b])) throw new IOException("trade transaction refused");
            return new(true, "Intercambio guardado.");
        });
        if (!result.Success) return false;
        // Publication is deliberately outside Attempt: once committed it cannot be reported as an aborted write.
        // Install every live account mirror first, including another online character of either account.
        foreach (var account in accounts)
            foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => AccountId(p.Character.Account) == account.Account && !p.Character.Transient))
                p.Character.Gold = account.Gold;
        publish(a!, b!);
        foreach (var account in accounts) Publish(account, []);
        return true;
    }
}
