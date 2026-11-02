using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    // 345319 gamedata.bin: <PersonalStatement characterlimit="270"/>. InitGui 15D8AC0 copies
    // the parsed limit to UIInput.characterLimit (UTF-16 code units, like System.String.Length).
    private const int CharacterStatementLimit = 270;

    /// <summary>
    /// 345319 OnSubmitCharacterStatement (15DBE00) trims the input and sends only key 0 (A7B3A0).
    /// It then requests fresh statistics by name (9B93C0 -> A6BC10), so event 142 below supplies
    /// the committed text, including a cleared statement. No target name/id is accepted by this operation.
    /// </summary>
    [Handles(OperationCodes.UpdateCharacterStatement, Status = SessionStatus.InWorld)]
    private AlbionResponse? UpdateCharacterStatement(UpdateCharacterStatementRequest req)
    {
        if (Player is not { } p || p.Character.Transient) return new GenericResponse { ReturnCode = 1 };
        var text = req.ahd ?? ""; // An omitted/empty string clears the statement.
        if (text.Length > CharacterStatementLimit || !ValidCharacterStatement(text)) return new GenericResponse { ReturnCode = 1 };
        text = text.Trim();
        var c = p.Character;
        if (c.Statement == text) return new GenericResponse();
        try
        {
            // Drain older snapshots before committing the copy. Publish the text only after it is durable;
            // a failed write leaves the live statement and the client's subsequent query unchanged.
            var saved = world.Saves.Commit(() =>
            {
                p.StorePosition(DateTime.UtcNow);
                Systems.Consumables.Store(p);
                var copy = c.Clone();
                p.Inventory.SaveTo(copy, id => world.Spells?.Name(id));
                foreach (var vault in p.OpenVaults.Values)
                    copy.Vaults[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
                copy.Statement = text;
                return world.Characters.Save(copy);
            });
            if (!saved) return new GenericResponse { ReturnCode = 1 };
        }
        catch (Exception e)
        {
            Log.Error("profile", $"Cannot save statement for {c.Id}: {e.Message}");
            return new GenericResponse { ReturnCode = 1 };
        }
        c.Statement = text;
        world.Saves.MarkChanged(c);
        return new GenericResponse();
    }

    private static bool ValidCharacterStatement(string text)
    {
        // The native label handles user-generated text. Keep punctuation and Unicode intact; reject
        // transport control characters and malformed UTF-16, while allowing ordinary multiline text.
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')) return false;
            if (char.IsHighSurrogate(ch))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// GetCharacterStats (145): key 0 is the name, key 1 RankingEntityTypes (0 character). The 345319
    /// client routes event 142 by name (9B8540), including offline characters; ddn is not its lookup key.
    /// Its 9BA5E0 consumer marks the statistics ready, and 143-146 separately complete the four histories.
    /// A successful operation response alone never completes these requests. The response callback
    /// (9B95E0) reports NotFound for a nonzero return code, including unsupported ranking entity types.
    /// </summary>
    [Handles(OperationCodes.GetCharacterStats, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetCharacterStats(GetCharacterStatsRequest req)
    {
        if (req.mg == 1) return GetGuildStats(req);
        // Guild/alliance/faction statistics have separate event contracts and lifetime aggregates.
        // Do not answer their requests with a character's figures or leave them waiting on an ACK.
        if (req.mg != 0 || string.IsNullOrWhiteSpace(req.mf)) return new GenericResponse { ReturnCode = 1 };
        var name = req.mf.Trim();
        var online = world.Maps.Maps.SelectMany(m => m.Players.Values)
            .FirstOrDefault(p => p.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var c = online?.Character ?? world.Characters.Find(name);
        if (c is null) return new GenericResponse { ReturnCode = 1 };
        var stats = c.Statistics;

        var guild = world.Guilds.Of(c.Id);
        var id = online?.Id ?? 0;
        long Current(int type) => c.Infamy?.Current.GetValueOrDefault(type) ?? 0;
        long Peak(int type) => Math.Max(Current(type), c.Infamy?.Peak.GetValueOrDefault(type) ?? 0);

        SendResponse(OperationCodes.GetCharacterStats, new GenericResponse(), RequestId, RequestEncrypted);
        SendEvent(new CharacterStatsEvent
        {
            ddn = id,
            dnz = c.Name,
            dn0 = guild?.Name ?? "",
            dn1 = (byte)(guild?.Find(c.Id)?.Role ?? GuildRole.None),
            dn2 = world.Alliances.Of(c.Id)?.Name ?? "",
            dn3 = c.Statement,
            // 345319 15DFCD6-15DFDCC shows @GENERIC_SILVER only for the local character;
            // 9BA87A-9BA89D takes this balance from key 6. Do not expose another character's wallet.
            dn4 = Player?.Character.Id == c.Id ? c.Silver : 0,
            dn5 = c.Fame, // Total fame, raw a7m. This is NOT lifetime PvE fame (key 13).
            dn6 = (float)c.Reputation,
            dn7 = new Dictionary<int, long> { [0] = c.MistStanding }, // StandingType.MistCity, raw a7m.
            // Current 345319 9BA8B6-9BA91C maps these fields to the native statistics rows:
            // 15DFECD PvP, 15DFF6E PvE, 15E0012 gathering, 15E00B6 crafting, 15E016F kills.
            dn8 = stats?.PlayersKilled ?? 0,
            dn9 = stats?.PvpFame ?? 0,
            dob = stats?.PveFame ?? 0,
            doc = stats?.GatheringFame ?? 0,
            doe = stats?.CraftingFame ?? 0,
            // 345319 9BA98E-9BA9B9 copies keys 25-28 to the profile model. Its header
            // (15DF0ED-15DF1C0) resolves the selected portrait using body type and flagging,
            // independently of the already correct Join/HUD appearance. Bots retain their own layout.
            don = online?.Bot?.Avatar ?? c.Template,
            doo = Systems.Avatars.Ring(c),
            dop = online?.Flag ?? c.Flag,
            doq = online?.Bot is not null ? c.Template : c.Appearance,
            dpd = Current(1), dpe = Peak(1), // Corrupted dungeons, current / peak (integer infamy).
            dpf = Current(2), dpg = Peak(2), // Hellgates 2v2.
            dph = Current(3), dpi = Peak(3), // Hellgates 5v5.
            dpj = Current(4), dpk = Peak(4), // Hellgates 10v10.
        });

        // Prospectively recorded only; coverage metadata stays in Statistics because this protocol
        // has no coverage field. Do not fabricate prior history from total fame or Journal/Armory.
        // Current consumers 9B9A60/9B9D50/9BA040/9BA340 require aligned arrays, including 16-byte GUIDs.
        var kills = stats?.Kills ?? [];
        var deaths = stats?.DeathHistory ?? [];
        var knockdowns = stats?.KnockdownHistory ?? [];
        var knockedDown = stats?.KnockedDownHistory ?? [];
        SendEvent(new CharacterStatsKillHistoryEvent
        {
            ddn = id, dpv = c.Name,
            dpw = [.. kills.Select(r => r.Opponent)], dpx = [.. kills.Select(r => r.Fame)],
            dpy = [.. kills.Select(r => r.Id.ToByteArray())], dpz = [.. kills.Select(r => r.FighterType)], dp0 = [.. kills.Select(r => r.Ticks)],
        });
        SendEvent(new CharacterStatsDeathHistoryEvent
        {
            ddn = id, dpp = c.Name,
            dpq = [.. deaths.Select(r => r.Opponent)], dpr = [.. deaths.Select(r => r.Fame)],
            dps = [.. deaths.Select(r => r.Id.ToByteArray())], dpt = [.. deaths.Select(r => r.FighterType)], dpu = [.. deaths.Select(r => r.Ticks)],
        });
        SendEvent(new CharacterStatsKnockDownHistoryEvent
        {
            ddn = id, dp1 = c.Name, dp2 = [.. knockdowns.Select(r => r.Opponent)],
            dp3 = [.. knockdowns.Select(r => r.Id.ToByteArray())], dp4 = [.. knockdowns.Select(r => r.FighterType)], dp5 = [.. knockdowns.Select(r => r.Ticks)],
        });
        SendEvent(new CharacterStatsKnockedDownHistoryEvent
        {
            ddn = id, dp6 = c.Name, dp7 = [.. knockedDown.Select(r => r.Opponent)],
            dp8 = [.. knockedDown.Select(r => r.Id.ToByteArray())], dp9 = [.. knockedDown.Select(r => r.FighterType)], dqa = [.. knockedDown.Select(r => r.Ticks)],
        });
        return null;
    }

    /// <summary>
    /// History rows open the native detail window (345319 E5C0E0). The response must echo the GUID
    /// as bytes even on failure (C05210 routes it); the detail itself is event 148, not response key 0.
    /// C05940/BF4C90 consumes GUID + JSON + knockdown flag. Sending the event first makes the model
    /// available before the operation callback; missing/expired records explicitly complete as failed.
    /// </summary>
    [Handles(OperationCodes.GetKillHistoryDetails, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetKillHistoryDetails(GetKillHistoryDetailsRequest req)
    {
        var record = req.ny == Guid.Empty ? null : world.Maps.Maps.SelectMany(m => m.Players.Values)
            .Select(p => p.Character.Statistics?.FindCombat(req.ny)).FirstOrDefault(r => r is not null)
            ?? world.Characters.FindCombatRecord(req.ny);
        if (record is null) return new GetKillHistoryDetailsResponse { ReturnCode = 1, a0e = req.ny.ToByteArray() };
        SendEvent(new KillHistoryDetailsEvent { drr = record.Id.ToByteArray(), drs = record.Details, drt = record.Knockdown });
        return new GetKillHistoryDetailsResponse { a0e = record.Id.ToByteArray() };
    }
}
