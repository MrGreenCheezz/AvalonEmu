using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Personal portraits and avatar rings. The character's vanity JSON stores earned names; account
/// cosmetics continue to use Vanity/Shop ownership. Every mutation runs under World.Sync.</summary>
public static class Avatars
{
    public static int Ring(Character c) => c.Vanity?.AvatarRing ?? 0;

    private static HashSet<string> Names(VanityLog log, bool ring) => ring ? log.AvatarRings : log.Avatars;

    private static bool Has(Player p, VanityCatalog.Portrait portrait, HashSet<short> account)
    {
        if (!portrait.Locked || p.Character.Vanity is { } saved && Names(saved, portrait.Ring).Contains(portrait.Name)) return true;
        // Preserve the already selected legacy portrait. It is recorded before selecting another one so an
        // existing earned portrait does not disappear merely because older saves had no ownership list.
        if (portrait.Id == (portrait.Ring ? Ring(p.Character) : p.Character.Template)) return true;
        var kind = portrait.Ring ? VanityCatalog.UnlockType.AvatarRing : VanityCatalog.UnlockType.Avatar;
        return p.World.Vanity.Catalog.Find(portrait.Name) is { } unlock && unlock.Type == kind && (unlock.Free || account.Contains(unlock.Id));
    }

    /// <summary>Current 345319 BFD032/BFA612 adds these original ids to the character's portrait/ring name
    /// sets. Send both at Join and after a reward; account portraits are resolved by their XML name.</summary>
    public static void SendOwned(Player p)
    {
        var catalog = p.World.Vanity.Catalog;
        var account = p.World.Vanity.Owned(p.Character);
        p.Session.Send(new UpdateUnlockedAvatarsEvent { d56 = [.. catalog.Portraits.Where(a => Has(p, a, account)).Select(a => a.Id)] });
        p.Session.Send(new UpdateUnlockedAvatarRingsEvent { d55 = [.. catalog.PortraitRings.Where(a => Has(p, a, account)).Select(a => a.Id)] });
    }

    /// <summary>148 response decoder 345319 9DACC8 resolves key 0 as portrait and key 1 as ring. Successful
    /// selection is durable before response/event 160; invalid or unowned ids leave both unchanged.</summary>
    public static ChangeAvatarResponse Change(Player p, int avatar, int ring)
    {
        var result = new ChangeAvatarResponse { ReturnCode = 1, ans = p.Character.Template, ant = Ring(p.Character) };
        var catalog = p.World.Vanity.Catalog;
        var account = p.World.Vanity.Owned(p.Character);
        if (catalog.FindPortrait(avatar) is not { } face || catalog.FindPortrait(ring, true) is not { } frame
            || !Has(p, face, account) || !Has(p, frame, account)) return result;
        if (avatar == p.Character.Template && ring == Ring(p.Character)) { result.ReturnCode = 0; return result; }
        var copy = Snapshot(p);
        var log = copy.Vanity ??= new();
        RememberLegacy(p, log, catalog.FindPortrait(p.Character.Template));
        RememberLegacy(p, log, catalog.FindPortrait(Ring(p.Character), true));
        copy.Template = avatar;
        log.AvatarRing = ring;
        if (!Commit(p, copy)) return result;
        p.Character.Template = copy.Template;
        p.Character.Vanity = log;
        p.World.Saves.MarkChanged(p.Character);
        result.ReturnCode = 0; result.ans = avatar; result.ant = ring;
        p.SendAround(new ChangeAvatarEvent { ddn = p.Id, bwp = avatar, bwq = ring });
        return result;
    }

    private static void RememberLegacy(Player p, VanityLog log, VanityCatalog.Portrait? portrait)
    {
        if (portrait is not { Locked: true }) return;
        // Account unlocks must remain account-scoped (including any later revocation).
        if (p.World.Vanity.Catalog.Find(portrait.Name) is { Type: VanityCatalog.UnlockType.Avatar or VanityCatalog.UnlockType.AvatarRing }) return;
        Names(log, portrait.Ring).Add(portrait.Name);
    }

    public static bool IsReward(SpellTemplate spell) => spell.Effects.Any(e => e.Name.LocalName == "unlockavatar");

    /// <summary>The original inventory unlock spells may award several portraits/rings at once. Resolve every
    /// target before consuming one item, reject unsupported mixed effects or a fully owned reward, then commit
    /// the entire unlock set and the inventory decrement together. "Use all" cannot eat duplicate portraits.</summary>
    public static short Consume(Player p, Item item, SpellTemplate spell)
    {
        if (p.Character.Transient || item.Count <= 0 || !p.Inventory.Bag.Contains(item)
            || spell.Effects.Any(e => e.Name.LocalName is not ("unlockavatar" or "nop"))) return Consumables.GenericFailed;
        var catalog = p.World.Vanity.Catalog;
        var account = p.World.Vanity.Owned(p.Character);
        var unlocks = new List<VanityCatalog.Portrait>();
        foreach (var e in spell.Effects.Where(e => e.Name.LocalName == "unlockavatar"))
        {
            var kind = (string?)e.Attribute("unlocktype");
            var name = (string?)e.Attribute("unlockname");
            if (kind is not ("avatar" or "avatarring") || (string?)e.Attribute("target") is not (null or "self")
                || name is null || catalog.FindPortrait(name, kind == "avatarring") is not { } portrait) return Consumables.GenericFailed;
            if (!Has(p, portrait, account) && !unlocks.Contains(portrait)) unlocks.Add(portrait);
        }
        if (unlocks.Count == 0) return Consumables.GenericFailed;
        var copy = Snapshot(p);
        var log = copy.Vanity ??= new();
        foreach (var portrait in unlocks) Names(log, portrait.Ring).Add(portrait.Name);
        var slot = p.Inventory.Bag.IndexOf(item);
        copy.Inventory[slot] = item.Count > 1 ? copy.Inventory[slot]! with { Count = item.Count - 1 } : null;
        if (!Commit(p, copy)) return Consumables.GenericFailed;
        // Install all committed state before any transport callback. Retrying cannot consume a second copy.
        p.Character.Vanity = log;
        item.Count--;
        if (item.Count == 0) p.Inventory.Bag.Take(slot);
        p.SaveItems();
        var now = p.World.GameNow;
        var here = SpellMovement.PositionOf(p, now);
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = here, bvy = spell.Id, bvz = now, bv0 = now,
            Extra = new() { [6] = (byte)0, [7] = (short)0 } });
        if (item.Count > 0) p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count });
        else p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
        SendOwned(p);
        // 345319 9792B5: false resolves the avatar, true resolves the ring; key 1 is its own catalogue id.
        foreach (var portrait in unlocks) p.Session.Send(new AvatarUnlockedEvent { btf = portrait.Ring, btg = portrait.Id });
        p.SendAround(Combat.CastHit(p.Id, p.Id, spell.Id, 0));
        return 0;
    }

    private static Character Snapshot(Player p)
    {
        Consumables.Store(p);
        var copy = p.Character.Clone();
        p.Inventory.SaveTo(copy, id => p.World.Spells?.Name(id));
        foreach (var vault in p.OpenVaults.Values) copy.Vaults[vault.Key] = Inventory.Records(vault.Container, id => p.World.Spells?.Name(id));
        return copy;
    }

    private static bool Commit(Player p, Character copy)
    {
        if (p.Character.Transient) return false;
        try
        {
            return p.World.Saves.Commit(() =>
            {
                copy.PositionX = p.Character.PositionX; copy.PositionY = p.Character.PositionY;
                copy.PlayedMilliseconds = p.Character.PlayedMilliseconds;
                return p.World.Characters.Save(copy);
            });
        }
        catch (Exception e)
        {
            Log.Error("avatars", $"Cannot save avatar reward/selection for {p.Character.Id}: {e.Message}");
            return false;
        }
    }
}
