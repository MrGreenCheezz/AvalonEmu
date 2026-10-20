using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.CombatTest;
using UnlockType = AlbionLocal.Server.Data.VanityCatalog.UnlockType;

namespace AlbionLocal.SelfTest;

/// <summary>StageN vanity (analysis/work/stageN/vanity/SPEC.md): cosmetics from content only, in Thetford with two players.</summary>
public static class VanityTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("vanity drop: the capture's ids, a forced themed drop unlocks a skin (408), GetVanityUnlocks lists it, buying is refused", Drop),
        ("vanity apply: wardrobe (387 → 416 to both, an unowned slot dropped), mount skin (386/385), saved (v35), NewCharacter 55", Apply),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Drop()
    {
        using var z = new QolTest.Zone();
        var v = z.World.Vanity;
        var cat = v.Catalog;
        Check(cat.Find(239)?.Name == "SKIN_COUGAR_DUELIST" && cat.Find(349)?.Name == "UNIQUE_HEAD_VANITY_DUELIST" && cat.Find(608)?.Name == "UNIQUE_CAPE_VANITY_DUELIST"
              && cat.All.Count == 721 && v.Pool.Count > 300, $"ids as the official GetVanityUnlocks (239 cougar, 349 hood, 608 cape of the Duelist); {cat.All.Count} unlocks, {v.Pool.Count} droppable");
        var a = z.A.S.Player!;
        int am = z.A.P.Events.Count;
        v.Random = new Random(7);
        var u = v.Drop(a, "test", Vanity.ThemeOf("T6_UNDEAD_BOSS"));
        Check(u is { Type: UnlockType.Skin or UnlockType.Mount } && a.Character.Vanity!.Unlocks.Contains(u.Id)
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.UnlockVanityUnlock && L(e.P, 0) == u.Id && e.P[1] is true),
              $"a forced drop unlocks {u}: UnlockVanityUnlock 408 {{0 id, 1 true}}");
        var list = z.Send(z.A, new GetVanityUnlocksRequest());
        Check(list is { ReturnCode: 0 } && list.Parameters[0] is short[] ids && ids.Contains(u!.Id), "GetVanityUnlocks 383 key 0 has it (the store shows it owned)");
        Check(z.Send(z.A, new BuyVanityUnlocksRequest { dt = 2, du = [5], dv = 10_000_000 }) is { ReturnCode: Vanity.Refused } && !a.Character.Vanity!.Unlocks.Contains((short)5),
              "BuyVanityUnlocks: refused, nothing unlocked");
    }

    private static void Apply()
    {
        using var z = new QolTest.Zone();
        var v = z.World.Vanity;
        var a = z.A.S.Player!;
        var hood = v.Catalog.Find("UNIQUE_HEAD_VANITY_DUELIST")!;
        var armor = v.Catalog.Find("UNIQUE_ARMOR_VANITY_DUELIST")!;
        var ox = v.Pool.First(s => s.Type == UnlockType.Mount && v.Catalog.MountCategories[s.Slot] == "OXEN");
        lock (z.World.Sync) { v.Grant(a, hood, "test"); v.Grant(a, ox, "test"); }
        int am = z.A.P.Events.Count, bm = z.B.P.Events.Count;
        var wanted = new[] { -1, -1, hood.Number, armor.Number, -1, -1, -1 };
        var set = z.Send(z.A, new SetWardrobeRequest { agb = wanted });
        int[] kept = [-1, -1, hood.Number, -1, -1, -1, -1];
        static bool Is(Dictionary<byte, object?> p, long id, int[] w) => L(p, 0) == id && p[1] is int[] got && got.SequenceEqual(w);
        Check(set is { ReturnCode: 0 } && a.Character.Vanity!.Wardrobe.SequenceEqual(kept)
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.UpdateWardrobe && Is(e.P, a.Id, kept))
              && Since(z.B.P, bm).Any(e => e.Code == EventCodes.UpdateWardrobe && Is(e.P, a.Id, kept)),
              "SetWardrobe: the owned hood kept at Head, the unowned armor dropped; UpdateWardrobe 416 to Ann and Bob");
        var category = ox.Slot;
        Check(z.Send(z.A, new SetMountSkinRequest { afw = category, afx = ox.Number }) is { ReturnCode: 0 }
              && z.Send(z.A, new GetMountSkinsRequest()) is { Parameters: var mp } && mp[0] is int[] skins && skins.SequenceEqual([ox.Number])
              && z.Send(z.A, new SetMountSkinRequest { afw = category, afx = v.Pool.First(s => s.Type == UnlockType.Mount && s.Slot == category && s != ox).Number }) is { ReturnCode: Vanity.Refused }
              && v.MountSkinFor(a.Character, z.World.Items.Find("T5_MOUNT_OX")) == ox.Number,
              $"SetMountSkin {ox}: rc 0, GetMountSkins lists it, another not unlocked: refused; a T5 ox wears it");
        lock (z.World.Sync) z.Characters.Save(a.Character.Clone());
        var saved = z.Characters.Find(a.Character.Id)?.Vanity;
        Check(saved is not null && saved.Wardrobe.SequenceEqual(kept) && saved.MountSkins.GetValueOrDefault(category) == ox.Number && saved.Unlocks.Contains(hood.Id),
              "saved with the character (schema v35): unlocks, wardrobe, mount skin");
        Check(a.BuildCreate(DateTime.UtcNow).cxg is { } w55 && w55.SequenceEqual(kept), "NewCharacter key 55 (for the others) is the wardrobe");
    }
}
