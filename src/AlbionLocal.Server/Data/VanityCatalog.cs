using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The client's vanity unlocks (analysis/work/stageN/vanity/SPEC.md §1): the short ids GetVanityUnlocks (op 383 key 0), BuyVanityUnlocks and
/// UnlockVanityUnlock (event 408 key 0) carry. The client numbers them with one counter (bgk.b's static, reset by bgk's ctor) in the order its
/// data post-processing registers them: characters.xml (bbl.db: the &lt;Mesh&gt; of every &lt;Customization&gt;/&lt;Item&gt;, then the &lt;Color&gt;
/// of every &lt;ColorSet&gt;), mountskins.xml (bed.db: every &lt;mountskin&gt; whose reference is a &lt;mountskin&gt; item of items.xml),
/// wardrobe.xml (bgm.db: every &lt;item&gt;, the first of a reference only, whose reference is an equipmentitem or weapon), emotes.xml (bcb.db:
/// every &lt;Emote&gt; with uniquename and command) and avatars.xml (bbg.db: &lt;peraccountavatars&gt;, then &lt;peraccountavatarrings&gt;).
/// Checked against the official GetVanityUnlocks of capture 002835 (ids 239/349/439/522/608 = the Duelist cougar, hood, armor, shoes, cape).
/// Besides the id, a mount skin has its own number (bed.b +0x58: every &lt;mountskin&gt; of the file counts) that GetMountSkins, SetMountSkin and
/// the mount's skin keys carry, with its category's index (bed.a +0x20); a wardrobe skin its number (bgm.b +0x58: every &lt;item&gt;) that the
/// wardrobe arrays (Join 108, NewCharacter 55, SetWardrobe, UpdateWardrobe) carry, at the EquipmentSlot of its category (MAIN_HAND 0 … CAPE 6).
/// </summary>
public sealed class VanityCatalog
{
    /// <summary>Albion.Common.Vanity.VanityUnlockType.</summary>
    public enum UnlockType { Emote = 0, Mount = 1, Skin = 2, Customization = 3, Avatar = 4, AvatarRing = 5 }

    /// <summary>
    /// One unlock: its id, type, uniquename (the item reference of a skin), state (Locked, Hidden, Unlocked, Buyable: bgk.a's default is
    /// Buyable with a goldprice, else Unlocked), availablevia, and for a skin its own number and slot / category (-1 otherwise).
    /// </summary>
    public sealed record Unlock(short Id, UnlockType Type, string Name, string State, string AvailableVia, int Number, int Slot)
    {
        /// <summary>Owned without being unlocked (state Unlocked).</summary>
        public bool Free => State == "Unlocked";

        public override string ToString() => $"{Id} {Type} {Name}";
    }

    public IReadOnlyList<Unlock> All { get; }

    /// <summary>mountskins.xml's &lt;mountcategory&gt; names by their index.</summary>
    public IReadOnlyList<string> MountCategories { get; }

    private readonly Dictionary<int, Unlock> _mountSkins = new(), _wardrobe = new();
    private readonly Dictionary<string, Unlock> _byName = new(StringComparer.OrdinalIgnoreCase);

    private VanityCatalog(List<Unlock> all, List<string> categories)
    {
        All = all;
        MountCategories = categories;
        foreach (var u in all)
        {
            if (u.Type == UnlockType.Mount) _mountSkins[u.Number] = u;
            else if (u.Type == UnlockType.Skin) _wardrobe[u.Number] = u;
            _byName.TryAdd(u.Name, u);
        }
    }

    public static readonly VanityCatalog Empty = new([], []);

    public Unlock? Find(int id) => id >= 0 && id < All.Count ? All[id] : null;

    public Unlock? Find(string name) => _byName.GetValueOrDefault(name.Trim());

    /// <summary>The mount skin of number <paramref name="number"/> (GetMountSkins / SetMountSkin / NewMountObject key 2).</summary>
    public Unlock? MountSkin(int number) => _mountSkins.GetValueOrDefault(number);

    /// <summary>The wardrobe skin of number <paramref name="number"/> (the wardrobe arrays).</summary>
    public Unlock? WardrobeSkin(int number) => _wardrobe.GetValueOrDefault(number);

    /// <summary>The index of mount category <paramref name="name"/> (a mount item's mountcategory); -1 = none.</summary>
    public int MountCategory(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (var i = 0; i < MountCategories.Count; i++)
            if (string.Equals(MountCategories[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The wardrobecategory names in EquipmentSlot order.</summary>
    private static readonly string[] Slots = ["MAIN_HAND", "OFF_HAND", "HEAD", "ARMOR", "SHOES", "BAG", "CAPE"];

    /// <summary>Reads the five files of <paramref name="folder"/> (the client's GameData) with <paramref name="items"/> for the references.</summary>
    public static VanityCatalog Load(string folder, ItemStore items) => Load(
        GameDataFile.Open(folder, "characters.bin").Root(), GameDataFile.Open(folder, "mountskins.bin").Root(),
        GameDataFile.Open(folder, "wardrobe.bin").Root(), GameDataFile.Open(folder, "emotes.bin").Root(),
        GameDataFile.Open(folder, "avatars.bin").Root(), name => items.Find(name)?.Kind);

    public static VanityCatalog Load(XElement characters, XElement mountskins, XElement wardrobe, XElement emotes, XElement avatars, Func<string, string?> itemKind)
    {
        var all = new List<Unlock>();
        void Add(UnlockType type, string name, XElement e, int number = -1, int slot = -1)
        {
            var gold = int.TryParse((string?)e.Attribute("goldprice"), out var g) ? g : 0;
            var state = (string?)e.Attribute("state") ?? (gold > 0 ? "Buyable" : "Unlocked");
            all.Add(new Unlock((short)all.Count, type, name, state, (string?)e.Attribute("availablevia") ?? (gold > 0 ? "Gold" : "None"), number, slot));
        }
        // characters.xml: the meshes of every customization's items, then the colours of every colour set (b3f, then b3e).
        if (characters.Element("Customizations") is { } cz)
        {
            foreach (var c in cz.Elements("Customization"))
                foreach (var it in c.Elements("Item"))
                    foreach (var m in it.Elements("Mesh")) Add(UnlockType.Customization, (string?)m.Attribute("uniquename") ?? "", m);
            foreach (var cs in cz.Elements("ColorSet"))
                foreach (var col in cs.Elements("Color")) Add(UnlockType.Customization, (string?)col.Attribute("uniquename") ?? "", col);
        }
        // mountskins.xml: every mountskin counts for the skin number; registered when its reference is a mountskin item (bed.b.a1 → bdq).
        var categories = new List<string>();
        var skinNumber = 0;
        foreach (var cat in mountskins.Elements("mountcategory"))
        {
            var name = (string?)cat.Attribute("uniquename");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty((string?)cat.Attribute("icon"))) continue;
            var index = categories.IndexOf(name);
            if (index < 0) { index = categories.Count; categories.Add(name); }
            foreach (var s in cat.Elements())
            {
                var number = skinNumber++;
                var reference = (string?)s.Attribute("reference");
                if (s.Name.LocalName == "mountskin" && !string.IsNullOrEmpty(reference) && itemKind(reference) == "mountskin")
                    Add(UnlockType.Mount, reference, s, number, index);
            }
        }
        // wardrobe.xml: every item counts; the first of a reference only, registered when it is an equipment item (bgm.b.a1 → bc9).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var itemNumber = 0;
        foreach (var cat in wardrobe.Elements("wardrobecategory"))
        {
            if (string.IsNullOrEmpty((string?)cat.Attribute("uniquename")) || string.IsNullOrEmpty((string?)cat.Attribute("icon"))) continue;
            var slot = Array.IndexOf(Slots, ((string?)cat.Attribute("uniquename") ?? "").ToUpperInvariant());
            foreach (var s in cat.Elements())
            {
                var number = itemNumber++;
                var reference = (string?)s.Attribute("reference");
                if (string.IsNullOrEmpty(reference) || !seen.Add(reference)) continue;
                if (itemKind(reference) is "equipmentitem" or "weapon") Add(UnlockType.Skin, reference, s, number, slot);
            }
        }
        foreach (var e in emotes.Elements("Emote"))
            if (!string.IsNullOrEmpty((string?)e.Attribute("uniquename")) && !string.IsNullOrEmpty((string?)e.Attribute("command")))
                Add(UnlockType.Emote, (string)e.Attribute("uniquename")!, e);
        foreach (var a in avatars.Element("peraccountavatars")?.Elements("avatar") ?? [])
            Add(UnlockType.Avatar, (string?)a.Attribute("name") ?? "", a);
        foreach (var a in avatars.Element("peraccountavatarrings")?.Elements("avatarring") ?? [])
            Add(UnlockType.AvatarRing, (string?)a.Attribute("name") ?? "", a);
        return new VanityCatalog(all, categories);
    }
}
