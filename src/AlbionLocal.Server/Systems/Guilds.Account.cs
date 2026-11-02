using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    /// <summary>
    /// Op 134: the current GuildAccountView sends bGold=false and a7m silver. Account history reason 2 is DEPOSIT.
    /// Character debit, guild credit and the new history row commit together; failure changes neither live balance.
    /// </summary>
    public short DepositAccount(Player player, bool gold, long amount, out string error)
    {
        error = "";
        var character = player.Character;
        if (Of(character.Id) is not { } guild || character.Transient)
        {
            error = "Debes pertenecer a un gremio para depositar plata.";
            return Refused;
        }
        if (gold || amount <= 0)
        {
            error = "El depósito debe ser una cantidad positiva de plata.";
            return Refused;
        }
        if (character.Silver < amount)
        {
            error = "No tienes suficiente plata para este depósito.";
            return Refused;
        }
        if (guild.SilverA7m < 0 || amount > long.MaxValue - guild.SilverA7m)
        {
            error = "El saldo del gremio no admite este depósito.";
            return Refused;
        }
        if (store is not IGuildAccountStore accountStore)
        {
            error = "El almacenamiento de la cuenta del gremio no está disponible.";
            return Refused;
        }

        var balance = guild.SilverA7m + amount;
        GuildProgress progress;
        long silver;
        try
        {
            (silver, progress) = world.Saves.Commit(() =>
            {
                // Clone after the save barrier refreshes live character position/state, never before it.
                var nextCharacter = character.Clone();
                nextCharacter.Silver -= amount;
                var nextProgress = JsonSerializer.Deserialize<GuildProgress>(JsonSerializer.Serialize(guild.Progress))
                    ?? throw new InvalidDataException("The guild progress could not be copied.");
                nextProgress.AccountTransactions.Add(new GuildAccountTransaction
                {
                    Character = character.Id, PlayerName = character.Name, LogType = 0, Reason = 2,
                    Amount = amount, At = world.GameNow,
                });
                if (!accountStore.SaveGuildAccountTransfer(guild.Id, balance, nextProgress, nextCharacter))
                    throw new InvalidOperationException("The character or guild membership no longer exists.");
                return (nextCharacter.Silver, nextProgress);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("guild", $"deposit by '{character.Name}' into '{guild.Name}' was not saved: {ex.Message}");
            error = "No se pudo guardar el depósito. Tu plata no se ha transferido.";
            return Refused;
        }

        character.Silver = silver;
        guild.SilverA7m = balance;
        guild.Progress = progress;
        // Preserve _progressChanged: this transaction does not write pending XP or contracts.
        _accountChanged.Remove(guild);
        Wallet.Send(player);
        SendTo(guild, AccountUpdate(guild));
        Log.Info("guild", $"'{character.Name}' deposited {amount} a7m into '{guild.Name}' (balance {balance})");
        return 0;
    }

    /// <summary>
    /// Op 135, current 345319 GuildAccountView: OnWithdrawalConfirmed 1098A80 -> A776F0 sends key 0 bGold,
    /// key 1 a7m amount, without a target or callback. OnGuildDataChanged 10983CB enables withdrawal with
    /// WITHDRAWAL_FROM_SILVER_ACCOUNT (0x100), not a role threshold. Only the authenticated member receives it.
    /// </summary>
    public short WithdrawOwnAccount(Player player, bool gold, long amount, out string error)
    {
        error = "";
        var character = player.Character;
        if (character.Transient || Of(character.Id) is not { } guild || guild.Find(character.Id) is not { } member)
        {
            error = "Debes pertenecer a un gremio para retirar plata.";
            return Refused;
        }
        if ((GuildRoles.Permissions(member.Role) & GuildRoles.WithdrawSilver) == 0)
        {
            error = "Tu rango no tiene permiso para retirar plata de la cuenta del gremio.";
            return Refused;
        }
        if (gold)
        {
            error = "La cuenta de oro del gremio no está disponible.";
            return Refused;
        }
        if (amount <= 0)
        {
            error = "La retirada debe ser una cantidad positiva de plata.";
            return Refused;
        }
        if (guild.SilverA7m < 0 || amount > guild.SilverA7m)
        {
            error = "La cuenta del gremio no tiene suficiente plata.";
            return Refused;
        }
        if (character.Silver < 0 || amount > long.MaxValue - character.Silver)
        {
            error = "Tu saldo no admite esta cantidad de plata.";
            return Refused;
        }
        if (WithdrawAccount(guild, player, player, amount) is not null)
        {
            error = "No se pudo guardar la retirada. No se ha transferido plata.";
            return Refused;
        }
        world.GuildGoals.PaidOut(guild, character.Id, amount);
        _accountChanged.Remove(guild);
        SendTo(guild, AccountUpdate(guild));
        Log.Info("guild", $"'{character.Name}' withdrew {amount} a7m from '{guild.Name}' (balance {guild.SilverA7m})");
        return 0;
    }

    /// <summary>
    /// Current 345319 OnSubmitTax 10984E0 converts the dialog's 0..100 percentage to a fraction, then
    /// A76E60 sends qh key 0 (op 137). 10000 means 100 %. The view checks CHANGE_GUILD_TAX at 1098899.
    /// A positive Guilds:SilverTax remains the existing mode override; it cannot silently accept an ineffective change.
    /// </summary>
    public short ChangeAccountTax(Player player, long tax, out string error)
    {
        error = "";
        if (player.Character.Transient || Of(player.Character.Id) is not { } guild
            || guild.Find(player.Character.Id) is not { } member)
        {
            error = "Debes pertenecer a un gremio para cambiar su impuesto.";
            return Refused;
        }
        if ((GuildRoles.Permissions(member.Role) & GuildRoles.ChangeTax) == 0)
        {
            error = "Tu rango no tiene permiso para cambiar el impuesto del gremio.";
            return Refused;
        }
        if (tax is < 0 or > 10000)
        {
            error = "El impuesto debe estar entre 0 y 100 %.";
            return Refused;
        }
        if (Config.GuildSilverTax > 0)
        {
            if (tax != TaxOf(guild))
            {
                error = "Este modo mantiene un impuesto fijo para el gremio.";
                return Refused;
            }
        }
        else if (guild.TaxA7m != tax)
        {
            var before = guild.TaxA7m;
            guild.TaxA7m = tax;
            try { Save(guild); } // existing atomic guild save includes tax and any pending account/progress data
            catch (Exception ex)
            {
                guild.TaxA7m = before;
                Log.Warn("guild", $"tax change of '{guild.Name}' was not saved: {ex.Message}");
                error = "No se pudo guardar el nuevo impuesto del gremio.";
                return Refused;
            }
            _progressChanged.Remove(guild);
            Log.Info("guild", $"'{player.Character.Name}' changed tax of '{guild.Name}' from {before} to {tax} a7m");
        }
        _accountChanged.Remove(guild);
        // Kind 2 refreshes balances but does not change the native tax model (345319).
        SendTo(guild, TaxUpdate(guild));
        SendTo(guild, AccountUpdate(guild));
        return 0;
    }

    /// <summary>Op 159: page * 100, count 101 (the extra row enables the next page), using the same filters as export.</summary>
    public GetGuildAccountLogsResponse AccountLogs(Player player, GetGuildAccountLogsRequest request)
    {
        var rc = QueryAccountLogs(player, request.mv, request.mw, request.mx, request.my, request.mz,
            request.m0, request.m1, request.m2, 101, out var rows, out var error);
        return new GetGuildAccountLogsResponse
        {
            ReturnCode = rc, DebugMessage = error,
            aw9 = rows.Select(row => row.PlayerName).ToArray(),
            axa = rows.Select(row => row.Reason).ToArray(),
            axb = rows.Select(row => row.Parameter).ToArray(),
            axc = rows.Select(row => row.Amount).ToArray(),
            axd = rows.Select(row => row.At).ToArray(),
        };
    }

    /// <summary>
    /// 345319: OnClickClipboard 1077FE0 requests 10,000 rows through A72E30, without a response callback.
    /// df8.c(apt), B0EA30, receives event 418: key 0 = chunk offset, key 1 = exported row count, keys 2..6
    /// = parallel name/reason/parameter/a7m/UTC-tick arrays. It invokes the clipboard action when the last row arrives.
    /// </summary>
    public short ExportAccountLogs(Player player, GetGuildAccountLogsLargeAmountRequest request, out string? error)
    {
        var rc = QueryAccountLogs(player, request.m3, request.m4, request.m5, request.m6, request.m7,
            request.m8, request.m9, request.na, 10_000, out var rows, out error);
        if (rc != 0) return rc;
        if (rows.Length == 0)
        {
            // B0ECC2 reads list[total - 1] via 21939C0: a zero-total event is not a valid empty result.
            // Report the empty export explicitly, without manufacturing a row or touching the clipboard.
            error = "No hay movimientos que coincidan con los filtros para exportar.";
            return Refused;
        }
        // Snapshot once under World.Sync. The offsets address this export, not the requested source-page offset.
        for (var offset = 0; offset < rows.Length; offset += 100)
        {
            var chunk = rows.Skip(offset).Take(100).ToArray();
            player.Session.Send(new GuildAccountLogEventEvent
            {
                cea = offset, ceb = rows.Length,
                cec = chunk.Select(row => row.PlayerName).ToArray(),
                ced = chunk.Select(row => row.Reason).ToArray(),
                cee = chunk.Select(row => row.Parameter).ToArray(),
                cef = chunk.Select(row => row.Amount).ToArray(),
                ceg = chunk.Select(row => row.At).ToArray(),
            });
        }
        return 0;
    }

    private short QueryAccountLogs(Player player, Guid owner, int logType, int offset, int count, int eventFilter,
        int amountFilter, string? search, int days, int maximum, out GuildAccountTransaction[] rows, out string? error)
    {
        rows = [];
        error = null;
        if (Of(player.Character.Id) is not { } guild || guild.Id != owner)
        {
            error = "No puedes consultar la cuenta de otro gremio.";
            return Refused;
        }
        if (logType is < 0 or > 3 || offset < 0 || count < 1 || count > maximum
            || eventFilter is < 0 or > 3 || amountFilter is < 0 or > 2 || days is not (1 or 7 or 28 or 60)
            || search?.Length > 256)
        {
            error = "Los filtros del historial del gremio no son válidos.";
            return Refused;
        }
        // Current TransactionLogs enums: silver=0, gold=1, energy=2, just_event=3;
        // EventFilter Any/Joiners/Leavers/Ranks=0/1/2/3; AmountFilter Any/Deposit/Withdraw=0/1/2.
        // These are independently applied to recorded rows; unsupported currencies remain truthfully empty.
        var earliest = world.GameNow - TimeSpan.FromDays(days).Ticks;
        var name = search?.Trim() ?? "";
        rows = guild.Progress.AccountTransactions.AsEnumerable().Reverse()
            .Where(row => row.LogType == logType && row.At >= earliest
                && (eventFilter == 0 || EventKind(row.Reason) == eventFilter)
                && (name.Length == 0 || row.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase))
                && (amountFilter == 0 || (amountFilter == 1 ? row.Amount > 0 : row.Amount < 0)))
            .Skip(offset).Take(count).ToArray();
        return 0;
    }

    // 345319 GuildHistoryLogEntry.GetLocalizedReasonLocaTag (107C520), switch table at 107C7EC.
    private static int EventKind(int reason) => reason switch
    {
        21 or 22 or 23 => 1, // invited, accepted, instant join
        24 or 25 => 2,      // left, kicked
        26 => 3,           // assigned role
        _ => 0,
    };

    private GuildAccountTransaction AccountRow(Guid character, string name, int reason, string parameter = "", long amount = 0) => new()
    {
        Character = character, PlayerName = name, LogType = EventKind(reason) == 0 ? 0 : 3,
        Reason = reason, Parameter = parameter, Amount = amount, At = world.GameNow,
    };

    private static GuildProgress CopyAccountProgress(Guild guild) =>
        JsonSerializer.Deserialize<GuildProgress>(JsonSerializer.Serialize(guild.Progress))
        ?? throw new InvalidDataException("The guild progress could not be copied.");

    /// <summary>Adds history before the caller's existing atomic guild/member save; no historical reconstruction.</summary>
    private void AddAccountEvent(Guild guild, Guid actor, string actorName, int reason, string parameter = "") =>
        guild.Progress.AccountTransactions.Add(AccountRow(actor, actorName, reason, parameter));

    private static string RoleLogParameter(GuildMember target) =>
        // 107CC45 splits on ';': [0] goes through the role-name adapter, [1] through the player-name adapter.
        // The native formatter localizes the first component and substitutes the second as a player name.
        GuildRoles.Names[GuildRoles.TagIndex(target.Role)] + ";" + target.Name;

    /// <summary>A real tax deposit, recorded with the same guild balance/progress save as the existing PvE tax route.</summary>
    public void DepositTax(Guild guild, Player taxpayer, long amount)
    {
        if (amount <= 0 || !_byId.ContainsKey(guild.Id)) return;
        // Local ledger policy: a tax credit is a deposit (native reason 2); the client has no distinct tax label.
        // The pickup validates both wallets before reaching this method. No reward/tribute deposit is relabeled as tax.
        Deposit(guild, amount);
        guild.Progress.AccountTransactions.Add(AccountRow(taxpayer.Character.Id, taxpayer.Character.Name, 2, amount: amount));
    }

    /// <summary>Existing .guild payout: commit guild debit, recipient credit and withdrawal row together.</summary>
    private string? WithdrawAccount(Guild guild, Player actor, Player recipient, long amount)
    {
        var character = recipient.Character;
        if (character.Transient || amount <= 0 || guild.SilverA7m < amount || character.Silver < 0
            || amount > long.MaxValue - character.Silver)
            return "the recipient's wallet cannot receive this payout";
        if (store is not IGuildAccountStore accountStore) return "the guild account storage is not available";
        var balance = guild.SilverA7m - amount;
        GuildProgress progress;
        long silver;
        try
        {
            (silver, progress) = world.Saves.Commit(() =>
            {
                var nextCharacter = character.Clone();
                nextCharacter.Silver = checked(nextCharacter.Silver + amount);
                var nextProgress = CopyAccountProgress(guild);
                // The native withdrawal row names the member performing the withdrawal. The recipient's
                // actual credit remains in this same transaction and in the existing payout/member logs.
                nextProgress.AccountTransactions.Add(AccountRow(actor.Character.Id, actor.Character.Name, 3, amount: -amount));
                if (!accountStore.SaveGuildAccountTransfer(guild.Id, balance, nextProgress, nextCharacter))
                    throw new InvalidOperationException("The character or guild membership no longer exists.");
                return (nextCharacter.Silver, nextProgress);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("guild", $"payout of guild '{guild.Name}' was not saved: {ex.Message}");
            return "the payout could not be saved, no silver was transferred";
        }
        character.Silver = silver;
        guild.SilverA7m = balance;
        guild.Progress = progress;
        // This transaction writes progress but not pending guild XP/contracts; retain their dirty marker.
        _accountChanged.Add(guild);
        Wallet.Send(recipient);
        return null;
    }
}
