using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// A city bank's personal vault per character and city (one for all cities under Bank:Shared, <see cref="MergeVaults"/>).
/// BankVaultInfo (413) announces the complete owned tab list, including the implicit default. Purchase/edit metadata use
/// ContainerManageSubContainer (100), with ownership and silver committed together before the event is refreshed.
/// The main default view is attached on RegisterToObject (<see cref="PushBankVault"/>), another view on ContainerOpen.
/// Moving items in and out is the
/// ordinary InventoryMoveItem with the vault's container Guid (<see cref="Player.FindContainer"/>) or InventoryMoveGivenItems
/// (shift+click), 128 slots and the CityBank weight limit; the vault is saved with the character (Character.Vaults, schema
/// v7 bank_items).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The default vault tab's Guid: bgf.u0[0] (bgf cctor 0x1805C4310 stores it at element 0, the verifier).</summary>
    public static readonly Guid DefaultTab = BankTabCatalog.Ids[0];

    /// <summary>The RC of a refused ContainerOpen (any non-zero code makes the client read keys 0-2 and close the view; not traced which it shows).</summary>
    public const short ContainerRefused = 1;

    /// <summary>
    /// RegisterToObject (op 45) also marks the object the player's client is busy with (a bank, a marketplace): ContainerOpen
    /// requires it. UnRegisterFromObject (op 46, no response class: unanswered as before) clears it and closes the vault tabs
    /// of that bank still open (as the last ContainerClose does), so a vault's container Guid works only at the bank; for the open
    /// loot bag it clears <see cref="Player.OpenBag"/>.
    /// </summary>
    [Handles(OperationCodes.UnRegisterFromObject, Status = SessionStatus.InWorld)]
    private AlbionResponse? UnRegisterFromObject(UnRegisterFromObjectRequest req)
    {
        if (Player is not { } player) return null;
        if (player.RegisteredTo == req.ahc) player.RegisteredTo = null;
        if (player.OpenBag?.Id == req.ahc) player.OpenBag = null; // stageM loot-moves: a closed loot window takes nothing more
        DetachVaults(player, v => v.Building == req.ahc);
        return null;
    }

    /// <summary>
    /// The container Guid of a vault (AttachItemContainer key 1): made from the character and the vault key, so it is the same
    /// for every opening (Q-B1: whether official uses the tab Guid itself is open; a Guid of its own is safe).
    /// </summary>
    public static Guid VaultContainerId(Guid character, string vaultKey)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"albionlocal:vault:{character}:{vaultKey}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3 (name based, MD5)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    /// <summary>
    /// ContainerOpen (op 98, re: 0 the bank's object id, 1 view slot 0 main / 1 additional, 2 tab Guid). Checked: the object is a
    /// bankbuilding on the player's map that its client registered to, and the tab belongs to this character/location. Then the tab is
    /// attached (<see cref="AttachVault"/>) and the response {0, 1, 2} with RC 0 follows (the client ignores an RC-0 body): items
    /// before the container, the container after the open, or the client drops it. A refusal: RC ≠ 0 with keys 0-2 echoed
    /// (the client closes the view). The client sends it only for a view it fills itself (another tab, the additional view):
    /// the main view's default tab comes with the RegisterToObject (<see cref="PushBankVault"/>).
    /// </summary>
    [Handles(OperationCodes.ContainerOpen, Status = SessionStatus.InWorld)]
    private AlbionResponse? ContainerOpen(ContainerOpenRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        var answer = new ContainerOpenResponse { aok = req.hi, aol = req.hj, aom = req.hk };
        AlbionResponse Refuse(string why)
        {
            Log.Info(Name, $"'{player.Character.Name}' ContainerOpen of object {req.hi} (view {req.hj}, tab {req.hk}) refused: {why}");
            answer.ReturnCode = ContainerRefused;
            return answer;
        }
        if (!map.Objects.TryGetValue(req.hi, out var o) || o is not Building { Template.Kind: "bankbuilding" } bank)
            return Refuse("not a bank on the player's map");
        if (player.RegisteredTo != req.hi) return Refuse("the client did not register to the bank (RegisterToObject)");
        if (req.hj is < 0 or > 1) return Refuse("invalid view slot");
        if (!PersonalBankTabs(player).Any(t => t.Id == req.hk)) return Refuse("the character does not own this tab here");
        if (BankUnusable(player, bank) is { } unusable) return Refuse(unusable);
        AttachVault(player, bank, req.hj, req.hk);
        return answer;
    }

    /// <summary>
    /// RegisterToObject attaches the default into main view 0, then announces owned metadata without waiting for
    /// ContainerOpen. The client creates that view locally (ckv.s 0x180B3C4D0); omitting its AttachItemContainer would leave
    /// the main vault unusable. Nothing while knocked down or beyond <see cref="StationRange"/>.
    /// </summary>
    private void PushBankVault(Player player, long building)
    {
        if (player.Map is not { } map || !map.Objects.TryGetValue(building, out var o) || o is not Building { Template.Kind: "bankbuilding" } bank)
            return;
        if (BankUnusable(player, bank) is { } why)
        {
            Log.Info(Name, $"'{player.Character.Name}' registered to the bank {bank.Id}: no vault, {why}");
            return;
        }
        // 413's first manager notification may initialize the visible UIListbox. Bind the default container first,
        // preserving the already working main-view lifecycle instead of rebuilding it against an unattached model.
        AttachVault(player, bank, 0, DefaultTab);
        PushBankTabInfo(player, bank);
    }

    /// <summary>Native 345319 op100: action1 buys the next bgf.u0 identity; action3 edits owned metadata.
    /// Keys5/6/7 are name/icon/packed color, key8 is reserved0 (not tab order). Deletion is not exposed by the native
    /// personal-tab GUI and is deliberately refused, preserving ownership and every bank_items key.</summary>
    [Handles(OperationCodes.ContainerManageSubContainer, Status = SessionStatus.InWorld)]
    private AlbionResponse? ContainerManageSubContainer(ContainerManageSubContainerRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        AlbionResponse? Refuse(string why)
        {
            Log.Info(Name, $"'{player.Character.Name}' bank tab action {req.hb} for {req.hc} refused: {why}");
            Systems.Expeditions.Tell(player, why);
            return null;
        }
        if (!map.Objects.TryGetValue(req.g9, out var o) || o is not Building { Template.Kind: "bankbuilding" } bank)
            return Refuse("No puedes administrar este banco.");
        if (player.RegisteredTo != bank.Id || BankUnusable(player, bank) is not null)
            return Refuse("Acércate al banco y vuelve a abrirlo.");
        if (!string.Equals(req.ha, BankReference(bank, map), StringComparison.Ordinal) || req.hh != 0)
            return Refuse("La referencia de esta pestaña del banco no es válida.");
        if (req.hb is not (1 or 3)) return Refuse("Esta operación de pestañas no está disponible.");
        var tabs = PersonalBankTabs(player).ToList();
        var index = tabs.FindIndex(t => t.Id == req.hc);
        // The durable commit precedes the notification. Repeating a completed purchase only refreshes ownership.
        if (req.hb == 1 && index >= 0)
        {
            PushBankTabInfo(player, bank);
            return null;
        }
        if (req.hb == 3 && index < 0) return Refuse("No posees esta pestaña en este banco.");
        var name = req.he;
        // c0.g/9E4EC0: native names have 1–20 ASCII letters/digits, interior spaces/underscore/hyphen;
        // the implicit default's localization key is also retained when an edit changes only its icon/color.
        var unchangedName = index >= 0 && string.Equals(name, tabs[index].Name, StringComparison.Ordinal);
        if (!unchangedName && (name is null || name.Length is < 1 or > 20
            || !Regex.IsMatch(name, @"\A(?:[a-zA-Z0-9]|[a-zA-Z0-9][ _a-zA-Z0-9\-]*[a-zA-Z0-9])\z", RegexOptions.CultureInvariant)
            || name.Contains("  ", StringComparison.Ordinal)))
            return Refuse("Usa un nombre de 1 a 20 letras o números, sin espacios dobles.");
        // The native chooser supplies atlas identifiers; never accept asset paths or rich-text markup.
        if (req.hf is not { Length: > 0 and < 100 } icon || !icon.StartsWith("icon_tag_", StringComparison.Ordinal)
            || icon.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            return Refuse("El icono de la pestaña no es válido.");
        long cost = 0;
        if (req.hb == 1)
        {
            var owned = tabs.Select(t => t.Id).ToHashSet();
            var next = BankTabCatalog.Ids.FirstOrDefault(id => !owned.Contains(id));
            var priceIndex = tabs.Count - 1; // The implicit default is free.
            if (next == Guid.Empty || req.hc != next || priceIndex >= world.Economy.BankTabCosts.Count)
                return Refuse("No se puede comprar otra pestaña en este banco.");
            // The current Join advertises gold at 5000 (no global silver discount). Keep the authored price authoritative;
            // a different global discount needs its matching backend formula before accepting a lower client quote.
            cost = world.Economy.BankTabCosts[priceIndex];
            if (req.hd != cost) return Refuse("El precio de la pestaña cambió. Vuelve a abrir la administración del banco.");
            tabs.Add(new BankTab(req.hc, name!, icon, req.hg));
        }
        else
        {
            if (req.hd != 0) return Refuse("Editar una pestaña del banco es gratis.");
            tabs[index] = new BankTab(req.hc, name!, icon, req.hg);
        }
        if (!Systems.BankTabs.TryCommit(player, BankLocation(player), tabs, cost, out var error)) return Refuse(error);
        PushBankTabInfo(player, bank);
        return null;
    }

    private string BankLocation(Player player) => world.Config.BankShared ? Character.SharedVault
        : world.Hideouts.VaultLocation(player.Map!) ?? Systems.Islands.VaultLocation(player.Map!);

    private IReadOnlyList<BankTab> PersonalBankTabs(Player player)
    {
        var location = BankLocation(player);
        var metadata = player.Character.BankTabs;
        var tabs = new List<BankTab>();
        var seen = new HashSet<Guid>();
        void Include(string from)
        {
            if (metadata.TryGetValue(from, out var owned))
                foreach (var tab in owned)
                    if (seen.Add(tab.Id)) tabs.Add(tab);
        }
        // Bank:Shared may be toggled after buying tabs. Inherit ownership as the existing item merge inherits
        // vaults, so a stored extra never requires a second purchase. Local metadata wins name/icon/color conflicts;
        // the union only reads other locations and never rewrites their metadata or bank_items.
        Include(location);
        if (world.Config.BankShared)
        {
            foreach (var other in metadata.Keys.Where(k => k != location).Order(StringComparer.Ordinal)) Include(other);
        }
        else Include(Character.SharedVault);
        var defaultIndex = tabs.FindIndex(t => t.Id == DefaultTab);
        var defaultTab = defaultIndex >= 0 ? tabs[defaultIndex] : BankTabCatalog.Default(world.Economy);
        if (defaultIndex >= 0) tabs.RemoveAt(defaultIndex);
        tabs.Insert(0, defaultTab);
        return tabs;
    }

    /// <summary>cot.aqr/BA25F0: the native cluster bankgroup when present, otherwise BuildingGuid@cluster.
    /// This wire reference does not change the existing server's resolved vault-location keys.</summary>
    private static string BankReference(Building bank, Map map) =>
        map.Template?.Info.Attrs.GetValueOrDefault("bankgroup") is { Length: > 0 } group ? group : $"{bank.Spawn.Guid}@{map.Cluster}";

    private void PushBankTabInfo(Player player, Building bank)
    {
        var tabs = PersonalBankTabs(player);
        // clb.s/0x180B41AC0 removes absent IDs; all four parallel arrays must include the default.
        SendEvent(new BankVaultInfoEvent
        {
            ddn = bank.Id, bth = BankReference(bank, player.Map!), bti = tabs.Select(t => t.Id).ToArray(),
            btj = tabs.Select(t => t.Name).ToArray(), btk = tabs.Select(t => t.Icon).ToArray(), btl = tabs.Select(t => t.Color).ToArray(),
        });
    }

    /// <summary>Why <paramref name="player"/> may not use <paramref name="bank"/> now (knocked down, or farther than the stations'
    /// <see cref="StationRange"/> from its centre), or null.</summary>
    private static string? BankUnusable(Player player, Building bank) =>
        player.Combat.Down is not null ? "knocked down"
        : Vector2.Distance(player.PositionAt(DateTime.UtcNow), bank.Position) > StationRange ? "out of range" : null;

    /// <summary>
    /// Attaches a vault tab of <paramref name="bank"/> into the client's view <paramref name="view"/>: loaded from the character
    /// on its first opening (the container Guid of <see cref="VaultContainerId"/>), then every stored item as an object
    /// (New*Item) and AttachItemContainer (99) {0 bank, 1 the vault's container Guid, 2 the tab, 3 the object id per slot (128,
    /// 0 = empty), 5 the slot count, 6 false}. A tab open already (in the other view, or attached again) gets the same objects
    /// and the same container Guid, so the client gets no second container.
    /// </summary>
    private Vault AttachVault(Player player, Building bank, int view, Guid tab)
    {
        // StageH islands (SPEC §0.2): an island's bank opens its home city's vault (world.xml bankgroup). StageP hideouts (SPEC §5): a hideout's
        // bank its own vault (Hideouts:BankVault Own), never a city's.
        var key = Character.VaultKey(BankLocation(player), tab);
        var id = VaultContainerId(player.Character.Id, key);
        if (!player.OpenVaults.TryGetValue(id, out var vault))
        {
            // Back from the shared vault only into a city's (PLAYERCITY_*) vault, never an island's or the outlands' own.
            var city = player.Map!.Template?.Info.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) == true;
            if ((world.Config.BankShared || city) && MergeVaults(player.Character, key, tab, world.Economy.BankSlots) is > 0 and var merged)
                Log.Info(Name, $"'{player.Character.Name}': {merged} item(s) of its other vaults moved into the vault {key} (Bank:Shared {world.Config.BankShared})");
            var container = new Container(id, world.Economy.BankSlots);
            var saved = player.Character.Vaults.GetValueOrDefault(key) ?? [];
            var usable = SpellUsable(player.Progression);
            for (var slot = 0; slot < saved.Count; slot++)
            {
                if (saved[slot] is not { } entry) continue;
                if (slot < container.Size && world.Items.Find(entry.Name) is { CanCreate: true } type) container.Put(slot, MakeItem(type, entry, usable));
                else if (slot < container.Size) container.Unloaded[slot] = entry;
                else Log.Warn(Name, $"vault {key} of '{player.Character.Name}': slot {slot} beyond {container.Size}, kept unsent");
            }
            vault = new Vault(key, container, bank.Id, tab, world.Economy.BankWeightLimit);
            player.OpenVaults[id] = vault;
        }
        vault.Views.Add(view);
        foreach (var item in vault.Container.Items) SendItem(item);
        SendEvent(new AttachItemContainerEvent
        {
            bst = bank.Id, bsu = vault.Container.Id, bsv = tab, bsw = vault.Container.ObjectIds(), bsy = vault.Container.Size,
            Extra = new() { [6] = false }, // [6] c15.r: false
        });
        Log.Info(Name, $"'{player.Character.Name}' opened the vault {key} in view {view} ({vault.Container.Count} item(s))");
        return vault;
    }

    /// <summary>
    /// Bank:Shared (PVE-DESIGN step 12): moves the items of a character's other vaults of <paramref name="tab"/> into the vault
    /// <paramref name="into"/> before it is opened, so a change of the setting loses nothing. Into the shared vault come the
    /// city vaults (in key order); into a city's vault (Bank:Shared false again; the caller merges only at a PLAYERCITY_*
    /// cluster's bank) comes the shared one. Under Bank:Shared every bankbuilding opens the shared vault: the city banks,
    /// the island banks and the outlands' (T1_BANK_OUTLAND) too. Each item takes the lowest
    /// free slot below <paramref name="slots"/>; what does not fit stays where it was (for a later opening), an emptied vault
    /// is dropped. The weight limit is not checked (it only refuses moves in). Returns the number of items moved.
    /// </summary>
    public static int MergeVaults(Character c, string into, Guid tab, int slots)
    {
        var suffix = $"|{tab}";
        var shared = Character.VaultKey(Character.SharedVault, tab);
        var sources = c.Vaults.Keys.Where(k => k != into && k.EndsWith(suffix, StringComparison.Ordinal) && (into == shared || k == shared))
            .Order(StringComparer.Ordinal).ToList();
        if (sources.Count == 0) return 0;
        var target = c.Vaults.GetValueOrDefault(into) ?? [];
        var moved = 0;
        var free = 0;
        foreach (var source in sources)
        {
            var items = c.Vaults[source];
            for (var slot = 0; slot < items.Count; slot++)
            {
                if (items[slot] is not { } item) continue;
                while (free < slots && free < target.Count && target[free] is not null) free++;
                if (free >= slots) break;
                while (target.Count <= free) target.Add(null);
                target[free] = item;
                items[slot] = null;
                moved++;
            }
            if (items.All(i => i is null)) c.Vaults.Remove(source);
        }
        if (moved > 0) c.Vaults[into] = target;
        return moved;
    }

    /// <summary>
    /// ContainerClose (op 99, rc: the same three keys; no response class, so the bare RC-0 answer): the view is closed; when no
    /// view shows the tab any more the vault is saved and its objects dropped, with DetachItemContainer (100) {0 its container
    /// Guid} (optional officially, Q-B2).
    /// </summary>
    [Handles(OperationCodes.ContainerClose, Status = SessionStatus.InWorld)]
    private AlbionResponse? ContainerClose(ContainerCloseRequest req)
    {
        if (Player is not { } player) return null;
        if (player.OpenVaults.Values.FirstOrDefault(v => v.Building == req.g6 && v.Tab == req.g8) is { } vault
            && vault.Views.Remove(req.g7) && vault.Views.Count == 0)
            DetachVaults(player, v => ReferenceEquals(v, vault));
        return new GenericResponse();
    }

    /// <summary>
    /// Saves and drops the open vaults of the player that <paramref name="which"/> picks, each with DetachItemContainer (100)
    /// {0 its container Guid}: the last view closed, the bank unregistered, or another object registered to.
    /// </summary>
    private void DetachVaults(Player player, Func<Vault, bool> which)
    {
        var closing = player.OpenVaults.Values.Where(which).ToList();
        if (closing.Count == 0) return;
        player.SaveItems();
        foreach (var vault in closing)
        {
            player.OpenVaults.Remove(vault.Container.Id);
            SendEvent(new DetachItemContainerEvent { b20 = vault.Container.Id });
            Log.Info(Name, $"'{player.Character.Name}' closed the vault {vault.Key}");
        }
    }

    /// <summary>Saves and drops every open vault of the player (it leaves the world).</summary>
    private static void CloseVaults(Player player)
    {
        if (player.OpenVaults.Count == 0) return;
        player.SaveItems();
        player.OpenVaults.Clear();
    }
}
