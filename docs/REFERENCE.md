# AlbionLocal

Local server emulator for the Albion Online client 1.32.030.345319 (research project, local only).

- `src/AlbionLocal.Photon` – Photon UDP transport (reliable channels, fragments, CRC), Protocol18
  serializer, payload encryption (DH 768 / AES-256-CBC), message layer.
- `src/AlbionLocal.Protocol` – Albion message classes (`Generated/`, from `analysis/tools/gen_protocol_cs.py`),
  wire rules (`AlbionSerializer`) and `MessageDump`; namespace `AlbionLocal.Server.Protocol`.
  Regenerate from the workspace root: `python -I analysis/tools/gen_protocol_cs.py analysis server/src/AlbionLocal.Protocol/Generated analysis/protocol-names.json`.
- `src/AlbionLocal.Server` – login server (5055) and game server (5056); `Data/` reads the client's GameData files.
- `content/` – server content under git: `starter.json` (start cluster, position and kit of new characters),
  `starter-fair.json` (the kit under `Progression:NewCharacters` = `FairStart`: the T1 items an empty destiny board allows),
  `opcode-stubs.json` (answers of side requests that have no handler: the empty response with the listed field
  values and return code, `"silent"` for no answer at all, `"ack"` for a bare success, as the official server answers
  them, `"ignore"` for the client's telemetry and other requests nobody answers; without the "unhandled" warning; a
  mistake in it stops the start), `combat.json` (the combat numbers the client
  data leaves open: auto-attack projectile speed, combo damage factors, loot bag time and reach), `spawns/<cluster>.json`
  (buildings the server adds to a cluster: Thetford's ownerless crafting stations), `kits.json` (the GM's item kits
  for `.kit`: `mounts`, `food`, `potions`, and `test` with all three), `journals.json` (labourer journals: filling and
  the journal merchant).
- `tests/AlbionLocal.SelfTest` – offline self-test with an in-process Photon client.

How the wire format and each response field were derived: `../analysis/NOTES-protocol.md`.

```
dotnet build
dotnet run --project tests/AlbionLocal.SelfTest
dotnet run --project src/AlbionLocal.Server -- -v
```

No game client, client file or client patch is part of this repository. Characters are stored in the SQLite database
`data/albionlocal.db` relative to the working directory (folder `Paths:Saves`). A `data/characters.json` from
an older version is imported on the first start with an empty database and renamed to
`characters.json.migrated` (kept, not deleted).

At startup the server reads its item, spell, stat and destiny board tables from that client's encrypted GameData
files (`items.bin`, `spells.bin`, `gamedata.bin`, `characters.bin`, `achievements.bin`, `craftingmodifiers.bin` in `Paths:ClientGameData`, by
default `../game-local/Albion-Online_Data/StreamingAssets/GameData`); it does not start without them, and a number it
cannot read is warned about and taken as 0. It logs the client build (`version.txt` of the client) and a SHA-256 per
file and keeps both in `data/clientdata.json`, so an official update that reaches `game-local` is warned about at the
next start. Loading takes under a second. The tables
in `data/` (`items.tsv`, `gamedata.json`, `spells.json`, made from the same files by `../analysis/tools/gen_items.py`,
`gen_gamedata.py` and `gen_item_spells.py`) are only the self-test's golden references now.

The cluster data (`cluster/world.bin`, `cluster/tiles.bin`, the `.cluster` and template files, `buildings.bin`,
`harvestables.bin`, `expeditionagents.bin`, `questgivertypes.bin`; for the open world also `resourcedistpresets.bin`,
`resourceprefabmappings.bin`, `dynamictemplates.bin`, `dynamicclusters.bin`, `worldsettings.bin` and
`rareresourcedistribution.bin`) is not read at startup: selecting a character resolves
its cluster in the background while the client loads it (about 1.5 s for the first cluster, which also indexes world.bin
and tiles.bin, then tens of ms per cluster), with one log line such as `cluster 0000: 5 buildings, 9 exits, ...`. To check the resolver against
world.xml for every cluster without starting the server (no port, save or log file is touched), build it into a folder
of its own, because a running server locks `src/AlbionLocal.Server/bin`, and run that build from `server/`:

```
dotnet build src/AlbionLocal.Server -o bin/check
dotnet bin/check/AlbionLocal.Server.dll --check-content      # summary, exit code 0 = all pass; -v lists every failure
```

With no server running, `dotnet run --project src/AlbionLocal.Server -- --check-content` does the same. It checks the
default templates (what world.xml counts), without the day's dynamic templates and resource nodes.

## World objects

Simple-behaviour buildings whose authored default has `visible="false"` are not
announced to the client. This includes the journal version of Captain Tia in Swamp
Cross: its `HIDDEN` state has no animator actions, and creating its view then applying
that state makes this client enumerate a null action list. Hidden buildings receive
no creation, state, access-status or leave events, including on reentry and reconnect.
Visible defaults receive `SimpleBehaviourBuildingStateUpdate` (645) immediately after
`NewBuilding`, independently for each viewer. Subsequent journal quest transitions
and activation of the hidden NPC are not implemented by this initialization.

The server sends the objects of the cluster that the client cannot place or use on its own (stage 2, step 12): the NPC
buildings (repair shop, vanity store, meld building, arena, faction trader; bank and market in their interiors), the exits
(city gates, bank and market doors, portals), the fast travel point (harbour), the war wagon, the island seller, the
expedition agent, the location markers and the static trees and rocks. The server log shows `map 0000: 309 world objects
(...)` when a map gets them and, at each Join, how many are in view. In Thetford, at the start position:

- the repair shop, the meld building and the harbour (fast travel) NPC appear at once, the vanity store and the arena when
  you walk towards them (about 40-50 m: the window is 9 x 9 cells of 10 m, not a circle), and they go away again when you
  walk off; the faction trader is always there. The doors, the war wagon, the island seller and the expedition agent are
  part of the landscape and were visible before; now they work once they are in view;
- clicking a building opens its window (the arena opens the arena UI; a wrong building id would open the farming
  merchant's), the doors are usable exits (see "Cluster travel" below), the trees near the start can be gathered (see
  "Gathering" below);
- no "not found in landscape" or "Building Type not existing" error is reported by the client.

Not there yet: the real-estate plots' ownership (the plots themselves are part of the landscape), crafting stations on
plots, hall-of-fame banners. `.near` lists what the server thinks is in view; to tell a rendering
problem from a visibility one, start the server with `--World:AnnounceAll=true`: every object of the cluster is then sent
at Join and none is ever taken away. The window is set by `Visibility:CellSize`, `EnterCells` and `LeaveCells` (10, 4, 6):
an object comes at 4 cells and goes at 6, so stepping back and forth over a cell border does not make buildings blink
(the official server leaves at 5 or 6, 0.2-1.5 s late; `LeaveCells` 5 leaves on the crossing itself).

## Cluster travel

Walking into an exit takes the character to the cluster behind it, as on the official server (step 13,
`../analysis/work/stage3/SPEC.md`): the screen fades, a loading screen of a few seconds follows, and the character
stands at the exit's arrival point in the new cluster, facing away from the door it came through. In Thetford that is:

- the **bank** door (at (-10, -10), next to the start) → bank interior 0006, at the door facing inside; out again → in
  front of the Thetford bank door, facing away from it;
- the **market** door (at (-25, 25)) → market interior 0007 and back the same way; the two interiors also have a door
  into each other;
- the **city gates** → the open-world clusters around Thetford (e.g. the north exit into 0204), and back.

After arriving, the client blocks the next exit for 1 s in a city (Thetford and its interiors) and for 10 s in the open
world; the server checks the same (a refused exit shows an error line in the chat and the character stays). The
cluster, position and facing are saved at the moment of the change, so quitting on the loading screen logs in at the
arrival point next time. The server log shows one line per change (`'Name' leaves 0000 for 0006 (exit:982df85e-…):
arrival (-26.5, 0.5) facing 90, saved`) and one at the new Join (`arrives from 0000`); the old game connection is closed
by the client and only logged. The first change into a cluster no one has visited yet resolves it in the background
(about 0.1 s, the answer waits for it).

Leaving the Thetford (or Lymhurst) bank puts the character a little further from the door than the door's arrival point,
as on the official server: the point touches a pillar of the entrance, and the character (0.75 m radius) is moved off it.

**Fast travel.** The harbour NPC of a city (the travel point) sells journeys to the other cities, as on the official
server (the window lists them and their price itself): pick a destination, and the character travels there through the same loading
screen as through an exit, arriving at the destination's travel point and facing the way it faced before. Routes follow
the client's rule: Lymhurst and Thetford have no direct connection, the journey goes via Fort Sterling (the server only
checks that some route exists); Caerleon can only be reached without luggage, so with anything in the bag the window
refuses it (and so does the server). The price is the one the window shows (an empty bag travels for free; resources,
journals or treasures in it make the journey cost silver): the server takes it from the character's silver and refuses
the journey with the client's "not enough silver" message when there is less (see Economy).

Island trips: see Personal islands below; corrupted dungeons, hellgates, the Roads of Avalon portals and the Mists: see their sections below. Not yet: exits that need a channel bar,
the other instanced clusters besides those and the random dungeons below; they come with later stages. A cluster nobody has been in for `World:MapIdleUnloadMinutes` (5) is unloaded once its harvested trees and
rocks have regrown, and the next visit builds it anew (new object ids). The checks are `Transfer:PositionTolerance` (2 m
outside the exit square) and `Transfer:CheckExitCooldown` (true).

The character database gets new columns (schema v2: facing, previous cluster, pending transfer) the first time this
version starts; a server build from before step 13 then refuses the file ("schema version 2 is newer").

### Random dungeons

The open-world clusters get random dungeon entrances (stageG, `../analysis/work/stageG/dungeons/SPEC.md`; the dungeon of the day
and the group scaling of `content/dungeons.json` only with `Dungeons:DailyBonus "content"` and `Dungeons:GroupScaling true`, the
pve preset's): on the
day's RandomExitPositionMarker spots of the cluster (0204 has 67 solo spots), as many as randomdungeons.xml allows for
the continent, subtype and tier (half of them for Royal T4 solo), the portal prefab of the cluster's biome. Walking into
one (or clicking a solo portal, which the client uses with a 2 s channel bar: `ExitChannelSeconds` 2, cancelled by moving
more than `ExitCancelDistance` 1 m, damage or a knockdown; an accepted click gets no response, since any response would end the client's bar) starts a loading screen into a dungeon made by a port of the client's own layout generator (Albion.Common d5/d6/d7,
`../analysis/work/stageN/dgen/SPEC.md`): from a Start a path of Basic rooms (corners, straights, junctions) of the
floor's pathlength to an Exit room with the client's own templates, the floor's highlightCount highlight rooms (picked by the
generator's HighlightCategories, any size) spaced along it or hung off a junction as a dead-end side room, on the last
floor an end-boss room just before the Exit, backdrops around and the master; mobs of the dungeon tier on the active layers, the exit's level (key 9, rerolled
every 30-90 s until the dungeon is made) raising the mobs' fame, silver and loot (× Rates) and giving every mob the
level's buff (MOB_RANDOM_DUNGEON_BUFF_01-04: more health, more damage against players). The highlight rooms' chests
(lootchests.xml) stand ready to open: their loot is rolled at the first opening from the chest's list for the dungeon
tier, × the level's loot factor × Rates:Loot, and taken like a mob's bag. Each chest stands as the model of the
cluster's biome and sub-biome (lootchests.xml `<Tiles>`, else its prefab; a chest with neither is not placed, and the
log says so once), and a chest whose data hides its rarity until it is unlocked or opened shows only its locked name
until then. The Start room's
exit leads back out in front of the portal; the Exit room's leads down to the next floor, or out on the last one; the
escape spell leads out too. Solo dungeons are one per party (or player), the others one per portal. Logging out inside
logs in inside again; a dungeon nobody is in closes after 30 s (solo, 300 s otherwise, as randomdungeons.xml), and one
that is gone puts the character back in front of its portal at the next login. A used portal goes after 30-90 s and
its spot gets a new one after the cooldown. A dungeon map used from the bag (T4_RANDOM_DUNGEON_SOLO_TOKEN_D1, …) puts
its portal on a free spot of its kind in a random cluster of the same zone colour up to the map's `maxdistance` (1-3)
cluster hops away (the player's own cluster included at distance 0; maps with distance 0 keep it in the own cluster), at
the map's level, for the map's time, and marks it for the player (a portal in another cluster opens the world map on that
cluster; that cluster stays loaded while the portal stands); with no free spot anywhere in range the map is not used
(@RANDOMDUNGEONMAP_NO_DUNGEON_FOUND). The Avalonian elite maps (T6-T8_RANDOM_DUNGEON_ELITE_TOKEN_D1-D4) work the same way: in the
black zone their T6-T8_AVALON_ELITE_CONSUMABLE portal (an Avalonian elite dungeon, levels 1-4) stands on an ELITE spot. Dying inside a lethal
dungeon respawns in front of its portal. `.rd T4_PORTAL_ROYAL_SOLO` puts a portal in front of a GM, `.rd chest` a chest.
The numbers the data leaves open are in `content/dungeons.json` (read at start). Not yet: shrines, the boss encounter
rules (the boss chest lock below is the stageH radius rule, an [assume]),
corrupted dungeons and hellgates. The database gets one column (schema v11: the way out of a dungeon
that is gone).

PvE changes (PVE-DESIGN step 10, `content/dungeons.json`, each with its own key):

- **Group scaling** (`GroupScaling`, the expeditions' formula): the mobs of a group dungeon (`GroupScaledSubtypes`: VETERAN,
  ELITE) have max health and damage x min(`ScalingMax` 1.0, `ScalingBase` 0.40 + `ScalingPerPlayer` 0.15 x players): 0.55
  alone, 0.70 for 2, 0.85 for 3, full from 4. The players are the entering player's party members online when the dungeon
  is made, or more once more went in; a floor takes the count when it is filled (the mobs already there keep theirs).
  `ScaleDamage` false scales health only. Solo dungeons are not scaled.
- **Boss chests** (`BossChests`): a chest of a boss spawnpoint (`…_LOOTCHEST_BOSS_RARE`, …) is locked (BossLocked, the
  client's own state) while the boss or miniboss within `BossChestRadius` (40 m) of it stands, or, with none that near,
  while any boss of the floor stands; it unlocks for everyone when they die (or are taken off, `.mob kill`). Its guards are
  the ones standing when the floor is filled: a boss that respawns later does not lock it again. A floor without a boss
  leaves it open.
- **Dungeon of the day** (`DailyBonus` 1.5, 1 = off): one faction of `DailyFactions` (Heretic, Keeper, Morgana, Undead, in
  turn) each day from `DailyResetHour` (10:00 UTC); its dungeons give x1.5 fame, silver and loot (mobs and chests, on top
  of the level's factors). Entering one says so in chat (`DailyText`, once the floor is joined); `.rd` shows the day's faction and each dungeon's
  player count and bonus.
- **Shrines** (buffshrines.xml; analysis/work/stageN/shrines/SPEC.md): every `<shrine shrinetype>` tile of a map's active layers
  stands as a buff shrine: in solo dungeon rooms GENERAL_SHRINE_COMBAT_BUFF (the room's "shrine" layer, 2 in 10), the faction
  fame/silver shrines of some rooms, the invisibility shrines of the cities and portal clusters. A click on a ready shrine asks the
  server (UseShrine) and casts OPEN_BUFFSHRINE; its end applies the shrine's spell (the combat shrine: one of three 60 s buffs,
  damage + speed, cooldowns + energy, or heal + reflect). A shrine used up stays spent for the dungeon's life (respawns="false");
  the city shrines respawn in seconds. A faction shrine with an alert radius stays locked while a mob of its faction stands near.

The one NuGet package, `Microsoft.Data.Sqlite`, is restored offline: `nuget.config` lists only the local
package cache (`%USERPROFILE%\.nuget\packages`) as a source, so the package must already be there.

Settings: `config/*.jsonc` (ports, logout time, the client's GameData folder, save and content folders, log level and log file,
the account levels; seasonal cluster layers `World:EventLayers`, `World:AnnounceAll`,
`World:MapIdleUnloadMinutes`, `World:RareResources` (enchanted resource nodes, on), the `Visibility` window, the `Transfer` checks, `Crafting:Returns` and `ClientNumbering:BuildingIdOffset`, only
as comments there; comments allowed, every key optional). Any key can be overridden on the command line, e.g.
`dotnet run --project src/AlbionLocal.Server -- --Log:Level=Debug --Network:GamePort=6000`; an unknown
key stops the start, and so do two listeners on one port (`Network:LoginPort` 5055, `GamePort` 5056, `ChatPort` 5057).

The server also appends its log to `data/server.log` (`Log:File`, `""` turns it off), so a server started in
another terminal can be followed with `tail -F data/server.log`. Over 16 MB the file is renamed to `server.log.1`
(replacing the older one) and started anew. Only one server writes it: a second one started next to it logs to its
console only.

## Mounts and carry load

The mount in equipment slot 7 is mounted over its mounttime (3 s for the ox and the fire drake); a dismount leaves it
standing 1.5 m beside the player (analysis/work/stageD/mounts/SPEC.md). While it stands the player is "half-mounted": a
mount with a halfmountedbuff (ox, horses and most others) keeps that buff's carry capacity (the ox about 1900 kg), and
within 2 m of it the remount takes 0.5 s. Walking more than its halfmountrange (18 m) away despawns it, as the client's
ring around the own mount shows: the mount disappears for everyone, the half-mounted capacity ends, and the next mount
is a full one (stageJ carry-load SPEC §3.1; inferred, no capture walks that far yet). There is no timeout.

Damage that reaches the rider's health interrupts either mounting cast with `MountCancel` and invalidates its
completion timer. A hit fully absorbed by a damage shield, or ignored by invulnerability, does not interrupt it.

The carry load follows the client's inventory bar (stageJ carry-load SPEC §1): only the bag's items weigh (worn items,
the mount, the potion and the food stacks weigh nothing), and the maximum is 50 kg plus the worn items' maxload passives
at their item power (T4 bag 75.6 kg) plus the timed buffs (halfmountedbuff, mountedbuff, potions). A mount whose capacity
is an item passive (the fire drake's PASSIVE_MAXLOAD_DRAKE, 1127 kg at T8; the direboar, swamp dragon, giant stag, horse,
combat mule and owl ones) counts whenever it is equipped, ridden or not, exactly as the client counts it. Dashes and
blinks need a load under 150 % (IfOwnLoadPercent), so an overloaded character still pulses SWORD_SPIN without dashing.

A blink (the cloth shoes' BLINK, DMG_BLINK, the bat mount's, the enigma blade's, DELAYED_TELEPORT) now really moves the caster
(`../analysis/work/stageM/movement/NOTES.md`): the caster's own client moves its player only when the server sends it a
ForcedMovement, so a blink goes to the caster and everyone who sees it as one of the teleport kind (1) that ends where it starts,
and the client jumps to the aim point on its next frame. Before, only the onlookers were told (Teleport, event 4), the caster
stayed put on its own screen and its next step put it back on the server too. A dash's or blink's ForcedMovement now also comes
before the caster's effect list, in the official order, and a pull goes out as the client's pull kind (4, it was 1 = teleport).
The info log shows, for every own dash and blink, how far the client's next step was from the path's end and start (`echoes its
forced move ... m from the path's end`) or that no answer came (`sent no echo`), and every error report the client sends
(`client error: ...`): if a dash still does not move on screen, that line tells whether the client got it.

## Gathering

The static trees and rocks of a cluster (the T1 bushes and trees around Thetford and in the open world, and whatever
other static harvestable a template holds) can be harvested as on the official server (stage 4a, from the capture
`capture 002835`): click one, the character walks up to it and the progress bar runs, 2 s for a T1 tree
by hand, 1 s with a T1 axe, 0.7 s with a T2 one (harvestables.xml's harvest time x the tool's tier factor). At its end
the wood goes into the inventory, onto a stack of it or into the lowest free slot (2 T1_WOOD: one harvest takes both
charges of a T1 tree), the tool loses durability (none for T1 wood) and the tree switches to its depleted look. The
client starts the next harvest of a node by itself while it has charges; an empty one is refused ("depleted"). Walking
off, even a step, and logging out cancel the harvest. A depleted node regrows one charge after the client data's
respawn time x 0.5-1.5 (1-3 min per charge for a T1 tree, never under 15 s), seen by everyone near it; until then it is
shown empty, also after walking away and back or leaving the cluster. The character is saved 2 s after each harvest.
The server log has a line per harvest
(`'Name' harvested 2 x T1_WOOD from harvestable WOOD T1 #123 at (44.5, 3.5): 0/2 charges left, new stack in inventory #2`)
and one per refusal with the reason.

Tiers from 2 up need a tool of their resource (axes WOOD, stone hammers ROCK, sickles FIBER, pickaxes ORE, knives HIDE)
of at most one tier below the node; `.item add T2_2H_TOOL_AXE` makes one, and `.harvest info` shows the server's view of
the nearest node (charges, next regrowth, what a harvest gives). A T2+ harvest gives fame and destiny board progress
(see Fame and the destiny board). With premium (see Premium, focus and learning points) the harvest adds half its
amount on top (HarvestFinished's premium bonus, the fraction at random). Bonus loot: each harvest of a T4+ node rolls its
tier's `<Loot>` of harvestables.xml once (× `Rates:Loot`): a gathering tome (`T4_SKILLBOOK_GATHER_WOOD`, …) now and then, put
into the inventory when it fits and shown with the harvest (HarvestFinished keys 9/10; `../analysis/work/stageN/econ/SPEC.md` §2).

### Static dungeons

The 98 fixed dungeons of world.xml (stageN, `../analysis/work/stageN/static-dungeons/SPEC.md`; 156 DUNGEON_* clusters of one or
two floors, such as Stoneroot Caverns T5 `DNG-KPR-02-MAIN-04` behind its main entrance in Tharcal Fissure 4206) are ordinary shared
clusters, as official: their entrances and floor-to-floor exits are plain exit travel, and their mobs, bosses, chests and respawn
timers come from their own cluster data. Their PvP and death rules are those of their colour's archetype (yellow: a knockdown; under
the pve preset only Red against Red anywhere). staticdungeons.xml's dungeon events run on a fixed schedule per dungeon (Royal
dungeons a 1 h event every 45-135 min, Outlands 2 h every 3-6 h): while one runs, chests made there keep the rarest of its
`rerollnumber` rolls. The world map shows the running events and each dungeon's value (the share of its mobs alive) on the
entrances (ClusterInfoUpdate 65-68), and a Join into a dungeon or an entrance cluster sends both (event 499).

### Resource nodes of the open world

In the open-world clusters (Birchcopse 1207, Windripple Fen 0204 and the other world clusters with a resource preset;
not in the cities) the T1-T4 trees, rocks, fiber and ore spawns are there now as well (stage 4b,
`../analysis/work/stage4b/SPEC.md`). The client places them itself when it loads the cluster, by a layout that depends
on the date of the server start (Login key 57: the server sends its start time, so the layout is that day's for the
whole run and changes only when the server is started on another UTC day); the server computes the same layout and
announces every node of it. Walking through 1207 you see the trees and rocks around you with their normal look (no
"not found in landscape" in the client's report), about one node in ten of T4 shining as enchanted (.1 to .3; the
server rolls that per node, `World:RareResources=false` turns it off). They are harvested like the static ones: a T2
tree takes 2 s x the axe's tier factor and gives T2_WOOD, a T4 one needs at least a T3 axe, an enchanted one gives its
enchanted wood or stone (T4_WOOD_LEVEL1, ...); a node keeps its full charges until harvested (T2-T4 3, T5/T6 5), is
shown empty when depleted and grows back one charge at a time as the static trees do. The same day's layout also rolls
the dynamic templates of the cluster's dyn slots (the mob camps, e.g. `M_FR_DynamicMobcamp_03` in a 1207 slot): their
trees and rocks are there and the default template's are not, and the camps' mobs stand in them (see "Mobs" below). The
server log shows per cluster `cluster 1207: 1026 resource nodes, 652 mob spawn prefabs, … mob spawn points (T4_FR_ROY_WLD/T4_FOREST, 2026-10-14); dynamic
templates instanceslot_04 M_FR_DynamicMobcamp_03, ...` and per map how many nodes are enchanted. `.near` lists them as
`resource node WOOD T4 at (x, z), 3/3 charges` (`WOOD T4.2` when enchanted), `.harvest info` adds the node's
enchantment and the prefab and node tile it sits on. Not yet: reputation and season points of a harvest.

### Fishing

The fishing zones of the templates are there (stageG, `../analysis/work/stageG/fishing/SPEC.md`): the swarm, fish and
chest spots show their VFX where the official server has them (0204; Thetford has base zones only, which show nothing),
and a spot that was fished out changes its look until it grows back. With a fishing rod in the main hand or anywhere in
the inventory (`.item add T3_2H_TOOL_FISHINGROD`; the client takes the best one itself), click on water: the rod appears
in hand, the charge bar runs (up to 1 s, 3-15 m), the float splashes, and after the zone's bite delay the float dips; a
click within 0.8 s opens the minigame (hold to pull, release to give line), a miss ends the cast. The client plays the
minigame itself and may report a success only after 3 s; then the catch from the cluster's loot.xml fishing list (city,
biome and tier; treasure lists at chest spots, the boss shark at the boss spot) goes into the inventory with its
items.xml fishing fame (× Rates:Fame × Rates:GatheringFame, into the GATHER_FISH nodes) and the rod loses 1 durability.
Each strike takes a charge of the zone it lands in (an empty spot leaves it to the base zone around it); charges come
back one by one. Other players see the casting, the float and the reel poses. The numbers the client data does not hold
(bite times, charge regrowth, the strike window, the minigame settings, …) are in `content/fishing.json`, read at start.
Walking off, mounting, a cast or attack, a knockdown and logging out end a session. **Bait** (`T1/T3/T5_FISHINGBAIT`, used
from the bag): a 10-minute buff with 10 charges; its fishingspeed (0.5 / 1.25 / 2.5) shortens the bite delay as the rod's
does (÷ (1 + rod + bait)), each strike takes a charge, the last one ends the buff. Not there yet: the black market catches,
the fishing speed of other gear.

## Mobs

Stage 5a (`../analysis/work/stage5/SPEC.md` §5a): every mob spawn point of a cluster has its mob, those of the cluster's
templates and those of the resource layout's prefabs (mob camps, roaming mobs, the critters of hide and fiber nodes). A
named point spawns its mobs.xml mob, a RANDOM one a weighted pick from its randomspawnbehaviors.xml block (the first one
that fits the cluster's tier and continent); the world boss points get their bosses separately (World bosses below). In 0204 the swamp toads, the undead and the two
guard towers stand at their points with full bars, the names and levels of their type; walking away makes them vanish
and walking back brings them again. They do not walk, aggro or fight back yet (stages 5d-5f). The
map's log line counts them (`map 0204: 3553 world objects (…, 1116 mobs); 51 kinds of mob at 1116 spawn points`). `.mob spawn <mob> [count]` puts mobs of a
mobs.xml uniquename around you (they never come back once gone), `.mob kill [all]` takes the nearest mob in view (or all
of them) away, and a spawn point spawns again after the mob's respawn time, or its block's for a RANDOM point (minutes),
divided by `Rates:Respawn` (1; `.rates respawn 100` or the older `.mob respawnscale 0.01` while the server runs makes it
seconds; see Rates).

Stages 5b and 5c (SPEC §5b, §5c): mobs take damage and die. Auto-attack (AttackStart) swings every 1 / attackspeed and
hits after characters.xml's attack damage time (melee 0.25 s, ranged 0.32 s, magic 0.36 s, × the weapon's
attackdamagetimefactor; ranged and magic add the projectile's flight at `AutoAttackProjectileSpeed` of
`content/combat.json`, 20 m/s); the spells chosen in the item slots are cast with their cast time, charged energy at the
end (round(energyusage × 1.0918^(IP/100)) of the granting item), and hit after hitdelay and the projectile's flight.
Damage is attackdamage × attackdamageprogression^(IP/100) for autos and change × abilitypower ×
abilitypowerprogression^(IP/100) / 100 for spells, times 100 / (100 + armor or magic resistance) of the mob; a T4 axe
at IP 945 hits a ghoul for 39, AXESMASH for 201. Spell effects run: direct damage and heals (single target and area),
damage over time, applyspell and pulsingspell; the start log line `spell effects: … kinds run, … as no-ops; not yet: …` lists the effect kinds
that are still no-ops (buffs and debuffs, spell areas, crowd control, …). The first hit puts the player in combat
(no health regeneration), which ends 20 s after the last hit or at once when its last enemy dies. A killed mob gives
fame (mobs.xml fame × the cluster's ClusterDangerBonus famefactor, safe 2.15, / mindivisor; saved with the character)
and its energy reward, drops its loot.xml items in a bag only the killer can open (it vanishes when empty or after
`LootBagSeconds`, 120 s), stays a corpse for 5 s and its spawn point respawns it. The equipment of the loot lists
(useblackmarket items) comes from the Black Market on the official server, which a local server has none of: such an
item drops only with `Loot:BlackMarketChance` (0.1, fitted to the 18 captured kills: a T4 ghoul drops a bag about one
kill in 16, a soldier or mage one in 5; 1 = every rolled item; an estimate, tune it after playing). Loot rates: `Rates:Loot`
scales the loot factor once (not the Black Market chance as well); `Loot:BlackMarketSoftCap` (4) thins out a roll past 4
such items (each further one with 4 / its number), so a chest whose lists name hundreds of Black Market rolls (Avalonian,
static, hellgate, world bosses) gives tens of items; an item more than one tier above the mob or chest comes one tier above it
unless an explicitly rare entry (chance at most 0.5, such as the T7 list of a T5 chest) rolled it. Chests other than dungeon
floors' get no cluster moblootfactor (a mob's factor). Typical rolls under the PvE preset (analysis/work/stageN/loot-balance):
a T5 corrupted rare chest 8-9 items (was 38), a T5 solo dungeon rare chest about 8 (was 29), a T5 veteran mob 0.3 (was 0.9). Every entry of a
mob's `<Loot>` is rolled, as in the client's copy of the roll, so its own `<Item>` entries (lore unlocks, silver bags,
quest tokens; 435 in mobs.xml) can drop too, not only its loot list references. "Take all" in the bag moves the items
into the inventory's stacks first, then into free slots; what finds no room stays in the bag. Single items come out by
shift+click, by a drag onto a bag slot (onto a stack of the same kind joins it), by a split drag, or by a drag (or double
click) onto the equipment, which equips the item straight away: the replaced item (with the offhand for a two-hander) goes
into a free inventory slot. A move the server refuses (the window closed, out of reach, no room) puts the item back into
the loot window; nothing goes into a loot bag. A world chest opens by clicking it: the client casts the chest's opening
spell (2 s for most) and the loot window opens from where it stands. A mob with silver in its
loot drops a silver pile (a T4 ghoul about 48 silver) that only the killer can take, by walking over it: it pays the
amount × the cluster's silver factor (safe 1.305: 49 → 63.94 silver), × 1.5 while premium (`Premium:SilverFactor`; the
official pickups of the captures had it, and 15 % guild tax); `Economy:SilverPickupFactor` (1) and `Economy:SilverPickupTax` (0)
add to that. The pile
vanishes after 5 min. The fame also fills the killer's destiny board, when it has one (see Fame and the destiny board).

Stage 5d (SPEC §5d): mobs fight back (`World:MobAi`, on; `.mob ai off` switches it off while the server runs). A hostile
mob with an aggroradius (the soldier veteran 8 m) picks the nearest living player inside it after aggrodelayafterspawn;
the others (aggroradius 0, critters) fight only when hit. Threat is damage × damageaggrofactor and healing ×
healingaggrofactor; the target changes past 1.1 × the current one's threat in attack range (1.3 × out of it). The mob
runs straight at its target at attackmovespeed (event 3, re-sent every `MobMoveResendSeconds`, 0.5 s), stops in reach
and swings every 1 / attackspeed, the first `MobReactionSeconds` (1 s) + its windup after the aggro; the hit lands
meleeattackdamagetime later (ranged and magic add the flight at 20 m/s) for attackdamage × 100 / (100 + the player's armor;
see Armour below). Being hit puts the player in combat; an equipment change
in combat keeps the current health and energy. Farther from home than pursuitradius, without a target, or
`MobLeashNoHitSeconds` (4 s) after its last swing while its target is out of reach, the mob resets: the MOB_RETURNHOME
buff ((base + 200) × 3 health regeneration), the run home immune to damage (attacks on it do not put the player in
combat), full bars on arrival and the buff's clear 0.15 s later. `.mob ai off` settles every mob where it stands. Idle
mobs that a player sees roam within roamingradius of home; GUARD points stand, PATROL points walk their waypoints.
StageN mobnav (analysis/work/stageN/mobnav/SPEC.md): on a map whose cluster has tile collision (open world, random dungeon
floors) a mob chases and runs home along a path round walls and cliffs (the bots' 1 m walk grid, built once per cluster in the
background; straight until it is ready), aggroes only players it can see, and swings or casts at its target only with line
of sight; without sight in reach it walks to the target until it sees it.

Invisibility to mobs also invalidates an existing threat target, including the get-up bubble. The mob switches to
another valid enemy, or resets if none remain. A stationary reset sends a fresh stop sample to its observers.

Armour (analysis/work/stageJ/armour/SPEC.md): a player's physical armour, magic resistance and crowd control resistance come
from its worn items with their item power, as the client's character window shows them: (share × 100 + the items.xml value) ×
1.03^(IP/100) − share × 100, where only the chest has an armour share (1.0) and the chest, shields (0.6) and talismans (1.8) a
crowd control share; IP counts quality, destiny levels and wear as for the health. A T4 plate chest (q1, IP 700) gives 199
armour, 161 magic resistance and 106 crowd control resistance, so a roaming undead soldier's 131 auto lands for 44; the T4 cloth
robe for 63-64. Shields add defence vs mobs and players (T4: 3.05 %, × 1.16^(IP/100)), which lowers every hit by that share,
as do the head and shoe passives (PASSIVE_BALANCE 1 %, PASSIVE_MR_AR 2.5 %). Buffs and debuffs add their points, clamped as
gamedata `<ActiveBuffScaling>` says: debuffs take at most 60 % of max(armour, 100) and the total never drops below 0, for
mobs too. Crowd control lasts 100 / (100 + crowd control resistance) as long. `Combat:GearArmor` (true) false goes back to
the raw items.xml sum of before (A-B checks only). `.defense` shows the numbers and the share of a mob's hit that lands. A
mounted player's hits go to the mount with the mount's resistance, as before; the rider's shield defence vs mobs
lowers them too, as its buffs and passives already did (open: no mounted capture settles it).

Worn damage bonus (analysis/work/stageM/pvp-damage/NOTES.md): a player's spells add the worn items' physical or magic spell
damage bonus to their damage, as the official server does: the chest's items.xml value as it stands (plate 0-0.1, leather
0.2-0.3, cloth 0.4-0.5) and an off hand's × 1.16^(IP/100) (a T4 demon skull at IP 750: 0.049). A T4 cleric robe (0.45) with
two damage passives makes a fire bolt 109 instead of 77 on a T4 mob, the captured number. Auto-attacks keep only buffs and
passives (the captured autos take about a third of the robe's attack bonus; open). The heal bonus and crowd control duration
bonus of off hands now scale the same way. `Combat:GearDamageBonus` (true) false goes back to buffs and passives only. A bot's
spells take the bonus only with `Bots:Gear:DamageBonus` (below). Open: a player's shields (ENERGY_BARRIER and the like) still
absorb without it, although the same capture shows them with the same factor (804 against the server's 520 for the robe wearer).

StageD mob spells (analysis/work/stageD/mobspells/SPEC.md): a fighting mob with an `<AI>` in mobs.xml casts its spells
(`MobSpells` in content/combat.json, on; `.mob spells off` switches them off). About every 0.1 s
(`MobAiThinkSeconds`) while it is free (not casting, standing after a cast, winding up a swing or locked out by a
cooldowntillnextcast) it takes the first `<Behaviour>` node that works: a Spell whose slot is off cooldown (recastdelay
from the cast end), whose energy is there (energyusage × abilitypower / 100) and whose conditions hold for a target in
range; If, SelectFirst, SelectRandom, CompoundAction, Variable and BroadcastVariable as in the client's model. An IfChance
is rolled at most once a second (`MobAiChanceRerollSeconds`). The casts go out as the official ones (CastStart, the
EnergyUpdate, CastFinished and CastSpell at the end, the hit, channels and NewSpellEffectArea areas), and their effects
hit the players (damage, damage over time, debuffs, pulses, areas, auras) and heal or buff the mob. A reset or death
cancels the cast and removes its areas. StageN spells-bosses (analysis/work/stageN/spells-bosses/SPEC.md): `<Movement>`
repositions (walked on the walk grid, never into a wall), IdleBehaviour, the mobs' own dashes and their knockbacks, pulls
and fears on players (ForcedMovement, stopped by walls), `<SpawnSpells>`, `<DeathSpells>` (death blasts, the elites' fame
buff) and `<KillRewardBuff>`; the bosses' enrage buffs already ran through their `<AI>`. A mob's telegraph now points at
its target (CastStart key 2 only for ground spells, the mob turns before it casts). The spell engine reads every If*
condition of spells.xml, `<valueoverride><IfCharge>` (the stacking damage of the axe
bleed and the flame orb), and the gathering tomes, the fish food's fishing fame, repair kits and the fame buffs work.
StageN spells2 (analysis/work/stageN/spells-bosses/SPEC2.md): the `<AI>` conditions IfTargetTookDamage (the dummies' and
chests' flinch, the heretic healers' heal) and the fire dragon's threat areas (IfHighestArea, IfTargetIsInHighestArea,
IfPlayersInArea: it turns to the side with the most threat), RandomGroundTarget (the meteors and flame artillery land
around), the bosses' `<AI>` onDeath effects, the mobs' changeaggro (alarm traps and hound alerts call their camp, the
mammoths drop their target) and the tracking mobs' EscapeSpellSequence (`.mob escape`; the hunts that trigger it are not
there yet). The two-step weapon spells that recast at a stored target work: AFTER_IMAGE's return, the knuckles' punch and
counter dash, the ice sculpture's explosion, the spider thread's pull, the holy shield's second cast.

Stage 5e (SPEC §5e): mobs do not kill a player, they knock it down (the PvE knockdown of gamedata DeathSettings). At 0
health the player falls with the "knocked down by @mob" screen (KnockedDown), out of combat; every mob drops it and
resets, and it regenerates slowly (max / 600 per second, max / 18 once no mob fights it). While down it cannot walk
(Move gets 881), attack, cast, harvest or loot, no heal reaches it (only a revive, below) and no mob aggroes it; its own damage over time and
spells still in flight stop doing anything. 18 s after the fight ended (600 s at the
latest) it stands up where it fell with full health and energy (Respawn), and 3 s later gets the knockdown
invulnerability (SPAWN_KNOCKDOWN_INVULNERABILITY: mobs leave it alone) until it attacks or casts, or 15 s pass.
Walking preserves this protection, as declared by the buff's effects. Mobs also ignore the player during the
three-second get-up animation before the buff arrives (an action then does not stop the buff from coming).
There is no real death in PvE: no respawn point, no silver or durability loss (the SPEC defines none for the knockdown;
`Pve:KnockdownWear` adds a share of each item's maximum durability, 0 by default),
and nothing of the knockdown is saved, so a player who leaves while down comes back standing
at that spot with full bars. `.damage <n>`, `.kill` and `.heal` try it on oneself. The knockdown screen's death recap
lists what hit the player (see "Death recap and PvP details" below).

Reviving (stageN, `analysis/work/stageN/revive/SPEC.md`; `Pve:PartyRevive`, on in the pve preset): a player knocked down (by a mob,
or a non-lethal PvP knockdown outside a duel) while a party or guild mate is on its map **bleeds out** instead (BleedOut 693: the
client's bleed-out bar, 120 points losing 1/s with a standing ally within 43 m, 4/s without, 30/s when every ally is down). A party
or guild mate clicks it and channels PLAYER_BLEEDOUT_REVIVE (slot 27, 6 s, 1.5 m); damage, a walk, another action or its own cancel
stops it. At the end the downed player gets up (Respawn) with 30 % health and BLEEDOUT_REVIVE_DEBUFF (-10 % max health, 45 min).
Run out (or the give-up button outside a lethal zone), the bleed-out becomes the plain knockdown; the 18 s get-up timer runs on
all the while. The holy staff's RESURRECTION works on any knocked-down friend: ResurrectionOffer, the target accepts (op 207) and
gets up with 30 % health.

### Open-world chests and camps

StageH (`../analysis/work/stageH/bosses/SPEC.md` §4.2-4.6; `World:OpenWorldChests`, on) puts loot chests into the open world,
on the chest tiles of the cluster's templates, with the client's own messages (NewLootChest, UpdateLootChest, the minimap
flag 395 and the local quest events 540-543):

- **POI chests** stand in the static mob areas (0204's undead ruins: three STATIC_UNDEAD_VETERAN_POI spots). The data allows
  one at a time per cluster, on a spot drawn at random; `Chests:PoiLimit` raises it (0 = a chest on every spot, the PvE
  preset's; −1 = the data's). The chest is locked while a mob of its faction whose spawn point is within 15 m lives
  (the boss of the spot, with the boss-chest minimap icon), unlocks when the last one dies (its rarity shows then) and locks
  again if one comes back before it is opened. The nearby boss decides the variant (a champion gives the CHAMPION chest, a
  miniboss the MINIBOSS one). An unopened chest is redrawn every 30-90 min; an opened one goes 5 min later (or when emptied),
  and the next comes 30-90 min after that, both divided by `Rates:ChestRespawn` (1).
- **Boss reward chests**: killing the mob of a boss point next to a mob-upgrade spot (0204: the mage boss at (17, −27)) puts
  a chest there at once, unlocked: CHAMPION for a champion, MINIBOSS, BOSS. It goes 5 min after its opening.
- **Camp chests** stand in the day's rolled mob camps, one per camp, but each player has its own: walking into the camp's
  circle (35 m for a small camp) starts its quest in the client's tracker; the fame of the camp's mobs you kill inside
  (raw fame × the zone's fame factor / the mob's mindivisor, a party member in the same circle counts too) fills it (7580
  for a small camp, about one clear), and at the end the chest unlocks for you alone. You roll your own loot; 10 min after
  opening it, or 10 min after you left the circle with the quest done and the chest unopened, your chest and quest reset
  (an open chest window closes), so the camp can be done again; the quest restarts as soon as you are in the circle (the
  official 11 s re-arm is left out). A camp chest that no per-player quest unlocks is not spawned.

Chests roll their loot for the cluster's tier, × `Rates:Loot` (no zone loot factor: that is the mobs'). `Rates:ChampionWeight` (1; the PvE
preset 2) multiplies the weight of the champion, miniboss and boss mobs whenever a RANDOM spawn point picks its mob, so
more elites (with their own fame and loot) stand in the open world and the dungeons. The chest state lives with the map: a
cluster unloaded after `World:MapIdleUnloadMinutes` starts afresh. `.chest` lists the cluster's spots, `.chest unlock`
opens the lock of the nearest chest (for you alone on a camp chest), `.quest local` shows your camp quests. Not yet: the
faction warfare camp chests and open quests, the Roads of Avalon chests (LootChestSpawnpointChestUpgrade), the camp markers
of LocalQuestInfos at Join.

### World bosses

StageH (`../analysis/work/stageH/bosses/SPEC.md` §4.1, §6.1; `content/bosses.json`; under `Preset official` without the file's
`ExtraSpawns` and with the killer's loot: `WorldBosses:ExtraSpawns` false, `WorldBosses:Loot Killer`, pve `true` / `"content"`): the WORLDBOSS spawn points of the client data
get their worldbosses.xml boss (1007 Inis Mon: the two T5 keepers; 2009, 0005 and the black zones of the SPEC's table), and
`ExtraSpawns` adds bosses of the server's own: the shipped file puts the T5 keeper seer into 0204 and the T5 earthmother into
1207, each on a critter spawn point far from the exits and the other mobs (walkable by the data, off the roads as far as the data
shows: check it in the client and move it if needed), with 0.3
of their health and half their damage, back 20-40 min after their death (÷ `Rates:Respawn`). A boss is a normal mob for the client
(NewMob, its name, ring and minimap icon from the client's data; no boss health bar, the client shows that only for dungeon bosses).

- **Schedule** (`Schedule`): `data` (default) brings a killed boss back at the next spawn time of worldbosses.xml (every odd UTC
  hour, as the client's world map banner shows; `DataFile` `worldbosses_asia` for the even hours), `respawn` 300-600 s after its
  death ÷ `Rates:Respawn`, `both` whichever comes first. A boss stands from its cluster's first map (`SpawnAtStart`); a boss still
  standing at a spawn time stays. The state is kept by the server while the cluster's map is unloaded: an unloaded map's boss stands
  again, with full health, when the map comes back.
- **Strength**: `HealthFactor` (1; 0.3 for two or three friends, 0.15 alone) multiplies the maximum health the client gets (NewMob),
  `DamageFactor` (1) its damage; an extra spawn may set its own.
- **Announcements** (`Announce`, `AnnounceToAll`): a system line in the chat of the players in the boss's cluster (with
  `AnnounceToAll` true: of every player in game) when a boss comes at its time or by `.boss spawn` ("World boss Keeper Seer (T5) has appeared in 1007 at (-10, -78)": the
  client cannot show a boss outside view on the map, so the line has its position) and when it dies, with its next time. A boss
  standing from its map's first fill is not announced; `.boss list` shows it. The texts are `AnnounceSpawn`, `AnnounceKill`
  and `AnnounceKillOnce` (a one-off `.boss spawn` boss, which has no next time).
- **Loot** (`Loot`): `shared` (default) is the one bag of the normal kill (the boss's raid boss chest, one roll) that every
  contributor may open (a contributor has at least `LootMinShare`, 5 %, of the boss's threat, i.e. its damage and healing, and the
  killer always counts); `personal` gives every contributor a bag of its own roll (N contributors make N times the items: a PvE
  departure), `killer` the official bag of the killer and its party. `Preset` does not touch `content/bosses.json`: for the official
  behaviour set `Loot` to `killer`, empty `ExtraSpawns` and set `Announce` to false. The fame is the normal kill's (the killer and its
  party near the boss).

`.boss list [all]` shows the slots of the cluster (state, health, next time), `.boss spawn <WORLD_BOSS_… | mob> [here]` brings a
slot's boss now or puts a one-off boss 5 m in front of you, `.boss kill` despawns the nearest boss (it comes back at its next time).
Not yet: the bosses' enrage, summon, pull and knockback spells (they fight with their other spells and auto-attacks), the fame buff
of their death spell, and slots of clusters nobody entered since the start (a cluster's bosses exist from its first map on).

### Hunts (tracking)

StageN hunts (`../analysis/work/stageN/hunts/SPEC.md`; client data `dynamichunt.xml`): the official tracking. Equip a **tracking
toolkit** (`T3`-`T8_2H_TOOL_TRACKING`, main hand; it can now exist in the bag and be crafted like other tools) and use its find
ability: after the `Tn_FIND_TRACK` cast (6-7 s, standing still) up to two wild **tracks** lie near you (within the spell's
175-300 m by tier), shown on the minimap for 17-30 s and listed in the chat with distance and direction. A track's creature is of the
toolkit's tier or one below: silver targets (earthdaughter, harvester, shadowmask, …), panther, ent, direbear, werewolf, imp,
elemental, eagle; group (veteran) targets when you are in a party. Walk to a track and read it (about 16 s; moving cancels).

Reading a wild track starts a **hunt** for you and your party members on the map (they share it). The hunt is a chain of quests,
each at a spot 100-400 m on, with a trail of footprints toward it, a minimap ping 20-60 m off the spot and a chat line with the
way ("~250 m north-east"):
- **track**: find and read the next track (some are guarded by a few mobs);
- **escape**: the creature rests at the spot; once hurt to its threshold (80/60/40/30 % by the quest), attacked by too many players,
  crowded, or after its combat timers it **flees** with its escape spells (dash, immunity) and leaves; its health stays low for the
  next encounter and the trail goes on from where it fled;
- **final**: the creature is cornered and fights to the death; if it escapes (timers, too many attackers) the trail goes on and the
  next quest is the final again.
The final's chance grows with every quest (dynamichunt.xml's chances). The kill gives the creature's normal loot and kill fame,
plus the final quest's fame (450 for a solo silver hunt) × the toolkit's tracking fame bonus (+5 % a tier above T3) to every member.
A quest that is not done within its timeout (20 min) aborts the hunt, as does leaving (no member online) or starting another hunt.
Hunts live in memory: a server restart loses a running one.

GM: `.hunt list`, `.hunt start <target>` (e.g. `EARTHDAUGHTER_SOLO_T5`, `WEREWOLF_SOLO`; starts from where you stand),
`.hunt escape` (the target flees now), `.hunt abort`.
Not yet: the client's hunt quest panel (HuntMissionStepStateUpdate is not sent: the way is told by pings and chat lines), the
party's start-hunt confirmation (members join automatically), tracking achievements, toolkit durability loss, hunts across
clusters (every spot is on the hunt's map) and the joker/undefined hunts.

### World events

PVE-DESIGN step 7 (`../analysis/work/stageH/PVE-DESIGN.md` §2.H; `content/events.json`): invasions and champion hunts in the open
world, made only of messages the client knows (NewMob, system chat lines, a personal loot chest).

- **Start**: by a player (`.invasion`, a ticket; see "Invasions on demand" below) or a GM; with `Scheduled` on (off by default since
  stage I) also every `IntervalMinutes` (30-60 min, counted from the start and from the end of the last event) in a
  random open-world cluster whose map has at least `MinPlayers` players and mob spawn blocks (no city, no dungeon; `Clusters` narrows
  the choice), a kind picked by its `Weight`; or by a GM, `.event start`. At most `MaxRunning` scheduled events at once, and one
  event per map in any case (a GM's start on a map with one is refused).
  The place is a mob spawn point of the cluster (walkable by the data) `Distance` (40-120 m) from a random player there.
- **Waves**: the mobs of the faction of the spawn block nearest the place (Undead, Keeper, …) and of the cluster's tier, picked by the
  blocks' weights: `standard` the standard and trash mobs, `champion`, `miniboss`, `boss` by their mobs.xml category (a kind the
  cluster has not falls back to the next lower one). They come through the `.mob spawn` code on a ring around the place, with the
  wave's `HealthFactor` and `DamageFactor`, and go for the nearest player at once (with the mob AI on) when that one is within the
  mob's pursuit radius of it (most mobs: 30-40 m; else it would give up on the way), the others aggro as players come. The next wave comes
  `WaveDelaySeconds` after the last mob of a wave died. The shipped invasion: two waves of 6-10 standard mobs, then 1-2 champions
  with 1.5x health; the champion hunt: one miniboss (or champion) with 3x health.
- **Lines** (system chat, to the players of the cluster; `AnnounceToAll`: everyone): the start with the distance and direction from
  each player and the position ("Undead invasion in 0204: 85 m north-east of you at (61, 74); 3 waves, 10 min": the client has no map
  marker for it), each wave, the win and the failure. The texts are `AnnounceStart`, `AnnounceWave`, `AnnounceWon`, `AnnounceFailed`
  and `AnnounceStopped`.
- **Reward**: the damage on the event's mobs counts per player; when the last wave is dead every player with at least
  `RewardMinShare` (5 %) of it, wherever it is then, gets `RewardFame` fame (x `Rates:Fame`; when online) and may open the personal
  reward chest at the place (one who died or left may come back to it; a chest of
  the `Chest` LootChestSpawnpoint, DEFAULT_TREASURE_VETERAN: the veteran treasure chests; each player rolls its own loot at the
  cluster's tier x `RewardFactor` 2; the other players see it locked). It stands `ChestMinutes`. An event not beaten in
  `DurationMinutes` (10) fails: its mobs go, no reward; so does a last wave gone with no player damage (a GM's `.mob kill`).

`.event list` shows the running events (wave, mobs left, time left, damage, who started it) and the next scheduled time,
`.event start [kind] [tier] [here]` starts one near you (`here`: 10 m in front of you; a tier 1-10 gives it that row of `Tiers`),
`.event stop [id]` calls one off. Not yet: healing does not count for the reward,
events are not saved (a restart ends them), a map unloaded during an event ends it. Departures from PVE-DESIGN §2.H: the place is
a mob spawn point (walkable by the data), not a POI/RandomExit spot, and the champion hunt's mob stands at the place instead of
roaming the cluster.

### Invasions on demand

Stage I (`../analysis/work/stageI/invasions/SPEC.md`; `content/events.json` `Player`, `Tiers`, `Merchant`, `Drops`): invasions no
longer start by themselves (`Scheduled` false); a player calls one, at a difficulty from T1 to T10 it picks, and the rewards follow it.
Only with `Events:PlayerInvasions` true (the pve preset): under `Preset official` there are no player invasions, no ticket sell
orders and no ticket drops, whatever the file says, and the corrupted maps open their portal (`Corrupted:MapsOpenDungeons` true).

- **Command** (every account level): `.invasion <1-10> [invasion|champion]` starts one at a mob spawn point `Distance` (15-45 m)
  from you on your map. The cluster (and your party anywhere) reads "Name called Undead invasion T7" and the usual start line.
  `.invasion` shows your ticket tier, cooldown and today's starts; `.invasion tier <n>` sets the tier your tickets start;
  `.invasion stop` calls yours off (no reward, the cooldown stays); `.invasion tiers` lists the table. `CommandSilver` can put a price
  on each tier (free by default); `Command` false leaves only the tickets.
- **Tickets**: using `T5_CORRUPTED_NONLETHAL_MAP` (an invasion) or `T6_CORRUPTED_LETHAL_MAP` (a champion hunt) from the bag starts
  one at your chosen tier (else the zone's tier); T9 takes 2 maps, T10 3 (`ItemCost`, from any stacks of the map in the bag). These corrupted dungeon maps do nothing else
  here (the corrupted dungeons are entered in the Hell Dens and the open world's corrupted portals, see Corrupted dungeons), so the client still names them dungeon maps.
  With `Corrupted:MapsOpenDungeons` true the maps open their corrupted portal again and the tickets move to the Knight's Vows
  (`T4..T8_GVGSEASONREWARD_FAMEBUFF_SHORT` invasions and `T4..T8_GVGSEASONREWARD_FAMEBUFF` champion hunts at their tier, the `.kit invasion`
  vows, the drop a T5 vow; the vows cannot be traded, so the market orders of the maps go). A refused use answers "You cannot use
  this item right now." with the reason in chat, and nothing is used; "use all" starts one. `Items` may add fixed-tier tickets
  (`"Tier": 1-10`). Tickets come from Thetford's market (NPC sell orders: 15 000 / 30 000 silver, `Merchant`), from open-world
  boss and miniboss kills (20 % / 5 %, `Drops`), from a won invasion's chest (25 % each) and from `.kit invasion`.
- **Where**: only on open-world maps of the `ClusterTypes` (safe, start, yellow, red and black zones, roads, passages; no city,
  island, dungeon, expedition or the tutorial), one event per map, one led invasion per character, a per-character cooldown
  (`CooldownMinutes` 10 after the command, `ItemCooldownMinutes` 2 after a ticket), `DailyLimit` (none), at most `MaxRunning` (5)
  player invasions on the server. Cooldowns and chosen tiers are kept in memory (a restart clears them).
- **Tiers**: T1-T8 bring mobs of that tier: the zone's invasion faction (`Factions`: Heretic, Undead, Keeper, Morgana, Avalon) in the
  same families as its spawn blocks, else the faction's other usable mobs; T1-T3 exist only for heretics, so a low tier in another
  zone is a heretic raid; a tier a faction lacks takes its nearest one x1.15 per step. T9 and T10 are T8 mobs with the dungeon level
  buffs `MOB_RANDOM_DUNGEON_BUFF_02` / `_04` (+36 % / +84 % health and damage, the buff icon), 1.5x / 2x kill fame and one / two extra
  waves (champions, then a miniboss). More players near the place bring more mobs (`GroupCountPerPlayer` 0.25 each, at most x2). The mobs of a player's
  invasion go for its starter and party, else for players within 60 m of the place, never for bystanders across the map.
- **Rewards** (each `Tiers` row): the personal chest (T1-T3 solo treasure rolled at T4 with the items moved to T2/T3, T4-T6 veteran,
  T7-T10 elite, else veteran) at the row's `LootTier` and `LootFactor` (x the kind's `RewardFactor`), T9 / T10 with +1 / +2
  enchantment on the items that have it; silver (1 000 at T1 to 120 000 at T10, x `Rates:Silver`) and fame (250 to 40 000) each.
  At most `MaxRewarded` (20) players by damage, plus (`PartyShare`) their party members within 60 m of the place.

### Expeditions

PVE-DESIGN steps 8 and 9 (`../analysis/work/stageH/expeditions/SPEC.md`; `content/expeditions.json`, whose SoloGroupCategories,
PartyAutoAccept, HardcorePartyCarry, GroupScaling, EveryRunItems and LeavePenalty apply only with `Expeditions:Rules Content`, the
pve preset; `Official` keeps the client's rules): the royal expeditions of the city
agents (Thetford's offers T3-T6), Standard solo, Veteran and Hardcore for 1-5 players, with the client's own windows and messages only.

- **Register** at the agent (`RegisterRange` 8 m): the category (T3_STANDARD, …; the agent's list) and, with no expedition chosen, one of
  its playable expeditions at random, not the last one done when there is a choice (26 of the 31 have a map). Refusals answer the client's
  codes (10843 + reason: 10856 already registered or inside, 10858 no expedition for the category, 10845 too far) with the reason as a
  chat line. A solo registration is ready at once: the Ready popup and its Join button for `ReadyTimeoutSeconds` (60). `CheckItemPower`
  (off: the client checks the real item power itself) refuses gear below the category's average.
- **Groups** (Veteran, Hardcore; SPEC §3.3): the party leader registers (the client itself refuses a leader of 2+ on Standard: register
  Standard outside the party or as a member). The party members on the agent's map within `InviteRange` (30 m) are registered with
  the leader and get the Ready popup at once (`PartyAutoAccept`, the default: the client's invitation window may refuse a party smaller
  than 5 before it sends its accept); with it off they get the invitation window (262) and `InvitationTimeoutSeconds` (30) to accept or
  decline, and when everyone answered all who accepted get the Ready popup. Each clicks Join; they go into one instance (the first one in
  makes it; one who comes after its completion gets a new one). Without a party (or as a member, "really solo") the registration
  waits `GroupQueueSeconds` (5) for others registering the same category and level, up to 5, who go together (taking the first one's
  expedition; a registration that chose its expedition, or opened it with a token, only joins one of the same). A member leaving the party
  drops out of the group's registration; the leader cancelling while invitations are open ends it for all. One player alone is fine
  (`SoloGroupCategories`; off: refused with "invalid party composition"). A member not on the map at the completion (a checkpoint
  reload, a relog) gets the rewards at its next Join into the finished instance.
- **Group scaling** (`GroupScaling`, SPEC §12.1): a group instance's mobs have max health and damage x min(`ScalingMax` 1.0,
  `ScalingBase` 0.40 + `ScalingPerPlayer` 0.15 x players): 0.55 alone, 0.70 for 2, 0.85 for 3, full from 4. The players are those
  registered together when the first one goes in. `ScaleDamage` false scales health only. Standard is never scaled.
- **Hardcore ladder** (T6_HARDCORE, 18 levels; SPEC §12.2): the level comes from the registration (0 = the highest open one). Level 1 is
  always open; completing level n opens n + 1 (`HardcoreLadder`; within the bonus time with `HardcoreUnlockInBonusTime`; off: every level open, and a registration without a level
  takes the character's recorded one, else 1). A party member may join the leader's level above its own and completing it opens the
  next one for it too (`HardcorePartyCarry`; off: such a member is left out, "level locked"). A locked level is
  refused with the client's "no token" text (10864), unless a token `QUESTITEM_EXP_TOKEN_D<n>_<expedition>` in the inventory opens it
  (`HardcoreTokens`; that expedition, the token is taken when its holder goes in). Inside, the mobs carry the level's
  `MOB_HARDCORE_EXPEDITION_BUFF_nn` (more health, more damage) and the kills its silver/loot/fame factors (1.9 at level 1 … 60.5 at 18);
  the completion pays the category's silver x the level's silverfactor, fame x mobfamefactor, and a token of the next level (its
  `tokenlootlist`); the lootfactor applies to the kills' loot only, the reward items keep their counts. Completing level 18 (`spawnbonuslootchest`) also
  puts the bonus chest HARCORE_EXPEDITION_LVL_18 on the map's HCE_FINAL_REWARD point (open it as any dungeon chest; tier-6 loot).
  The agent's drop-token window works as official: dropping a `QUESTITEM_EXP_TOKEN_D<n>_<expedition>` on the hardcore tab opens that
  expedition's details at level n, and its Register is accepted by the token in the bag (`../analysis/work/stageN/econ/SPEC.md` §3).
- **Join**: a `EnteringSeconds` (10) channel in front of the agent, not in combat (moving more than `CancelDistance`, taking damage, a
  knockdown or the client's cancel stops it; the exit channel the same), then the lock effect and the change into a new instance, a copy of the expedition's map (`@EXPEDITION@<guid>`), at its
  start point; the place in front of the agent is saved as the way back (also over a restart).
- **Inside**: the exits, narrators and checkpoints of the map; its mobs spawn once (no respawn). The tracker shows the tasks and the
  time left (`timeout`, 60 min for Standard); every counted kill updates it. Walking into a checkpoint makes it the respawn point: a
  death inside comes back there (`CheckpointRespawn`), gear kept; with it off the death respawns in the city and ends the run for that
  player. `SpawnAliases` maps spawn names of the maps that mobs.xml lacks to a mob (four built in, SPEC §4.3; `""` drops one). The start
  exit (5 s channel, `ExitUseSeconds`) leaves at any time; the finish exit opens with the completion. No leave penalty by default:
  `LeavePenalty` true blocks a new registration for `PenaltySeconds` (180) after leaving through the start exit or the escape spell before
  the completion (the client's "penalty" text).
- **Rewards** at the completion: the category's rewards (T3 1000 silver …), or its bonus rewards instead (T3 3000 silver, the royal-sigil
  lootbag, a tome) when done within the bonus time and the daily bonus is open (`DailyBonus`: `Expedition` = once a day per expedition,
  `Character` = once a day as official, `Off` = every run; reset `BonusResetUtc` 10:00). `EveryRunItems` (the royal-sigil lootbag) comes with
  every completion. Silver x `Rates:Silver`, fame x `Rates:Fame`; items that do not fit the inventory wait (saved) and are put in at
  the next Join with free slots (a departure from SPEC §5.1, which drops them to the bank or a bag at the feet: nothing is lost or doubled).
  `Substitutes` maps a reward item the server cannot create to another (none needed today).
- **End**: the time up fails the run (everyone out, no reward); an instance closes `EmptyCloseSeconds` (60) after the last player left,
  or `FinishedLingerSeconds` (600) after the completion; a player still inside goes to its way back (a dead one respawns there). A relog
  inside a live instance comes back into it; otherwise to the way back.

`.exp list` shows the registrations, groups and instances, `.exp start <index|uniquename> [level] [players]` sends you into a new
instance (the way back is where you stand; a hardcore level, the players its mobs are scaled for), `.exp finish` completes yours, `.exp
leave` takes you out, `.exp level <n>` opens the hardcore levels up to n. The escape spell inside leaves as an exit does. Not yet: the
official role matchmaking (TANK/HEAL/DPS).

### Corrupted dungeons

Stage L phases A and B (`../analysis/work/stageL/corrupted/SPEC.md`; config `Corrupted:*`, `content/corrupted.json`): solo runs into one of the
20 corrupted maps (`CORRUPT-001` … `020`) at Hunter (T5), Stalker (T6) or Slayer (T8), with the client's own entrance window, tracker
and texts. PvE by default: an invasion only comes to a player who used the Demonic Shrine of his run (consent), mobs only knock you down.

- **Entrance**: every royal city's Hell Den (the HELLDEN portal of the city; Thetford's is `0000-HellDen`) has the corrupted entrance at
  its static spot, and the open world has corrupted portals (`OpenWorldPortals`: `CORRUPTED_SOLO_NONLETHAL` / `_LETHAL` on the SOLO
  exit markers by randomdungeons.xml's distribution, about a third of the royal T4 solo spots; walking in opens the same window, Hunter
  only from a non-lethal zone, Stalker and Slayer only from a lethal one, the way back in front of the portal). A click opens the client's difficulty window with your infamy; its gates are the client's and the server's copies
  (`CheckItemPower`, off: the client checks its own 700 / 900 / 1100, the server's plain average of the gear is lower; `CheckInfamy`:
  Slayer needs 100 000 peak infamy; in a Hell Den every rank is open). Enter starts the 3 s cast (damage, a knockdown, moving or
  another action stops it), then the loading screen into a new instance (`@CORRUPTEDDUNGEON@<guid>`) of a map picked by weight (`Maps`
  in `content/corrupted.json` changes them); the spot you cast from is your way back (also over a restart). A refusal shows the
  client's cast error and the reason in chat.
- **Inside**: the map's demons at the rank's tier with the rank's danger bonus (Hunter fame x2.4, Stalker and Slayer x4.2), no respawn,
  no mounts or equipment changes (an item dragged into or out of the equipment goes back), healing −40 % on arrival (`EnterBuff`). The
  tracker counts points: every demon killed adds its share of the points the rank needs (100 / 500 / 2000), so that kills worth
  `ClearShare` (60 %) of the map's mob fame draw the demon out in its lair (traps, hazards and watchers count nothing). The demon's death
  completes the run: the boss chest opens, the infamy and rewards come, the run closes `LingerSeconds` (300) later. The standard chests
  open once no demon lives within `ChestGuardRadius` (20 m); every chest rolls the corrupted loot of the rank's tier.
- **Infamy and rewards**: a completed run gives (points + 300 / 1500 / 6000) x `InfamyFactor` infamy (pve 2), leaving before the demon
  `EarlyLeaveShare` (pve 0.5) of points x factor. The client's infamy popup and ranks follow it; each milestone (50 … 10 M) is a chat
  line with the new bonus. The bonus is the client's own curve (up to +20 %, the value its infamy tooltip shows) on the run's mob silver
  and loot, its chests and the completion silver. The completion pays 15 000 / 40 000 / 120 000 silver and 20 000 / 60 000 / 200 000
  fame (`Completion`), x Rates and x `RewardFactor` (pve 1.5).
- **Leaving**: the exit portal on the start room's exit (`ExitPortal`: `Always` in pve, `AfterDemon` official, `Off`), the escape spell
  (the client's cast; inside a corrupted dungeon its cast gets no server answer), or `.cd leave` (not knocked down or in combat; only
  with `Corrupted:LeaveCommand`, pve true, official false). A
  death respawns in the city and counts as leaving (the early-leave infamy, a new run at once). A logout inside keeps the run
  `RelogMinutes` (10); the relog comes back into it. A run the owner left closes `EmptyCloseSeconds` (30) later, every run after
  `MaxMinutes` (120); a character in a run that is gone joins at its way back.
- **The Demonic Shrine and invasions** (phase B): the shrine of the start room is usable for 45 s after you arrive (`Invasions`
  `Consent`; `Off` leaves it inactive). Using it (the client's confirmation, `ShrineUseSeconds` 3 of use, damage cancels it) is your
  consent: the tracker shows "searching", and `BotInvaderDelaySeconds` (60-180 s) later a bot invader (`BotInvaders`, needs
  `Bots:Enabled`; without bots only players may come) comes in at the invader spawn point farthest from you (120 m when there is one), with the rank's gear, health ×
  `Invader:HealthFactor` and damage × `Invader:DamageFactor` (0.8 / 1 / 1.2). It hunts you over the whole map and is Hostile to you
  alone. Three Demonic Shards stand in the dungeon: beat the invader or destroy the shards and it is repelled (`InvaderReward` of
  `content/corrupted.json`: 5 000 / 15 000 / 40 000 silver and 300 / 1 500 / 6 000 infamy × `InfamyFactor`); if it knocks you down it
  leaves, and you lose nothing (`InfamyLossOnDefeat` 0). Only a real fight pays: an invasion that ends because one side left, escaped or
  was offline 10 s, or whose shards the invader broke for you, pays nobody. The dungeon stays yours: an invader's kills give no points,
  a demon he slays rises again, and only you open its chests. Both sides get full health at the start and the end. Two players of the same
  rank who both used their shrine may meet instead (`PlayerInvasions` `Consent`): the invader sees "invading in" for
  `InvadeCountdownSeconds` (10), is moved into the other's dungeon, the two are Hostile to each other only (a knockdown under pve), and
  30 s after the end the invader is back in its own run where it stood (offline then: at its next login, even when the other dungeon
  has closed). A winning invader's silver carries its own run's infamy bonus. One invasion per run (`MaxInvasionsPerRun`). The official
  preset rolls every 30 s with the data's chances (1.6 % without, 5.9 % with the shrine), real players only, 0.1 of the loser's infamy
  lost, under full loot a winning invader takes `percentbosslootonkill` (1-2.2 %) of the owner's silver, and the anti-reset imps (`AntiReset`) come every 180 s of a
  fight. A bot invader beaten counts for the journal's "beat an invader" and the PvP goals as a bot victim; it drops no bag.
- **The chain** (`Chain`): the slain demon opens a chain exit in its lair, and the escape key offers Next Dungeon / Surface; Next
  Dungeon (at the chain exit or from the escape window) casts you into a new dungeon of the same rank on another map, your way back
  kept; each dungeon of the streak gives `ChainBonus` (pve 0.1) more completion silver, fame and infamy, at most `ChainBonusMax` (0.5).
- **Goals**: the journal's corrupted achievements progress (the demon kills, the infamy reached, beating an invader or a host, the
  rank's player kills), and the bounties of kind `corrupted`. The Conqueror's Challenge might (`mightreward`) waits for that window.

`.cd` shows your infamy, its peak, the bonus per rank, your runs and completions; `.cd leave` takes you out. GMs: `.cd enter <1-3> [map]`
(straight into a new run, no gates), `.cd finish` (completes yours now), `.cd infamy <n>`, `.cd list`, `.cd shrine` (the consent
now), `.cd invade [bot|player]` (an invasion now); `.rd CORRUPTED_SOLO_NONLETHAL` places an open-world corrupted portal where you stand.

### Hellgates

Stage L phases A and B (`../analysis/work/stageL/hellgates/SPEC.md`; config `Hellgates:*`, `content/hellgates.json`): a party's run through one
of the 60 hellgate maps (`HELLGATE-01-2v2-01` … `HELLGATE-10-5v5-01-HRD`) in the four modes of the client: `hellgate_2v2_non_lethal`
(1, T5, IP 700, 100 points), `hellgate_5v5_non_lethal` (2, T5, IP 700, 100 points), `hellgate_2v2` (3, T6, IP 900, 500 points) and
`hellgate_5v5` (4, T7, IP 900, 500 points), with the client's own shrine, tracker, chests and exit portal. PvE by default: the non-lethal
modes end with a demon guardian; only the two lethal ones (`VersusModes`) bring an enemy team, of bots, and under pve their hits only knock
you down (choosing such a mode is the consent; nothing is lost).

- **Entrance**: every royal city's Hell Den has the hellgate shrine (`SHARED_HELLGATE_ENTRANCE`; Thetford's `0000-HellDen` at (760, 130)).
  The client wants a party (every mode is for teams) and checks its own average item power (700 / 900; the server's copy
  `CheckItemPower` is off under pve). Under pve the shrine is a fixed-mode one (`ShrineMode` `Fixed`): walking in uses it in your mode
  (`.hg mode`, default `DefaultMode` `hellgate_2v2_non_lethal`), so 2-4 players can run a 5v5; `Gui` (official) opens the client's mode
  window, whose Join wants a full party. Each member at the shrine channels 3 s (`ChannelSeconds`; moving 1 m, damage or a knockdown
  stops it), the party sees the shrine's count. The party's mode is the first member's: a member starting another mode is refused (each
  one consents to an enemy team by choosing that mode itself; `.hg mode` to match). When every member that channels is done the lock
  runs 3 s (`LockSeconds`); a party member standing at the shrine (within 5 m) without channelling holds the others one more channel
  time at most, with a chat line naming it, and can follow later. Then the party is moved into a new instance (`@HELLCLUSTER@<guid>`) of a map of the mode's mapset by weight (32 plain, 10 with a miniboss, 5 with a
  boss; the boss maps x `BossMapFactor`, pve 3; `Maps` in `content/hellgates.json` changes them). The way back is 6 m from the shrine on
  your side, outside its trigger, so coming back does not start a new channel at a fixed shrine.
  A member coming later joins the party's open instance through the shrine while it has room.
- **Inside**: the party starts far from the objective chest, away from every demon (`StartClearance` 25 m); the demons spawn once, with
  their health (and damage, `ScaleDamage`) scaled for the players sent in (`GroupScaling`, pve: (0.40 + 0.15 n) / (0.40 + 0.15 x team),
  0.786 for one in a 2v2). The tracker counts points: each demon slain adds round(fame / `FamePerPoint` (100)), at least 1. At the
  mode's requirement the second objective starts (the match start spell heals the party): in a PvE mode the guardian (the map's boss on
  maps 06 and 10, else a brute boss at the objective chest); in a versus mode the remaining demons vanish 2.4 s later and an enemy team
  of bots (`EnemyTeamSize`: pve as many as you are, official the mode's team) comes in from the far start, with gear of the map's tier,
  Hostile to the party alone and hunting it over the whole map (without bots, `Bots:Enabled`, the guardian comes instead). Defeat it:
  the objective chest appears at its marker (the mode's pve chest, or the pvp one after a team), and the REWARD layer's chest of maps
  03/04/07 opens with the minibosses' death, of maps 06/10 with the guardian's. In a versus run the map's boss never comes: beating the
  team opens the BOSS chest, and minibosses that vanish with the demons open the CHEST. The enemy bots stay while you are out (a relog,
  `.hg leave`); a team that leaves unbeaten (`.bot off`) is replaced by the guardian. Opening the objective chest (or `ClaimSeconds`, 120,
  later) claims the reward: the exit portal appears. Loot, silver and fame follow the rates; a beaten bot gives its usual rewards.
- **Losing and leaving**: every member inside down at once loses the run (`WipeEndsRun`): the bots leave, the exit portal opens, no
  objective chest. A PvP knockdown inside lasts `KnockdownSeconds` (pve 30; official the zone's 600). The exit portal's confirm starts
  the client's 7 s escape; during the run the escape spell takes the client's 15 min; under pve `.hg leave` (`LeaveCommand`) takes you
  out at once (not knocked down or in combat). A death respawns in the city and ends the membership (official lethal modes). A relog
  inside comes back in while the instance lives; one in a closed instance joins at its way back. The instance closes
  `FinishedLingerSeconds` (pve 300) after the claim or the loss, `EmptyCloseSeconds` (60) after its last player left, at the latest
  `MaxMinutes` (30) after its start.
- **Infamy** (phase B): every win (the guardian or a team beaten) gives each winner inside `InfamyWin` (100) hellgate infamy of the mode's
  kind (2v2 or 5v5, the client's InfamyType 2 and 3; the official rating is an MMR this server does not model), x the chain's factor:
  the client's infamy update, a chat line, and one for each milestone of `hellgate.xml` passed (50, 100, 250, ...). The corrupted
  dungeons' entrance window shows it with theirs; `.hg infamy` tells it.
- **The chain** (phase B, `Chain`): after the claim a second portal opens at the map's chain exit (the client marks it on the map) beside
  the exit portal. Walking into it takes a winner into the next hellgate of the same mode (another map when there is one), its chests
  x the client's chain factor (1 + ceiling (1 - e^(-n^scaling / constant)): +0.45 % for the second of a non-lethal 2v2); the way back
  stays the first one's. If the next map is still being read the portal refuses you once: step in again.
- **Party against party** (phase B, `Matchmaking`; official `Players`, pve `Bots`): under `Players` a party entering a versus mode while
  another party's hellgate of that mode is younger than `MatchSeconds` (60) and still gathering points joins it at the far start (B).
  Both gather the points together; then the fight is between the two parties (no bots; Hostile to each other whatever `Pvp:Mode`, by
  having chosen the mode; never within one party). The party whose members inside are all down (or that has had nobody inside for `EmptyCloseSeconds`) has lost: its tracker
  shows the loss, the winners get the pvp chest and the boss chest of the reward room (both refused to the losers, also once a winner
  has opened them), their infamy and the chain. Without a second party in time the bots come as usual; a second party that is not
  inside when the fight begins cannot come in later (the bots are sized for the first one). `MatchSeconds` stands in for the spec's
  `BotFillSeconds`: the bots never come before the points are done, so the wait for a second party is the time it may still join.
- **Restricted area** (phase B, `RestrictedArea`; official on, pve off): 90 s after the start of a fight against a team (bots or a party)
  the ring of fire of the map (its template's centre, e.g. 100 -> 30 m on map 06) closes in at 0.3 m/s, 120 s after reaching its end
  radius it closes to nothing at 0.2 m/s; the client draws it. Outside the radius, in the ring, you lose 1 % of your maximum health a
  second, beyond it 3 % and 0.25 % more each second after the tenth (8.25 % at most; the client's HELLGATE_DAMAGE_HIT); at 0 a PvP
  knockdown (the hellgate's get-up time, no quick PvE get-up). The enemy bots take the same damage; one it beats counts as beaten.
- **Spawn buffs** (phase B, `SpawnBuffs`; official on, pve off): arriving in a hellgate gives the cluster type's spawn buffs: in a 2v2
  HELL_2V2_ENTER (-35 % healing, +10 % damage against players) and, in a versus mode, 15 s of SPAWN_INVULNERABILITY_HELL.
  HELL_2V2_ENTER ends when you leave the hellgate.
- Not done (the spec's client checks first): bot companions for solo players (the client wants a party; bots fight no mobs here), open-world
  hellgates, the might of the weekly PvP challenge (this server has none), the HellgateSuspense effects, the double-healer penalty, the
  journal and achievement hooks of the client's MissionType ChainHellgate / CompleteHellgate / WinHellgatePvp (no such missions here),
  and the optional `EntryItem` (a map from a member's bag at the lock; hellgates cost nothing to enter here).

`.hg mode [name|1-4]` lists the modes and sets yours (the fixed shrine uses it); `.hg leave` takes you out; `.hg infamy` your hellgate
infamy. GMs: `.hg enter <1-4> [map]` (straight into a new instance, no shrine or party), `.hg finish` (the points done, the enemy beaten,
or the reward claimed: the chain exit), `.hg area` (the restricted area now), `.hg close`, `.hg list`.

### Roads of Avalon

Stage L phases A and B (`../analysis/work/stageL/roads/SPEC.md`; config `Roads:*`): the portal network of the client's Roads of Avalon. The 400
road clusters (`TNL-001` … `TNL-400`, 200 files) are ordinary world maps that load and fill like any open-world zone; only their portals
come and go. The network follows the client's own data (`tunnelsystem.xml` and each cluster type's `tunnelpreset`), the same in both
presets; under pve the roads stay black zones in which only Red-flagged players fight (`Pvp:Mode` `Consensual`, a knockdown, nothing
lost), so a Blue player is safe there.

- **The network**: every cluster with a tunnel preset and portal spots (world.xml's `<tunnelexit>` critical objects: 6 in each royal,
  red and black zone that has them, 2-6 in a road) gets its exits split over its preset (a royal road: one exit to a blue zone, one to a
  yellow one, one to another road, three incoming). An outgoing exit rolls a link with its chance and a keyword by weight and takes a
  random free incoming exit of a cluster of that keyword (never its own cluster nor one it is linked to); an open-world portal is a
  group portal (7 charges, a charge every 10 min, 10 min "recently used"), a road-to-road one solo (7, 5 min), a deep raid one raid (20,
  20 min). A link lives 12-24 h (`LifetimeMinutes`; at the first start 0-24 h, staggered); then its outgoing exit rolls again at once and
  the incoming one waits for a new partner. An exit that finds no free partner tries again every `RetrySeconds` (60). The network is
  kept in the character database (schema v26) and taken up again at the next start; `.roads regen` makes it anew.
- **Portals**: a zone shows its linked portals, a road all of its spots (an unlinked one as an empty frame), as the client's own
  objects: the clock ring of the link's time, the charges, the next charge, the target's name in the tooltip, the minimap marker. **Click**
  a portal within 5 m (gamedata's `defaultexitusagedistance`; `EnterRadius`; walking into it does nothing) to travel: you arrive in
  front of the paired portal (4 m in the open world, 5.5 m in a road), facing away from it. Each pass costs a charge and opens the
  "recently used" window, in which the portal lets players in even at 0 charges, so a party can follow its leader. A portal without a
  link or charges refuses with a chat line ("This portal leads nowhere now.", "This portal has no charges left; next in m:ss.").
- **Rescue** (`Rescue`: pve true, official false, a server addition; `RescueMinutes` 60): a road with a player inside and no link at all (a deep raid road has incoming exits only,
  and every road has a short gap between a link's end and the next) gets one at once, from an exit that could lead there or from a
  royal blue or yellow zone. Nobody is stuck in a road (officially one waits for a new link or uses the escape).
- **Content**: the roads' Avalonian camps, critters and resources come from the client data at the map's tier (T4/T6/T8, black-zone fame
  x4.2, then `Rates`). The road chests (`Chests`): each chest spot of a camp rolls its chance (0.648) and a chest by weight, BASE (level 4,
  weight 6), CHAMPION (6, 3) or BOSS (8, 1); the camp's points within 60-100 m spawn the guards of that level (minions, champions, the knight
  boss), level 4 without a chest. The chest is locked while a guard within 60 m lives and shows its rarity once unlocked. Its guards come
  back with each new chest, which comes 2-4 min after an opening; an unopened locked chest nobody sees is redrawn now and then.
- **Bots** (zone class `Roads` in `content/bots.json` `Counts`: 3 gatherers and 2 patrols for one player): the patrols walk in groups of
  `Bots:Roads:PatrolGroupSize` (2-3) and are the road gankers: roads are black zones, so under the pve preset's
  `Bots:PvpRulesByZone` (red and black Official) they attack Blue players too (a defeat is a knockdown under pve).
  `Bots:PvpRulesByZone` may name `Roads` to set the roads apart from the open black zones (`"Roads": "Consent"`: Red players only).
- **Road dungeons** (phase B, `FixedDungeons`, whatever `content/dungeons.json` `FixedExits` says): the camps' fixed dungeon entrances stand on every
  road map, one per entrance tile of the map's tier: Heretic, Keeper, Undead and Morgana solo and veteran dungeons, and the Avalonian elite
  ones on T6/T8 roads. Each draws its level once (2, 3 or 4 at 6 : 3 : 1, the client's `ROADS_FIXED_DUNGEONS`), a solo one is per party, and
  the dungeon behind is a `DUNGEON_RD_TUNNEL` one (fame x4.2, mob loot x3, silver x3.45 before `Rates`). Walk into the entrance as into any
  random dungeon exit (see Random dungeons). The entrance is the camp's own fixed-exit tile, placed at the layout's rotation.
- **Bots through portals** (phase B, `Bots:Roads:Portals`): on a road the bots arrive through its portals and leave through them; each
  pass takes a charge and opens the "recently used" ring you see, so a ring on a portal nobody of yours used means someone just came
  or went (a bot never takes a portal's last charge). A gank group passes as one: its first member pays the charge, the others follow
  free inside the ring, as a party does. A road patrol walks a beat from portal to portal within 400 m and waits
  `Bots:Roads:PortalWaitSeconds` (20-60 s) at each for a Red player coming through. The road maps now have a walk grid of their own (from
  the points in front of their portals), so their bots spawn at all.
- **Official rules** (phase B, the official preset; pve leaves them off): the logout in a road takes `LogoutSeconds` (900, the count the
  client runs there itself, with its roads logout message; pve `"world"` = `World:LogoutSeconds`, and the client goes back to the
  login screen when the server's shorter time is up), and a character that logs in to a road it left `ThrowOutAfterMinutes` (60) ago or
  longer is thrown out: it joins in its respawn city (`Pvp:RespawnCluster`, else its last city, else Thetford) with the client's
  `ThrownOutAvalonRoads` loss, 20-40 % durability of everything and a third of each stack destroyed, its equipment, weapons and mounts kept,
  and a chat line says so (pve 0: never). Only time offline counts: a reconnect while the old connection still holds the character never
  throws it out, and a server shutdown counts as everyone's logout (after a crash the saved time can be the road entry). The rule is
  decided at the character selection, so the Join never contradicts it.
- **Closing portals** (phase B, `TempPortalSeconds` 5): an open-world portal that closes while you look leaves the client's short-lived
  portal visual (NewTunnelExitTemp) on its spot for 5 s.
- **Road bounties** (`content/goals.json`): kinds `roadchest` (an Avalonian road chest opened) and `portal` (a road portal passed), two of
  them in the shipped pool.
- Hideout roads are in the network (links only: this server has no hideouts). Not done: the road hunts (the server has no hunt system yet),
  the 15 s spell cooldowns on entering a road (`spellcooldownsonenteringseconds`: how the client gets them is not traced), the long town
  portal of the roads (no town portals yet), the Mists' city entrance on the roads (with the Mists). Passing with goods is free, as
  officially: a road can be a shortcut past paid fast travel.

`.roads` (pve every player, official GMs: `Command`) lists the portals of your cluster (target, type, charges, the next charge, closing)
and the nearest royal open-world entrances into the roads (the black zones' too in a black zone); `.roads route <cluster>` the shortest way over the open portals,
the portal to take here marked with its distance and direction, each hop with its type and charges. GMs: `.roads info`
(clusters, exits and links per preset), `.roads link <cluster|preset>` (the nearest free spot here to a free one there, 30 min),
`.roads close` and `.roads charges <n>` (the nearest portal's link), `.roads regen`, `.roads dungeons` (the road dungeon entrances of this map).

### The Mists

Stage L phases A and B (`../analysis/work/stageL/mists/SPEC.md`; config `Mists:*`, `content/mists.json`): the client's Mists, the generated zones
behind the wisp portals of the open world, with their camps, treasures, mist bosses and the ways on, out and to Brecilien. PvE by default:
the zones are yellow (PvP allowed) or black (PvP forced, lethal) as the client shows them, and under pve only two Red-flagged players fight
there, with knockdowns (`Pvp:Mode` `Consensual`, `Pvp:LethalDeath` false); a Blue player is safe.

- **Portals**: the open world's random dungeon spots (`RandomExitPositionMarker`) of royal T5-T7, the outlands T5-T8, Avalon and the
  Brecilien wilds (5100-5103) draw mists portals by the client's distribution (`MISTS_SOLO_YELLOW` in royal T5 and non-lethal T6,
  `MISTS_SOLO_BLACK` and the duo `MISTS_DUO_BLACK` in lethal T6, T7 and beyond; weights x `OpenWorldWeight`, 0 = none). A portal is the
  wisp prefab with a rarity colour (Mundane, Standard, Uncommon, Rare, Legendary): under pve 60/16/12/8/4 % (`RarityWeights`), officially
  the client's rerolls (95.9 % mundane), redrawn every 2.5-7.5 min until used. It goes 5 s after its last use.
- **Entering**: click the portal; the client's 3 s ENTER_MISTS channel runs (moving or another action cancels it, damage does not), then
  you are moved into the zone. A duo portal without a party asks first. Under pve every team gets a zone of its own (`Matchmaking`
  `Private`): in a solo mode each player, also in a party; in a duo mode the first two party members through the same portal, also when both click it at once, and they
  arrive at the same border (a third goes alone, with a chat line). Officially (`Shared`) teams of the same mode and rarity share a zone up to the map's player limit (6-12),
  and none joins it after its first 10 minutes. A duo zone entered alone has its mobs at `DuoSoloScaling` (pve 0.6) of their health and damage; when a partner
  follows later, the mobs that spawn from then on are at full strength.
- **A zone** is one of the client's 309 mists maps of the mode, drawn by its rarity (rarity 0: the T5 or T6 commons; 4: the T7 or T8
  rares), built like the client's own generator: a 760 m master turned at random, its slots filled from the map's template pool (enemy
  camps of the heretics, Morgana or undead, resource fields of the rarity's tier, and one misc slot: at rarity 0 a Brecilien entrance, an
  open-world exit or nothing, from rarity 1 always the open-world exit, often a Brecilien entrance too). The mobs come from the client's
  Mists spawn tables at the map's tier, with the instance's mob level drawn once (the client's `mists` difficulty group: up to x1.81 loot,
  silver and fame). You arrive at a border of the zone with a return portal at your feet for 30 s. The camps work as in the open world: the
  camp quest bar fills with the camp's kills and opens your own camp chest.
- **Treasures** (`content/mists.json` `Treasures`): each zone rolls the client's treasure table of its mode: a treasure chest (45 %), up
  to three coffers (33 % each), and from rarity 1 up to two **mist bosses** (65 % each: the mythical Griffin, Spider or Fairy Dragon of the
  zone's tier). Each appears at a camp's spot, at least 40 m from the arrivals and 30 m from the others, at a random time between the
  data's block times (60-420 s) x `BlockFactor` (pve 0.5); the chests need their channel. A slain mist boss is told to the zone and,
  under pve (`BossChest`), leaves a reward chest that only the players with `BossChestShare` (10 %) of its threat (and its killer) may open.
  The client's power-crystal wisps, the crystal spiders and the resource treasures are not done (phase B).
- **Ways on and out**: walk into a **border** exit (the client names its target zone after your way back): the next zone of the mode, of
  a rarity drawn anew, arriving at its opposite border (built first: the answer may take a moment). The **Brecilien entrance** takes you to
  Brecilien's entrance hub (5001); the **open-world exit** under pve back in front of the portal you entered by (`OpenWorldExit`
  `Entrance`), officially to a world zone of a drawn danger (safe, yellow, red or black) and the zone's tier ± 1 on the continent you entered from (the
  other one only when yours has none of that danger: black from royal). The **return portal**
  takes you back to the way back in its first 30 s. Your way back is kept over the borders (saved with the character).
- **Lifetime**: a zone shuts down after 20 minutes x `LifetimeFactor` (pve 40); 5 minutes and 1 minute before, a chat line warns, and at
  the end everyone inside is moved out to the way back. A zone nobody is in closes `EmptySeconds` (120) later. A relog inside comes back
  into the zone while it lives; after a restart or its close, you log in at your way back (`FallbackCluster` 5001 without one). A death in
  a zone (only the official preset's black zones kill) respawns in `RespawnCluster` (5001), also when the zone closes or you log out dead.
- **Brecilien** (5000-5003) is a city as in the client data: its bank (5002) and market (5003) are a location of their own, the harbour
  travels to the royal cities and Caerleon at the usual paid price, and `content/spawns/5000.json` adds the twelve public crafting and
  refining stations of Thetford on free plots. The mists world clusters around it (5100-5103) carry Brecilien entrances.
- **Knightfall Abbey** (phase B): in an open zone the dungeon's portals come in waves, the first 30-150 s after the zone opens (the data's
  60-300 s x `BlockFactor`), then every 170-214 s, each alive 2 minutes somewhere away from you (T5/T6/T7 at 80/14/6 % in the yellow mists,
  T6/T7/T8 in the black). Click one like a mists portal: Knightfall Abbey of that tier, a 124-room castle built like the client's own generator.
  Three of its rooms are boss rooms (at least 90 m apart): the boss guards its chest, which unlocks when it dies. In each other room the mobs
  of the room, and once the room is cleared its statue gives either a reward chest (15 %) or a shrine's blessing (85 %: the rose, scale or
  sword buff). One treasure chest and three coffers wait on the castle's own treasure spots (6-11 minutes and 3-9.5 minutes
  in, x `BlockFactor`). Its 14 exits lead back into a new zone of the mists you came from; the castle closes after 17.5 minutes x `LifetimeFactor`.
- **Wisps** (phase B): in a zone's last 5 minutes portal wisps appear (in the open mists one a minute, at the wisp trees where the map has
  them). Come within 5 m and the wisp flees and bursts 4 s later into a portal of its rarity (30 s) that leads into another zone: the
  coloured wisps are the way into the rarer zones. In a black solo zone a resource wisp may show up too (`ResourceWispChance` 50 %): its
  portal leads into a **resource zone** (Silken Glade, Misty Wilds, Ironheart Vale, Veiled Crags, …): a small zone of one resource for 7.5
  minutes x `LifetimeFactor`, arriving on its own spawn points, whose way on leads back into the black mists.
- **Brecilien's static entrance** (phase B, `StaticEntrance`): its gate in the entrance hub (5001) lists the open modes; choose one within
  8 m and after the 3 s countdown you are in a new zone of that mode (mundane), your way back the gate. The gate then stays closed to you for
  5 minutes (the client's debuff). The roads entrance beside it is shown, but its use is refused (the Roads of Avalon have no city link here).
- **Standing** (phase B): every mob fame you earn in the mists raises your Brecilien standing by as much x `StandingFactor` (pve 2;
  `StandingPerFame` 1), up to the client's 10 000 000 in 10 ranks (a chat line at each new rank, the client told at every login and cluster change; `.mists standing`), and gives favour
  (`FavorPerStanding` 0.01 of it, also once the standing is full). The **City Trader** in 5001 sells its goods for that favour (the tokens
  675, the Brecilien capes, the owl mounts, the avatar) behind the client's standing gates (the token from 150 000) [design: where official
  favour comes from is not in the client data]. The client is told your favour at each login and cluster change (Join key 104, as
  captured), not as it changes (its currency event is not sent for it): `.mists standing` shows it live.
  The standing and the favour are kept in the character database (schema v27: `mist_standing`, `mist_favor`; an older server build
  then refuses the converted file).
- **Drifters** (phase B, bots; `content/bots.json` zone class `Mists`: 1): a lone Red-flagged wanderer of the zone's tier that attacks only a
  player the PvP rules let it (under pve a Red one). In your own zone it only comes while one of you is Red (`DrifterNeedsRed`) and goes again
  (out of a fight) once none is, so a PvE team never meets it. Brecilien's bank and market interiors get fewer city bots (`Clusters.Override`).
- **Official only** (phase B, `UnrestrictedZones` with `Pvp:Mode` `Official`): each camp is a free-for-all circle (the client's 50-90 m
  ring): while the camp's quest is under way for one of them, two players in it (not one party; guild mates not under
  `Guilds:NoFriendlyFire`) may fight whatever their flags. Under `Consensual` or `Off` there is none, whatever `UnrestrictedZones` says.
- Not done: the power-crystal wisps and the crystal spiders (no client objects for them), the resource mists' global resource budget, the
  shrines' own objects (the blessing comes with the cleared room), the roads entrance's party binding, the favour's UpdateCurrency (85), and
  of the official-only PvP the camps' UnrestrictedPvpZoneUpdate/Status (308/309: the 544 circle goes out inactive) and the treasures'
  `pvpzoneradius` 30.

`.mists` tells you your zone (mode, rarity, tier, the time left, your way back), `.mists standing` your Brecilien standing. GMs: `.mists portal
<mode> [rarity]` (a portal of `MISTS_SOLO_YELLOW`, `MISTS_SOLO_BLACK` or `MISTS_DUO_BLACK` on the random dungeon spot nearest you, rarity
0-4), `.mists list`, `.mists close [id]` (your zone, or that one, closes now: everyone out), `.mists dungeon` (a Knightfall Abbey portal next
to you), `.mists wisp [rarity]` (a portal wisp next to you), `.mists standing add <points>`.

## Faction warfare (PvE side)

StageN (`../analysis/work/stageN/faction/SPEC.md`; schema v30). The faction trader in each royal city and Caerleon enlists you
(its Enlist button: one faction at a time; switching to another faction is allowed 24 h after the last change, the game data's
cooldown; your standing and points with the old one stay). Enlisted and flagged to your faction (the trader's Flag button; under the
pve preset a faction flag makes nobody hostile, as before), the fame of mob kills (× 0.007), gathering and fishing (× 0.1) in the
faction warfare clusters (the yellow and red duchies of `factionwarfare.xml`, not the cities) earns your faction's points (× your
rank's point multiplier) and standing (21 ranks, the game data's thresholds), shown by the client as they come and at every login.
The trader sells for those points what the game data lists: faction hearts, cape blueprints (capes are then crafted at the toolmaker
from a cape, the blueprint and a heart), the faction mount babies and the campaign chests, each behind its standing gate (T5 cape
blueprint 15 000, mounts 120 000 / 1 400 000, …). `.faction` shows your enlistment, standing, rank and points; GMs: `.faction add
<points>`, `.pvp faction <faction>` (no cooldown). Not done: outposts and fortresses, the PvP capture part, cluster control and
payouts, the weekly upkeep and reports, campaign rewards, the faction's PvE fame bonus and group bonus, the nameplate rank (483).

## PvE preset

The root key `"Preset"` of `config/common.jsonc` picks the defaults of every key left out. `"official"` (the shipped value, and the
default when the key is missing) gives the official game's rules and rates; every server addition is off there and has a key of
its own. `"Preset": "pve"` (or `--Preset=pve`) gives the PvE server of `../analysis/work/stageH/PVE-DESIGN.md` §1: the keys below take
these values. Any key written in the file or on the command line wins over its preset's value. The commented examples in
`config/world.jsonc` show the official values, with the pve value in the note.

| Key | pve | official |
|---|---|---|
| `Pvp:Mode` | `Consensual`: only two Red-flagged players (outside one party) and duel opponents may fight; a black zone counts as a red one | `Official`: by the zone (the client's table below); `Off` = duels only |
| `Pvp:LethalDeath` | `false`: zero health from a player in an orange, red or black zone is a knockdown as elsewhere; the player gets up by itself; no execute, no die button | `true` |
| `Pvp:FullLoot` | `false`: a death (only with `LethalDeath` true) keeps every item, only the wear applies | `true` |
| `Pvp:KnockdownWear` | `0`: a PvP knockdown wears nothing (a share 0..1 of the maximum durability) | `"gamedata"` (5 %) |
| `Pvp:RedLockMinutes` | `0`: the Red flag is the consent to PvP and can be taken off again at once (FLAG_BLUE's 15 s cast stays) | `"spell"` (15 min of CANNOTDISABLEPVP) |
| `Pve:KnockdownWear` | `0` (0.01-0.03 adds a little risk to a mob's knockdown) | `0` |
| `Loot:BlackMarketChance` | `0.15` (a few pieces of gear per dungeon chest; was 0.35 × Rates:Loot 1.5, far too much) | `0.1` |
| `Chests:PoiLimit` | `0`: a POI chest on every spot (see Open-world chests and camps) | `-1`: the data's (one per cluster) |
| `Bm:BuyFactor` | `5`: the Black Market in Caerleon buys equipment at 5 × its item value (see Economy) | `0`: no NPC buyer |
| `Bots:Enabled` | `true`: player bots in the towns and the open world (see Player bots) | `false`: no bots |
| `Bots:Pvp:CrimesAgainstBots` | `false`: hitting a bot is never a crime | `true`: a Red player's hit on a non-Red bot is a crime |
| `Bots:Gear:Mastery`, `Bots:Gear:DamageBonus`, `Bots:Combat:HealthFactor` | since the botpower change the same as official: `1.0`, `true`, `1.0` (bots are as strong as an equal-tier full-spec player; `Bots:Difficulty` scales them; see Player bots, Bot strength) | `1.0`, `true`, `1.0` |
| `Companions:GearMastery`, `Companions:GearDamageBonus` | `0.5`, `false`: hired companions keep the old soft bot strength | `1.0`, `true` |
| `Bots:Rewards:DeathGearDropChance` | `0.5`: a bot that dies (red and black zones) drops each worn combat item with this chance | `1.0` (full loot) |
| `Bots:Rewards:MinItems` | `1`: a beaten bot's bag holds at least one of its worn items | `0` |
| `Companions:Enabled` | `true`: players hire companion bots into their party (see Companions) | `false` |
| `Guilds:CreateCost` | `100000` silver (see Guilds) | `1000000` |
| `Guilds:JoinRole` | `Member`: an accepted invitation makes a member | `Recruit` |
| `Guilds:RejoinCooldownMinutes` | `0`: no wait between two guilds | `4320` (3 days) |
| `Guilds:NoFriendlyFire` | `true`: guild mates never fight each other (see Guilds) | `false` |
| `Guilds:XpShare`, `Guilds:ClearXp` | `0.1`, `2000`: guild experience from the members' PvE fame and clears | `0`, `0` |
| `Guilds:ShareWithoutParty` | `true`: guild mates near a kill share it like a party | `false` |
| `Guilds:SilverTax` | `0.05`: of each silver pile a member takes, into the guild account | `0` |
| `Guilds:Contracts` | `true`: weekly guild contracts | `false` |
| `Guild:Weekly` (content/goals.json) | `true`: weekly guild goals with the guild blessing (see Guild goals) | `false` |
| `Guild:ChallengeFactor`, `SeasonPointsFactor` (content/goals.json) | `20`, `5` | `1`, `1` |
| `Guild:MightFactor` (content/goals.json) | `10`: ten times the client data's guild might | `1` |
| `Islands:StarterIsland` | `true`: a free personal island at the first island merchant visited (see Personal islands) | `false`: islands are bought |
| `Islands:StarterBuildings` | `true`: a new island (and one without buildings, on its map load) comes with a farm and a herb garden | `false`: only a GM builds them |
| `Islands:GrowthSpeed` | `4`: crops grow in 5.5 h instead of 22 h | `1` |
| `Islands:WaterFocusFactor` | `0`: watering a crop takes no focus | `1`: 1000 focus (the seed's cost) |
| `Premium:Mode` | `Free`: every character is premium, always, at no cost (see Premium, focus and learning points) | `None`: nobody (no premium shop) |
| `Focus:Start`, `LearningPoints:Start` | `30000`, `500`: a new character's focus and learning points (the premium lifetime reward) | `0`, `0` |
| `Mail:RewardOverflow` | `true`: reward items that do not fit the inventory come as a mail with them attached (see Mail) | `false`: they wait for a later login or `.bounty claim` |
| `Corrupted:ExitPortal` | `Always`: a corrupted dungeon's exit portal stands from the start (see Corrupted dungeons) | `AfterDemon` |
| `Corrupted:InfamyFactor`, `EarlyLeaveShare`, `RewardFactor` | `2`, `0.5`, `1.5`: more infamy, half of it for a run left early, more completion silver and fame | `1`, `0`, `1` |
| `Corrupted:Invasions`, `PlayerInvasions`, `BotInvaders` | `Consent`, `Consent`, `true`: only after your Demonic Shrine; a bot invader, or a player who used his shrine too | `Official`, `Official`, `false`: the data's rolls, real players |
| `Corrupted:InfamyLossOnDefeat`, `MaxInvasionsPerRun`, `AntiReset`, `ChainBonus` | `0`, `1`, `false`, `0.1`: nothing lost, one invasion a run, no anti-reset imps, +10 % per chained dungeon | `0.1`, `3`, `true`, `0` |
| `Hellgates:ShrineMode`, `VersusModes`, `EnemyTeamSize` | `Fixed`, the two lethal modes, `Party`: the shrine opens in your mode, only the lethal modes bring an enemy team of bots, as many as you are (see Hellgates) | `Gui`, all four, `Mode`: the client's mode window, every mode a team of its size |
| `Hellgates:CheckItemPower`, `BossMapFactor`, `GroupScaling`, `KnockdownSeconds`, `LeaveCommand`, `FinishedLingerSeconds` | `false`, `3`, `true`, `30`, `true`, `300`: no server item power gate, boss maps 3 x as likely, demons scaled for a small party, 30 s knockdowns, `.hg leave` | `true`, `1`, `false`, the zone's (600), `false`, `120` |
| `Hellgates:Matchmaking`, `RestrictedArea`, `SpawnBuffs` | `Bots`, `false`, `false`: the enemy team is always bots, no ring of fire, no hellgate spawn debuffs | `Players`, `true`, `true`: a second party of the mode may join within 60 s, the ring of fire, the spawn buffs |
| `Roads:Command` | `All`: every player may use `.roads` and `.roads route` (see Roads of Avalon) | `Gm` |
| `Roads:LogoutSeconds` | `"world"`: the logout in a road takes `World:LogoutSeconds` | `900` |
| `Roads:ThrowOutAfterMinutes` | `0`: never thrown out of a road for being away | `60` |
| `Mists:Matchmaking`, `LifetimeFactor`, `BlockFactor` | `Private`, `2`, `0.5`: a mists zone of your own (your party in a duo mode) that lasts 40 min, its treasures twice as soon (see The Mists) | `Shared`, `1`, `1`: zones shared up to the map's player limit, 20 min |
| `Mists:StandingFactor` | `2`: twice the Brecilien standing per mists fame (see The Mists) | `1` |
| `Mists:DuoSoloScaling`, `OpenWorldExit`, `BossChest`, `RarityWeights` | `0.6`, `Entrance`, `true`, 6000/1600/1200/800/400: a duo zone alone has weaker mobs, the open-world exit leads back to the portal, a mist boss leaves its damagers a reward chest, enchanted portals are common | `1`, `Random`, `false`, the data's (95.9 % mundane) |
| `Rates` | Fame 2, Silver 1.5, Loot 1, Respawn 2, Harvest 1.5, Regrowth 2, ChampionWeight 2 (the others 1) | all 1 |
| `Progression:NewCharacters` | `UnlockAll`: every destiny board node unlocked (see Fame and the destiny board) | `FairStart`: an empty board that fame fills |
| `Loot:BlackMarketSoftCap`, `Loot:TierCap` | `4`, `true`: big chests give tens of Black Market items, not hundreds; a common loot entry at most the content's tier + 1 | `0`, `false`: the loot lists as rolled |
| `Events:PlayerInvasions` | `true`: `.invasion`, the invasion tickets, their sell orders in Thetford and their drops (see Invasions on demand) | `false`: none, whatever content/events.json says |
| `Bounties:Enabled`, `PvpGoals:Enabled` | `true`, `true`: content/goals.json's bounties and PvP challenge points (see Goals) | `false`, `false`: no bounties; the official PvP rules (no points) |
| `Season:Rules` | `Content`: content/goals.json Season as written (PvE kills x3, dungeons x3.5, boss bonus, premium rewards granted, daily extra) | `Official`: the client data's Adventurer's Challenge |
| `Dungeons:DailyBonus`, `Dungeons:GroupScaling` | `"content"`, `true`: content/dungeons.json's dungeon of the day (x1.5) and group scaling | `1`, `false`: neither |
| `Expeditions:Rules` | `Content`: content/expeditions.json's solo group runs, auto-accept, hardcore carry, scaling, Royal Sigil bag every run, no leave penalty | `Official`: the client's rules |
| `WorldBosses:ExtraSpawns`, `WorldBosses:Loot` | `true`, `"content"`: content/bosses.json's own bosses in 0204 and 1207, its shared loot | `false`, `Killer`: the data's bosses only, the loot to the killer and its party |
| `Guilds:BossSeasonPoints`, `Guilds:SeasonRewards` | `true`, `Content`: guild season points for boss kills, content/goals.json's bracket silver and items, `.guild payout` | `false`, `Official`: none (`.guild payout` only with `Guilds:SilverTax` or `Guilds:Contracts`) |
| `Corrupted:MapsOpenDungeons`, `Corrupted:LeaveCommand` | `false`, `true`: the corrupted maps are invasion tickets; `.cd leave` | `true`, `false`: the maps open a corrupted portal, as the client data says; out only by the exit portal or the escape spell |
| `Roads:Rescue` | `true`: a road with a player inside and no link gets one at once | `false` |
| `Arena:*` | bots fill in, 10 s countdown, 6 / 15 tickets of 1 per death, 15 min, 5000 / 1500 silver and 30000 / 10000 fame, a duel ladder (see Arena) | no bots (a match only with both teams of players), 15 s, 150 tickets of 2 per death, 25 min, no silver or fame, no duel rating |
| `Islands:BuyWithoutPremium` | `true` | `true` for now: everyone may buy and upgrade islands until premium can be had (the strict official rule is `false`) |
| `Tutorial:Opening` | `true` (without the key) | `true` (without the key) |

`.who` and `.save` are GameMaster commands in both presets (the official game has neither; `.who` shows every player's position).

Under `Consensual` nobody can attack a Blue or faction-flagged player, so there are no crimes, reputation losses or outlaws.
The client still judges hostility by its own table: in a black zone it shows other players as enemies and lets you press
attack, and the server refuses the attack as it does one on a dead mob. The client's warning on entering a red or black zone
("you may lose everything") cannot be removed; on this server it means nothing. `.pvp` shows the mode in force. Player
spells and areas do not hit players at all yet (see below), so no area effect reaches a player who is not Hostile.

The PvP button on your character frame works in every zone, a city included (the client has no zone check for it): standing
still, unmounted and not casting, a click starts the 15 s cast and the flag turns Red (or Blue again) at its end. Under
`Consensual` the Red flag is kept on entering a city or a blue zone; there nobody is Hostile, so it only matters once you
reach a yellow, red or black zone, where two Red players may fight. If the server refuses the change, the cast bar stops
at once and the chat gets a line with the reason ("PvP flag not changed to Red: ..."). If the client refuses (moving,
mounted, busy), the client's own message shows and nothing reaches the server. A character that was Red under `official` still has FLAG_RED's saved
CANNOTDISABLEPVP buff after switching to pve: the server ignores it, but the client may show it and refuse FLAG_BLUE on its
own side until its 15 minutes run out.

## PvP and duels

The rules below are the official ones (`"Preset": "official"`); the PvE preset above narrows them.

Who may attack whom follows the client's own rule (`../analysis/work/stageE/pvp/SPEC.md` §1): the cluster's worldsettings
archetype (city: nobody; green: enemy factions; yellow/red: Red-flagged players against everyone, factions against each
other; black: everyone outside one's party) with each player's flag, and a duel opponent always. An auto-attack on a player
who is not Hostile is refused as for a dead mob; a hit on one does the normal damage × the target's armor and its
bonusdefensevsplayers (× 1 / (1 + Σ), as against mobs). A cluster whose data is still loading allows no PvP and no duels.

Flags (SPEC §3, `../analysis/work/stageJ/flag-toggle/NOTES.md`): the PvP toggle starts the 15 s FLAG_RED cast in any
cluster (the client checks no zone, and Red players are seen in royal cities; under `official` not in a blue cluster, which
would take it away again), FLAG_BLUE takes 15 s back (not within the
15 minutes of CANNOTDISABLEPVP after flagging Red), a faction flag is instant for a player enlisted to that faction
(enlist at a faction trader, see Faction warfare below, or a GM's `.pvp faction`), also mounted. There is no combat check; damage taken or a knockdown cancels
the cast. A refused change ends the client's own cast (CastCancel) and says why in the chat. The flag is saved; a Red player
keeps it in a city but is Blue again on joining a blue (green) cluster, and a faction-flagged one cannot enter a cluster that
forbids it. The log shows each change at Info (`pvp`: refused with the reason, the cast start, the flag set) and the
request itself at Debug.
Reputation: a mob kill and a harvest give the level's share of their fame; a Red player who hits a player who is not Red and
not an outlaw loses that player's level penalty (once per victim in 120 s, more for a knockdown) and is an outlaw (OUTLAW)
for 3 minutes. Blue players may hit Red ones back for free; enemy factions fight each other outside cities. Zero health from
a player outside a duel is a knockdown (yellow/orange 90 s, else 180 s, then the zone's get-up buff): every item loses 5 % of
its durability, the attacker gets "no fame for this kill". A player's hit on a mounted player dismounts it. Not yet: enemy
spells and area effects on players (below), the silver drop of a knockdown and the outlaw protection buffs.

Death in lethal zones (SPEC §4; nothing of it was ever captured, so much of it is inferred): in orange, red and black
clusters zero health from a player kills at once (a mob still only knocks down). A knocked-down player there can be executed
(the attack on it casts PLAYER_COUPDEGRACE, 1.5 s; damage, its own cancel or another action ends it) or use the die button
after 3 s; enemy factions may also execute each other in yellow and green zones, which kills without any loss. The dead
player drops everything (orange: only the inventory, the gear stays with 5 % wear) into a loot bag where it fell: each item
loses 20-40 % of its durability (15-35 % after an execute, more with several attackers) and a third of each stack is
destroyed (a single item with that chance). For 120 s (90 s in black) only the killer and its party may loot it, then anyone;
it vanishes after 30 minutes. The killer gets the kill message and fame (the summed item value of the victim's gear, shared
with its party by damage, × the fame rates), a Red killer of a non-Red player the kill penalty. 3 s later the dead character
disappears for the others (its loot bag stays); the victim's respawn screen (or 10 minutes) takes it to the respawn city
(`Pvp:RespawnCluster`, else its last city, else Thetford) with full bars; a player who logs out dead joins there, one who
logs out or disconnects knocked down in a lethal zone dies first. Knocked-down and dead players cannot change cluster; arenas
and cluster types without an archetype kill without any loss. With `Pvp:DeathSilverDrop` it also drops 75 % of its silver for
the killer (off by default: the amount is a guess).

Duels (SPEC §2): challenge from the player's context menu (within 25 m, any wager the challenger has), the other one has 13 s
to accept. Both wagers are put in at once; the flag goes up between the two, and the duel starts 10 s later and lasts at most
5 minutes. Only the opponent can be hit. The duel ends when one is knocked down (the winner takes both wagers; the loser
stands up 6 s later with full health and the duel get-up buff), stays more than 25 m from the flag for 10 s (out of it
during the 10 s preparation: at once), leaves or logs out, or the time runs out (a draw: each gets its wager back). Nothing
else is lost in a duel. `.duel end` stops one as a draw, and so does stopping the server; a crash during a duel loses
both wagers (they are held only in memory).

### Arena, ladders and tournaments

`../analysis/work/stageN/arena/SPEC.md`. The arena agency in a city takes registrations: its Register button queues for the
**1v1** (the client's 1v1 arena, map ARENA-1v1-01), the **5v5** (unranked, map ARENA-STANDARD) or the **Crystal Arena** (ranked,
below; the 5v5 with `Arena:CrystalArena` false); tick "as a group" to queue with your party. `.arena join 1v1|2v2|5v5 [group]` does the same from chat, and is the only way to the **2v2** (the
client offers none). The queue HUD shows the wait; when a match is found the client's ready popup asks you to join within 60 s
(or `.arena accept`); `.arena leave` leaves the queue or forfeits a match, `.arena status` lists queues and matches. Teams are
filled by count, then by rating. With too few players a match starts after `Arena:BotWaitSeconds` (20) with player-bots: by
default each team gets bots up to the other team's player count (one player against one bot; `Arena:BotFill Full` fills every
slot). In the arena both teams start at their camps; nobody can be hurt during the 10 s countdown. A player at zero health is
only knocked down — no item loss, no durability loss, no execute — and costs the team a ticket: the 1v1 is best of three rounds
(both reset at their camps after each), the team modes respawn the fallen at their camp after 10 s until a team has no tickets
left (2v2: 6, 5v5: 15) or the time runs out (1v1 10 min, else 15). Score and result come as chat lines (the client's match HUD
is not driven). The result pays 5000 silver and 30000 fame for a win, 1500 and 10000 for a loss (× the rates), plus the
official unranked items (a win lootbag, the first 3 wins a day a royal sigil bag; tomes for a loss). 15 s later everyone goes
back where they registered.

Ratings: an Elo ladder (start 1200, K 20) per mode (`1v1`, `2v2`, `5v5`, `crystal`) and for duels, saved (schema v32). Arena matches
with bots are not rated (`Arena:RateWithBots`); a duel counts when won by knockdown, the area rule or the opponent leaving.
`.ladder [duel|1v1|2v2|5v5|crystal] [count]` lists the best, `.rating [name]` shows a player's ratings and places.

Tournament: a GM opens a sign-up with `.tournament open`, players `.tournament join` (`.tournament` shows the state), the GM
`.tournament start`s it: single elimination seeded by duel rating (an odd count gives the best a bye). Each pair fights a normal
duel; the first duel won between them decides it (a GM can `.tournament win <name>`); the rounds follow until one is left.
`.tournament cancel` ends it; nothing of it is saved. GMs also have `.arena end blue|red|draw`.

Config (`Arena:*`): `Enabled`, `Bots`, `BotWaitSeconds`, `BotFill` (Balanced/Full), `BotTier` (0 = from the players' item
power), `BotDamageFactor`, `BotHealthFactor`, `ReadyCheck`, `AcceptSeconds`, `ArrivalSeconds`, `CountdownSeconds`,
`RespawnSeconds`, `Tickets2v2`, `Tickets5v5`, `TicketsPerDeath`, `MatchMinutes`, `EndSeconds`, `Rated`, `RateWithBots`, `StartRating`, `K`,
`DuelsRated`, `WinSilver`, `LossSilver`, `WinFame`, `LossFame`, `RewardItems`, `DailyWins`, `RewardsWithBots`, `FallbackCluster`,
`ItemPowerCap`, `DoubleHealerPenalty`, `EnterBuffs`, `WarcampDefense`, `Psp`, `Orbs`, `OrbRoundLookup`, `MatchHud`, `CrystalArena`,
`RankPoints`, `RankPointMinGain`, `RankPointBoosters`.
The figures above are the pve preset's. Under `Preset official` the arena follows matchtypes MATCH_ARENA: a 15 s countdown, 150
tickets with 2 lost per death and 10 per runestone the other team claims (below), 25 minutes, no silver
or fame (season points instead, below; the unranked items stay), no duel ladder, and no bots (`Arena:Bots` false). A
match never starts with empty slots when bots are off or unavailable (`Bots:Enabled` false): a lone registration waits for players,
and a match whose other team could not join goes back to the queue.

**Official arena rules** (`../analysis/work/stageP/league-pvp/SPEC.md` §2.5, §3.3; on under `Preset official`, all off under pve,
which keeps the arena described above). `Arena:ItemPowerCap`: in a match every worn item counts at most 600 item power in the
unranked modes (2v2, 5v5), and in the 1v1 900 plus a fifth of the power above (1000 → 920); the bars, armour, damage and spell
power follow, and lift again on leaving. `Arena:DoubleHealerPenalty`: a team with two or more healers (a holy or nature staff in the
main hand) all carry DOUBLEHEALER_PENALTY (healing received −35 %, renewed while it lasts). `Arena:EnterBuffs`: on arrival the
map's ARENA_* buffs are cast: ARENA_ENTER (full health and energy, cooldowns reset), ARENA_OVERLOAD_PROTECTION, and in the 1v1
ARENA_1V1_ENTER (healing −60 %). `Arena:WarcampDefense`: each camp shoots the other team within 18 m for 10 % of its maximum
health every 0.4 s during the fight (a knockdown there costs a ticket), and its own team standing within 8.7 m of it is
invulnerable (ARENA_WARCAMP_INVULNERABILITY: attacking breaks it until you step out and back in). The turret has no visual of
its own (NewWarCampObject is not decoded): the hits show as plain health loss. `Arena:Psp`: the result pays the client's
personal season points while an Adventurer's Challenge season runs: unranked win 1200, loss or draw 400 (premium 1500 / 600),
1v1 400 / 175 (premium 500 / 250), the Crystal Arena 1200 / 500 (1500 / 750): the first rank's figures, with `Arena:RankPoints`
those of the player's rank (below).

**Runestones** (`Arena:Orbs`, official on, pve off; §3.2). The 2v2/5v5 map has three runestones (ORB_ARENA). They wake 60 s
into the fight; a team that stands alone with at least one player within 9 m of one for 10 s claims it, and the other team loses
10 tickets (chat line). Taking the other team's runestone needs 4 s more first (it turns neutral, then 10 s to claim); with both
teams in the circle the capture pauses; a capture left alone for 20 s is lost. The 1v1 map has none. The client is sent the
runestones (NewOrbObject) and their claims and resets (OrbClaimed, OrbReset); the crystal maps' channel runestones (a 7.5 s
channel, broken by a hit or by moving, −1 ticket per claim, and every 120 s the side holding more takes 20/30/40) are in place
for the Crystal Arena (below). `Arena:OrbRoundLookup` (false) adds MATCH_ARENA's 60 s orb-difference
score, which the tutorial does not mention. `Arena:MatchHud` (false in both presets until captures confirm the fields) sends the
match HUD's OrbUpdate, MatchUpdate, StartArenaMatchInfos and MatchNewCombatRound / MatchEndCombatRound; off, nothing partly
decoded goes out and the score stays in chat.

**Crystal Arena** (`Arena:CrystalArena`, official on, pve off; §3.4). The agency's ranked category (Crystal Arena) is its own 5v5
queue (`.arena join crystal`, ladder `crystal`) on the Crystal Realm (CrystalRealm03) with the client's MATCH_CRYSTAL_GVG_NONLETHAL:
150 tickets, 6 lost per death, respawn after 18 s, 24 minutes. Its three runestones are claimed by a 7.5 s channel within 8 m
(broken by a hit or by moving; −1 ticket for the other team); every 120 s the side holding more runestones takes 20, 30 or 40
tickets (a difference of 1, 2, 3) and its runestones turn neutral. Item power is soft-capped at 900 (× 0.2 above), the camps
shield their team (WARCAMP_INVULNERABILITY) and shoot the other one (non-lethal), and the result pays arena.bin's ranked rewards
(win bag and season points of the player's rank, tomes for a loss, the daily royal sigil shared with the other queues). Bots fill
empty slots only under the usual arena bot rules (`Arena:Bots`). Not yet: the realm's exits and winner/loser chests. Off, the
ranked category queues for the 5v5 as before.

**Arena ranks** (`Arena:RankPoints`, official on, pve off; §3.3). The two ranked queues, the Crystal Arena and the 1v1, each keep
rank points (RP) and one of the season's 17 ranks (arena.bin RANKED_BRACKETS_ARENA_S26: Iron 1-4, Bronze 1-4, Silver 1-4, Gold 1-4,
Crystal). A rank is reached at 150, 300, … 2400 RP and lost below 0, 100, … 2350 RP. Each new rank gives its rank-up chest once
a season (the Crystal Arena's T4_LOOTBAG_ARENA_RANK_UP_*, the 1v1's T4_LOOTBAG_1V1_ARENA_RANK_UP_*; a player offline at the
result gets it with the next one), and the win and loss rewards and season points follow the player's rank (the 1v1 then pays
its own 1v1 bags instead of the unranked ones). The arena window shows the rank and RP (GetArenaRankings). Only rated results
count (`Arena:Rated`, not with bots unless `Arena:RateWithBots`). The client data has no rank point formula, so this is a
placeholder [H] until a capture shows real RP changes: a win gives winrankpointmodifier (4 at Iron 1 … 1 at Gold 4) × the Elo
gain, plus elowinbooster (10, Crystal 5) while the Elo is above the rank's expectedelo; a loss takes loserankpointmodifier (1) ×
the Elo lost, plus elolosebooster (1) while the Elo is below expectedelo; the 1v1 counts half (rankpointfactor 0.5); a draw
nothing; RP never go below 0. `Arena:RankPointMinGain` (1) is the least Elo change counted, `Arena:RankPointBoosters` (true)
the boosters. Ranks are saved (schema v42); the season's rank-down at a new season is not applied yet.

### Spells on other players

Phases 0 to 4 of `../analysis/work/stageJ/pvp-spells/SPEC.md`. Every spell and effect node now reads its target class the
way the client does (`Systems/SpellTargets`: enemy, friendall, friendother, enemyplayers, enemymobs, all, allplayers, …). For a
player's spell, **Hostile** means a player or bot it may hit with its sword right now (the same rule as auto-attacks: the
zone's flags under `Pvp:Mode`, so under the pve preset only Red against Red, the duel opponent once the duel has started, and a
bot by the zone's own flags, so a Blue player may hit a Red patrol; neither knocked down, not invulnerable). "Friend" means the caster or a **Friend**: another player (not a bot) on the same map
who is not knocked down, is Hostile neither under `Pvp:Mode` nor under the client's own table, and is not in a duel. A player
who is neither a Friend nor Hostile gets nothing from another player's spell.

- Enemy spells reach Hostile players and bots (phase 2). A cast at a player (CastStart key 5) needs it in view, Hostile and
  within castrange + 1 m; at a player who is not Hostile it is refused like an invalid target (code 960: the client aborts the
  cast and its cooldown does not start). The victim and everyone who sees it get what they get against a mob: CastStart with the
  victim in key 7, CastHit {0 victim, 1 caster} right before the HealthUpdate {6 caster}, the debuffs, DoTs and crowd control
  in the victim's ActiveSpellEffectsUpdate with the caster in key 10, ForcedMovement {9 caster} for a knockback, pull or fear.
  Damage is × the caster's bonusdamagevsplayers × the victim's armour or magic resistance and bonusdefensevsplayers; a mounted
  victim's mount bar takes it; a killing hit knocks down or kills as a sword's does (with the PvP knockdown's wear, the
  crime's penalties and the PvP goals). An effect without damage of its own (a stun, a slow, a knockback) counts as an attack
  too: both are in combat, an outsider's effect on a duelist ends the duel, a Red player's on a Blue one is a crime under the
  official preset. Crowd control on players uses their worn crowd control resistance and the diminishing returns. A stun ends
  the victim's cast, channel, flag change and execute; an `interruptscasting` hit and `interruptcast` do the same (not through
  `notinterruptible`). The victim's own reactions (ReceiveDamage triggers, a thorns reflect) answer the attacking player; a
  `reflectdamage="never"` hit is not reflected. A patrol or gatherer bot hit or stunned by a spell answers as after a sword hit;
  a stunned bot stands without turning, chasing or swinging, and a knocked-back bot follows the path and stands 300 ms at its
  end (the time a player's client takes to answer the path) before its brain and walk go on from there (a pause at a corner
  included).
- Areas, auras, chains and radius nodes (phase 3) hit Hostile players and bots by the same rule, and never a Friend, a party
  mate, a protected Blue player or anyone knocked down: SWORD_SPIN's damage pulses, ground areas (GLACIALFIELD), skill shots
  that stop at their first target (SEARING_FLAME flies past a party mate and a Blue player to the Red one behind them), the
  enemy chains. The victim and everyone who sees it get the area's CastHits before its HealthUpdate. An area with
  `removeonleave` takes its effect off a player who walks out (the list goes out at once). `interruptcast` (INTERRUPT, FLARE)
  ends a Hostile player's cast and channel (ChannelingEnded {1 2}), except one whose spell has `interruptiblebyspell="false"`
  (MOUNTSPELL_BASTION_SHIELD_CHANNEL, the BEAR_ spells), as on a mob; a damage hit with `interruptscasting` and a stun still
  end those [inference]. When a channel or toggle ends, its linked effects come off every player and bot it reached
  (DISEMBOWEL's root). A player's energy drain on a Hostile player is ready (the victim's EnergyUpdate names the caster in
  key 5), but no spell of the client drains another unit's energy (every player's negative energy node is on the caster,
  HYPER_FOCUS and the like), so it is not seen in play. The mobs' relative drains on players work: the Undead harvester's
  ENERGY_DRAIN channel, the shade's spectral touch and the Avalon drone take 10 % of the maximum energy, with the same
  guards as a mob's hit (not through invincibility or nothittable) and the player in combat. The heretic shadowmask's flat
  drains (−5 / −25 / −125, with health punishments under IfTargetEnergyPercentage) stay skipped and logged, as before:
  heretic dungeons are unchanged. The damage a reflect
  sends back from one hit is at most 2 × the reflector's maximum health (gamedata `maxReflectCapacityFactorOfMaxHp`; per
  hit [inference]), against mobs and players alike.

- Friendly spells reach other players. Heals, shields and buffs work on a Friend as a single target (HOLYTOUCH, HEALINGBEAM
  and other `friendall` / `friendother` casts on another player) and through radius nodes, areas, pulses, auras and the
  CELESTIAL_SPHERE chain. A heal is × (1 + the healer's healbonus from buffs and worn items: T4 plate armour 0.1) × the
  receiver's healmodifier. The receiver and everyone who sees it get the HealthUpdate with the healer in key 6. A heal at a
  Hostile player, at a stranger the client calls hostile (a Red player under the consensual mode) or at a duelist is refused
  like an invalid target (code 960). `friendother` spells cannot be cast on oneself.
- PvE fixes from the same rules: a node only hits units of its class, so HELL_ARROW_DAMAGE hits a mob once (its
  `enemymobs` value) and `enemyplayers` slows stay off mobs. An applied spell's own class filters by kind:
  ARCANEORB2_SILENCE_PLAYERS never lands on a mob. Friendly radius buffs and auras no longer buff mobs. A ground area
  carries its effect to everyone inside, not only to its owner. `IfTargetType` is read for the effect's unit
  (mob, player, self, enemy, friendall, groupmember, guildmember). A caster's worn `bonusccdurationvs*` lengthens its crowd
  control (T4 plate armour +40 %). `castablewhenstunned` spells (HOLYTOUCH, the cleanses, ICEBLOCK2) can be cast while
  stunned. A damage node with `interruptscasting` cancels a mob's cast. A mob spell's `reflectdamage="never"` is not
  reflected. A cast on oneself names the caster in CastStart key 7 and in CastFinished / CastSpell key 1, as the official
  server does. CastStart key 2 (the aim) goes out for a ground cast only, not for a cast at a unit or on oneself.
- What a spell's own parts reach: a `pulsingspell` pulses on its unit only while that unit matches the node's class, so
  ARMOR_WEB's enemy pulse never purges a friend's speed buffs, and a friendly pulse (MAGICCIRCLE, NATURE_ULTIMATE_SINGLE)
  runs on its caster too. A unit in an area that no node of the area's effect acts on (a friend on your CALTROPS,
  GROUNDMINE or ARMOR_WEB) is not a hit: it does not use up `maxeffectareatargets` and gets no CastHits. An aura whose
  spell has class `all` (DASHDMG_EFFECT, QS_SLOWROPE) scans the enemies only, as before, so its owner is not its own target.

Config: `Pvp:FriendlySpells` (`All` in both presets; `Group` = party and guild mates only; `Self` = the caster alone),
`Pvp:DuelIsolation` (true in both presets: duelists give and take no friendly spells but their own) and `Pvp:Spells` (true in
both presets: enemy spells reach Hostile players and bots; false = mobs only, a cast at a player is refused, auto-attacks as
before). Phase 3 has no keys of its own.

- Bots cast their own spells (phase 4, `Bots:Combat:Spells` `Full` in both presets). A bot's gear has a spell in every active
  slot: the one candidate where a slot has one, else a random usable one (a player's new item leaves those to him), a fighting one
  (damage, crowd control, a dash) first and never one that cannot be cast in combat (OUTOFCOMBATHEAL). A fighting
  patrol or a gatherer that fights back casts the active spells of its gear (Q, W and E of its weapon or tool, armour, helmet,
  shoes) through the same checks as a player's CastStart (the slot, stun and silence, the target's class and castrange, cooldown,
  energy), with a chance of `Bots:Combat:SpellChance` (0.6; 0.35 before stage M) per second of game time in its fight (the extra
  decisions that hits on it bring add none). By intent: a heal or shield on itself below 70 % health first, then its attacks, a
  buff only when no attack is ready and the buff is not on it already. An enemy spell or one for any unit (FIRESTAFFIGNITE2_SPREAD) at its target, a ground spell at the
  target's position only with the target within castrange (no area on empty ground, no stop in the chase), a self spell (a buff,
  a spin around itself) with the target within 5 m. A spell with `usableincombat="false"` (OUTOFCOMBATHEAL, VANITY_RUBBERBANDING)
  is refused in combat for players as well (the client's own rule). It stops
  facing the target first, dismounts, and swings nothing while its cast or channel runs. The human sees the bot's CastStart, its
  projectile, its hits {6 the bot}, its debuffs and its own dash as from a player. A bot's effects follow its target choice: they
  never reach a mob (bots never fight mobs: a mob in its area is left alone, its TAUNT pulls none) nor a human as a Friend (a Blue
  player's heal area skips bots), nor a duelist; a patrol's reach its target, the players it answers and the ones it would attack
  on sight (not a player it spared after a knockdown or at the leash, nor one within `JoinGraceSeconds`, nor a rider without
  `AttackMounted`); a gatherer's only the players it answers (those that hit it or its party in the last 20 s), never another Red
  player nearby. Gatherers carry tools without spells, so in practice the patrols cast. `Off`: bots only auto-attack, as before.

Not yet (SPEC phase 5): resurrection. Focus fire (resilience) and healing sickness: see "Death recap and PvP details" below.

### Death recap and PvP details

`../analysis/work/stageP/league-pvp/SPEC.md` §2 (phases P1-P3). All on in both presets (the official rules; the pve preset keeps
them as they are).

**Death recap** (`DeathRecap:*`, §2.1). The server records what happens to a player: every hit by a mob or a player (bots count as
players), its heals, and every spell another unit puts on it that is crowd control or a debuff (and any spell of a mob), plus its
own spells on itself. At a knockdown or death the last `DeathRecap:Seconds` (gamedata `<DeathRecap timeframe="180"/>`) are frozen as
its recap; the knockdown screen's request (op 492) and the character menu's later one get it, in the layout of the two official
responses: entry 0 the victim, then one entry per causer (most damage first) with its buckets: damage or heals of one spell (or
auto-attack, -1) merged with their count and the first hit's time, each crowd control or debuff its own bucket with its end time,
the kind 0 spell, 1 melee and 2 ranged auto-attack. Mob rows are as captured (mobs.xml avatar and ring, flag 0x80); player rows
carry the name, HUMAN_MALE/FEMALE, AVATAR_nn, RING1, the flag, the main hand's item and quality, the guild and the average item
power. The recap is kept per character for the server's run (a relog still shows it, a restart forgets it). Not recorded: the
mount bar's hits and damage the server deals in the player's own name (the hellgate ring). `DeathRecap:Enabled` false: the empty
recap of before.

**Healing sickness** (`Pvp:HealingSickness`, §2.2). A player or bot healed by two or more distinct other players or bots within 10 s
(gamedata `<PlayerHealingSickness lookbackseconds="10">`) gets `HEALINGSICKNESS_n` for n healers (8 s; 2 and 3: -35.3 % healing
received, up to -78.2 % at 20), before the heal that made it n is worked out; a higher one is not replaced by a lower one. Heal spells,
heal-over-time ticks and heal areas of others count; one's own heals, potions, food and regeneration do not [infer: the tooltip only
says "healed by too many sources"]. It cuts every heal received, × (1 − value) on top of the other healing modifiers. The client
shows it as a normal debuff (ActiveSpellEffectsUpdate). Mobs never.

**Resilience** (`Pvp:Resilience`, §2.3, focus fire protection). Every damage hit of a player or bot on another notes the attacker;
with two or more distinct attackers within 10 s (gamedata `<PlayerFocusFire lookbackseconds="10" effectdurationinsec="5">`) the
target's resilience is raised to that count for 5 s, renewed by each such hit. While it lasts every player hit on it does
× (1 − m × (1 − penetration)): m the row's meleemodifier or rangedmodifier by the attacker's weapon (2 attackers 14.3 %, 3 25.9 %, …
26 and more 75 %), the mountedmodifier (up to 80 %) on a mounted target, penetration the attacker's
`focusfireprotectionpenetration` (the Resilience Penetration trait, its items) [infer: how penetration combines]. Mob hits neither
count nor are reduced. The client has no spell for it and no reference to its status text was found, so it shows no icon here
[infer; capture R1 will show how the official server displays it].

**Zone-entry protection** (`Pvp:SpawnBuffs`, `Pvp:EnterSpellCooldowns`, §2.4). On the Join that ends a cluster change (and a
respawn's arrival) the cluster type's worldsettings `<spawnbuff>` list is applied as officially: its `SPAWN_*` buffs in document
order whose conditions hold (isclusterchange, isrevive, ispvppossible = the zone allows any PvP, fromshortspawnbuffcluster = the
cluster one came from has `hasshortspawnbuffonleave`, i.e. a dungeon, expedition or tunnel; hideouts, GvG and territories do not
exist here). In blue, yellow, red and black zones that is SPAWN_INVULNERABILITY (30 s invincible, invisible to mobs, immune to
damage, debuffs and crowd control; ends on attacking, casting, interacting or after 150 m; SPAWN_INVULNERABILITY_SHORT, 50 m,
coming out of a dungeon), SPAWN_SPEED (+20 % for 15 s) and SPAWN_BUFFIMMUNITY (no second shield within 60 s and 150 m); dungeon
and expedition entrances give SPAWN_INVULNERABILITY_EXPEDITION; a respawn's SPAWN_BUFFIMMUNITY_ONDEATH blocks the shield for 10 s. A
spot-bound effect of the last cluster ends first, so each zone change gives a new shield [infer: capture R6]. Hellgates keep giving
their own lists (`Hellgates:SpawnBuffs`); the lists' other names (ARENA_*, MISTS_*, CORRUPTED_DUNGEON_ENTER, hideout and shrine
buffs) are their systems'. `spellcooldownsonenteringseconds` (15 s in the yellow, red and black zones, their dungeons and tunnels)
puts every gear spell slot (not potion and food) on that cooldown on arrival [infer: which slots; capture R6]. Not on a login.

## Fame and the destiny board

Fame (`../analysis/work/progression/PROGRESSION.md`): a mob kill gives its fame × the zone's famefactor, a harvest of a T2+
resource items.xml famevalue × the amount (T2 fiber 4.5, T4 15); T1 resources give none. Both raise the total
(UpdateFame, saved). A character with a destiny board of its own also fills its nodes: every node in progress that the
fame feeds gets all of it — the main chain (ADVENTURER_*, START) every fame, a kill the combat nodes of the worn weapon,
off hand, head, armour and shoes (base node and spec node), a harvest the gathering node of the resource, a craft the
crafting or refining node of the crafted item (CRAFT_SWORDS, CRAFT_REFINE_WOOD_T4, …) — within the
tier limits of each node's level. A node that reaches its level's fame goes up (the client shows the level-up and unlocks
the level's items itself: levels 1/10/30/60/100 of a weapon or armour line unlock its T4/T5/T6/T7/T8 items), and finishing
a node opens its neighbours on the board. Levels and the fame inside the current level are saved per character. While
premium the nodes get 1.5 × the fame of kills and harvests (the total stays × 1; tomes and player kills × 1), and nodes marked
for auto-learn spend learning points (see Premium, focus and learning points). Fishing gives fame too (see Fishing; tomes feed
the board only).

`Progression:NewCharacters` in `config/*.jsonc` decides the board of a **new** character: `UnlockAll` (the
default, as before) = no board of its own, everything unlocked, fame only raises the total; `FairStart` = an empty board
(START in progress) and the kit of `content/starter-fair.json` (T1 sword, shield and leather set, T1 tools): 45 fame
finish START (T2 bag and cape), 250 kill fame then COMBAT_TRAINEE (T2 weapons and armour), and so on. Existing
characters keep what they have; `.progress mode fair|unlockall` switches one (it starts from an empty board), `.fame add`
and `.progress set` speed things up. Running a server of this version converts `albionlocal.db` to schema v6 (the fame
inside a node's level); an older server build refuses the converted file.

## Economy

Silver, NPC shops, repair, crafting, the bank and the marketplace (stage 6, `../analysis/work/stage6/SPEC.md` with its
verifier's corrections). Running a server of this version converts `albionlocal.db` to schema v7 (silver and gold per
character, the bank vaults, the market orders); everything stays as it was, characters start with 0 silver (an older
server build refuses the converted file).

- **Silver** is per character (Join, the client's purse; gold too). New characters get `Silver` of `content/starter.json`
  (0: silver comes from killed mobs and the market). `.money` shows it, `.money 5000` sets it, `.money +5000` / `.money -200` /
  `.money add 5000` changes it (an optional second number is the gold). Everything that costs silver (repairs, shops,
  crafting, fast travel, market orders) checks it first; there is no selling to NPCs, except full labourer journals to the
  journal merchant (below) and equipment to the Black Market (below).
- **Repair** at a repair shop (and the marketplace): the client's own price (item value × missing durability × 16.5 in
  whole silver per item), taken when the repair ends 0.8 s later; the items come back at full durability.
- **Salvage** at a repair shop or the meld building (the "Salvage" tab, equipment and the items marked salvageable): the
  item is destroyed 0.8 s later for part of its recipe's resources (count × durability × 0.2, rounded at random) and, only
  for items with an itemvalue or salvagevalueoverride in items.xml, silver (that value × durability × 0.75); weapons and
  armour have neither, so they give resources only, as the client's preview shows. Every NPC building now sends its access
  rights (AccessStatus; on a Join all of them after JoinFinished, later right after the building's NewBuilding), without
  which the client refused with "you don't have permission to salvage here".
- **Enchanting** at the meld building in the cities (the "Enchant" tab): an item goes one level up (.0 → .1 → .2 → .3) for the
  runes, souls or relics its items.xml upgraderequirements ask (a T4 one-hand sword: 288 T4_RUNE, then 288 T4_SOUL, then 288
  T4_RELIC; × the stack for potions and food), taken 0.8 s later; the item keeps its quality, spells and durability share
  (it becomes a new object in the same slot, worn items too). **Quality reroll** at a repair shop: equipment below its
  highest quality is rerolled for item value × 4.4 / 5.5 / 6.6 / 27.5 silver (quality 1-4), by gamedata RerollQualityChances
  (never lower; normal always improves). **Crafted quality** now rises with the destiny board's quality bonuses for the item
  (itemcraftquality) and with focus (+50): every chance above normal × (1 + bonus / 100), an assumption (the official formula
  is server-side; analysis/work/stageN/enchant/SPEC.md).
- **Awakened weapons** at the meld building (its awakening tabs; analysis/work/stageN/awakening/SPEC.md): a weapon of the
  legendaryitems.bin groups (the swords, axes, staffs, bows, ... of every line) at tier 4+ and enchantment .4 is awakened for
  6 000 000 silver and 100 UNIQUE_GVGTOKEN_GENERIC + 100 QUESTITEM_TOKEN_AVALON, each × the tier scaling (T4 0.021: 124 534
  silver and 2 + 2 tokens; T8 the full amounts), 1.5 s later. It gets a soul attuned to you with 3 random traits of the trait
  pool (rolled 1-3 %) and shows as awakened (gold frame, traits in its tooltip); soul and traits are saved with the item
  (database v34, also in the bank, mail, market and trades). Kills give the weapon in hand attunement (fame × 0.037, up to
  100 000 × the tier scaling). Spending it: **reroll a trait's value** (6667 attunement × tier scaling, and × 33 as silver;
  a fresh roll that replaces the value when higher, else +2-6 %, 33.75 % of the time a critical +10-30 %; never lower, up to
  100 %), **replace a trait** (10 000 × tier scaling attunement and × 33 silver for 3 options, then pick one or keep the old),
  **reattune** a weapon attuned to someone else (1 000 000 × tier scaling silver) and **disenchant** (the soul goes, free).
  Missing attunement can be paid with focus (× 0.0975). The traits work only for the attuned wielder: max health, max energy,
  defense, ability and auto-attack damage, healing dealt and received, attack speed, cast speed, cooldown reduction, CC
  resistance and duration, mob fame, lifesteal and item power on the weapon apply; energy cost reduction, threat, attack
  range and resilience penetration are not used by the server yet. Strain stays 1 and the soul's legendary rating, event
  history and the Black Market's awakened items are not made.
- **Shops:** buying at an NPC shop is a craft at it (the client's "buy"): FARMING_MERCHANT sells seeds, baby animals and
  the like for silver (furniture cannot be created yet). Shops that take tokens or other currencies refuse for now, unless
  the price is items in the inventory. Thetford has no farming merchant; `content/spawns/0000.json` can add one.
- **Crafting and refining** at a crafting station: `content/spawns/0000.json` adds ownerless public stations to Thetford
  (smelter, forge, carpenter, tannery, weaving mill, stonemason at the official station plots; hunter's lodge, mage's
  tower, toolmaker, alchemist, cook and butcher on free plots near the centre). The same twelve stand in Lymhurst
  (`1000.json`), Bridgewatch (`2000.json`), Martlock (`3004.json`), Fort Sterling (`4000.json`), Caerleon (`3003.json`)
  and Brecilien (`5000.json`), on real-estate plots near the bank; each city refines and crafts with its own bonus
  (Martlock hide, Lymhurst fiber, Bridgewatch rock, Fort Sterling wood +40 %, plus its weapon and armour lines). The recipe's silver and
  resources are taken at the start, one craft takes 0.4 s, moving or casting cancels the rest (its silver and resources
  come back). Part of the resources come back by the city's bonus (Thetford ore 36.7 %; `Crafting:Returns` false turns it
  off). Crafted equipment gets a random quality (normal 68.9 % … masterpiece 0.1 %). Each craft gives crafting fame
  (item value × count × 1.5^(tier + enchantment − 2): a T3 plank 12, a T4 sword 864) to the total and the destiny board;
  shop purchases give none. With the craft window's "use focus" on, a craft spends crafting focus for more resources back
  (see Premium, focus and learning points).
- **Stables:** every city above (Thetford and Brecilien too) also has a public stable (`T8_STABLE`) on a free plot near
  the bank. It makes the mounts of its list: a T3-T8 riding horse or ox from one grown animal of the same tier (raised on
  the player's island, see Islands) and 20 leather of that tier; the armoured horses and the rare mounts need their own
  items. The grown animal is never returned; the leather comes back by the city's crafting bonus.
- **Bank:** the vault of a city bank holds 128 slots (weight limit 100 000) per character and city, the default tab only
  (more tabs cannot be bought yet); drag items in and out as in the inventory. With `Bank:Shared` (off in both presets, so
  hauling goods between cities stays part of the game; on only by this key) every bank opens the same vault: the city banks, and the island and outland banks too. Nothing is lost on switching:
  when a bank opens, the items of the character's vaults of the other kind (its city vaults of before, or the shared
  vault after switching it off; that one only at a city's bank) move into the free slots of the vault it opens, in city
  order; what does not fit stays where it was and moves at a later opening.
- **Marketplace:** sell orders and buy orders of the players, per city; buying from a sell order pays the seller at once
  (an offline seller at its next login), the bought items wait in the "Finished" tab to be collected; selling into a buy
  order pays from the order's escrow. Fees as the client shows them (gamedata.xml): a new order pays a 2.5 % setup fee (at
  least 1 silver, not given back), the seller of a sale gets the total less 8 % sales tax (4 % while premium, as the window shows).
  Orders last the duration the client sends (30 days); aborting gives the items (to collect) or the escrow back. The lists
  filter by category, tier, enchantment, quality and the search text, sort by price, time or item, and page by 50.
  The inventory's quick sell lists, per selected item, the best buy order it can fill (highest price, quality reached).
  A filled, expired or cancelled order mails its owner the client's market notice (see Mail).
  **Price history:** every sale is recorded per city (item, quality, amount, price, time; kept 29 days, table `market_sales`)
  and the item window's chart (24 hours / 7 days / 4 weeks) shows that city's sales by hour, quarter day or day, with the
  average price (`../analysis/work/stageN/econ/SPEC.md` §1).
- **Black Market** (Caerleon, the BLACKMARKET building at (-65, -92); `../analysis/work/stageH/blackmarket/SPEC.md`): the
  client opens its marketplace window in Black Market mode, where a player can only sell. The server gives it standing NPC
  buy orders (PVE-DESIGN step 12): every tradable T4-T8 weapon and armour piece at its item value × `Bm:BuyFactor` (whole
  silver; a T4 broadsword 384 × 5 = 1920), times the quality (1 normal … 5 masterpiece: an excellent broadsword 5760;
  an item fills the orders up to its quality, the window's sale takes the dearest); the sale pays at once less the 8 % tax the window
  shows, and the item is gone. Each character may sell for `Bm:DailyLimit` silver a day (1 000 000, from 10:00 UTC; 0 = no
  limit): an order shows how many can still be sold and disappears when the rest of the day's limit does not pay for one.
  Players place no orders of their own there (refused; a sell order also gets a system chat line saying why). `Bm:BuyFactor` 0 (the official preset) leaves the list empty.
  Running a server of this version converts `albionlocal.db` to schema v16 (one column: the day's Black Market sales; an
  older server build refuses the converted file).
- Splitting, stacking, sorting and destroying inventory items work (also in an open vault).

Not checked in the client yet: the station and market windows themselves (`../analysis/work/stage6/SPEC.md`, client checks
C-A1…C-D4); the exact keys of the market's "create order" requests were read from the client's code, not from a capture.
The Black Market window is not captured either (its SPEC §4: C-BM1, C-BM2).

## Premium, focus and learning points

`../analysis/work/stageJ/focus-lp/SPEC.md`. **Premium** has no shop on this server: `Premium:Mode` `Free` (the pve preset) makes
every character premium, always, `None` (official) nobody. The client shows it (the character list's premium label, the premium
state after the Join) and works out the halved market tax itself. While premium, the factors of the client's characters.xml apply:
the destiny board gets 1.5 × the fame of kills and harvests (`Premium:FameFactor`), a silver pile pays 1.5 × (`Premium:SilverFactor`),
a harvest gives half its amount on top (`Premium:GatherBonusChance` 0.5), an island crop twice its harvest (`Premium:CropYieldFactor`
2; not its seed), the market's sales tax is halved (4 %), the season's premium rewards can be claimed (and its learning-points
rewards, for characters without premium, cannot), focus regenerates and learning points come in. These stack with the Rates
(pve: fame to the nodes 2 × 1.5 = 3, silver 1.5 × 1.5). Not modelled: premium mob loot, faster animal growth (no animals yet).
The `Premium:*` keys take a number or `data` (the client data's value, the default).

- **Focus** (the bar in the character window): at most 30000 (`Focus:Max`), regenerating 10000 a day while premium
  (`Focus:RegenPerDay`; offline too), none without. A new character starts with `Focus:Start` (pve 30000). Focus potions add
  to it. With the craft window's "use focus" toggle on (saved per character), a craft or refine at a station spends the
  recipe's focus × the crafts, halved for each 100 points of the destiny board's focus reduction for that item (the node's
  own item 2.5 a level, its category 0.3: a T4 item at level 100 costs 1/5.66), and adds 0.59 to the return bonus (Thetford ore:
  36.7 % → 53.9 % back). With less focus than the cost, all that is left goes in for the same share of the bonus. A cancelled
  batch gives back the focus of the crafts not done (all of it, even above the cap). `Focus:Crafting false` never spends any, and
  neither does `Crafting:Returns false` (focus only buys return bonus). Recipes that give nothing back
  (no returnable resource) cost no focus, as the client shows. Watering island crops takes `Islands:WaterFocusFactor` × 1000.
- **Learning points** (the destiny board's header): one every 2880 s while premium (30 a day, `LearningPoints:SecondsPerPoint`;
  offline too, paid at the next login and every minute online), at most 999999 (`LearningPoints:Max`); a new character starts with
  `LearningPoints:Start` (pve 500). Learning point books (awardlearningpoints items) and the season's learning-points rewards add
  to them. They are spent only by auto-learn: nodes marked with the board's auto-learn toggle (saved), while its "use learning
  points" switch is on, take 5 × the fame they get and pay its price in points (the level's LP price), as the client's preview
  shows; a node's neighbours inherit the mark when it opens them (with points to spend or not).

Running a server of this version converts `albionlocal.db` to schema v23 (the focus stamp, the learning points, the use flags and the
auto-learn nodes; an older server build refuses the converted file). Characters from before v23 keep their focus and start with 0
learning points (`Focus:Start` and `LearningPoints:Start` are for new characters; `.focus` and `.lp` set them). The season's
unlocked, unclaimed rewards are judged again with the character's premium of now, so an older save's premium rewards can be claimed
under `Free`. Commands: `.premium`, `.focus`, `.lp` (GameMaster).

Not checked in the client yet (SPEC §8): the premium label and state (C1), the focus bar rising by itself and the craft preview's
focus cost against the server's (C2-C4), the learning point header, auto-learn and its notifications (C5), the season's premium
rewards (C6), the official preset without premium UI (C7).

## Labourer journals

Labourer journals (`../analysis/work/stageH/journals/SPEC.md` §A, PVE-DESIGN step 4) fill in the inventory, as the
client shows them: an empty journal (`T4_JOURNAL_MERCENARY_EMPTY`, `T4_JOURNAL_WOOD_EMPTY`, ...) takes the fame of its kind,
and once full it turns into the full journal (`..._FULL`) with the overhead text "journal full". Running a server of
this version converts `albionlocal.db` to schema v13 (one column: the fame of a partly filled journal; an older server
build refuses the converted file).

- **What fills which journal:** mercenary journals (and the mercenary trophy journals) from mob kills of at least their
  tier (not from player kills); lumberjack, stonecutter, prospector, cropper and gamekeeper journals from harvests of the
  resources they list (their tier up to two above); blacksmith, fletcher, imbuer and tinker journals from crafts of the
  items they list (the destiny board's share of the craft fame); fisherman journals from catches; the general trophy
  journals from any fame. A T4 mercenary journal takes 3264 kill fame; resource and crafter journals fill faster than
  1:1 by items.xml (a T4 lumberjack's journal: 1200 gathering fame fill its 1800). The fame counts as the client shows it
  (zone bonus, `Rates` included). One journal per kind takes each gain: a partly filled one first, else a single empty one, else one is taken off a
  stack of empty ones (that needs a free slot: "no inventory slot" otherwise); fame over the top is lost. Only the
  inventory counts, not the bank or the equipment. `"Fill": false` in `content/journals.json` turns it off.
- **Journal merchant:** besides the labourers on the islands (see Personal islands), the marketplace of each city in `MerchantMarkets` (default
  Thetford, `0000`) has standing NPC orders: a sell order of every empty journal (T2-T8) at its labourer price × `SellFactor`
  (1: T2 500, T3 1000, T4 2000 ... T8 32000 silver) and a buy order of every full journal at that price × `BuyBackFactor`
  (1.5; 0 = none). They never run out, the bought journals wait in the market's "Finished" tab as any purchase, selling into a
  buy order pays at once less the 8 % sales tax (quick sell finds it too). Category "Other → Labourers" lists them. Full
  journals can also be sold to players on the market; a bought partly filled journal needs a free slot to be collected.
- `.item add T4_JOURNAL_MERCENARY_EMPTY 5` gives journals for testing (`_FULL` for full ones; the base `T4_JOURNAL_...`
  item is never used in play and is refused).

Not checked in the client yet: the journal names and fill bar (C-A1, C-A2), the overhead text (C-A3), the merchant's
orders in the market window.

## Goals: Adventurer's Challenge, Albion Journal, bounties

Daily and long-term PvE goals (`../analysis/work/stageH/journals/SPEC.md` §B, §C, §E, PVE-DESIGN step 11), in
`content/goals.json`. Running a server of this version converts `albionlocal.db` to schema v15 (one column with the
character's goals; an older server build refuses the converted file). Under `Preset official` the file's server additions are
off: `Bounties:Enabled` false (no bounties), `PvpGoals:Enabled` false (the built-in official PvP rules, no points), `Season:Rules
Official` (the client data's Adventurer's Challenge; only `Season.Enabled` is read). The pve preset lets the file decide.

- **Adventurer's Challenge** (the client's season window and the points popup): every fame gain gives season points =
  factor × √fame (whole fame as the popup shows it). The shipped file uses PvE factors: kills `Pve` 3 (official 1.5),
  kills inside random dungeons and expeditions `Dungeon` 3.5, harvests `Gathering` 3, catches `Fishing` 3; crafting, player
  kill fame and tomes give none, as officially (PvP gives points of its own, see "PvP goals" below). A boss, miniboss or world boss kill adds `BossBonus` (25) for the killer and its
  party near it; `PointsFactor` multiplies everything. The daily goal is 1200 points: its rewards (a T4 tome, 50 000 points for
  the main and secondary bars, the learning-points book for characters without premium) are claimed in the window. An unfinished
  daily bar keeps its points over midnight UTC; a finished one starts again (the client's own rule). The main (600 000:
  a mount and an avatar ring) and secondary (150 000 … 600 000 by the season's week: loot chests) rewards are "premium" only:
  a premium character (everyone under `Premium:Mode` Free) claims them itself, and as the client never lets a character without
  premium claim them, with `GrantPremiumRewards` (shipped true) the server
  gives them into the inventory when they unlock, with a chat line (items the server cannot make yet are named and left
  out; one that does not fit the inventory waits for `.bounty claim`). `DailyExtra` items come with every finished daily
  (the same: what does not fit waits). `.season add <points>` (GM) gives points.
- **Albion Journal:** achievements progress from kills (the bestiary of CREATURES and the PVE kills: mob lists, tiers,
  open world vs. dungeons, zone colours, mob names, random dungeons), fame (killing, gathering, fishing, any), harvested
  items (tier, enchantment), finished expeditions (category, hardcore level, time limit) and season points. Pinned
  achievements show their progress in the tracker; a finished one pops up and its reward is claimed in the journal
  window (the badge counts what can be claimed). PvP and duel achievements progress from the PvP goals below; achievements of
  other kinds (islands, market …) stay at 0 (the GUILD challenge and season ones progress, see Guild goals);
  so do, inside PVE, the loot-chest ones (unlocklootchest), the static / home-base kills, hunts and a few special
  conditions: about half of the PVE category and nearly all of CREATURES can progress. The achievements that mirror Steam
  achievements (SA_*) stay at 0 unless `Journal:SteamMirrors` is true (shipped false: whether the client passes their
  completion on to Steam is not traced, and a local server must not unlock real Steam achievements).
  `Journal:Categories` limits it to some categories (`[]` = all). Category rewards (avatars, vanity unlocks) cannot be given
  yet ("invalid reward").
- **Bounties:** 3 daily goals (from `Bounties:ResetUtc`, 10:00 UTC) and 1 weekly (Mondays) are drawn from `Bounties:Pool`,
  the same for every player: kill N mobs (tier, zone, champions …), a world boss, random dungeon bosses, an expedition,
  N resources or fish, N corrupted dungeons (kind `corrupted`, `MinTier` the rank's tier: Hunter 5, Stalker 6, Slayer 8).
  Progress comes as chat lines at every quarter, the completion with its reward (silver, fame, items
  straight into the inventory). `.bounty` lists them with your progress and your season points; `.bounty claim` gives
  the bounty and Adventurer's Challenge reward items that did not fit; `.bounty reroll` (GM) draws other daily ones until
  the next reset or a server restart (then the day's own pick is back). A day gives at most `Daily` completed bounties and
  a week `Weekly`, rerolls included. A world boss counts for everyone who fought it (its looters), not only the killer's
  party. The client has no window for these.
- **PvP goals** (`../analysis/work/stageI/pvp-goals/SPEC.md`, `Pvp` in `content/goals.json`): every PvP kill, non-lethal PvP
  knockdown (the pve preset has no PvP deaths: a knockdown is the kill there) and duel won by knocking the opponent down is a
  credit for the winner, and an assist for each other player with `AssistMinShare` (15 %) of the damage on the victim. A credit
  gives Adventurer's Challenge points: `KillPoints` 20 + `KillPointsPerTier` 5 × the victim's tier (its best combat gear: weapons,
  armour, cape, not the mount or food; a T6 victim 50), a duel `DuelWinPoints` 15, × the zone factor by its warning colour (`ZoneFactors`: red 1, black 1.5, others 0
  = none), × `BotFactor` 0.5 for a player-bot, × `AssistFactor` 0.5 for an assist, at most `DailyPointsCap` 600 a bounty day
  (half the daily challenge: PvE stays the main way), with a chat line. It counts for the bounties of kind `pvpkill` (victim
  tier, `Zone`, `Bots` any / none / only, `Assists`) and `duel` (`Bounties:PvpDaily` / `PvpWeekly`, shipped 1 each and 0 built in,
  are extra slots for them on top of the PvE ones, so a solo player keeps all PvE bounties; without `Pvp:Enabled` none is drawn;
  the pool ships three), and for the journal's PvP achievements: player kills (`KnockdownIsKill` counts a knockdown as one), knockdowns,
  kill fame, killing sprees (also the PvE sprees of CHARACTER_PROGRESSION), a death to a player, duels finished and won, with the
  zone filters, enemy factions and the attacker count of large battles. Anti-farming, no credit at all (journal included): a
  crime unless `CrimesCount`, the same party, the same account (alts; not the shared local account of legacy logins
  while no login has claimed it), a victim
  below `MinVictimTier` T4, the same victim again within `SameVictimCooldownMinutes` 30 or more than `SameVictimPerDay` 3 times
  a day, a duel shorter than `DuelMinSeconds` 20 or the same pair more than `DuelSamePairPerDay` 2 times a day. A knockdown and
  the execute that follows it are one result: the execute adds only what the knockdown did not give (killplayer without
  `KnockdownIsKill`), and a third player's execute of a credited knockdown gives nothing. `.bounty` shows
  today's PvP points and the refused credits. `KnockdownFameFactor` (0) gives that share of the kill fame on a credited
  knockdown (UpdateFame only, no kill message; only to sharers whose own credit is not refused). Without the section the official rules apply (no points, a journal kill only
  for a death). Under the pve preset PvP is between two Red players (and duels) only. The Conqueror's Challenge window (op 451)
  stays the stub.

Not checked in the client yet: the season window's bars and claim buttons for a character without premium (C-C1), the
points popup (C-C2), the journal window's finished and claimable entries (C-B1) and the tracker's progress (C-B2).

## Side windows

What the client asks for besides the game itself (`../analysis/work/stageH/ops-audit/SPEC.md`): every request the ops audit
lists now gets a handler, an answer from `content/opcode-stubs.json` or, for its telemetry, a quiet `"ignore"`, so
`.ops unhandled` and the log's "unhandled" warnings mostly show what is new (a few others, e.g. GetSurveyLogs at the
login, still warn).

- **World map:** opening the map (M) answers the clusters in view with their markers in the official empty form (no
  cluster value, treasures or dungeon states yet; outposts as world.xml lists them). Clusters with guild territories
  (the black zone) are left unanswered, since there is no official sample of their markers.
- **Albion Journal tracker:** a character that tracks nothing is shown four journal achievements after its Join (one of
  tutorial, gathering and PvE in turn; on the first Join after a server start the list can come a moment later, once
  the journal file is read); pinning and unpinning in the journal window changes the list, which is kept with
  the character (schema v12: one column; an older server build refuses the converted file). The entries show
  their progress (see "Goals" above).
- **Season window:** the Adventurer's Challenge shows its points (see "Goals" above); the past seasons tab is empty (op 373 still the stub answer, no event 377); the faction warfare campaign shows the current month.
- **Island showroom:** the merchant's "Showroom" travels there (see Personal islands).

## Rates

The section `Rates` of `config/world.jsonc` multiplies the rewards and timers; under `"Preset": "official"` every rate
is 1 (the official numbers) unless set, the PvE preset starts from Fame 2, Silver 1.5, Loot 1, Respawn 2, Harvest 1.5 and
Regrowth 2 (see PvE preset); fractions are allowed (`"Rates": { "Harvest": 2, "CombatFame": 3 }`, or `--Rates:Harvest=2` on the
command line). Each applies where its quantity is worked out, so what the client shows (fame popups, harvest amounts,
silver piles and the purse) already carries it.

| Rate | Multiplies |
|---|---|
| `Fame` | every fame gain: kills, harvests, crafts, tomes; the three below multiply with it |
| `CombatFame` | the fame of a mob kill (UpdateFame and its zone-bonus part) |
| `GatheringFame` | the fame of a harvest |
| `CraftingFame` | the fame of a craft (the destiny board's share with it) |
| `Harvest` | the resources a harvest yields, resource nodes and corpses (the base amount; 1.5 × 3 gives 4 or 5, the fraction at random as the gathering bonus); the tool wear and the fame keep the unscaled amount |
| `Silver` | a killed mob's silver pile and silver-giving items (awardsilver) |
| `Loot` | the item drop chances of mob drops and loot chests (all kinds): their total loot factor, once (not `Loot:BlackMarketChance`); not the loot of opened items (bags) |
| `Respawn` | the mob respawn **speed**: 2 = back in half the time, 100 = within seconds (0.01-1000) |
| `Regrowth` | the resource regrowth **speed**: 2 = a node's charge back in half the time (0.01-1000) |
| `CraftingReturn` | the share of the resources a craft gives back (`Crafting:Returns`; at most all of them) |
| `ChampionWeight` | the weight of the champion, miniboss and boss mobs in a RANDOM spawn point's pick (3 = three times as likely as the data says) |
| `ChestRespawn` | the open-world chest respawn and reroll **speed** (0.01-1000) |

The client's own previews (the craft window's fame and return rate) show the official numbers; what is received (the
resources, the fame popups, the purse) carries the rates. A harvest is refused when its scaled yield does not fit one
stack (the item's stack limit) or rounds to nothing.

`World:MobRespawnScale` of earlier versions is gone: it was a factor on the respawn time, `Rates:Respawn` is its inverse
(MobRespawnScale 0.01 = Respawn 100). An old config with it still starts and reads it that way; giving both is refused.
While the server runs, `.rates` shows the active values. An Admin can change the whole running world
with `.rates <name> <value>`; `.rates reset` restores effective startup values. These changes do not
write files and are discarded on restart. The `save` argument is rejected. Old `rates.json` files
are preserved but ignored with a warning. Edit `Rates` in world.jsonc for permanent changes.
Already scheduled timers and awarded rewards are not recalculated. See [configuration](CONFIGURATION.md).

## Accounts

New databases start without characters. With AutoRegister disabled, create an account through client registration or the server console. Legacy claiming requires existing characters.

Logging in needs a login name and a password (the two fields of the login screen; analysis/work/accounts). The server
keeps only a PBKDF2-SHA256 hash of each password (100 000 iterations, random salt) in `albionlocal.db`; names count
ignoring case and spaces at either end. Steam and console logins are refused.

- **Register in the client:** on the login screen press "Create account", type an email address (the client insists on
  one) and a password of 8 to 72 plain ASCII characters, not the email, without a space at either end, twice; accept the
  consent popups. The account exists at once (there is no email to confirm, whatever the client's message says); the
  client fills the login form with it, press Login. A name that has an account already, a bad password or a bad email
  are shown by the client as such. `Accounts:Registration` false turns the dialog's accounts off.
- **Levels:** each account has its own level, `Player`, `GameMaster` or `Admin`, which decides the chat GM commands it
  may run (see GM commands). The account of the characters of before stage 7 (the local account) is `Admin`; every account
  made from now on — registered in the client, by `Accounts:AutoRegister` or by `.account create` — gets
  `Accounts:DefaultLevel` (default `Player`). `.account level <name> Player|GameMaster|Admin` on the console changes it;
  `.account list` shows it. Accounts made by an earlier version keep the level they were stored with (that was
  `Accounts:LocalAccountLevel`, `Admin` by default): lower them with `.account level` on a server others can reach. At
  every start the log warns with the names of the accounts besides the local one that are `Admin`.
  `Accounts:LocalAccountLevel` now only sets the local account's level when a new database is made.
- **On the server console:** `.account create <name> <password>` makes an account with any login name (the same password
  rules); see the GM commands below for the others.
- **Accounts of before:** the account that holds the characters saved before stage 7 (taken by `Accounts:LegacyName`, or
  when that is empty by the first login name from this PC, never from another address) and the stage-7 accounts of each login name have no password yet: their next successful login
  sets the password typed (logged as `… had no password: the one typed at this login is its password now`, never the
  password itself). So log in with your usual name once and pick the password then.
- **Unknown names** are refused like a wrong password ("incorrect credentials"), unless `Accounts:AutoRegister` is true:
  then a login with a new name and a password makes the account (handy on a LAN of friends).
- **Wrong passwords:** `Accounts:MaxFailedLogins` (5) in a row lock the account for `Accounts:LockMinutes` (15); the
  client shows how long. `.account unban <name>` or `.account password <name> <new>` ends a lock early.
- **Bans:** `.account ban <name> <minutes> [reason]` (0 minutes = for good) closes the account's connections; its next
  login (with the right password) shows the ban's end and reason. `Accounts:Maintenance` true refuses every login with the
  client's maintenance message.
- One account plays on one client at a time: a second login of the account closes the first one's connections (its
  character is saved as on any disconnect). The game server takes a Join only for an account whose login connection is
  logged in and not banned.
- More than 20 logins or registrations a minute from one address are refused with the client's generic error (each
  password check takes the server a moment).
- The password travels inside the login request; the client encrypts that request, but on any network beyond your own PCs
  use passwords you use nowhere else. The log never shows passwords (Login keys 5, 7, 9, 13 and CreateAccount keys 7,
  9, 11 read `<redacted len N>` at every log level).

Running a server of this version converts `albionlocal.db` to schema v5; an older server build refuses the converted file.

## Web panel

A small built-in web panel (`../analysis/work/stageN/webpanel/SPEC.md`): plain HTML pages with no scripts, dark and
light, usable on a phone. On by default under `Preset: pve`, off under `Preset: official`; it listens on
**http://127.0.0.1:8080/** (this PC only) and the log says so at the start (`[web] web panel on http://127.0.0.1:8080/`).

- **Public pages:** `/` the status (players online, uptime, server and client build, rules, whether registration is open);
  `/register` makes an account with a login name and a password, by the same rules and at the same level
  (`Accounts:DefaultLevel`) as `.account create` (name: 3 to 254 printable characters without spaces, an email works;
  password: 8 to 72 printable ASCII characters, no space at either end, not the name). At most 5 registrations per 10
  minutes per address; closed when `Web:Registration` or `Accounts:Registration` is false.
- **Admin pages** (`/login`, then the links at the top): online players with **Kick**; accounts with **Ban** (minutes,
  0 = for good, and a reason), **Unban / unlock**, **Set level**, **Set password** and **Delete** (on a page of its own,
  typing the name to confirm); the characters of an account (name, silver, fame, cluster, read-only); the **Log** (the
  tail of `Log:File` with a level filter and a 5 s auto-refresh); **Backups** with **Back up now**.
- **The first admin account:** only accounts of level `Admin` may log in to the panel. On the server console type
  `.account create <name> <password>` and then `.account level <name> Admin` (or raise an account that exists:
  `.account level <name> Admin`). Then open http://127.0.0.1:8080/login. Your own account cannot be banned, deleted or
  lowered from the panel (the console can).
- **Safety:** a login gives an HttpOnly, SameSite=Strict cookie that ends after `Web:SessionMinutes` (30) without a
  request, or at once when the account loses Admin or is banned; every form that changes something carries the session's
  CSRF token; at most 10 admin logins per 5 minutes per address. Every change runs through the console's own account
  commands on the world thread and writes an audit line (`[web] audit: admin 'Name' from 127.0.0.1: ban account …`);
  passwords and hashes are never logged or shown. It is plain HTTP: keep `Web:BindAddress` at 127.0.0.1, or on a LAN
  use passwords you use nowhere else.
- **Backups:** `Back up now` saves the unsaved characters, then copies `albionlocal.db` with SQLite's `VACUUM INTO` (a
  consistent snapshot while the server runs) to `data/backups/albionlocal-yyyyMMdd-HHmmss.db`. `Backup:EveryHours` (0 =
  off) makes one every N hours while the server runs, with or without the panel; only the newest `Backup:Keep` (10) stay.
  To restore, stop the server and copy a backup over `data/albionlocal.db`.

```jsonc
"Web": {
  "Enabled": true,           // the panel (Preset pve: true, official: false)
  "BindAddress": "127.0.0.1", // "0.0.0.0" = every interface (other PCs; plain HTTP)
  "Port": 8080,
  "Registration": true,      // the public registration page (Accounts:Registration false closes it too)
  "SessionMinutes": 30       // an admin session ends after this long without a request
},
"Backup": { "EveryHours": 0, "Keep": 10 } // automatic backups into data/backups (0 = only the panel's button)
```

## Several players

Several clients can play on one server at the same time (stage 7, `../analysis/work/stage7/SPEC.md`). Each account
(see Accounts above) has its own characters, saved in `albionlocal.db`; character names are unique on the whole server.
After the upgrade from the single-account server the characters saved so far belong to the first login name that logs
in from this PC (the log says so: `account … takes over the local account with its N character(s) …`), or to the name in
`Accounts:LegacyName` when that is set; every new account starts empty and creates characters as before.

Players on the same cluster see each other within the same window as buildings (4 cells of 10 m around their cell, gone
from 6): the character with its look, gear and bars, its walking (every Move is passed on to the others, never back to
the mover), equipment and spell changes, and its Say lines (shown over the speaker and in the chat of everyone who sees
it; the speaker gets its own line back the same way; GM command lines stay private). Leaving view, the cluster, the game
or the connection removes the character from the others' screens. `.who` (GameMaster) lists everyone in game with cluster and
position. Parties and guilds have sections of their own (Guilds below); the chat channels (Local, world, languages, help,
guild), whispers, the friends list and muting are in Chat below.

To try it on one PC, start two clients and log in with two different accounts. One account is for one client at a
time: its second login closes the first one's connections. An account only ever
plays its own characters, with their own items, fame and progression: a game connection whose account has no character
(or that names neither an account nor a character) is refused (`join refused: …`) instead of playing someone else's.
A game connection that names a character id instead of its account (a character's Guid can be seen in world events)
joins nothing either, unless `Accounts:JoinByCharacterId` is true (the self-test's setting; default false). Select,
create and delete at the character selection act only on the logged-in account's characters.
A **second PC** on the LAN:

1. On the host, let the server listen on the network and tell the clients the host's address, in
   `config/*.jsonc` (or on the command line):
   `"Network": { "BindAddress": "0.0.0.0", "PublicAddress": "192.168.1.10" }` with the host's LAN address
   (`PublicAddress` is required with `0.0.0.0`). Use `0.0.0.0` when the host plays too: a client on the host that
   connects to `127.0.0.1:5055` is not answered by a listener bound to the LAN address alone. The game server address handed out
   in SelectCharacter and GetGameServerByCluster is `PublicAddress:GamePort` for every client, the host's own included,
   so a loopback `BindAddress` with a LAN `PublicAddress` reaches nobody (the server warns at start).
2. Open UDP 5055 (login), 5056 (game) and 5057 (chat) in the host's firewall for the LAN, e.g. in an admin PowerShell
   `New-NetFirewallRule -DisplayName AlbionLocal -Direction Inbound -Protocol UDP -LocalPort 5055,5056,5057 -Action Allow -Profile Private`
   (without 5057 the game works, but the chat channels and whispers stay empty for that PC).
3. The second PC's client must reach the host's login server; how a client is pointed at a server is outside this
   repository. The game server address comes from the server itself.

### Quality of life: emotes, loadouts, /stuck, map pings

StageN qol (`../analysis/work/stageN/qol/SPEC.md`).

- **Emotes**: the emote wheel and `/dance`-style commands work: the emote shows on the player and on everyone who sees it,
  with its chat line. All 16 emotes of the current client are free (the locked ones are commented out of emotes.xml); an
  unknown one gets the client's "not unlocked" message. Dance, sit and sleep loop until the player walks, attacks, casts,
  mounts or is knocked down, and a player who comes into view later sees them too.
- **Loadouts** (the Loadouts tab of the bank and market windows): save, rename, recolour, reorder and delete builds of
  the equipment and inventory; they are saved with the character (`albionlocal.db` schema v29). *Equip* takes each
  named item from the inventory, else from the bank tab open in this city, and puts back what it replaces where that
  came from; what is missing stays missing (nothing is ever created), and the inventory part is fetched from the open
  bank tab while the bag has room. Guild loadouts are not made.
- **/stuck**: after the client's confirmation the character waits (gamedata: 180 s, attackable, cannot move) and is then
  moved to the arrival point of the nearest zone entrance of its map (a walkable cell near it), or to the nearest
  walkable cell when the map has no exit. Not in combat or knocked down; once every 30 minutes (gamedata
  `StuckCommand`), counted over relogs until the server restarts.
- **Minimap pings** (Default, Attack, Defend): shown to the pinger and to its party members on the same map, with the
  pinger's name; at most two a second.

### Cosmetics (vanity): from content only

StageN vanity (`../analysis/work/stageN/vanity/SPEC.md`). Nothing is sold: the **vanity store** in the cities shows the
client's catalogue (wardrobe skins, mount skins, ...), what you own marked as owned; its buy button only answers with a chat
line that cosmetics come from content. Wardrobe and mount skins drop **very rarely** instead (`Vanity:*`, same under both
presets, `Vanity:ChanceFactor` scales them all, 0 turns a source off):

| Source | Key | Default |
|---|---|---|
| world boss (each looter) | `Vanity:WorldBossChance` | 2 % |
| other boss / miniboss / champion (the killer) | `Vanity:BossChance` / `MinibossChance` / `EliteChance` | 0.3 % / 0.1 % / 0.03 % |
| boss chest of a dungeon, static, corrupted, hellgate or avalonian dungeon (first opener), × (1 + rarity) | `Vanity:BossChestChance` | 0.4 % |
| other chests | `Vanity:ChestChance` | 0 |
| a hunt's final kill (each member) | `Vanity:HuntChance` | 2 % |
| arena win (each winner) / duel tournament winner | `Vanity:ArenaWinChance` / `TournamentChance` | 0.5 % / 25 % |
| each Adventurer's Challenge reward / weekly guild goal (each member online) | `Vanity:SeasonRewardChance` / `GuildGoalChance` | 1 % / 1 % |
| total fame passing 1M, 5M, 10M, 25M, 50M, 100M | `Vanity:FameMilestones` / `FameMilestoneChance` | 25 % |

A drop is a skin your account does not have yet, themed by its source with `Vanity:ThemeChance` (75 %): undead bosses give
undead/skeleton skins, keepers keeper skins, and Morgana, heretic, demon (hellgates), mists and corrupted sources theirs; the
rest come from the whole pool (about 500 skins). Owning them all pays `Vanity:DuplicateSilver` (25000) silver instead. The
unlock shows the client's popup and a chat line; unlocks belong to the account (all its characters).
Apply them with the client's own windows: the **vanity tab of the character window** (wardrobe: weapon, off-hand, head, armor,
shoes, bag, cape skins over your gear, seen by everyone) and the **mount skins** (per mount kind, shown on the mount).
Saved with the character (`albionlocal.db` schema v35). Avatars, avatar rings and emotes do not drop (every emote is free).
GM: `.vanity list [text]` (yours, or the catalogue entries with the text and their ids), `.vanity grant <id|name>`,
`.vanity revoke <id|name>`, `.vanity drop [theme]` (a drop now, e.g. `.vanity drop UNDEAD`).

## Player bots

Under the pve preset the towns are not empty (stage I, `../analysis/work/stageI/bots/SPEC.md`, phase 1): **city bots** stand at
the bank and market doors, the crafting stations, the travel points and the other places of a royal city or start town, now
and then sit, dance, wave or cheer, and walk from one place to the next or 15-40 m around. To the client a bot is an ordinary
other player: name, look (one of the 22 avatar looks of the character creation), portrait, gear by tier (T4-T8 in royal
cities, T1-T2 in start towns), its walking and its emotes, seen by every player around alike. A bot has no account, no
saved character and no connection; its name is never one of a real character's, and a player who creates a character of a
bot's name takes it (the bot leaves). Bots never walk through walls: the server builds a 1 m walk grid of the cluster from the
client's own tile collision data (the same grid the client keeps its own player in), a path lattice over it and the places
of interest, the first time a player enters a cluster that gets bots (off the world thread, a fraction of a second; a failed
build is tried again 10 minutes later). City bots are never
in a fight, whatever the zone or flags: attacking one is refused as for a friendly player.

**Gatherer bots** (phase 2) work the open world of every tier and the start towns (and, with `Bots:City:Gatherers`, the T1 trees
of royal cities): each gathers one resource of its tier band (its tier − 2 up to its tier; on a map without such nodes the tier
the map has), with the tool of its tier in hand and the matching gatherer set. It walks to a node with charges that no player
stands at (6 m) or harvests and no other bot took, away from hostile mob camps (12 m), riding when the way is long, stops by the
node facing it and shows one to three harvests: the players around see the harvest animation, the "+N" and the sound as for any
other player, and the node keeps its charges (`Bots:ConsumeNodes` true: it takes them like a player). After 6-20 nodes or its stay
it walks out through an exit (or logs out where it stands, 10 s, when no way leads to one); attacked on the way, it walks on after
the fight. Since this phase **players also see each other gather** (the official behaviour), also when they
walk up to someone in the middle of a harvest. A gatherer is Blue in safe, yellow and orange zones and on the Roads of Avalon, and Red in red and black ones
(`Bots:Gatherer:Flag`): it never starts a fight, but **answers an attack**: whoever may attack it by the zone rules (a Red player a
Blue gatherer in a yellow zone; under the pve preset a Blue player may not attack a Blue one) is fought back for 20 s after his
last hit (and his party), also where the zone would let the bot attack him, with the tool, after a short reaction; 60 % of the
gatherers (`Gear.GathererWeaponShare` in `content/bots.json`) carry a weapon in the bag (one of the `Weapons` that exist at the
gatherer's tier: at T1 only the sword, at T2 also the bow and the fire staff), take it in hand when attacked and cast its
spells (a tool has no spell slots, so a gatherer without one casts only the self buffs of its gatherer set), and take the tool
back after the fight; it turns to an
attacker who dealt half again as much damage as its target in the last 5 s; a riding bot is dismounted first; below a quarter of its health
it runs off once per fight with half a chance. Hitting a bot is no crime under pve (`Bots:Pvp:CrimesAgainstBots`). At 0 health it
is knocked down (in a lethal zone it dies) and disappears 10 s later (3 s after a death); its place gets the same "player" back
after 1-3 minutes. A player knocked down by a bot gets the usual PvP knockdown; the bot gains nothing, and its hits never count as a PvP assist
(nor in the assisters' damage).

**Patrol bots** (phase 3) are Red players in groups of 1-3 (`Bots:Patrol:GroupSize`) in the yellow, orange, red and black zones
(5, 6, 8 and 10 bots by default; never in cities, islands or green zones, where a Red player cannot be). The group's leader walks one
of the cluster's roads end to end and back (without roads a loop through exits, mob patrol paths and camps), stopping 2-8 s at
each stop and 5-20 s at a road's end, riding long stretches; the others follow behind it. A patrol **attacks on sight** whoever
the server's PvP rules let it fight within 15 m (`Bots:Patrol:AggroRadius`), when a way leads to him (no aggro through a wall or
up a cliff); the whole group joins in. Under the pve preset that is a **Red** player only: a Blue player is never attacked
unprovoked, but may attack a patrol where the zone allows it (it is Red), and then the group fights back for 20 s after his
last hit on any of them. Under the official preset, or with `Bots:PvpRules Official`, patrols follow the client's zone table
(they attack Blue players in yellow zones and everyone in black ones); `Bots:PvpRulesByZone` does that for some zones only, e.g.
`{ "red": "Official", "black": "Official" }` keeps the pve rules in yellow and orange zones and makes red and black ones
dangerous. That is the pve preset's default (the user's choice, 2026-10-18; the roads are black zones too): in red and
black zones patrols attack Blue players on sight as well, and a defeat there is still only a knockdown under pve. The
official preset sets no zone (it has no bots unless `Bots:Enabled`). Not attacked: a player who arrived on the cluster in the
last 10 s, one in a duel, a riding one when `Bots:Patrol:AttackMounted` is false, one it knocked down in the last 5 minutes;
it gives up 40 m from where the fight began and walks back to its road. Its auto-attacks are its weapon's, and it casts the
spells of its gear (`Bots:Combat:Spells`, see "Spells on other players"). A
player it knocks down gets the usual PvP knockdown (`Bots:Pvp:LethalToPlayers` true: a death in a lethal zone when
`Pvp:LethalDeath` allows deaths at all); the bot gains nothing.

**Beating a bot** (a patrol or a gatherer) rewards like a small PvP kill: half its kill fame (`Bots:Rewards:FameFactor`, shared
by the party as for a player kill, the season's PvP fame included; a knockdown shows no kill line, a death in a lethal zone
does), the PvP goals of the season, bounties and journal with the bot as the victim (their own anti-farming rules apply), and,
when it is gone (at once after a death), a silver pile by its tier (T4 250, T8 2500 × `Rates:Silver`) and a loot bag with each of
its worn combat items at a 20 % chance (`Bots:Rewards:GearDropChance`; a bot that dies in a red or black zone 50 %,
`Bots:Rewards:DeathGearDropChance`, official 100 %), at least one of them (`Bots:Rewards:MinItems`), worn by the death's durability
loss; a gatherer adds half of what it gathered and carries 5-20 of its resource from the start (`Gear.GathererStartCarry`), a
patrol may add a healing potion (25 %) or a stew (15 %; `Rewards.PatrolExtra`, at the next lower tier that exists), the killer's
party's for the protection time. The same bot beaten again by the same player within 10
minutes gives a quarter of the fame and nothing else; at most 30 bags and piles of bots an hour per player. The items are the
bot's own (moved, never copied) and nothing of a bot is ever saved, so no duplicate is possible.

- The population fills while a player is on the cluster: `Counts` per zone class in `content/bots.json` (since stage M about
  2.5 × the first tables: 24 in a royal city, 8 city bots and 6 gatherers in a start town, 14 gatherers in a safe zone, 12
  gatherers and 5 / 6 / 8 patrols in yellow / orange / red zones, 10 + 10 in black ones; 3 gatherers and 2 patrols on a road of
  Avalon, one Drifter in a mists zone: see The Mists) × `Scale` by the players there (a factor
  above 1 adds bots, up to `Bots:MaxPerMap` 40; `Bots:MaxTotal` 400 in all), at most 4 new ones every 5 s, each **out of view** (it appears more than `Visibility:EnterCells + 1` cells away). A bot thinks only while a
  player is within 120 m or sees it.
- **Turnover:** a bot stays 10-30 minutes, then walks into an exit (gone at the exit, as a player leaving) or logs out where
  it stands (10 s); the next one of its place arrives through an exit 20-90 s later, another "player" with another name.
- 30 s after the last player left a cluster its bots are gone; nothing of them is ever saved.
- `config/*.jsonc` `"Bots"` has the switches and timings (Enabled, caps, the population pass, stay and arrival times,
  think interval, look, the walk grid's lattice, emote chance, `PvpRules`, `PvpRulesByZone`; for gatherers `Gatherer` { Flag, RedShare, Radius,
  Nodes, AvoidPlayerRadius, AvoidMobRadius, FleeHealth, FleeChance }, `ConsumeNodes`, `MountDistance`, `Pvp` { EngageSeconds,
  EngageParty, CrimesAgainstBots, LethalToPlayers }, `Combat` { TickMs, ReactionMs, DamageFactor, HealthFactor, Spells, SpellChance,
  Tactics, KiteDistance, RetreatHealth, Potions, Food, FocusWeakest }, `Difficulty`, `Gear` { Mastery, DamageBonus, RedZoneTierBonus,
  Quality, EliteChance, EliteEnchant, EliteQuality }, `Patrol` {
  LeashRadius, AggroRadius, SpareMinutes, JoinGraceSeconds, AttackMounted, GroupSize, RedGroupSize, RouteDwellSeconds, RouteEndDwellSeconds,
  Makeups }, `Support` { Enabled, HealBelow, ShieldBelow },
  `Defeat:DieInLethalZones`, `DownDespawnSeconds`, `Rewards` { FameFactor, GearDropChance, DeathGearDropChance, MinItems,
  ResourceDropShare, LootProtectionSeconds, BagsPerHour, SameBotMinutes, EliteSilverFactor, EliteExtraItems }, `City:Gatherers`), `content/bots.json` the tables
  (counts per zone, clusters excluded or overridden, city tiers, avatars, emotes, name syllables, gear patterns with
  `GathererWeaponShare` and `GathererStartCarry`, `Rewards` { Silver by tier, PatrolExtra }). Preset official: no bots.
- **Inspecting** a bot or another player (the inspect button of the target frame) shows its gear, the mastery of each weapon and
  armour piece and its item power: the server answers GetCharacterEquipment (op 143) with the worn items, sent to the inspecting
  client first. The duel invite window asks the same for the challenger, so it shows his gear too; the character is found on the
  requester's map or else on any map, and only an unknown one fails (the client then shows "failed retrieving" and closes the
  invite).
- **Bot strength:** a bot's gear is that of a player with the destiny board spent on it: `Bots:Gear:Mastery` is the share of the
  full board's item power it carries (1.0 = all of it, a T7 gatherer's gear at IP 1288; 0 = the items' own, T7 1000).
  `Bots:Gear:DamageBonus` (true): a bot's spells add its worn spell damage bonus like a player's (a cloth patrol's spells × 1.4-1.5,
  leather × 1.2-1.3, plate × 1-1.1). **Since the botpower change (2026-10-18) the pve preset no longer softens the bots**: Mastery 1,
  DamageBonus true, HealthFactor 1 (before 0.5 / false / 0.7, about 40 % shorter fights): fighting a patrol is a real risk.
  `Bots:Difficulty` (1.0, 0.1-10) multiplies the health and the hits on players of every bot but the companions (0.7 is roughly
  the old pve strength, 1.5 a hard server). Hired companions keep their own `Companions:GearMastery` 0.5 / `GearDamageBonus` false.
- **Gear by zone:** world bots wear quality `Bots:Gear:Quality` ("1-2"); patrols in red and black zones and on the roads one tier
  above the map's roll (`Bots:Gear:RedZoneTierBonus` 1) and walk in groups of `Bots:Patrol:RedGroupSize` ("2-3"). A patrol or
  Drifter is an **elite** with `Bots:Gear:EliteChance` (6 %): weapon, offhand and armour enchanted .1-.3 (`EliteEnchant`), quality
  3-4 (`EliteQuality`); beaten it gives `Bots:Rewards:EliteSilverFactor` (2) × the silver and `EliteExtraItems` (1) more of its gear
  in the bag (dropped as worn, enchantment and quality kept; still under BagsPerHour and SameBotMinutes).
- **Tactics** (`Bots:Combat:Tactics` true): besides the SpellChance roll a fighting bot casts at once, by cooldown, what the
  situation asks for: a dash or sprint when a melee bot is far from its target, a heal or shield (its own) below 45 % health, crowd
  control on a target that is casting or channelling, its damage spells on a target below 35 %. Below `RetreatHealth` (0.3) it drinks
  one of its `Potions` ("1-2" healing potions in the potion slot), without one it falls back once for 3 s. A ranged bot (weapon range
  of 6 m or more) steps back along the walk grid from a target nearer than `KiteDistance` (5 m). Patrols and Drifters eat a stew at
  spawn (`Food`). `FocusWeakest`: a patrol attacks the weakest player in reach and turns to one below 35 % health. The consent rules
  are untouched: bots still start fights only where `PvpRules` / `PvpRulesByZone` and the zone's flags allow (pve: Blue players only
  in red and black zones, Red players where the zone lets them be fought), never in safe zones.
- GM commands: `.bot` (the population of every loaded cluster and its walk grid), `.bot list`, `.bot spawn city|gatherer|patrol [count] [tier]`,
  `.bot clear [here|all]`, `.bot on|off`, `.bot info`, `.bot grid`, `.bot path <x> <z>` (a bot walks the path, to see that bots
  go around walls).

Client checks of this phase: in Thetford or Lymhurst the bots' look, gear, name, portrait and sex render; nobody walks through
houses, walls, the bank pillars or into water; a sitting or dancing bot keeps it for a player who walks up later; clicking or
inviting a bot gives an error or nothing; frame rate with 20 bots in view. If every bot has the default look, NewCharacter keys
5/6 need the official byte[] form (SPEC §7 Q1). Phase 2: in Birchcopse (T4) the gatherers' harvest animation with the right tool,
the "+N" and sound at the end, the node not shown emptied, a bot met mid-harvest shown harvesting; attacked mid-harvest the
animation stops and it turns to fight with its tool (a riding one dismounts first); its knockdown, then gone after 10 s. Phase 3:
in a yellow zone a patrol shows as an enemy (red name) to a Blue character and walks past it, answers once hit; a Red character is
attacked on sight (the swing animation and the damage numbers), knocked down gets up after 90 s and is left alone for 5 minutes;
patrols keep to the roads, chase around obstacles, give up at the leash, ride between far stops; a beaten patrol: knockdown, the
fame popup without a kill line, 10 s later its bag (its gear on the corpse, opens) and silver; in a red zone its death, the kill
line and the bag at once. Botpower: a patrol fight takes clearly longer than before (full health), patrols in red/black zones
come in 2-3 with one tier higher gear; a low patrol drinks a potion (the drink animation, its health rising), a bow or staff
patrol steps back when you close in, a melee one dashes in; now and then an elite with enchanted (.1-.3) gear whose bag holds two
pieces of it.

### Switches

Bots are not an official feature (like AzerothCore's playerbots module): `Bots:Enabled` is the master switch over every kind
(official `false`, pve `true`; companions have their own `Companions:Enabled`). Under it each kind has its own switch, `true` in
both presets, so the official preset is all off through the master alone and the pve preset runs everything as before:

| Key | Default | Controls |
|---|---|---|
| `Bots:World:Enabled` | `true` | the open-world population (City, Gatherer, Patrol, Drifter); `false` = none, the content bots stay |
| `Bots:City:Enabled`, `Bots:Gatherer:Enabled`, `Bots:Patrol:Enabled`, `Bots:Drifter:Enabled` | `true` | one world role each, without editing the counts of content/bots.json |
| `Bots:Zones` | `[]` | zone classes (City, Start, Roads, Mists, Safe, Yellow, Orange, Red, Black) or cluster ids where world bots may spawn; empty = everywhere content/bots.json gives bots |
| `Bots:Arena:Enabled` | `true` | bots fill arena teams (with `Arena:Bots`), also with `Bots:World:Enabled` false |
| `Bots:Hellgate:Enabled` | `true` | a hellgate versus run's bot enemy team (else the guardian), also with `Bots:World:Enabled` false |
| `Bots:Invader:Enabled` | `true` | corrupted dungeon bot invaders (with `Corrupted:BotInvaders`), also with `Bots:World:Enabled` false |
| `Bots:Pvp:Enabled` | `true` | `false` = world bots never start a fight with a player; they still answer an attack (on them or their group) for `Bots:Pvp:EngageSeconds`. Arena, hellgate and invader bots are not affected |
| `Bots:Rewards:Enabled` | `true` | `false` = a beaten bot gives no fame, PvP goal credit, silver or bag (the kill message and a crime's reputation stay) |
| `Bots:Travel:Enabled` | `true` | a bot walking into an exit reappears next door (see Travel and gathering trips); `false` = gone there as before |

Behaviour figures that were fixed before (both presets, today's values): `Bots:City:PoiShare` 0.6 (next goal a point of interest,
else a spot `Bots:City:WanderMeters` 15-40 m away), `Bots:City:ExitShare` 0.5 (walk out through an exit, else log out),
`Bots:Gatherer:FleeSeconds` 20 / `FleeDistance` 25 m (a fighter's fall-back runs half), `Bots:Patrol:FollowSpacing` 2.5 m,
`Bots:Combat:RetargetSeconds` 5 / `RetargetShare` 1.5, `Bots:Combat:KiteSeconds` 1.2 / `KiteEverySeconds` 2.5.

`.bot on|off` switches the master for the running server only: it is not saved, and the next start reads `config/world.jsonc`
again. The other switches change only with the file and a restart (there is no `.bot reload`: the config is read once at the
start). `.bot` shows the switch matrix in its first lines.

### Social presence and chat

With `Bots:Social:Enabled` (default `true`, so on under pve and off under official through the master) a world bot (city,
gatherer, patrol, Drifter) is an online player to the social features, so a player no longer gets "player not found" for a
name he sees walking by:

- a **party invite** (by name or from the bot's menu) goes out as to a player and is answered after `Bots:Social:ReplyDelaySeconds`
  (`"2-6"` s): declined, or accepted under `Bots:Party` (below);
- a **friend request** is answered RC 0 with the bot's entry and **declined** after the same delay;
- a **whisper** is taken (the "to <name>" line) and, with `Bots:Social:WhisperReplyChance` (0.8), once per sender in 30 s, answered
  after the delay with a line of content/bots.json `Chat.Whisper` ("busy farming, sorry", "afk", ...).

Nothing of this is stored: no friend, request, mute or character row is written for a bot. With `Bots:Chat:Enabled` (default `true`)
bots also speak on the local (Say) channel, to the players that see them: city chatter (`Bots:Chat:SayChancePerMinute` 0.05 per city
bot in view, lines `Chat.Say`), an emote back when you emote at a city bot or near one (8 m; `Bots:Chat:EmoteReplies`, the
`Chat.EmoteReplies` table, sometimes a `Chat.Greet` line) and a `Chat.Win` / `Chat.Loss` line half the time after a fight
(`Bots:Chat:FightLines`). All bot lines of a cluster are capped at `Bots:Chat:MaxLinesPerMinutePerCluster` (4) a minute and one per
bot in 30 s. The lines are content/bots.json `"Chat"`.

| Key | Default | Controls |
|---|---|---|
| `Bots:Social:Enabled` | `true` | world bots answer party invites, friend requests and whispers (`false` = "not found" as before) |
| `Bots:Social:ReplyDelaySeconds` | `"2-6"` | how long a bot takes to answer |
| `Bots:Social:WhisperReplyChance` | `0.8` | the chance a bot answers a whisper (else silence) |
| `Bots:Chat:Enabled` | `true` | bots' local lines and emote replies |
| `Bots:Chat:SayChancePerMinute` | `0.05` | city chatter per city bot in a player's view |
| `Bots:Chat:EmoteReplies` | `true` | a city bot answers a player's emote |
| `Bots:Chat:FightLines` | `true` | "gg" and taunts after a fight |
| `Bots:Chat:MaxLinesPerMinutePerCluster` | `4` | the cap of bot lines per cluster and minute (0 = silent) |

#### Bots in your party

With `Bots:Party:Enabled` (default `true`; it needs `Bots:Enabled` and `Bots:Social:Enabled`, so on under pve, off under official) a
world bot may **accept** a party invite, like a random bot of AzerothCore. It accepts when it stands out of a fight, is in no party, is
on your map, its gear tier is within `Bots:Party:TierRange` of yours (your average item power: T4 = 700 … T8 = 1100), its kind is
allowed (`Bots:Party:City` / `Gatherer` / `Patrol` / `Drifter`) and your party has fewer than `Bots:Party:MaxPerParty` such bots;
then with `Bots:Party:AcceptChance`. Otherwise it declines as before.

Once in, it leaves its world role (a gatherer draws the weapon from its bag, a rider dismounts, a patrol leaves its group, its
population slot is freed for a newcomer) and plays as your **companion** (see Companions below): it follows you, fights the mobs that
fight the party, uses its own spells, and goes with you over cluster changes and into dungeons, expeditions, hellgates and the mists
exactly as a hired companion does (where companions cannot go, it cannot either). Its role comes from its own gear: **healer** with a
holy or nature staff, **tank** with a shield, mace or hammer, **ranged** with a weapon of 6 m reach or more, else **melee** (a city bot
without a weapon fights bare-handed); its gear, tier and spells stay its own. It counts as a member in the party's fame split (its
share is not kept anywhere), its kills are credited to you as a companion's, and it never takes loot or silver. With `Bots:Chat` on it
says a `Chat.Join` line when it joins and a `Chat.Leave` line when it goes (content/bots.json `"Chat"`).

It leaves when you kick it from the party frame, the party breaks up, you (its inviter) leave the party or are out of the game for
`Companions:OwnerGraceSeconds`, `.companion dismiss` names it, or after `Bots:Party:MaxMinutes`. Then it **logs out** (despawns): it
does not go back to its old role; its slot was refilled when it joined. Nothing is saved, and a restart ends it.

| Key | Default | Controls |
|---|---|---|
| `Bots:Party:Enabled` | `true` | world bots may accept party invites (`false` = always declined) |
| `Bots:Party:AcceptChance` | `0.7` | the chance an eligible bot accepts |
| `Bots:Party:TierRange` | `1` | how many tiers its gear may be from yours |
| `Bots:Party:MaxPerParty` | `2` | world bots one party may have (hired companions not counted) |
| `Bots:Party:MaxMinutes` | `120` | how long it stays in the party at most |
| `Bots:Party:City` / `Gatherer` / `Patrol` / `Drifter` | `true` | which kinds may accept |

#### Support roles in patrol groups

With `Bots:Support:Enabled` (default `true`; it needs `Bots:Enabled`, so on under pve, off under official) a patrol group may bring
a **healer** and a **tank** instead of damage dealers only. A new population group draws one makeup of its zone class from
`Bots:Patrol:Makeups` by weight; its members take the makeup's parts in order as they spawn (a fallen healer's replacement is the
group's healer again), the members beyond the makeup are damage dealers, and a lone Drifter always is one.

- A **healer** carries a holy or nature staff (content/bots.json `Gear.HealerWeapons`, a book with a one-handed one) in cloth, its
  kit's heals chosen first (from the item's own spell lists, as a hired healer's). In a fight and after it, it heals the member of its
  group (itself included) with the lowest health share below `Bots:Support:HealBelow`, walking into range when needed; in a fight it
  also shields the lowest one below `Bots:Support:ShieldBelow` and puts its friendly buffs (HOLYHOT) on the fighting members. Every
  cast goes through the players' checks (target class, `Pvp:FriendlySpells`, range, cooldown, energy): it never targets a human (a bot
  and a human are never Friends), a hostile unit or a bot of another group (under `Pvp:FriendlySpells Group` the patrol group is a
  bot's group); an area heal reaches whom a player's would (under `All` another bot standing in it too).
- A **tank** carries a mace or hammer (`Gear.TankWeapons`, a shield with a one-handed one) in plate (SET2/SET3 from T4: the armour with
  TAUNT), its kit's taunts chosen first. It engages without the reaction time, casts its taunts as soon as they are ready, and turns on
  whoever hits its group's healer.
- A healer or tank whose weapon items.xml lacks at its tier (no holy staff or mace below T3) is a damage dealer.
- A healer patrol that joins your party (`Bots:Party`) plays as a companion healer: it heals and revives your party with the companion
  code (`Companions:HealBelow`).
- `Bots:Support:Enabled false`: every patrol a damage dealer of `Gear.Weapons` whose friendly spells go on itself only, as before.

| Key | Default | Controls |
|---|---|---|
| `Bots:Support:Enabled` | `true` | healers and tanks in patrol groups and their help (`false` = as before) |
| `Bots:Patrol:Makeups:<zone>` | Default `["dps=90", "healer,dps=7", "tank,healer,dps=3"]`; Red, Black, Roads `["dps=80", "healer,dps,dps=12", "tank,healer,dps=8"]` | a zone class's makeups, `"part,part,...=weight"` (parts `dps`, `healer`, `tank`; weight 0-1000, 1 when left out); `Bots:Patrol:Makeups` alone or `:Default` for every zone class without its own; on the command line a `;` list. The defaults keep nine groups in ten (eight in red and black zones and on the roads) as before |
| `Bots:Support:HealBelow` | `0.7` | a healer heals a member below this health share |
| `Bots:Support:ShieldBelow` | `0.9` | in a fight a healer shields a member below this health share |

Client checks: in a yellow zone a patrol with a holy or nature staff and cloth among the others; hit its mate, and the healer's heal
animation and green numbers on the mate (never on you); a plate patrol with a mace casts TAUNT at whoever hits the healer and turns to
him; the party frame of a healer patrol you invited, its heals on you.

#### Roster, hours and gear

**Roster.** Like AzerothCore's random bots, the population is a fixed **roster** of `Bots:Roster:Size` identities derived from
`Bots:Roster:Seed` and the name syllables of content/bots.json: each has a name, a character Guid, a kind (40 % city, 35 % gatherer,
25 % patrol/Drifter), a home tier (city bots by `CityTiers`, some T1-T2 for the start towns; the others T1-T8), a look and its own
random numbers for its gear. A free slot takes an offline identity of its kind whose home tier is nearest to the zone's roll and wears
that tier where the zone has it (a royal city's `CityTiers`, a start town's T1-T2, the open world's map tier ±1); a respawn keeps the
slot's identity, a turnover's newcomer is another one. So the same names come back, here or on another map, with the **same look and
gear** (in the same zone and tier) over respawns, turnovers and server restarts; nothing is stored in the database. A roster name a
character has (or takes later) is skipped, and when every identity of a kind is online a fresh name is drawn as before. An identity
that is offline is "not online" to a whisper, an invite or a friend request like any offline player; a bot that leaves your party logs
out and its identity may come back later.

**Hours.** `Bots:Schedule` scales every map's counts by the hour of the server's local time (UTC + `Bots:Schedule:UtcOffsetHours`):
`Hours` is 24 factors (hour 0 first), or `Points` a few anchors (`{ "0": 0.5, "18": 1.2 }`) with the hours between linear; between two
hours the factor is linear too. When it rises, the free slots fill as usual (at most `Bots:SpawnPerPass` per pass, out of view, the
newcomers through an exit); when it falls, `Bots:Schedule:LeavePerPass` surplus bots per map and pass go: one no human sees logs out at
once, a seen one ends its stay and walks out or logs out as at the end of a stay. Default: flat `1.0` (no change) in both presets;
config/world.jsonc has a commented evening-peak example.

**Gear.** A population bot's set is mixed: each armour piece, weapon, offhand and cape is with `Bots:Gear:MixedShare` 1 to
`Bots:Gear:TierSpread` tiers above or below its tier (tools, bags, mounts and a gatherer's set stay); each weapon, offhand and armour
piece is enchanted `Bots:Gear:Enchant` (.1-.3) with its zone's `Bots:Gear:EnchantChance`, and each weapon and armour piece an artifact
(content/bots.json `Gear.ArtifactSets` / `ArtifactWeapons`, T4+) with its zone's `Bots:Gear:ArtifactChance`, mostly in black zones.
The elites (`Bots:Gear:EliteChance`), qualities and red-zone tier bonus are as before. GM `.bot` bots, arena, hellgate and invader
bots and companions keep their plain sets.

| Key | Default | Controls |
|---|---|---|
| `Bots:Roster:Enabled` | `true` | the stable roster (`false` = a fresh name per bot) |
| `Bots:Roster:Seed` | `1` | the roster's seed |
| `Bots:Roster:Size` | `1500` | identities (1-8000) |
| `Bots:Schedule:Hours` | 24 × `1.0` | the factor per hour (0-3); or `Bots:Schedule:Points` `{ hour: factor }` |
| `Bots:Schedule:UtcOffsetHours` | `0` | local time = UTC + this |
| `Bots:Schedule:LeavePerPass` | `1` | surplus bots per map and pass that go when the curve falls |
| `Bots:Gear:MixedShare` / `TierSpread` | `0.25` / `1` | pieces off the bot's tier, and how far |
| `Bots:Gear:EnchantChance:<zone>` | City 0.03, Start 0, Safe 0.02, Yellow 0.04, Orange 0.06, Red 0.08, Black 0.12, Roads 0.12, Mists 0.08 | an enchanted piece per zone class |
| `Bots:Gear:Enchant` | `"1-3"` | its level |
| `Bots:Gear:ArtifactChance:<zone>` | Red 0.02, Black 0.06, Roads 0.06, Mists 0.03, else 0 | an artifact piece per zone class |

#### Travel and gathering trips

**Travel.** A city bot, gatherer or patrol member that walks into an exit no longer just vanishes: it leaves the map as a player
taking the exit does, is "in transit" for `Bots:Travel:Seconds`, and then appears at the matching entrance of the neighbouring
cluster (where a player coming through stands) as the same identity: name, Guid, gear, health and what it carries. There it takes a
free slot of its role (its old slot gets a newcomer later, as at any turnover). Bots only live where players are, so the rule for
map loading is simple: it travels only when, at the moment it takes the exit, the target map is **loaded with a player on it**, its
zone gets world bots (`Bots:Zones`, `Bots:<Role>:Enabled`; never a dungeon, island, hellgate, arena, expedition or mists instance),
its walk grid is ready, it has room under `Bots:MaxPerMap` and a slot of the role is free. Otherwise it simply logs out at the exit,
as before. If the target stops taking it while it is in transit (unloaded, emptied, the slot taken), it logs out on the way. Nothing
is stored. City bots therefore travel only between city maps that have city bots; gatherers and patrols move through the open world.
A road's portals keep their own rules (Bots:Roads:Portals).

**Route.** A leaving bot prefers an exit to a neighbour that takes it, with a zone of its own tier first (a royal city's tiers, a start
town's T1-T2, the open world's map tier ±1), then the nearest tier; the other exits come after, nearest first.

**Gathering trips.** A gatherer's home city is the nearest royal city or rest (with a bank; not an interior, hall, portal town or the
Hell Den) within `Bots:Travel:MaxHops` clusters of where it came online. When its bag is full (`Bots:Gatherer:Nodes` nodes) or
`Bots:Gatherer:TripMinutes` are over and it carries something, it walks home cluster by cluster along the shortest way over the
cluster exits (never through an instance, a building interior, a road or a zone `Bots:Zones` leaves out), arriving in its city as a
visitor when the city has no gatherer slot. There it walks to the bank door, pauses a few seconds and its gathered resources are
banked: gone, in that city only. The regional economy stays as it is: per-city banks and markets, no global storage, and bots bank
nothing anyone can take out. Then a new trip begins (a visitor walks out again). City bots already walk to their own city's bank and
market doors among their points of interest. If a hop on the way home does not take it (no player there), it logs out at that exit.

| Key | Default | Controls |
|---|---|---|
| `Bots:Travel:Enabled` | `true` | exits lead next door (needs `Bots:Enabled` and `Bots:World:Enabled`: off under official, on under pve); `false` = gone at the exit as before |
| `Bots:Travel:Seconds` | `"10-20"` | time in transit |
| `Bots:Travel:MaxHops` | `6` | how far a gatherer's home city may lie (1-50 clusters) |
| `Bots:Gatherer:TripMinutes` | `30` | a trip's time budget (1-1440) |
| `Bots:Gatherer:BankAtHome` | `true` | gatherers walk home to bank at the end of a trip (needs travel); `false` = they leave through an exit as before |

### Companions

Companion bots (stageN, `../analysis/work/stageN/companions/SPEC.md`) fill a party for a player who plays alone or with one or two
friends. In chat, every player (not only GMs):

- `.companion hire <tank|healer|melee|ranged> [tier]` hires one: a **tank** (mace and shield, plate: TAUNT and the other threat
  spells of its kit), a **healer** (holy staff and book, cloth: its heals), a **melee** damage dealer (claymore, leather) or a
  **ranged** one (bow, leather). The gear tier follows your average item power (T4 = 700 … T8 = 1100, at least T3) unless you give one.
- `.companion dismiss [name|role|number|all]` sends companions away (all without an argument); `.companion list` shows them.

At most 4 per player and a party of 5 with them (`Companions:MaxPerPlayer`, `MaxPartySize`). A companion joins your party (a new
one you lead when you have none): the party frame shows it, and kicking it from there dismisses it. Content that needs a party
takes you with your companions: a hellgate (it needs a party), a group expedition registered by you as the party leader (a
player with companions counts as a group). Companions follow you through every exit, portal and instance (open world, random,
static and corrupted dungeons, expeditions, hellgates, the mists, the roads): on your new map they appear next to you once you
arrive. Within a map they walk after you on the mobs' walk grid; left 60 m behind or stuck for 6 s they are put next to you.

They fight only the mobs that already fight your party, within 25 m of you (they never pull a camp on their own), and give a
fight up beyond 35 m of you: the damage dealers assist you (your own enemies first), the tank takes every mob that hits someone
else (its taunts, its threat counts 3 ×), the ranged keep their distance, the healer heals the party member with the lowest health
under 85 % and revives a bleeding-out one (the players' 6 s revive; `Pve:PartyRevive` lets you bleed out even with only
companions around). Mobs fight companions as players: aggro, threat, their spells; a knocked-down companion gets up as a player
does after the fight, or is revived by you or a healer. **Loot and fame:** a companion's kill is yours (fame, silver, the loot
bag and its rights); companions take no loot and no fame share, and your fame is not split with them (the party's fame share and
group bonus count players only). They are never in a PvP fight (Friendly to everyone, every bot too). Nothing of them is saved:
they go when you log out (after `Companions:OwnerGraceSeconds` without you in game), leave the party, or at a restart.
`Companions:*` in `config/*.jsonc` (all on under the pve preset, off under official).

What to check in the client: the party frame lists the companions with your character (names, the leader crown on you); they
stand behind you, walk after you and come with you through an exit (after your loading screen); in a fight the tank's taunt pulls
the mob off you (the mob turns to it), the healer's heal and its revive cast show on you; a companion that lies down gets up after
the fight. Not done: a hiring NPC in the cities (the chat command only), party roles in the frame (their role icons stay empty).

## Chat, friends and muting

The client's chat goes to a third server next to login and game (`../analysis/work/stageJ/chat/SPEC.md`): SelectCharacter
names it (`PublicAddress` or the bind address, `Network:ChatPort` 5057) with a token for the login, and the client keeps that
connection over every cluster change and a reconnect of its login connection. `Chat:Enabled false` turns it off (no listener; the client then has Say, party lines
and `.g` only, as before).

- **Channels**: the 22 global channels of the official server (Global, Trade, Recruit, Looking for group, Help, the 15
  language channels, Announcement), **Local** (one per cluster: everyone on the same map) and **Guild** (the SOCIAL tab).
  The client joins those its chat tabs show: GENERAL has Local and the client language's channel, HELP / LFG / RECRUIT their
  own. With `Preset pve` (`Chat:WorldChannel`) GENERAL also holds **Global**, so every player has one world channel, and
  `Chat:MergeLanguageChannels` makes the 15 language channels one (a line in English reaches the Russian client's channel
  too); `Preset official` keeps both off. Say (nearby) and party chat stay on the game server as before.
- Lines on Global, Trade, Help, LFG, Recruit and the languages have the client's 2 s cooldown (`Chat:GlobalCooldownMs`; the
  client shows its cooldown text); `Chat:MaxLineLength` 1000. Only GameMaster and Admin accounts may post on Announcement
  (and Staff, which only they see); their lines carry the client's `[GM]` tag (`Chat:StaffTags`).
- A `.` command typed into any chat channel (Local, Global, Guild …) runs as in Say and is never sent to the channel, so a
  command line (a password included) is not broadcast.
- **Whispers** (`/w name text`, the reply key): to any player in game by name, any case; "player not online" for an offline
  name. `Chat:WhisperCooldownMs` 100 between two whispers.
- **Guild chat**: the guild's channel in the SOCIAL tab; `.g <text>` posts there too (members without a chat connection get
  it as the old `[Guild]` system line instead, never both).
- **Announcements**: world bosses and events announced server-wide (`AnnounceToAll`) and the bounties' new day go on the
  Announcement channel (every tab) for players who joined it (`Chat:Announcements`, on); cluster-local announcements stay
  system lines.
- **Friends**: the friends window works: add by name (the other player gets the request, also when it was offline at the
  time), accept or decline, take back, remove; the list shows who is online (and tells the friends when one logs in or
  out), the guild and the last time an offline friend was seen. `Social:FriendLimit` 200 friends (also checked when a request
  is accepted: with a full list the request stays open), `Social:FriendRequestLimit` 50 open requests sent or received. A player
  whose client closes on a loading screen counts as logged out after 2 minutes.
- **Muting** (`/mute`, `/unmute`, `/showmute`, the friends window's muted list): a muted player's chat lines and whispers no
  longer reach you (the whisper is dropped quietly); at most `Social:MuteLimit` 100.
- Friends, open requests and mutes are saved in `albionlocal.db` (schema v21; an older server build then refuses the file) and go with a deleted character; chat channels,
  lines and whispers are not stored. Not there yet: changing the chat tabs (the client's settings are reset to the default tabs
  at every Join), faction channels (no faction warfare), chat history for players who log in later,
  item links shown from a snapshot, the official whisper limit.

Commands: `.friends [name]` (yours; a GM may name a character), `.chat` (GameMaster: the chat connections and channels).

## Mail

The mailbox (the envelope on the HUD, `../analysis/work/stageJ/mail/SPEC.md`): the personal inbox and, for the guild master and The
Right Hand (the guild right "access guild mail"), the guild's; the filters (all, player, guild, market, system), pages of 50, reading,
"mark unread", deleting (one by one or "select all"). The HUD envelope shows the unread count and follows every new mail and every read.

- **Writing**: the write view sends text to a player (by name, any case, offline too), to a guild (into its mailbox) or, with the
  right, to the own guild's members (all, or those of one rank: one copy each). Each mail costs `Mail:Postage` 50 silver ("not
  enough silver" when short) and counts against `Mail:DailyLimit` 30 a day (from 00:00 UTC, the client's "try again in"); an unknown
  name, a guild that does not exist and a recipient who muted you get the client's messages. The client shows its own 50 / 30
  whatever the keys say. Subjects up to `Mail:SubjectMax` 50, texts up to `Mail:TextMax` 1000 characters. Players cannot put items
  or silver into a mail (the client has no slots for it), so mail moves no goods between cities.
- **Market notices** (`Mail:MarketNotices`): a sell order sold out ("Items Sold in <city>"), a buy order filled, and an order
  that expired or was cancelled get the client's own notices from "Marketplace Notification", with the totals (the client works
  out the average, the tax and a refund itself; its tax line uses gamedata's rate and premium, so with another Economy tax it can
  differ from what was credited). The items and silver stay in that city's "Finished" tab as before. Expired orders are now
  found once a minute, so the notice comes on time. Market mails go after `Mail:MarketExpiryDays` 30 days (the client's timer),
  other mails after `Mail:KeepDays` (0 = never); over `Mail:MaxPerBox` 500 the oldest read mails go. A mail with items is never
  deleted, by the player or by its end.
- **Rewards that do not fit** (`Mail:RewardOverflow`, the pve preset): expedition rewards, bounty and Adventurer's Challenge items
  and the loot of an opened box that do not fit the inventory come as a mail with them attached ("Reward items"); take them out
  by dragging them onto a free slot or with "take all" (only what fits moves; the rest stays). `Preset official` keeps the waiting
  lists (a later login, `.bounty claim`).
- **Guild notices**: a member joining, leaving or kicked and a guild account payout leave a notice in the guild's mailbox.
- Mails are saved in `albionlocal.db` at once (schema v22: the mails, their attachments, the day's sends and the amount each market
  order was placed with; an older server build then refuses the file) and go with a deleted character or guild. `Mail:Enabled
  false` gives the empty mailbox of before (nothing is made or sent; stored mails stay).

Not checked in the client yet (SPEC §7): the rows and bodies of the market notices, the envelope count, the write view's error boxes,
and whether the read view drops a claimed item by itself (the client reads the mail again only after a refusal; reopen the mail if
an item still shows). Alliance invitations arrive as their own mail kind (README Alliances). Not there: guild applications by mail,
gold market and real estate mails.

Commands: `.mail <name> <subject> | <text>` and `.mail item <name> <item> [count] [quality]` (GameMaster: a server notice, with an item attached).

## Trading between players

The trade window (right-click a player, "Trade"; `../analysis/work/stageJ/trade/SPEC.md`): face to face only, so trading carries
nothing between cities and the regional markets and banks stay as they are.

- **Invitation**: the other player must be in view and within `Trade:InviteRange` 7 m. Your window opens at once, the other
  player gets the 10 s question; unanswered, it lapses after `Trade:InviteSeconds` 15. Refused with the client's messages: you or
  the other already trading, too far away, busy (knocked down, at a station, casting, or in combat unless `Trade:AllowInCombat`),
  hostile to each other, or a zone without trading (`Trade:ClusterRules`: the hellgates, corrupted dungeons, hardcore expeditions,
  the 1v1 arena and most of the Mists; the dragon areas and HELL_GREEN within one party only). `Trade:Enabled false` refuses all.
- **Offers**: up to `Trade:Slots` 11 bag items (whole stacks; equipped items and items marked not tradable cannot go in) and
  silver. Every change shows on both sides and takes both accepts back. Accept counts only for the offer you saw and not within
  `Trade:AcceptDelaySeconds` 1.5 s of a change (the client itself waits 2 s); taking an accept back always works. An offered item
  that leaves the bag (moved, used, sold, destroyed) leaves the offer.
- **Exchange**: when both accept, everything moves at once or not at all: the items (with their quality, durability, spells and
  journal fame) come in as new objects, the silver moves, and both characters are saved in one database transaction, so a crash
  keeps both as before the trade or both as after it. When one side has no room for the other's offer after its own leaves, the
  accepts go back and that side is told "Not enough free inventory slots"; nothing moves. Walking more than `Trade:KeepRange` 9 m
  apart, leaving the map, disconnecting, being knocked down or stopping the server cancels the trade; nothing moves before the end.
  Gold cannot be offered (the client sends none; `Trade:AllowGold`).

Not checked in the client yet (SPEC §5): whether the items offered in a cancelled trade can be moved again at once (the client
locks offered items itself; Q2), the market value line of the window (not sent), and the spells of traded equipment on a receiver who
cannot use them (they are chosen again for the receiver).

Commands: `.trade` shows the player's trade, `.trade end` cancels it (GameMaster).

## Guilds

Guilds work through the game's own guild window (`../analysis/work/stageH/guilds/SPEC.md`; PVE-DESIGN step 13 part 1):

- **Create** from the guild window with a name and a crest: 3-30 letters, digits and single spaces, unique ignoring case;
  costs `Guilds:CreateCost` silver (pve 100000, official 1000000). The founder is the guild master.
- **Invite** by name from the members tab (the invited player must be in game): the other client gets the Yes/No box with
  a 60 s timer (`Guilds:InviteSeconds` 70 on the server). Yes joins as `Guilds:JoinRole` (pve `Member`, official
  `Recruit`); No tells the inviter. A full guild (`Guilds:MaxMembers`, 300) takes nobody.
- **Ranks** are the eight role tags of the official server (Recruit … The Right Hand, Guildmaster), changed in the
  guild's tag matrix; giving the Guildmaster tag hands the guild over (the old master becomes The Right Hand). Rights as
  official: Officer and higher (not Guardsman, Member, Recruit) may invite; the guild master kicks anyone, The Right Hand
  (the only other rank with the kick right) only recruits and other Right Hands (the client's own rule).
- **Leave**: a guild master names a successor (the client asks); the last member out disbands the guild and frees
  its name. After leaving or a kick a character waits `Guilds:RejoinCooldownMinutes` before the next guild (pve 0,
  official 4320).
- The guild's name and crest show over its members for everyone, in the character selection and in the guild window
  (members online / offline, roles). Guild mates are Friendly in black zones and Neutral in red ones, as official.
- **Guild chat**: the guild's channel on the chat server (SOCIAL tab, Chat above); `.g <text>` posts there, and as a system
  line `[Guild] name: text` to the members without a chat connection.
- Guilds, members, roles and crests are saved in `albionlocal.db` (schema v17); open invitations are not.
  `Guilds:Enabled false` turns guilds off (saved ones stay; a deleted character still leaves its guild, and a guild
  left empty is removed at startup). Not there: the guild bank window and the official tax setting, territories and GvG, the guild finder,
  guild mail, custom access tags, crest change; the guild keys of other messages (party invite key 6, Died 3/11, the
  player loot bag 20/21, a mob's owner guild, NewMountObject 9-17) stay empty for now.
- Known limit: a member whose connection drops in the middle of a cluster change stays "online" for its guild mates
  until its next login or disconnect (the same holds for party members).

The PvE guild (SPEC §10, PVE-DESIGN step 13 part 2; every part behind its own key, all off under `Preset official`):

- **No friendly fire** (`Guilds:NoFriendlyFire`, pve true): guild mates are never enemies outside a duel, even with two
  different faction flags in a red zone (official: enemies there), so their attacks and areas never hit each other.
- **Guild experience and levels**: `Guilds:XpShare` (pve 0.1) of every PvE fame gain of a member (kills, harvests,
  catches, crafts, the fame of bounties, expeditions and events; not PvP fame, not a GM's `.progress fame`) and
  `Guilds:ClearXp` × tier (pve 2000) per dungeon cleared (its first boss or miniboss killed, once per guild and dungeon
  instance) or expedition completed go to its guild. `Guilds:Levels`
  (0, 50000, 250000, 1000000, 5000000) are the levels' thresholds; each level above 1 gives every member
  `Guilds:FamePerLevel` (0.02) more PvE fame, `Guilds:SilverPerLevel` (0.02) more silver in its mob kills' piles,
  `Guilds:GatheringPerLevel` (0.01) more gathering and fishing yield (with the gear's bonus) and `Guilds:RepairPerLevel`
  (0.05, at most 90 %) off its repairs (the repair window shows the full price; less is charged). A new level is a
  system line `[Guild] … reached guild level n: …` to the members in game; `.guild info` shows level, experience,
  perks and the account (the client has no guild level outside GvG).
- **Shared hunting** (`Guilds:ShareWithoutParty`, pve true): guild mates within the party fame radius (50 m) of a kill
  share its fame like party members (the same split and group bonus) and may take its silver, without a party.
- **Guild account** (`Guilds:SilverTax`, pve 0.05): of each silver pile a member takes this share goes to the guild's
  account instead of `Economy:SilverPickupTax` (the client shows it as the guild tax of the pickup and as 5 % in the guild
  window, whose silver is the account). `.guild payout <name> <silver>` (with `Guilds:SeasonRewards Content`, a `SilverTax` or
  `Contracts`) pays a member in game from it (Master of Coin,
  The Right Hand or the guild master). A guild that is deleted (its last member leaves, or `.guild disband`) pays its
  account to that last member or to its guild master (at once, or at the next login when offline).
- **Contracts** (`Guilds:Contracts`, pve true): every `Guilds:ContractPeriod` (`Weekly` from Monday 10:00 UTC, or
  `Daily`) a guild gets `Guilds:ContractCount` (3, at most 4) goals out of: kill 500 mobs, clear 5 dungeons, gather
  2000 resources, complete 3 expeditions (a fifth of each daily), each of the tier most members in game are playing at
  (3-8, 4 by default) or higher, the counts × (members + 1) / 2 (1-5). Everything a member (or its sharing guild mates,
  once per kill) does counts; progress is told at each quarter, a completion gives `Guilds:ContractXp` (20000) guild
  experience and `Guilds:ContractSilver` (100000) silver into the account (× tier / 4). `.guild contracts` shows them;
  a login tells the open ones.
- The account, the experience and the contracts are saved every 30 s and at shutdown (schema v18).


Commands (Player unless noted): `.g <text>`, `.guild info [name]`, `.guild create <name>` (default crest),
`.guild invite <name>`, `.guild accept`, `.guild leave [successor]`, `.guild kick <name>`, `.guild rank <name> <role>`
(`Recruit`, `Member`, `Guardsman`, `Officer`, `Warmaster`, `MasterOfCoin`, `TheRightHand`, `Guildmaster`), `.guild contracts`,
`.guild payout <name> <silver>`, `.guild list` and `.guild xp <guild> <amount>` (GameMaster), `.guild disband <name>` (Admin):
the same rules as the window, for when a dialog does not do what it should.

### Alliances

Guilds can join in alliances through the guild window's alliance tab (`../analysis/work/stageP/hideouts/SPEC.md` phases A1/A2;
`Alliances:Enabled`, true in both presets):

- **Create** with a name (3-30 latin letters, digits, `-`, `_`, single spaces) and a tag (2-5 latin letters and digits), both
  unique ignoring case. Only a guild master or The Right Hand (the ALLIANCE_MANAGEMENT right) creates, and the cost,
  `Alliances:CreateCost` (5000000 silver, gamedata), is paid from the **guild account** (official; the client says so when it is
  short). The creating guild is the founder: it alone disbands the alliance and cannot leave it while other guilds are in it.
- **Invite**: the founder guild's master or Right Hand invites a guild by name; the invitation is an "Alliance Invitation"
  mail (kind 2) in the invited guild's mailbox (its master and Right Hand read it), whose Accept / Decline buttons answer it.
  The founder can withdraw an open invitation in the member list (shown there as "(Invited)"). Open invitations are saved.
- **Kick**: the founder guild kicks any other guild; the founder guild itself cannot be kicked.
- **Limits** (gamedata): `Alliances:MaxGuilds` 100 guilds (open invitations count) and `Alliances:MaxPlayers` 30000 players in
  all its guilds; the latter also stops guild invitations and joins of its guilds (4251 / 4123).
- **Leave / disband**: a member guild leaves (its guild master or The Right Hand); the founder alone leaving disbands it.
  After founding or joining an alliance a guild founds or joins no other for `Alliances:ChangeCooldownMinutes` (10080,
  gamedata changecooldownminutes); the same time keeps it from rejoining the alliance it left [H].
- The alliance shows on the guild window and over every member: GuildUpdate kind 5 (keys 16/17), Join 79/80, NewCharacter
  51/52 and GuildMemberWorldUpdate 11/12. Whether the official nameplate key carries the alliance's name or its tag is open
  (capture R3): `Alliances:PlateText` `Name` (default, the SPEC's reading) or `Tag`.
- **Allies are friends**: in PvP an ally counts like a guild mate (black zones Friendly, red zones as the guild row of the
  official table), which is what the client draws once it knows the alliance. A guild that leaves (or is kicked, or whose
  alliance is disbanded) stays friendly with it for `Alliances:LeaveProtectionSeconds` (30, gamedata) [H].
  `Alliances:NoFriendlyFire` (pve true, official false, as `Guilds:NoFriendlyFire`) makes a Hostile ally (two factions in one
  alliance) Neutral. Spells with the `alliancemember` target condition reach allies.
- **Alliance chat**: the chat server's Alliance channel (type 25, `Alliances:Chat` true) for the members of all its guilds,
  joined and left with the alliance like the guild channel.
- The alliance window's member list (AllianceGetMemberInfos) and the alliance details (AllianceGetDetailInfo, by name or tag)
  are answered; the meaning of a few of their fields is inferred (SPEC §2.1-§2.2).
- Saved in `albionlocal.db` (schema v38: alliances, their guilds, open invitations and the cooldown history). A guild that is
  deleted leaves its alliance (the founder role passes to the oldest guild; an alliance left without guilds goes).
  Not there: alliance logs and stats (QueryAllianceGuildStats stays unanswered), the gold upkeep flag, territories.

Commands (Player unless noted): `.alliance info [name or tag]`, `.alliance invite <guild>`, `.alliance accept [alliance]`,
`.alliance decline [alliance]` (also the way to answer with `Mail:Enabled false`), `.alliance kick <guild>`, `.alliance leave`,
`.alliance list` and `.alliance disband <name or tag>` (GameMaster).

Not checked in the client yet: the invitation mail's text (its body is stored as `inviting guild|alliance name` [H] for the
client's "{0} wants you to join the Alliance '{1}'"), the meaning of AllianceGetMemberInfos keys 1/4/5/18 and
AllianceGetDetailInfo keys 2/3/8-12/20/21, the nameplate text (`Alliances:PlateText`).

### Guild territories and castles

The guild territories of the black zones (`../analysis/work/stageP/territories/SPEC.md`, phases P1-P5;
`Territories:Enabled` and `Castles:Enabled`, true in both presets): the data, the world presence, the claim, upkeep and season points
of the watchtower territories and the castle fights. Running a server of this version converts `albionlocal.db` to schema v41 (the tables
territory_owners, v39, and territory_state, v41: a territory's food, access and tower power; an older server build refuses the
converted file).

- **Data**: world.xml's 268 watchtower territories (170 resource, 98 farm) and 118 castle territories (23 castles, MEDIUMCASTLE, and
  95 castle outposts, SMALLCASTLE) in 269 clusters, with territorytypes.bin (claim cost 2 000 000, food, tower levels, base season
  points by cluster type), castles.bin, the timeregions of worldsettings (prime time = the timeregion's start, UTC, for 1 h; Europe
  US_22_00 20:00-21:00) and gamedata (1 000 000 guild fame, 15 s claim); the region is `Guild:Region` (europe). The season's
  seasontimes give the claimable windows (SEASON_34 Europe: day 1 18:00 to day 29 11:00, day 29 18:00 to day 57 11:00); a local
  season after the client's last one reuses SEASON_34's. All three region files filter those events by the same six timeregions.
- **World presence**: every watchtower territory's monolith (NewMonolithObject, bound to the landscape tower, key 7 the territory)
  and every castle territory's castle or castle outpost (NewCastleObject from its tile T1_CASTLE …, the castles.xml type, its phase,
  owner and chest) stand in their clusters.
- **Castle fight** (`Castles:Timers` "data" = castles.xml): the castle tile's guards stand (its basic guards and mortars; health ×
  `Castles:GuardScale`, pve 0.6). Hitting or killing one starts the fight; when all basic guards are dead the elite guards come
  (castle outposts after 1 min, castles at once), when those are dead the castle lord (outposts after 1 min), and his death opens the
  capture zone. A phase not won within 30 min fails the attack: the castle stands as before. A capture zone nobody claims for 300 s
  brings the lord back. The client hears each phase (CastlePhaseChanged 417: 0 at rest, 1 guards, 3/4 elites coming/standing, 5/6
  lord coming/standing, 7 capture zone, 8 the lord back; only 0 and 7 are decoded in the client, the rest is the server's numbering).
  No gate yet (the gate buildings are not spawned) and no GameEvent texts.
- **Capture**: the zone (12 m around the castle) belongs to the team that stands in it alone (a guild with its alliance; a player
  without a guild never claims; mounted players count as presence only): 16 s fill the claim, another team alone empties it in 8 s,
  an empty zone lets it run out in 30 s, two teams (or a defender) pause it. A full claim makes the guild the owner (saved, the
  members are told, the owner's name and crest on the castle; the old owner's guild is told it lost it); the owner's own team in the
  zone ends the fight and keeps the castle. Then the guards come back after 1 h (territorytypes GuardSettings). A disbanded guild's
  castles are unowned. The client shows the claim (CastleClaimProgress 513, the claiming guild's crest 514).
- **Chests** (`Castles:Chests`, `ChestSchedule`): at every respawn of treasuredistribution's castle schedule (every 2 h UTC 10:00 to
  22:00, the weekend's 18:00 always legendary; "hourly" = every full hour) the data's draw (castles 240/120/40/21 standard to
  legendary or nothing 579, outposts 400/200/70/21 or nothing 309) puts a TREASURE_CASTLE_* chest (loot of lootchests.xml) on the
  castle's treasure spot, for 30 min. With `ChestNeedsOwnership` (official) only owned castles get chests and only the owner guild's
  members open them; pve: every castle, anyone. The castle shows the chest (ChestState) and its rarity.
- **Claim** (op 302 at the tower, a 15 s channel, `Territories:ClaimChannelSeconds`): a member of a guild with the right Claim Territory
  (the master always) claims a free watchtower territory when the season has it claimable (`SeasonSchedule` "data"), in the zone's prime
  time (`PrimeTimeOnly`; not needed within the claimlimitoverrideminutes, 16 h, after the season opens the claims), with no Tower
  Guardian standing (`Invader`), not in the first 2 minutes after entering the zone, under `MaxPerGuild`, with the guild fame
  (`GuildFameRequirement` "data": 1 000 000 for the first and each further territory) and the cost in the guild account (`ClaimCost`
  "data": 2 000 000 silver, taken at the channel's end). Moving, damage or a knockdown break the channel. A refusal shows the client's
  territory error and a system line saying why. The tower then shows the guild's name and crest, the members get the territory in
  their guild list, and a hideout's tribute in that zone goes to the new owner.
- **Food** (`Territories:Food`): a claim stores 4000 food; one is eaten every 22.2 s (the storage of 38 880 lasts 10 days); anybody at
  the tower may feed it food items (items' nutrition; op 223). An empty store loses the territory (the guild is told). pve: nothing
  is eaten.
- **Give up and access**: a member with Give Up Territory lets it go (op 115); Territory Access changes the access setting (op 116;
  kept and saved, no shield exists yet).
- **Guild list**: GetMyTerritories (op 138) lists the guild's territories and castles with the food and tower power of each.
- **Season schedule** (`SeasonSchedule` "data"): the season's resetterritories (SEASON_34: day 1, 29 and 57 at 11:00 UTC) frees every
  territory, and castle (the season lists CASTLE), held from before it; spawninvader puts the season's Tower Guardian
  (T6_MOB_ARCANE_ELEMENTAL_ELITE_BOSS) at each free tower of a loaded zone, and claims wait until it is dead (a server restart brings it
  back until killed again). "off" (pve): always claimable, never reset, no Tower Guardian.
- **NPC holders** (`Territories:NpcOwners`, pve true): a free watchtower is held by the "Outlands Wardens": fortification guards
  (T4-T8_MOB_WATCHTOWER_SWORD / CROSSBOW / STAFF by the zone's tier, on the territory's guard spots nearest the tower, health ×
  `Castles:GuardScale`) stand at the tower; with all of them dead it is free to claim for 1 h, then they hold it again. A territory
  given up, starved or reset goes back to them. They pay and get nothing (no tribute, no season points).
- **Territory season points**: every `Territories:ScoreTickMinutes` (60; the official tick is not in the data) a held territory pays
  its guild's season the base points of its zone (territorytypes: black zone 1 60, 2 75, 3 90, 4 110, 5 130, 6 160) × the season's
  score multiplier (SEASON_34: 2 from day 29) × `Territories:SeasonPointsFactor`, while the season's territory scoring runs for its
  timeregion (SEASON_34: from day 1 18:00 to day 29 00:00 local and again from day 29 18:00; always with `SeasonSchedule` "off"),
  as Holding Territories; a tick in the zone's prime time pays `Territories:PrimeTimeBonus` (1) × as much again as Prime Time:
  Territory Bonus Points. They count for the standings, brackets and rewards like every season point (the Overview rows, 447). The
  NPC holders score nothing.
- **Tower power**: a territory's tower power points (0 at a claim; nothing but the GM raises them yet: power crystals are a later
  phase) set its tower level by the levels' pools stacked (level 2 from 9613, 3 from 24 609, … 8) and decay by the current level's
  pool over its decaydays (2 days at level 1 … 4.8 at level 8), level by level; the zone's players get TowerPowerPointUpdate (433)
  when they are set.
- **Castle season points**: `Castles:SeasonPoints` (0 = off, both presets: the official amount is not in the data) × `Territories:SeasonPointsFactor`
  per `Territories:ScoreTickMinutes` to the owner guild's season (the Overview's Territory row).
- **Territory window**: GetMonolithInfo (117) answers a held territory's food and tower level (0/0 otherwise), GetClaimInfo (118)
  the guild's fame (its members' fame since joining: PvE, PvP, gathering, crafting), the territories it owns and whether the season
  lets the territory be claimed now (the client adds its own prime-time check), GetTerritorySeasonPoints (120) only whether the
  reader's guild holds it (no points breakdown yet: tower level -1, the rest 0).
- **World map**: the territory clusters stay without map info (the client's map indexes keys 4-18 per territory without checks
  and no official sample confirms them): `Territories:WorldMapInfo` true sends them, one neutral entry per territory, every
  array of the same length (a self-test checks it). Try it only with that in mind.
- `Raids` and `Attacks` are read for the phases still to come; pve presets: 200 000 silver, no fame, any time, no food, no season
  schedule, no raids, NPC holders, castle chests for anyone, guards × 0.6.
- Commands: `.territory [id or name]` (the counts and rules, or one territory's cluster, owner, prime time and season window; a
  castle's phase, claim, chest and guards; a tower's food, power, NPC holder and Tower Guardian), GM
  `.castle capture|reset|chest|free|owner [guild]` on the nearest castle of the map, GM `.tower owner [guild]|free|food <n>|power <n>|beat`
  on the nearest territory tower.

Not checked in the client yet (captures T1, T3, T5): the monolith's keys 8-20 and 28 and the castle's key 11 are sent as their
neutral values (0, false); the castle's key 13 is the standing chest's rarity name ("standard" … "legendary", "" without); the
phase numbers other than 0 and 7; CastleClaimProgress keys 3-6 (the claim as a7m 0-10000 at the stamp, rate 0: an update every
change instead) and the crest order of 514 (read from bux.j's call: 2 schema, 3/4 colours, 5 symbol, 6 symbol colour, 7/8 scale and
offset); whether the client itself lets a non-owner open a castle chest (pve). GetMonolithInfo's two numbers and GetClaimInfo key 2
have no reader found (food and level, false), and the world map's territory order is world.xml's. Claim (capture T1/T2): the cancel
reason of an interrupted claim (341 key 1 12080; only 12085, the territory error, is decoded), GuildMemberTerritoryUpdate's keys (the
territory, gained), GetMyTerritories keys 4-6 (0) and its hideout keys 7-18 (left out), the access byte's meaning, the reading of
claimlimitoverrideminutes as "claims outside prime time", and whether the Tower Guardian stands at every free tower. Scoring (capture
T6): the tick length and the prime-time bonus, the tower level's reading of powerpointspool and decaydays, GetTerritorySeasonPoints
keys 1-8; no ReceivedGvgSeasonPoints (432) is sent for territory points (its key 2 is a mob type).

### Guild hideouts

A guild's hideouts in the black zones (`../analysis/work/stageP/hideouts/SPEC.md`, phases H1-H4; section `Hideouts` of
`config/world.jsonc`, the same in both presets). Running a server of this version converts `albionlocal.db` to schema v40 (the tables
hideouts and hideout_homes; an older server build refuses the converted file).

- **Placement** (H1): the kit UNIQUE_HIDEOUT (items.xml hideoutitem, sent as a simple item) is placed like a placeable building: the
  client's confirmation sends PlaceableObjectPlace (69) with the kit and the hideoutbuilding's type (decoded in the client, not
  ConstructionSiteCreate). The rules of the data: a zone whose clustertype allows hideouts (OPENPVP_BLACK_1-6; the Roads' hideout
  tunnels), with a territory (`RequireTerritory`), a guild member whose role has Manage Hideouts, during the zone's prime time
  (the timeregion's start + 60 min, `RequirePrimeTime`, `PrimeTime`), at most 6 hideouts per guild (`MaxPerGuild`) and one per zone,
  180 m from another hideout (60 m in a tunnel), and `BlockRadius` (50 m, a choice: the data has no radii) from a territory's monolith
  (castles twice that), a zone exit and an outpost. After the kit's 15 s the kit is used and the hideout stands; a refusal is a system
  line with the reason (the client's own CanBuild texts are named in it) and the kit stays.
- **Construction** (H1): hideouts.xml's phases as timers: 1200 s, then invulnerable until the zone's next prime time, then 1200 s; then
  the hideout is built at level a (HIDEOUT-0001a, 2 shields) with its starting food (15 000). `ConstructionSeconds` (a number) makes
  the whole construction that long. Attacks are not made (`Sieges` false): no phase can be lost. The guild gets a mail at the start
  and at the end (plain text).
- **Surface object**: NewHideoutObject (419) with the owner guild and alliance, key 13 the level index, 14 the shields left, 15 the
  mode (1 construction with 17 the phase and 18 its end, 2 established), 21/22 the durability bar; sent again to the zone at every
  change. AccessStatus: the preset (closed, private, public) with the owner guild's role.
- **Interior and entry** (H2): a built hideout's interior is the instance `@HIDEOUT@<guid>` built from the world.xml cluster of its
  level (HIDEOUT-0001a: bank, repair shop, travel point; 0001b/c add the market, the farming merchant, the season store, meld
  building, vanity store). ExitEnterStart (237) on the surface object channels hideouts.xml enterchanneltime (4 s, 253/255 to those
  in view; a move, damage or knockdown breaks it with 254), then the push {0 the instance, 1 the level's cluster, 2 the guild's
  name}; the arrival is the exit's spawn point inside. Inside: NewHideoutManagement (420, key 1 the instance id) with
  HideoutManagementUpdate (425: level, food, tribute, a running upgrade) and NewHideoutExit (421); the exit leads out the same way
  to the zone, 12 m in front of the hideout. A ChangeCluster on either object goes at once. Access by the preset
  (accessrights.xml): private (the default) = the owner guild, public = everyone, closed = the guild master and the members with
  Manage Hideouts; none while under construction or being upgraded. The alliance has no right of its own in the presets: allies
  enter a public hideout. A login inside a hideout that is gone puts the character in front of it (without its row: in its last
  city). HideoutGetOwnerInfo (399) answers the owner guild's name. A disbanded guild's hideouts go, players inside are put out.
- **Services** (H3): the bank inside opens the hideout's **own vault** (`BankVault` "Own": the location `@HIDEOUT@<guid>`, a vault no
  city bank opens, and no hideout bank opens a city's; the per-city logistics rule). `BankVault` "City" opens the nearest city's
  vault instead (by world.xml exits, its "Bank of …" interior). Under `Bank:Shared` it is the shared vault like every bank. The
  market of levels b and c (MARKETPLACE_HIDEOUT) is a market of its own location (the instance id), the repair shop an ordinary
  one. **Make Home** (MakeHome 205 on the management; hideouts.xml makehome: levels a/b the owner guild, c also its alliance) and
  Leave Home (206): saved, Join key 67 carries the home (the instance id), UpdateHome (216) tells the client at once. A death
  respawns inside the home hideout (`RespawnAtHome`; not while it is being upgraded, not from a mists zone), and the travel
  window's home entry (BuyJourney kind 1) is the **Journey Back** into it (to its travel point; 8047 while it is being upgraded).
  The access preset is changed by AccessRightsChangeSettings (198) on the hideout or its management by the guild master or a
  member with Manage Hideouts (custom roles are read as the nearest preset); the AccessStatus goes out again.
- **Food, upgrades, tribute** (H4): HideoutFillNutrition (397) on the management takes food items of the inventory (items.xml
  nutrition, the client's own test) into the store, as many as the level's storage takes (150 000 / 400 000 / 750 000). At each
  prime-time start of the zone (`Upkeep`) the level's dailyupkeepnutrition (15 000 / 20 000 / 25 000) is eaten; a day with too
  little food empties the store and costs a shield (the guild gets a mail), a fed day gives one back up to the level's
  (shieldregenerationrate 1). Attacks are not made, so a starving hideout only loses its shields (no destruction yet, phase H5).
  UpgradeHideout (394) by a member with Manage Hideouts: the next level's silver (20 M / 80 M) from that member and its resources
  (GvG tokens, T1 wood and rock, T6 stone blocks) from its inventory, then its time (48 h / 96 h); nobody enters meanwhile, at the
  end the players inside are put out and the next entry builds the new level (HIDEOUT-0001b/c, with the market and more); the
  return code is the client's UpgradeResult (2 already upgrading, 3 last level, 4 not the owner guild, 5 no permission, 6 not
  enough silver, 8 not enough resources). HideoutSetTribute (400) by the guild's managers (else 16000 NoPermission) sets the daily
  tribute, paid at the prime time from the guild account (at most what it holds) to the guild owning a territory of the zone (the
  first owned one: today the castles; watchtowers once they can be claimed); nothing without an owner. GM `.hideout day` runs a
  day's upkeep and tribute now.
- Commands: `.hideout list [all]` (your guild's hideouts; a GameMaster: all), GM `.hideout finish` (the construction or upgrade of
  the hideout you are in or the nearest one ends now), GM `.hideout place` (a hideout of your guild where you stand, no kit, no rules), GM `.hideout day` (a day's food and tribute now).

Not checked in the client yet (no capture holds a hideout, SPEC §9 P1/P5): NewHideoutObject keys 10, 12, 16, 19, 20, 23, 24, 26-29 are
sent neutral (left out: 0, false, ""), key 11 is the placement time [H], key 22 the durability's stamp [H]; the mode values 0 and 3
of key 15 are not used. The way in is ExitEnterStart (237) on the hideout [H, SPEC Q6] and the way in's ChangeCluster push carries the
guild's name as key 2 [H]. NewHideoutManagement key 5 is the surface position [H], keys 6 and 8-13 neutral; HideoutManagementUpdate
keys 4, 5 and 11-14 neutral. MakeHome, LeaveHome, AccessRightsChangeSettings, HideoutFillNutrition and HideoutSetTribute answer the bare
return code (0 or the codes above), UpgradeHideout the UpgradeResult as its return code [H]. HideoutGetInfo (398) keeps the server's default empty
answer, as before (its keys are not decoded). Not made yet: destruction and recovery (H5), power cores (H6), sieges (H7), HQ, boosters, moving and the
world map's hideout keys (H8); outlaws and the static-dungeon rule are not checked at the entrance.

### Guild goals: the Guild Challenge, weekly goals, ranking and guild might

The guild's season, weekly goals, ranking and might (`../analysis/work/stageI/guild-goals/SPEC.md` phases 1 to 4; `Guild` in
`content/goals.json`). Running a server of this version converts `albionlocal.db` to schema v25 (one column of guild goals
on the guild row; an older server build refuses the converted file). With the Roads of Avalon (v26), the Mists (v27) and the island
animals (v28) merged in, the conversion goes on to v28.

- **Guild Challenge** (guild window → the Guild Season window, its Guild Challenge tab): every Adventurer's Challenge point a
  member earns while in the guild (kills, harvests, catches, boss bonus, credited PvP; see Goals) gives its guild
  `ChallengeFactor` challenge points (pve 20, official 1), a craft `CraftChallengeFactor` (0.002, both presets, as the SPEC)
  per fame point (the personal season pays nothing for crafts); fractions add up from gain to gain. The tab shows the guild's level (the client's own table of 99
  levels: level 2 at 100 000 points, 3 at 300 000, 11 at 5.5 M, 99 at 485.1 M), the bar to the next level and the members
  by the points they gave (a member who left drops out of the list; the total keeps his points). The client refreshes it
  every few minutes, at once after a level-up. Each level reached gives the guild its season points (20 per level from
  level 2, at most 300), a guild line `[Guild] Guild Challenge level N reached: +S guild season points`, the client's
  "New Guild Challenge Level reached!" popup to the members in game and the journal's GUILD achievements (contribute N
  challenge points, reach guild challenge level N; the season-points ones of GUILD_GUILD_SEASON now follow the guild's
  season points, not the personal total: the progress saved from the personal total is dropped once at the first login with
  this version, achievements already done that way stay done). Members who were offline get the journal's level and points at
  their next login.
  The window's header shows the guild's rank among the local guilds with season points (a tie goes to the guild that reached
  the score first; `.guild season` shows the same rank) and its points (bracket iron
  1000, bronze 7500, silver 25 000, gold 80 000, crystal 250 000).
- **Seasons**: the client's own guild seasons (`Region` europe: SEASON_34 runs to 2026-11-09 11:00 UTC; the client data has
  no later one). Afterwards the server keeps local seasons of `LocalSeasonDays` (28) days with the same table, shown by
  `.guild season` and in chat only: the client's Guild Season window has no season then and is expected to fail to open
  (its own data, not the server; Member Logs and everything else keep working). A new season starts at level 1 and 0 points
  (a line to the members in game).
- **Weekly goals** (`Weekly`: pve true, official false): every Monday at `Bounties:ResetUtc` (10:00 UTC) a guild gets
  `GoalsPerWeek` (3) goals, one from each of the `Pools` drawn (pve: kill 300 T4+ mobs, defeat 3 bosses, clear 2
  dungeons / expeditions / world events / corrupted dungeons, a random dungeon once per run whatever its floors; pvp: 5
  credited PvP kills in a zone with PvP points, 20 000 credited PvP fame with the same filter, never rolled where PvP gives no
  fame, as in the pve preset without lethal PvP and with `Pvp:KnockdownFameFactor` 0; gathering: 1500 T4+ resources, 30 fish; crafting: 50 000 crafting fame), the same for a guild and week after a
  restart. Targets are per active member (seen within `ActiveDays` 7) × members^`TargetExponent` (0.8), two digits; mob and
  resource goals take the tier the members played at last week when it is higher. Every member's activity counts (a shared
  kill once per member); PvP counts only what the PvP goals credit (no refused kill, no assist, no duel; bots with
  `CountBotKills`). Progress is told at each quarter. A finished goal pays once: `GoalSilver` (100 000 × tier / 4) into the
  guild account, `GoalXp` (5000) guild experience, `GoalSeasonPoints` (150 × `SeasonPointsFactor`: pve 5, official 1) guild
  season points (the "Guild Challenge" row of the season window), and a **guild blessing** step for next week: every
  member gets `BlessingFame` (+5 %) PvE fame, `BlessingSilver` (+5 %) mob silver and `BlessingYield` (+3 %) gathering and
  fishing yield per goal finished the week before (at most `MaxBlessing` 3), on top of the guild level's perks. The client has
  no window for them: `.guild goals`, the lines at login and at each change.
- **Member Logs** (guild window → logs → Member Logs): PvE, PvP, gathering (with fishing and crops) and crafting fame, the
  silver tax paid into the account and the silver balance (tax paid less payouts received) per member for This Week, Last
  Week and All Time; the other columns stay 0.
- **Boss kills** (only with `Guilds:BossSeasonPoints`, pve true, official false): a boss killed by guild members gives their guild `BossSeasonPoints` (world boss 20, boss 5, miniboss 2)
  × `SeasonPointsFactor` season points, split between the guilds credited (rounded), and every member credited the client's
  floating "+N Season Points" with its chat line naming the boss and the guild.
- **Ranking**: the season window's Rankings tab lists the guilds of this server with season points (rank, crest, points;
  ties go to the guild that got there first), its Overview the points by activity ("Guild Challenge" = challenge levels and
  weekly goals, "Killing Mobs" = bosses, "Might: …" = might levels). `.guild ranking` shows the top ten in chat.
- **Season history and participation** (`../analysis/work/stageP/territories/SPEC.md` phase P2): the Rankings tab's history of a
  guild (GetGvgSeasonHistoryRankings: every past client season it has points in, with its rank and points), the members' history
  of a season (GetGvgSeasonGuildMemberHistory: their challenge points and might; this client never reads it), and the reward
  tab's participation (GvGSeasonPlayerGuildParticipationDetails: the guild, its points and rank, and the member's participation
  shown as time: this server counts none, so the bar is the member's share of the `MinParticipation` rule above, full when the
  member qualifies). At every Join of a client season GvgSeasonUpdate carries the previous season's winner among this server's
  guilds (name, alliance, crest, points, seasons won) for the client's season winner window; without one only the season keys.
  The Overview's territory rows (TerritoryHold, TerritoryConquer, TerritoryPrimeTime) get points once territories score.
- **Season end** (the silver and items only with `Guilds:SeasonRewards Content`, the pve preset's; official: none): when a season ends (or at the first start after a downtime across its end) each guild with points gets,
  once, `BracketSilver` of its bracket (iron 0.5 M, bronze 1 M, silver 2 M, gold 4 M, crystal 8 M silver) into the guild
  account, and every member who gave something and at least `MinParticipation` (51 %) of the members' average, in Guild
  Challenge points or in guild might, gets `BracketItems` (iron 10 T4 crystal shards, bronze 20 T4, silver 20 T5, gold 30 T5, crystal 50 T6; `{ "bracket": {
  "ITEM": count } }`) and the journal's bracket achievements (GUILD_GUILD_SEASON: the bracket and the ones below); a member
  offline at that moment gets them at its next login, also after a restart or after leaving the guild. Items that do not
  fit come by mail (`Mail:RewardOverflow`) or with `.bounty claim`. The guild gets a line with its rank and bracket,
  everyone in game the top three. The new season starts at 0. When a guild is disbanded (or its last member leaves) with
  rewards still owed, a member in game gets them at once and the others the items by mail (with mail off they are lost).
  The bracket names in `goals.json` are lowercase (`iron` … `crystal`); an unknown item is skipped with a warning.
- **Guild might** (the season window's Guild Might tab, from the Conqueror's data of the client): every member's might
  goes to the guild's category: PvE kill fame × 0.0016 (a third in a random dungeon without a party) to PVE, gathering and
  fishing fame × 0.017 to GATHERING, a finished corrupted dungeon its objective's might (Stalker 148.41, Slayer 180.88) to
  CORRUPTED, nothing in orange-warning zones; all × `MightFactor` (pve 10, official 1; 0 = none). The other categories of
  the tab (castles, hellgates, power cores …) have no source here and stay 0. The tab shows each category's level from the
  client's own table (PVE level 1 at 20 000 might, 2 at 60 000 …) and, on a click, the members by might. Each level gives
  the guild its season points (PVE and GATHERING 200, CORRUPTED 50; not × `SeasonPointsFactor`: the client's popup shows
  the table's value), a guild line and the client's might level popup. The personal Conqueror's Challenge window (op 451)
  is still not served: the might exists only on the guild.
- Commands: `.guild season` (season, level, points, bracket, rank, points by activity, contributions, might),
  `.guild goals`, `.guild ranking`; GameMaster `.guildgoal challenge <points> [guild]`, `.guildgoal points <n> [guild]`,
  `.guildgoal might <category> <might> [guild]`, `.guildgoal done <n> [guild]` (with its reward), `.guildgoal roll [guild]`,
  `.guildgoal season end` (every guild's current season closed and paid now as at its end, then on from 0: a test of the
  season-end rewards before the real end).
  `Enabled false` gives empty answers (no rank, no rankings, no challenge or might data, Member Logs zeros) and no rewards.

Not checked in the client yet: the Guild Challenge tab's bar and list, the level popup, the header's rank, the Member Logs
values, the journal's GUILD progress display, the Rankings tab and its crests, the Overview's activity rows, the boss kill's
"+N Season Points" and chat line (whether the boss name resolves), the Guild Might tab, its member list and the might popup,
the season-end lines and the journal's bracket rewards (`.guildgoal season end`).

## Personal islands

Phases 1-3 of `../analysis/work/stageH/islands/SPEC.md` (PVE-DESIGN step 14): an owned personal island, the trip there and
back, a login on it, buying and upgrading at the island merchant, the showroom, and farms and herb gardens with their crops.
Animals (phase 4) and building farm buildings from the build menu (phase 5) work; houses, furniture and labourers come later.

- **Getting one.** The island merchant (the NPC at the harbour of Thetford, Lymhurst, Bridgewatch, Caerleon, Martlock or
  Fort Sterling) sells the island of its city: the first personal island costs 20,000 silver, every further one (another
  city) 1,000,000, as worldsettings.xml prices them; `Islands:FreeIslands true` makes buying and upgrading free. The window
  offers "Buy" only with 7 premium days: the server sends `Islands:PremiumDays` (31, Join key 91; 0 = none, no buying) to a premium
  character, and with `Islands:BuyWithoutPremium` (true in both presets for now) to everyone: until premium can be had for gold,
  everyone may buy and upgrade islands. The strict official rule is `false` (islands for premium characters only).
  With `Islands:StarterIsland` (pve true, official false) a character that owns no personal island gets one free when it
  opens the merchant, at `Islands:StarterLevel` (0 = the smallest, template `…0001a`; up to 5 = `…0001f`), a PvE choice
  beyond the SPEC. A GM gives one with `.island give [city] [level]` (one per character and city).
- **Upgrading.** The merchant's Manage view upgrades the island one level for the price of that level (the first time any
  of your islands reaches a level it costs less: level 1 500,000 then 2,500,000, … level 5 2,000,000 then 8,000,000). For
  130 s it is "upgrading": nobody can travel there, players on it are sent to the city ("kicked because upgrading"), and the
  next visit loads the bigger template. The favourite and hidden marks of the merchant's list are kept.
- **Showroom.** "Showroom" at the merchant travels to the city's showroom island (`ISLAND-PLAYER-<biome>-0001e-SHOWROOM`;
  none for Caerleon); its travel point leads back to the merchant's city.
- **Going there.** The merchant's window lists the island with "Visit": it loads the island (the minimap shows the
  owner's name) and the character stands at its harbour (180, 56 on levels 0-2). The island is a private instance
  `@ISLAND@<guid>` built from the template of its level: the training dummies, the T1 wood, rock and hide, the fishing
  spot, the farming merchant (seeds), the bank (it opens the home city's vault: Lymhurst's island bank the Lymhurst bank's)
  and the island management (its window opens; nothing can be built from it yet). No PvP on islands.
- **Farming.** A farm (`T1_FARMHOUSE`: carrots, beans, wheat, turnips, cabbages, potatoes, corn, pumpkins) or a herb
  garden (`T1_HERBGARDEN`: agaric … yarrow) has 3 × 3 slots 6 m apart around its centre. With `Islands:StarterBuildings`
  (pve true) a new island gets one of each (at (145, 105) and (145, 135)), and so does an island without any building
  when its map loads (one given before farming existed); otherwise the player builds them (below) or a GM
  (`.island build T1_HERBGARDEN [x z]`). Planting a seed from the inventory into a slot takes 1 s (dismounted first); a
  crop grows for 22 h (`<harvest growtime>` 79200 s) divided by `Islands:GrowthSpeed` (pve 4: 5.5 h; the client's bar runs
  at normal speed between the server's updates, which come every 5 min while the speed is not 1 and when the crop is
  grown). Watering once with focus (the seed's 1000 × `Islands:WaterFocusFactor`, pve 0) makes the seed come back for sure
  (`Islands:FocusSeedReturn`, the SPEC's guess for the watering bonus); otherwise the seed returns with its chance (0 for
  carrots, 87 % for T6 herbs). Watering, harvesting and destroying need the player within 12 m of the crop. The harvest gives the seed's loot list (T6 foxglove 3-6 and sometimes a worm) and 100
  farming fame (× `Rates:Fame`; the destiny board's farming nodes). A crop can also be destroyed.
- **Animals.** A pasture (`T1_PASTURE`: chickens … cows, horses, oxen, giant stags) or a kennel (`T1_KENNEL`: cougars,
  direwolves, direboars, swamp dragons, direbears), built from the build menu (below) or by a GM (`.island build T1_PASTURE [x z]`), takes
  baby animals from the farming merchant into its slots the same way. A baby grows only while it has food: feeding it
  (the client's feed button) takes crops (`foodcategory` plants; meat for the kennel) at 48 nutrition each (the favourite
  twice that) up to its `nutritionmax`; a full feed lasts its whole growtime (T3 horse: 10 crops, 44 h, ÷
  `Islands:GrowthSpeed`). A grown baby is collected: the grown animal (`T3_FARM_HORSE_GROWN`, the stable's mount
  resource), the baby back with its offspring chance (84 % for the T3 horse; more after a focus care), and its fame. A
  grown chicken, goat, … placed in a pasture eats too and gives its product (eggs, milk) once per production time.
- **Building (phase 5, `../analysis/work/stageN/islands5/SPEC.md`).** On the own island the client's build menu places a farm
  (`T1_FARMHOUSE`), herb garden, pasture or kennel (the four buildings.xml farm buildings) on a free plot: the island template's
  buildable ground (tiles.xml BuildingAndFertile), 2 x 2 cells of 10 m per building, so level 0 has 1 plot (centre (185, 115)) and
  level 5 16. There is no construction site: the building's `placecost` in silver (50-100) and its materials (pasture: 30 T1_WOOD,
  30 T1_ROCK, 30 T3_PLANKS, 30 T3_STONEBLOCK; kennel: 50 T6_WOOD, 50 T6_ROCK, 10 T6_METALBAR) are taken from the inventory at once and
  the finished building stands there; a refusal says in chat what is missing. Giving a building up (the building's give-up button)
  removes it with its crops, nothing comes back (destructionreturnfactor 0); not while an animal stands in it. Houses are not built yet.
- **Picking up.** A placed animal (baby or grown) is picked up again from its window ("Pick Up"): the item goes back into the
  inventory, its growth and food are lost (the client warns). Seeds cannot be picked up. A hungry animal only stops growing.
- **Yield.** An island's crops and animal products listed for its home city in farmingmodifiers.xml give 10 % more (Lymhurst:
  carrots, burdock, goose eggs, pumpkins), times the premium factor (now for products too). Collecting a product with the client's
  "take all" setting on (the default) collects every ready animal of the same kind in that building.
- **Houses and labourers (`../analysis/work/stageN/labourers/SPEC.md`).** The build menu offers the player house
  (`T2_PLAYERHOUSE`: 100 silver, 30 T1 wood, 3 T1 stone, 180 T2 stone blocks) on a free plot; the house's Upgrade
  (renovation) raises it a tier (T3 … T8, each tier's silver and resources taken at once) for more labourers (1 / 2 / 2 / 3)
  and furniture (5 / 7 / 10 / 14). Beds, tables and trophies (any indoor furniture item) are placed inside the house from the
  inventory and picked up again from their window. A labourer is hired from the house's "Hire labourer" menu (a T2 one,
  1000 silver) or by placing a labourer contract (`T4_LABOURER_CONTRACT_WOOD`: a T4 lumberjack, the contract is used up)
  inside the house. It settles in for 30 minutes, then takes a full journal it accepts (its tier or lower, its profession or
  the trophy journals) and comes back after 22 hours with the journal's loot: about `baselootamount` picks of its loot list
  (a T4 lumberjack's journal 48 T4 wood, some enchanted) times the yield. The yield depends on its happiness: a bed and a
  table of the journal's tier (and trophies of its profession) give 100 %, more up to 150 %, less below (under 50 % it does not
  start). Happiness moves towards what the house's furniture gives by 100 every 6 minutes. Each loot teaches the labourer
  fame; with enough it advances a tier ("Advance to Tier"). It also sells empty journals at the journal merchant's price.
  A house with labourers or furniture in it cannot be given up. Labourers are saved in schema v33 (`island_labourers`);
  furniture is an `island_farmables` row of its house.
- **Coming back.** The harbour's travel point on the island offers the home city; the journey is free and ends at the
  city's harbour travel point.
- **Logging out** on the island logs in there again. A character whose island is gone (deleted, or `Islands:Enabled`
  false) logs in at the harbour of the island's city (else its last city; an interior such as the bank goes to its city).
- Islands are saved in `albionlocal.db` (schema v19 `islands`, v20 `island_buildings` and `island_farmables`, v28 the animals'
  nutrition and growth: crops keep
  growing while the server is down); their objects are made anew whenever the island's map loads. `.island level <0-5>`
  changes the level at once (players on it are sent to the city, the next visit loads the new template; no 130 s upgrade
  time; refused while the owner's journey onto it is under way). `Islands:Enabled false` turns them off: the merchant's
  list is empty and every island trip is refused (the client's "journey not possible").

Commands (GameMaster): `.island give [city] [level]`, `.island level <0-5> [city]`, `.island list [all]`,
`.island go [city]` (onto the island without the window); on the own island: `.island build <building> [x z]` (a farm
building, its centre at x z or here), `.island unbuild` (the nearest one within 15 m, with its crops), `.island farms [city]`
(buildings and crop growth), `.island ripen [city]` (every crop grown now).

Not done: construction sites (materials are taken at once), house repairs and skins, decorative furniture use (chests), the
labourers' loot container window (the loot goes straight into the inventory), city plots, the rabbits of the island templates (world.xml `<mobcounts>`; the server spawns no mobcounts, so the SPEC's
`Islands:ExtraMobCounts` would need that first), guests on islands, guild
islands. Not yet checked with the client (SPEC §1.8 C1-C6, §2.6 C7-C11, §3.5 C12-C15): "Visit" / "Buy" in the merchant
window, the island's loading and map name, the way back, the login on the island, the island bank and farming merchant,
the island management window; other effects of Join key 91 (premium UI elsewhere?), the purchase and the "upgrading"
state, the showroom trip; the buildings standing on the island's ground at level 0, a planted seed shown at once, the
growth bar, the watering animation and focus, the harvest. Only the owner may visit (no guild islands, no visitors).

## GM commands

Commands start with a `.` and are typed either into the game's chat or into the server console:

- **In game:** write the line with the chat input channel set to **Say** (local chat), e.g. `.item add T4_ORE 5`,
  or from any channel as `/say .item add T4_ORE 5`. A Say line that starts with `.` and a letter is a command, not chat; the replies appear in the chat as system lines ("info" or
  "error"; also logged as `[game] reply: …`). A command typed into another channel (Local, Global, Guild, …) runs the
  same way and is never shown to that channel (Chat above). A command runs at the level of the speaker's account (Accounts above).
- **Server console:** type the line where the server runs (the leading `.` is optional) and press Enter; it runs at
  the next world tick as Admin and the reply is logged as `[console] …`. It acts on the only player in game; with
  several, put the character name first: `LocalHero .gps`. A server started in the background has no console input.

| Command | Level | What it does |
|---|---|---|
| `.help [command]` | Player | the commands you may use, with their arguments (`.help item`) |
| `.save` | GameMaster | saves every player in game now |
| `.who` | GameMaster | the players in game, one per line: object id, cluster, position |
| `.gps` | Player | cluster and position of the player |
| `.friends [name]` | Player | your friends (online or not), open friend requests sent and received, and muted players; a GameMaster may name another character |
| `.chat` | GameMaster | the chat server's connections (character, channels joined, logged out) and the channels that have members |
| `.mail <name> <subject> \| <text>` | GameMaster | a server notice into the mailbox of the character (any case, offline too), e.g. `.mail anna Welcome \| See you in Thetford` |
| `.mail item <name> <item> [count=1] [quality=1]` | GameMaster | a server notice with the item attached (equipment one attachment per piece), to be taken out in the mail window |
| `.go <x> <z> [facing=0]` | GameMaster | moves the player within its cluster to (x, z); the others in view see it jump there (not yet tried in the client) |
| `.near [count=20]` | GameMaster | the world objects in view of the player, nearest first: kind, type, position, distance (and whether the client has been told); resource nodes with their charges and enchantment; mobs with type id, tier, health and spawn key |
| `.exits` | GameMaster | the exits of the player's cluster, nearest first: object id, exit Guid, position and trigger square, target cluster, arrival point and facing (a target not resolved yet is resolved in the background: ask again) |
| `.tele <cluster> [x] [z]` | GameMaster | moves the player to a world.xml cluster (e.g. `.tele 0006`, `.tele 0000 0 0`) through a loading screen, at (x, z) or the cluster's spawn point (respawn point, travel point, else the first exit's arrival point); islands, expeditions and instanced clusters are refused |
| `.rd [exit\|chest\|close] [spawnpoint]` | GameMaster | `.rd T4_PORTAL_ROYAL_SOLO` (a RandomExit or FixedExit of randomdungeons.xml) puts a random dungeon entrance 3 m in front of the player (it goes after `GmExitSeconds` unused); `.rd chest [HERETIC_SOLO_LOOTCHEST_STANDARD]` a loot chest of that lootchests.xml spawnpoint (once a dungeon floor with chests has been made); `.rd close` closes the dungeon the player is in (everyone inside is put out); `.rd` lists the cluster's entrances and the open dungeons |
| `.ops unhandled` | GameMaster | operations the client sent that have no handler yet, most frequent first; those answered from `content/opcode-stubs.json` on their own lines ("stubbed") |
| `.harvest info` | GameMaster | the harvestable nearest to the player: type, tier and enchantment, object id, position and distance, for a resource node its prefab and node tile, charges and the next regrowth step, what a harvest gives and needs, the harvest under way |
| `.mob spawn <mob> [count=1]` | GameMaster | puts `count` (1–50) mobs of a mobs.xml uniquename (e.g. `T4_MOB_UNDEAD_SOLDIER_VETERAN`, any case) on a ring 3 m around the player; they belong to no spawn point and never respawn |
| `.chest [list\|spawn\|unlock] [spawnpoint]` | GameMaster | `.chest` lists the cluster's open-world chest spots (kind, chest, ChestState), nearest first; `.chest spawn STATIC_UNDEAD_VETERAN_POI` a chest of that spawnpoint 3 m in front of the player (it never respawns); `.chest unlock` unlocks the nearest chest within 20 m (a camp chest for the player alone) |
| `.quest local` | GameMaster | the player's camp quests on this cluster: fame of the target, done, inside the circle |
| `.boss list [all]` | GameMaster | the world boss slots of the player's cluster (`all`: of every cluster since the start): boss, mob, place, schedule, factors, standing with its health or the next spawn time |
| `.boss spawn <boss> [here]` | GameMaster | `.boss spawn WORLD_BOSS_T5_KEEPER` brings the boss of that slot of the cluster now (announced); with `here`, or a boss or mobs.xml mob the cluster has no slot of, a one-off boss 5 m in front of the player (bosses.json factors; never back) |
| `.boss kill` | GameMaster | despawns the nearest world boss of the map (no corpse, loot or fame); its slot comes back at its next time |
| `.event list` | GameMaster | the running world events (kind, place, wave, mobs left, time left, players and damage) and when the schedule starts the next one |
| `.event start [kind] [tier] [here]` | GameMaster | starts a world event of `content/events.json` (`invasion`, `champion`; the first by default) at a mob spawn point near the player, or 10 m in front with `here`; announced as a scheduled one; a tier 1-10 gives it that row of `Tiers` (mobs, waves, rewards) |
| `.event stop [id]` | GameMaster | calls off the world event of that id (default: the one on the player's map): its mobs go, no reward |
| `.invasion` | Player | your invasion status: the tier your tickets start, the cooldown, today's starts, the event on your map |
| `.invasion <1-10> [invasion\|champion]` | Player | calls an invasion (or a champion hunt) of that tier 15-45 m from you (open world only; cooldown 10 min) |
| `.invasion tier <1-10>` | Player | the tier your next invasion ticket (corrupted dungeon map) starts |
| `.invasion stop` | Player | calls off the invasion you started (no reward; the cooldown stays) |
| `.invasion tiers` | Player | the T1-T10 table: mob tier, buff, extra waves, chest, loot, silver, fame, tickets |
| `.bounty` | Player | the daily and weekly bounties with your progress, your Adventurer's Challenge points, today's PvP challenge points (of `Pvp:DailyPointsCap`) and the PvP credits refused today by reason |
| `.guild season`, `.guild goals`, `.guild ranking` | Player | your guild's season (Guild Challenge level and points, season points, bracket, rank, might), its weekly goals with the blessing and the guilds by season points (see Guild goals) |
| `.guildgoal challenge <points> [guild]`, `.guildgoal points <n> [guild]`, `.guildgoal might <category> <might> [guild]`, `.guildgoal done <n> [guild]`, `.guildgoal roll [guild]`, `.guildgoal season end` | GameMaster | Guild Challenge points, guild season points, guild might of a category, a weekly goal finished with its reward, the week's goals rolled again, the current guild season ended and paid now |
| `.exp list` | GameMaster | the expedition registrations (state) and the live instances (members, step and task counts, time left) |
| `.exp start <index\|uniquename> [level] [players]` | GameMaster | sends the player into a new instance of that expedition (`.exp start 3` = T3_EXP_STD_HERETIC_MINE; `.exp start 20 5 2` = a hardcore level 5 scaled for 2), the way back where it stands |
| `.exp finish` | GameMaster | completes the player's expedition now (the rewards and the finish exit) |
| `.exp leave` | GameMaster | takes the player out of its expedition to its way back |
| `.exp level <n>` | GameMaster | opens the hardcore expedition levels 1-n for the player (the ladder) |
| `.cd` | Player | your corrupted dungeon infamy and its peak, the reward bonus per rank, your runs and completions, the run you are in |
| `.cd leave` | Player (`Corrupted:LeaveCommand`) | takes you out of your corrupted dungeon to where you cast the entrance (not knocked down or in combat) |
| `.cd enter <1-3> [map]` | GameMaster | sends the player into a new corrupted dungeon (1 Hunter, 2 Stalker, 3 Slayer) on `CORRUPT-0nn` (or its number; default 1), without the entrance or its gates, the way back where it stands |
| `.cd finish` | GameMaster | completes the player's corrupted dungeon now (infamy, rewards, the boss chest, the exit portal) |
| `.cd infamy <n>` | GameMaster | sets the player's corrupted dungeon infamy and its peak (Slayer opens at 100 000) |
| `.cd list` | GameMaster | the live corrupted dungeon runs (owner, rank, map, points, demon, time, streak, shrine, invasion) |
| `.cd shrine` | GameMaster | uses the Demonic Shrine of the player's corrupted dungeon now: the consent to invasions (window and range not checked) |
| `.cd invade [bot\|player]` | GameMaster | an invasion of the player's corrupted dungeon now: a bot invader (default), or the consenting player of its rank who waits longest |
| `.hg mode [name\|1-4]` | Player | the hellgate modes (team, item power, points, "vs bots") and yours; with a name or id it sets your mode, the one the fixed shrine uses |
| `.hg leave` | Player | takes you out of your hellgate to the shrine (`Hellgates:LeaveCommand`, pve; a GM always; not knocked down or in combat) |
| `.hg enter <1-4> [map]` | GameMaster | sends the player into a new hellgate of that mode on `HELLGATE-NN-...` (or NN of its mapset; default a random one), without the shrine, channel or party, the way back where it stands |
| `.hg finish` | GameMaster | advances the player's hellgate: in state 1 the points are done (the guardian or the enemy team comes), in state 2 the enemy is beaten (the objective chest; a match: the second party loses), in state 3 the reward is claimed (the exit portal and the chain exit) |
| `.hg close` | GameMaster | closes the player's hellgate now (everyone inside out to the way back) |
| `.hg list` | GameMaster | the shrine channels and locks under way, the live hellgates (mode, map, members, state, points, enemy bots or the two parties, time, chain, restricted area) |
| `.hg infamy` | Player | your hellgate infamy (2v2, 5v5), its peak and the next milestone |
| `.hg area` | GameMaster | starts the restricted area of the player's hellgate now (in its second objective); again: its final phase |
| `.roads` | Player | the road portals of your cluster (target, type, charges, next charge, closing; a road's unlinked spots too) and the five nearest open-world entrances into the Roads of Avalon (`Roads:Command`: pve every player, official GMs) |
| `.roads route <cluster>` | Player | the shortest way over the open road portals from your cluster to a world.xml cluster, the first portal marked with its distance and direction, each hop with its type and charges (`Roads:Command`) |
| `.roads info` | GameMaster | the road network: clusters, links (rescue and GM ones) and exits by state (linked, searching, idle, waiting) per tunnel preset |
| `.roads link <cluster\|preset>` | GameMaster | links the nearest free road spot of your cluster to a free spot of that cluster or of a random cluster of that tunnel preset (`TUNNEL_ROYAL`, ...) for 30 minutes |
| `.roads close` | GameMaster | closes the link of the nearest road portal now (its ends roll again) |
| `.roads charges <n>` | GameMaster | sets the charges of the nearest road portal's link (the charge-up starts below the maximum) |
| `.roads regen` | GameMaster | generates the road network anew (every link closes, the saved one is dropped) |
| `.roads dungeons` | GameMaster | the road dungeon entrances of your map (fixed exits: name, generator, tier, level, solo/veteran/elite, distance) and the dungeons open behind them |
| `.mists` | Player | the mists zone you are in: its mode, map, rarity, tier, the minutes to its shutdown and your way back |
| `.mists portal <mode> [rarity]` | GameMaster | a mists portal (`MISTS_SOLO_YELLOW`, `MISTS_SOLO_BLACK`, `MISTS_DUO_BLACK`) of rarity 0-4 on the random dungeon spot nearest you |
| `.mists list` | GameMaster | the live mists zones (mode, map, tier, rarity, players, time left, treasures to come, bosses, private or shared) |
| `.mists dungeon` | GameMaster | puts a Knightfall Abbey portal (your zone mode's dungeon, a tier by the data's weights) 4 m next to you in your mists zone |
| `.mists wisp [rarity]` | GameMaster | puts a portal wisp (rarity 0-4, default a roll) 6 m next to you in your mists zone: come near and it bursts into a portal of the zone's next mode |
| `.mists standing` | Player | your Brecilien standing, its rank (1-10) and the favour the City Trader takes |
| `.mists standing add <points>` | GameMaster | gives you that much Brecilien standing (and its favour), as the mists' kills do |
| `.mists close [id]` | GameMaster | your mists zone (or the one of that id) closes now: everyone inside goes out to the way back |
| `.mob kill [nearest\|all]` | GameMaster | takes the mob nearest to the player in view (or every mob in view) off the map (Leave); its spawn point respawns it after the scaled respawn time. A despawn, not a kill: no corpse, rewards or loot |
| `.mob respawnscale [scale]` | GameMaster | shows or sets the factor on mob respawn times, the old form of `Rates:Respawn` (= 1 / it; 0.01 = a hundred times faster) |
| `.mob escape` | GameMaster | the nearest living mob in view flees: its mobs.xml EscapeSpellSequence (a tracking mob's immunity, escape spell and dash away from its threat), then it leaves the map; a mob without one leaves at once |
| `.mob ai [on\|off]` | GameMaster | switches the mob AI (`World:MobAi`, on); without an argument: whether it is on and what the nearest living mob in view is doing (AI, state, threat, target, walk, home, last reset) |
| `.bot` | GameMaster | the player bots of every loaded cluster: zone class, players, bots by role, slots filled, the walk grid (cells, walkable, lattice, POIs, exits, build time); whether bots are on |
| `.bot list` | GameMaster | the bots of this cluster, nearest first: name, role, tier, persona, object id, distance, what it does |
| `.bot spawn <city\|gatherer\|patrol> [count=1] [tier=0]` | GameMaster | puts `count` (1–20) GM city, gatherer or patrol bots 3-8 m around the walkable cell nearest to the player, gear tier `tier` (0 = by the zone; a gatherer takes the tier of the nodes the cluster has when none of its band is there; patrols in groups of up to 3 on the road nearest to the player); refused while the walk grid is being built, with nothing walkable within 6 m, for a gatherer on a cluster with nothing to gather, or for patrols where no Red player can be (a city, a green zone); extra to the population, they never come back once gone |
| `.bot clear [here\|all]` | GameMaster | despawns the bots of this cluster or of all (Leave); the population fills again after `Bots:RespawnSeconds` |
| `.bot on` / `.bot off` | GameMaster | switches the bots on or off for this run (`Bots:Enabled`, not saved); off takes every bot away |
| `.bot info` | GameMaster | the nearest bot: role, tier, persona, flag, health, position, what it does (a gatherer's node and resource), its path, emote, fight (target, attackers, flight, defeat), what it carries, stay; a patrol's group, route and the players it spares |
| `.bot grid` | GameMaster | the bots' walk grid at the player: the cell blocked or walkable, the nearest walkable point, the grid's numbers |
| `.bot path <x> <z>` | GameMaster | the bots' path from the player to (x, z): legs and length; a new GM city bot walks it |
| `.mob spells [on\|off]` | GameMaster | switches the mobs' `<AI>` spells (`MobSpells` in content/combat.json, on); without an argument: whether they are on |
| `.rates [name value]` | GameMaster / Admin | display active rates; Admin changes are temporary and global; `.rates reset` restores startup values; `save` is rejected |
| `.vanity list [text]`, `.vanity grant <id\|name>`, `.vanity revoke <id\|name>`, `.vanity drop [theme]` | GameMaster | the cosmetics (Cosmetics above): owned or catalogue search, unlock, take back, a drop now |
| `.damage <amount>` | GameMaster | takes `amount` health from the player (a HealthUpdate caused by itself, no combat); at 0 it is knocked down |
| `.kill` | GameMaster | knocks the player down (PvE knockdown); it gets up by itself 18 s later |
| `.heal` | GameMaster | full health and energy; a knocked-down player gets up at once |
| `.defense` | Player | the worn items' armour, magic and crowd control resistance and defence vs mobs / players, and the share of a mob's physical and magic hit that lands (Armour above) |
| `.pvp` | GameMaster | the map's PvP rules, the player's flag, enlisted faction, reputation and outlaw time, its duel and what each player in view is to it (Friendly, Neutral, Hostile) |
| `.pvp flag <status>` | GameMaster | sets the player's flag at once (no cast, no checks): `blue`, `red`, a faction (`highland`, `forest`, `steppe`, `mountain`, `swamp`, `caerleon`) or its number; ChangeFlaggingFinished to it and its observers; saved |
| `.pvp faction <faction>` | GameMaster | enlists the player to a faction (`blue` = none), which a faction flag needs; the client sees it from the next Join |
| `.pvp reputation [points]` | GameMaster | the player's reputation and level, or sets the points (-50000 to 50000) |
| `.duel end` | GameMaster | ends the player's duel as canceled by the system (a draw, the wagers go back) |
| `.premium [on\|off\|mode]` | GameMaster | the player's premium, focus, learning points, use flags and auto-learn nodes; on / off switches its premium for the session (sent live), mode = `Premium:Mode` again |
| `.focus <amount>` | GameMaster | sets the player's crafting focus |
| `.lp <amount>` | GameMaster | sets the player's learning points |
| `.trade` | GameMaster | the player's trade: id, state, revision, each side's offered items (slot, item, count), silver and accept |
| `.trade end` | GameMaster | cancels the player's trade (both windows close; nothing moves) |
| `.fame add <amount> [node]` | GameMaster | `amount` fame as a kill with the worn gear gives it (board nodes of the equipped items and the main chain; no tier limit), or all of it to one destiny board node in progress (`COMBAT_SWORDS` or its id); it fills no labourer journals |
| `.progress show` | GameMaster | the player's board: mode, total fame, finished nodes, the nodes in progress with level and fame |
| `.progress set <achievement> <level>` | GameMaster | sets a node's level (a plain node: 1 = finished; a template 0–100, 120 with elite levels), its neighbours open; the board is sent anew (only with a board of its own) |
| `.progress mode unlockall\|fair` | GameMaster | `unlockall`: no board of its own, everything unlocked; `fair`: an empty board that fame fills (what was earned on a board before is dropped); the board is sent anew |
| `.account list` | Admin, console only | the accounts: characters, online, password set or not, lock, ban |
| `.account create <name> <password>` | Admin, console only | a new empty account (8-72 ASCII characters, no space at either end, not the name) |
| `.account password <name> <new>` | Admin, console only | sets the password and ends a lock |
| `.account level <name> <level>` | Admin, console only | sets the account's level: `Player`, `GameMaster` or `Admin` (which chat GM commands it may run) |
| `.money [silver] [gold]` | GameMaster | shows the player's silver and gold, or sets them (`5000`) or changes them (`+5000`, `-200`, `add 5000`); whole units |
| `.account ban <name> <minutes> [reason]` | Admin, console only | bans the account (0 minutes = for good) and closes its connections |
| `.account unban <name>` | Admin, console only | lifts the ban and a lock |
| `.account delete <name>` | Admin, console only | deletes the account with all its characters; refused while it is online (ban first). The local account is emptied instead |
| `.item add <item> [count=1] [quality=1]` | GameMaster | a new item (uniquename as in items.xml, e.g. `T4_ORE`, `T5_MAIN_SWORD@1`) in the first free inventory slot; equipment gets one slot per item and a quality 1–5, at most its items.xml `maxqualitylevel` (1 for the battle mounts, drakes and the UNIQUE_ items); saved 2 s later. Only equipment, simple items (resources, consumables, seeds) and journals (`_EMPTY`, `_FULL`); furniture and the other kinds are refused for now |
| `.kit <name>` | GameMaster | puts every item of a kit of `content/kits.json` into the inventory as `.item add` does, while free slots last, and names what did not fit. Shipped: `mounts` (8 mounts T3–T8; the frost ram, hell spider, fire drake, battle swamp dragon and battle mammoth have mount spells), `food` (10 meals T1–T8), `potions` (13: healing, energy, resistance, gigantify, berserk, poison, acid), `test` (all three, 31 slots), `invasion` (10 + 5 invasion tickets). Only foods and potions whose spell effects the server runs; read at start |
| `.kit list` | GameMaster | the kits with their item counts |

The `.account` commands run only on the server console, never from chat (whatever the account's level);
their passwords are never echoed or logged (a logged line shows `<redacted>`). Arguments are separated by spaces
(`"two words"` in quotes); a mistyped item name gets the closest known one as a
hint. New commands are static methods with `[Command("path", Level = …, Help = "…")]` (see `Commands/Builtin`);
their arguments are parsed by parameter type (string, int, long, float, bool, enums, `ItemTemplate`, `MobTemplate`, `Tail` for the rest
of the line, default values for optional ones).

## Scripts

Behaviour beyond the default path is a class in the server (`Scripting/`, found once at startup by `ScriptRegistry`;
the bindings are logged). An item script derives from `ItemScript`, carries `[ItemScript("T4_MAIN_SWORD")]` or a mask
such as `[ItemScript("T?_MAIN_*")]` (`?` one character, `*` any run, ignoring case; without `@` it covers the
enchantments too) and overrides `OnEquip` / `OnUnequip`. A player hook implements `IPlayerHooks` with any of
`OnLogin(player, first)`, `OnLogout(player)` and `OnItemAdded(player, item)`. Item changes made in these calls are
saved; one made anywhere else needs `player.SaveItems()`. Scripts cannot change health or energy yet. Two equally
precise item scripts for one item stop the start; a script that throws is logged and the game goes on. The server has
no scripts of its own yet.
