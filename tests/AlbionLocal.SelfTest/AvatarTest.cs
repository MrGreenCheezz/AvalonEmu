using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using UnlockType = AlbionLocal.Server.Data.VanityCatalog.UnlockType;

namespace AlbionLocal.SelfTest;

/// <summary>Public issue #22: changing the portrait avatar / ring (ChangeAvatar 148) and the body (ChangeCustomization 388), in Thetford with two players.</summary>
public static class AvatarTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("avatar change: free avatar + owned ring applied (148 response, 160 to both), saved (v46), Join keys 3/4 after a rejoin; a locked avatar refused (5922) unchanged", Avatar),
        ("customization change: female look applied (410 to both), saved, Join keys 5-7 after a rejoin; an unowned Buyable hair refused unchanged", Customization),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static (FakePeer P, JoinResponse R) Rejoin(QolTest.Zone z, Guid id)
    {
        lock (z.World.Sync) z.A.P.Disconnect();
        var peer = new FakePeer();
        var s = new WorldSession(peer, z.World);
        peer.Disconnected = s.OnDisconnected;
        lock (z.World.Sync) s.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
        var r = new JoinResponse();
        AlbionSerializer.FillFields(r, peer.Responses[0].Parameters);
        return (peer, r);
    }

    private static void Avatar()
    {
        using var z = new QolTest.Zone();
        var v = z.World.Vanity;
        var a = z.A.S.Player!;
        var avatar = v.Catalog.Avatars.First(e => e.Name == "AVATAR_05").Number;
        var ring = v.Catalog.Rings.First(e => e.Name == "AVATARRING_TELLAFRIEND");
        var locked = v.Catalog.Avatars.First(e => e.Name == "AVATAR_FAMERANK_01").Number;
        Check(avatar == 4 && ring.Number > 0 && ring.AccountUnlock == ring.Name, $"avatars.xml numbers: AVATAR_05 = 4, {ring.Name} = ring {ring.Number} (needs its vanity unlock)");
        lock (z.World.Sync) v.Grant(a, v.Catalog.All.First(u => u.Type == UnlockType.AvatarRing && u.Name == ring.Name), "test");

        int am = z.A.P.Events.Count, bm = z.B.P.Events.Count;
        var r = z.Send(z.A, new ChangeAvatarRequest { d7 = avatar, d8 = ring.Number });
        static bool Is(Dictionary<byte, object?> p, long id, int av, int rg) => L(p, 0) == id && L(p, 1) == av && L(p, 2) == rg;
        Check(r is { ReturnCode: 0 } && L(r.Parameters, 0) == avatar && L(r.Parameters, 1) == ring.Number
              && a.Character is { Template: 4 } c && c.AvatarRing == ring.Number
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.ChangeAvatar && Is(e.P, a.Id, avatar, ring.Number))
              && Since(z.B.P, bm).Any(e => e.Code == EventCodes.ChangeAvatar && Is(e.P, a.Id, avatar, ring.Number))
              && a.BuildCreate(DateTime.UtcNow) is { cvz: 4 } nc && nc.cv0 == ring.Number,
              "ChangeAvatar: RC 0 with the values, ChangeAvatar 160 to Ann and Bob, NewCharacter keys 2/3 carry them");

        am = z.A.P.Events.Count;
        r = z.Send(z.A, new ChangeAvatarRequest { d7 = locked, d8 = ring.Number });
        Check(r is { ReturnCode: Vanity.AvatarLocked } && L(r.Parameters, 0) == avatar && a.Character.Template == avatar
              && !Since(z.A.P, am).Any(e => e.Code == EventCodes.ChangeAvatar),
              "a locked avatar (AVATAR_FAMERANK_01): RC 5922 with the avatar in force, no event, unchanged");

        lock (z.World.Sync) z.Characters.Save(a.Character.Clone());
        Check(z.Characters.Find(a.Character.Id) is { Template: 4 } saved && saved.AvatarRing == ring.Number, "saved with the character (avatar_ring, schema v46)");
        var (_, join) = Rejoin(z, a.Character.Id);
        Check(join.a8d == avatar && join.a8e == ring.Number, $"after a rejoin Join keys 3/4 are the avatar and ring (got {join.a8d}/{join.a8e})");
    }

    private static void Customization()
    {
        using var z = new QolTest.Zone();
        var a = z.A.S.Player!;
        var female = z.World.GameData!.Looks.Items[1];
        int hair = female.ToList().FindIndex(i => i.Name == "HAIR");
        var buyable = Enumerable.Range(0, female[hair].Meshes.Length).First(m => !female[hair].FreeMeshes.Contains(m));
        int[] meshes = [1, 0, 20, 5], colors = [0, 4, 14, 3];
        Check(female.Count == 4 && hair == 2 && female[hair].FreeMeshes.Contains(20), "HUMAN_FEMALE: FACE, SKIN, HAIR, UNDERWEAR; hair 20 is free");

        int am = z.A.P.Events.Count, bm = z.B.P.Events.Count;
        static bool Is(Dictionary<byte, object?> p, long id, int[] m, int[] c) =>
            L(p, 0) == id && L(p, 1) == 1 && p[2] is int[] gm && gm.SequenceEqual(m) && p[3] is int[] gc && gc.SequenceEqual(c);
        Check(z.Send(z.A, new ChangeCustomizationRequest { ek = 1, el = meshes, em = colors, en = true }) is { ReturnCode: 0 }
              && a.Character is { Appearance: 1 } c && c.Customization.SequenceEqual(meshes) && c.Clothes.SequenceEqual(colors)
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.CustomizationChanged && Is(e.P, a.Id, meshes, colors))
              && Since(z.B.P, bm).Any(e => e.Code == EventCodes.CustomizationChanged && Is(e.P, a.Id, meshes, colors))
              && a.BuildCreate(DateTime.UtcNow) is { cv1: 1 } nc && nc.cv2!.SequenceEqual(meshes) && nc.cv3!.SequenceEqual(colors),
              "ChangeCustomization: RC 0, the character is female with the arrays, CustomizationChanged 410 to Ann and Bob, NewCharacter keys 4-6");

        am = z.A.P.Events.Count;
        Check(z.Send(z.A, new ChangeCustomizationRequest { ek = 1, el = [1, 0, buyable, 5], em = colors, en = true }) is { ReturnCode: Vanity.Refused }
              && z.Send(z.A, new ChangeCustomizationRequest { ek = 0, el = meshes, em = colors, en = true }) is { ReturnCode: Vanity.Refused }
              && a.Character.Customization.SequenceEqual(meshes) && a.Character.Appearance == 1
              && !Since(z.A.P, am).Any(e => e.Code == EventCodes.CustomizationChanged),
              $"an unowned Buyable hair ({female[hair].Meshes[buyable]}) and male with the 4 female entries: refused, no event, unchanged");

        lock (z.World.Sync) z.Characters.Save(a.Character.Clone());
        var (_, join) = Rejoin(z, a.Character.Id);
        Check(join.a8f == 1 && join.a8g!.SequenceEqual(meshes) && join.a8h!.SequenceEqual(colors), "after a rejoin Join keys 5/6/7 carry the new look");
    }
}
