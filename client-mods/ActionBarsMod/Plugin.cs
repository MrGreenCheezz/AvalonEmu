using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ActionBarsMod;

public enum CastMethod { Auto, Slot25, Slot25Direct, Op }

/// <summary>
/// Two extra action bars of 6 buttons whose abilities come from the worn equipment (server side: content/extraspells.json,
/// Studio "Доп. способности", GM .extrabar). Protocol: analysis/work/stageN/extrabars/SPEC.md.
///  - Net: Harmony prefix on c3s.akw (Photon OnEvent of the game peer, VA 0x180A8D960): events 9001-9005 are parsed and consumed
///    (the game never sees them), Albion event 2 JoinFinished arms the hello (op 9001), sent from the next frame through
///    g7o.h(byte, Dictionary&lt;byte,object&gt;, g73) = PhotonPeer.SendOperation (vtable +0x228, what c3s.i(x6,..) 0x180A8E7A0 calls).
///  - Cast: slot 25 (CharacterSpellSlot.FakeSpell): a48.u(bio, bdf, long) 0x1808F2060 puts the spell in, the client cooldown of
///    slot 25 is cleared, then LocalInputHandler.StartCast(slot, hotkey, alt) 0x1819AE480 = the game's own key path (targeting,
///    ground aim, cast bar, animation, CastStart). Fallback op 9002.
///  - Keys: own polling + Harmony prefix on the hotkey dispatcher dvh.f(KeyCode, EventModifiers, bool) 0x181894130 that swallows
///    only our modifier combos.
///  - UI: injected MonoBehaviour. Style Native: clones of the game's own spell button (NGUI) under SpellButtonGui (NativeBar); Atlas /
///    Classic: IMGUI, sprites cut from the NGUI atlas textures (Blit + ReadPixels).
///  - Tooltips: the game's spell tooltip (gdn.h + ProximityTooltipManager.Show(Spell)); item tooltips get the extra abilities the server
///    resolves by its rules for that item and quality (op 9003 -> event 9005, cached; ItemExtras), the catalog of explicit entries (event 9004)
///    as the instant fallback: UITooltip.Show text + an IMGUI panel next to the item info window (Tooltips).
/// </summary>
[BepInPlugin("albionlocal.actionbars", "AlbionLocal ActionBarsMod", "0.3.0")]
public class Plugin : BasePlugin
{
    internal const string Version = "0.3.0";
    internal static ManualLogSource L;

    internal static ConfigEntry<bool> Enabled, ShowKeys, ShowTooltips, LocalizedNames, AutoAnchor, AutoSize, CastAsHotkey, BlockClicksUnderBars, LogEvents,
        ShowCooldownNumbers, ItemTooltips, ItemTooltipColors;
    internal static ConfigEntry<BarStyle> Style;
    internal static ConfigEntry<TooltipMode> SpellTooltips;
    internal static ConfigEntry<int> ItemTooltipChars;
    internal static ConfigEntry<int> Rows;
    internal static ConfigEntry<float> ButtonSize, Spacing, OffsetX, OffsetY, Opacity, RowGap;
    internal static ConfigEntry<CastMethod> Method;
    internal static readonly ConfigEntry<string>[] Keys = new ConfigEntry<string>[12];

    public override void Load()
    {
        L = Log;
        var c = Config;
        Enabled = c.Bind("General", "Enabled", true, "Master switch. false = no hooks, no hello (the server then never sends extra-bar events).");
        Method = c.Bind("General", "CastMethod", CastMethod.Auto,
            "How an extra spell is cast. Slot25 = the client's own path: the spell is put into the fake slot 25 and cast like a normal key " +
            "(targeting, ground aim at the cursor, cast bar, animation). Slot25Direct = slot 25 + direct cast at the cursor ground point " +
            "(cwc.u, no targeting mode). Op = op 9002 to the server (no own animation/cast bar). Auto = Slot25, Op when the client path is unavailable.");
        CastAsHotkey = c.Bind("General", "CastAsHotkey", false,
            "Slot25: pass 'cast via hotkey' to LocalInputHandler.StartCast. With the game's 'cast on key release' scheme the targeting then waits " +
            "for a key release the game never sees from our keys, so false (targeting ends with a click like a mouse cast) is the default.");
        LocalizedNames = c.Bind("General", "LocalizedNames", true, "Tooltip: spell name from the game's localisation (a8c.f). false = uniquename only.");
        LogEvents = c.Bind("General", "LogEvents", true, "Log received bars/cooldown/refusal events.");

        Style = c.Bind("Layout", "Style", BarStyle.Native,
            "Native = the buttons are clones of the game's own spell button (NGUI widgets: frame, background, cooldown sweep, numbers, key " +
            "label), placed above the game's bar with its size and spacing. Atlas = drawn by the mod (IMGUI) from the same sprites of the game's " +
            "atlases. Classic = the first version's flat look. Native falls back to Atlas by itself if cloning fails.");
        ShowCooldownNumbers = c.Bind("Layout", "ShowCooldownNumbers", true, "Seconds left on a cooling button.");
        Rows = c.Bind("Layout", "Rows", 2, "Bars shown (1-2; never more than the server sends).");
        ButtonSize = c.Bind("Layout", "ButtonSize", 48f, "Button size in pixels (Style Classic, AutoSize off; Native/Atlas use the game's size).");
        Spacing = c.Bind("Layout", "Spacing", 4f, "Gap between buttons in pixels.");
        RowGap = c.Bind("Layout", "RowGap", 6f, "Gap between the two bars and between the game's bar and ours, in pixels.");
        OffsetX = c.Bind("Layout", "OffsetX", 0f, "Horizontal shift of the bars in pixels (+ = right).");
        OffsetY = c.Bind("Layout", "OffsetY", 0f, "Vertical shift in pixels (+ = up). Without AutoAnchor the bars sit 130 px above the bottom edge + OffsetY.");
        AutoAnchor = c.Bind("Layout", "AutoAnchor", true, "Place the bars right above the game's own spell bar (measured from its NGUI widgets every second).");
        AutoSize = c.Bind("Layout", "AutoSize", false, "Button size = the game's spell button width (AutoAnchor needed).");
        Opacity = c.Bind("Layout", "Opacity", 0.95f, "0.1..1.");
        ShowKeys = c.Bind("Layout", "ShowKeys", true, "Key label in the button corner.");
        ShowTooltips = c.Bind("Layout", "ShowTooltips", true, "Tooltip when the cursor is over an extra button.");
        SpellTooltips = c.Bind("Tooltips", "SpellTooltips", TooltipMode.Game,
            "Game = the game's own spell tooltip (the one of its spell bar; ProximityTooltipManager). Mod = drawn by the mod with the same texts " +
            "(name, stats, description, granting item, cooldown). Off = none. Game falls back to Mod by itself if it fails.");
        ItemTooltips = c.Bind("Tooltips", "ItemTooltips", true,
            "Item tooltips (inventory, equipment, bank, market, loot, chests) list the extra abilities the item grants (\"Доп. способности\"), " +
            "and the item info window gets a panel with their icons and descriptions. Needs the server's catalog (event 9004).");
        ItemTooltipColors = c.Bind("Tooltips", "ItemTooltipColors", true, "Colour the section in the game's text tooltip (NGUI [rrggbb] codes). false if you see the codes as text.");
        ItemTooltipChars = c.Bind("Tooltips", "ItemTooltipDescriptionChars", 160, "Description length in item tooltips (first paragraph, cut at a word; 0 = full).");
        BlockClicksUnderBars = c.Bind("Layout", "BlockClicksUnderBars", true, "A click on a button does not also walk/attack in the world.");

        string[] defaults = { "Shift+Alpha1", "Shift+Alpha2", "Shift+Alpha3", "Shift+Alpha4", "Shift+Alpha5", "Shift+Alpha6",
                              "Ctrl+Alpha1", "Ctrl+Alpha2", "Ctrl+Alpha3", "Ctrl+Alpha4", "Ctrl+Alpha5", "Ctrl+Alpha6" };
        for (int i = 0; i < 12; i++)
            Keys[i] = c.Bind("Keys", $"Bar{i / 6 + 1}Slot{i % 6 + 1}", defaults[i],
                "Modifiers (Shift, Ctrl, Alt) + a Unity KeyCode name (Alpha1, Q, F5, Mouse3 ...), e.g. Shift+Alpha1, Ctrl+Q, Alt+3. Empty/None = no key.");

        if (!Enabled.Value) { L.LogInfo("ActionBarsMod: disabled in config (General.Enabled = false)"); return; }

        var h = new Harmony("albionlocal.actionbars");
        Net.Install(h);
        if (ItemTooltips.Value) Tooltips.Install(h);
        KeyInput.Install(h);
        ClickGuard.Install(h);
        try { AddComponent<BarsOverlay>(); L.LogInfo("ActionBarsMod: overlay component injected"); }
        catch (Exception ex) { L.LogError("ActionBarsMod: class injection failed, no UI/keys: " + ex); }
        L.LogInfo("ActionBarsMod " + Version + " loaded, cast method " + Method.Value);
    }
}
