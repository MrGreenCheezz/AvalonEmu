using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The spell in a slot changed by the server (Systems/SlotSpells, IPlayer.SetSlotSpell, the rules' slot.set), light, in one arena: an option of the
/// axe goes into Q (the item and the action bar re-sent, OnSlotSpellChanged) and casts; a sword spell (mode Any, 2 s) casts from Q and goes back;
/// a .swapw command rule (slot.is condition, slot.set action) changes W and its slot.changed rule answers.
/// </summary>
public static class SlotSpellTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("slot spells: SetSlotSpell(Q, RENDINGSPIN) re-sends the axe and key 7 and casts it; a foreign spell is refused FromItemList, casts with Any and goes back after 2 s; rule slot.set via a command + slot.changed", SetCastRevertRule),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private sealed class Watcher : PluginBase
    {
        public readonly List<SlotSpellChangedArgs> Seen = [];
        public override string Name => "slot-watcher";
        public IPlayer? Find(string name) => Server.FindPlayer(name);
        protected override void OnLoad() => Hooks.Spells.OnSlotSpellChanged(a => Seen.Add(a));
    }

    private static void SetCastRevertRule()
    {
        var dir = NewDir();
        var folder = Path.Combine(dir, "mods", "rt", "rules");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "swapw.json"), """
            { "id": "swapw", "trigger": { "type": "command", "params": { "command": "swapw" } },
              "conditions": [ { "type": "slot.is", "params": { "slot": "Q", "spell": "RENDING*" } } ],
              "actions": [ { "type": "slot.set", "params": { "slot": "W", "spell": "AXEBOOST", "cooldown": "reset" } } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "said.json"), """
            { "id": "said", "trigger": { "type": "slot.changed", "id": "AXEBOOST" },
              "actions": [ { "type": "message", "params": { "text": "slot now {spell}" } } ] }
            """);
        using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true, ModsPath = Path.Combine(dir, "mods"), ModPacks = ["rt"], SavesPath = dir });
        var world = arena.World;
        var (session, peer, me) = arena.Join("Slotter", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        lock (world.Sync) world.Plugins.Rules.Reload();
        var watcher = new Watcher();
        Check(world.Plugins.Load(watcher) is null && world.Plugins.Rules.Rules.Count == 2, $"the watcher plugin and both rules load ({world.Plugins.Rules.Summary()})");
        var spells = Client.Spells!;
        short spin = spells.IdOf("RENDINGSPIN"), heroic = spells.IdOf("HEROICSTRIKE2"), boost = spells.IdOf("AXEBOOST");
        var axe = me.Inventory.Equipment[0]!;
        var player = watcher.Find("Slotter")!;

        // FromItemList: an option of the axe's first slot.
        var mark = peer.Events.Count;
        string? why;
        lock (world.Sync) why = player.SetSlotSpell(SpellSlot.Q, "RENDINGSPIN");
        var events = Since(peer, mark);
        var item = events.LastOrDefault(e => e.Code == EventCodes.NewEquipmentItem && L(e.P, 0) == axe.Id).P;
        var bar = events.LastOrDefault(e => e.Code == EventCodes.CharacterEquipmentChanged && L(e.P, 0) == me.Id).P;
        Check(why is null && axe.Active[0] == spin && item?[9] is short[] keys && keys[0] == spin && bar?[7] is int[] slots && slots[0] == spin
              && player.GetSlotSpell(SpellSlot.Q) == "RENDINGSPIN"
              && watcher.Seen is [{ Slot: SpellSlot.Q, From: "RENDINGSTRIKE", To: "RENDINGSPIN", Source: SlotChangeSource.Plugin, Temporary: false }],
              $"Q = RENDINGSPIN: NewEquipmentItem key 9 and CharacterEquipmentChanged key 7 carry it, OnSlotSpellChanged once (why {why ?? "-"}, seen {watcher.Seen.Count})");
        mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = spin, d1 = ghoul.Id, d4 = 1 });
        arena.Run(1.5);
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.CastFinished) && peer.Responses[^1].ReturnCode != WorldSession.CastRefused,
              "RENDINGSPIN casts from Q (CastFinished)");

        // Any: a sword spell, for 2 s.
        lock (world.Sync) why = player.SetSlotSpell(SpellSlot.Q, "HEROICSTRIKE2");
        Check(why is not null && axe.Active[0] == spin, $"HEROICSTRIKE2 is refused FromItemList ({why})");
        lock (world.Sync) why = player.SetSlotSpell(SpellSlot.Q, "HEROICSTRIKE2", SlotSpellMode.Any, TimeSpan.FromSeconds(2));
        Check(why is null && axe.Active[0] == heroic && axe.ToRecord(id => spells.Name(id)).Active?[0] == "RENDINGSPIN",
              $"with Any for 2 s Q holds HEROICSTRIKE2 while the save keeps RENDINGSPIN (why {why ?? "-"})");
        mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = heroic, d1 = ghoul.Id, d4 = 2 });
        arena.Run(1);
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.CastFinished) && peer.Responses[^1].ReturnCode != WorldSession.CastRefused,
              "the foreign HEROICSTRIKE2 casts from Q server-side (CastFinished)");
        mark = peer.Events.Count;
        arena.Run(1.5);
        bar = Since(peer, mark).LastOrDefault(e => e.Code == EventCodes.CharacterEquipmentChanged && L(e.P, 0) == me.Id).P;
        Check(axe.Active[0] == spin && axe.Temporary is not { Count: > 0 } && bar?[7] is int[] back && back[0] == spin
              && watcher.Seen[^1] is { Source: SlotChangeSource.Revert, From: "HEROICSTRIKE2", To: "RENDINGSPIN" },
              $"after 2 s Q is RENDINGSPIN again, sent as key 7, OnSlotSpellChanged Revert (Q {spells.Name(axe.Active[0])})");

        // The rules: .swapw (slot.is Q RENDING*) sets W = AXEBOOST, the slot.changed rule answers.
        mark = peer.Events.Count;
        session.OnRequest(Request(new SayRequest { aa9 = ".swapw" }, null), encrypted: false);
        var lines = Since(peer, mark).Where(e => e.Code == EventCodes.SystemMessage).Select(e => e.P[0] as string ?? "").ToList();
        Check(axe.Active[1] == boost && lines.Contains("slot now AXEBOOST"),
              $"rule slot.set: W = AXEBOOST, and slot.changed fired (W {spells.Name(axe.Active[1])}; lines: {string.Join(" | ", lines)})");
        world.Plugins.Shutdown();
    }
}
