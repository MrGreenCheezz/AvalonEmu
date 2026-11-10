# Changelog

## 2026-11-11 — Open-world treasure coffers and chests

- **Treasures** (issue #19, `World:TreasureCoffers`, on in both presets, docs/REFERENCE.md "Open-world treasures"): the contested coffers and treasure chests now appear in the open world. Coffers stand on the clusters' coffer tiles; treasure chests stand on the treasure spots that the client's resource placement puts on resource nodes. Which cluster gets which, how many at once and how often all come from `treasuredistribution.xml`. Royal yellow (T5-T6) and red (T6-T7) zones and the Outlands (T5-T8) get up to 5 coffers. The Outlands also get one treasure chest at a time: solo, veteran or elite.
- Each treasure counts down its protection first: coffer 90 s, solo 3 min, veteran 20 min, elite 40 min. Then it opens with the channelled cast and rolls loot for the cluster's tier. It disappears 5 minutes after opening, or after 24 h if nobody opens it, and its slot draws again.
- **PvP circle** (`Pvp:Mode` `Official` only, not in safe zones): within 30 m of a treasure (40 m for elite), two players fight each other whatever their flags, unless they are in one party. The Mists treasure chests have the same circle now.
- `TreasureCoffers:RespawnMinutes` (0 = the data's timing), `TreasureCoffers:Sizes` and `TreasureCoffers:TestAlways` (dev aid) in `config/world.jsonc`. GMs: `.coffer` lists the slots and spots, `.coffer spawn [coffer|solo|veteran|elite]` places one, `.chest unlock` ends the countdown.
- Built from the client's code and data, not checked in the client yet. Not done: the world-map marker of veteran and elite chests, the faction chests and the Red Zone Event.

Thanks to Gostavo2003 for the request and the research.

## 2026-11-10 (later) — City portals into the Outlands

- **Realmgates** (issue #14): the portal in a royal city's Conquerors' Hall and the portal of its Outlands portal town stood there but could not be clicked: the server sent neither object and answered none of the portal window's requests. Both are sent now (NewPortalEntrance 322, NewPortalExit 323). The Realmgate leads into the city's portal town (Thetford Portal and so on), the town's portal back into the hall.
- **Binding**, as the client's texts describe it: a pass binds the character to that city and the other cities' Realmgates stay shut until the binding is removed in the portal window, once per 7 days (`PortalSettings bindingresetdays`). Coming back through another city's portal moves the binding there. `Transfer:PortalBinding` (true) switches the rule off, `Transfer:PortalBindingResetDays` overrides the 7 days.
- Database schema v44 (`characters.portal_binding`); the migration runs at start.
- Built from the client's code and data, without a capture of the official exchange, and not checked in the client yet. The portal towns' S.A.F.E. portal is not done.

Thanks to Gostavo2003 for the report.

## 2026-11-10 — Fix: the mule of a fair start

- **Starter kit** (issue #15): with `Tutorial:Opening` false under the official preset (`Progression:NewCharacters` = `FairStart`) a new character had no mount. Such a character gets the kit of `content/starter-fair.json`, not `content/starter.json`, and that kit left the mule out, although `T2_MOUNT_MULE` needs no destiny board unlock. The fair kit now has the mule equipped. Existing characters are not changed; a server with its own `starter-fair.json` keeps that file's kit.

Thanks to Gostavo2003 for the report.

## 2026-11-08 — Faction warfare outposts; no mounts in dungeons and expeditions; smaller fixes

**Faction warfare outposts** (`FactionWarfare:*`, docs/REFERENCE.md "Faction warfare"; on in both presets)
- Every yellow and red zone shows its towers (1 or 3, from `world.xml`), at first owned by its duchy's faction. All 274 towers of the 100 zones resolve to their template position.
- Each tower has its owner's guards from `outpost.xml`: 1 boss and 7 guards, T5 in T4-T5 zones and T6 above. They attack players flagged for another faction, and only those players can hurt them.
- With all guards dead a tower on the front line can be taken: stand in its 12 m circle, flagged and on foot. Alone, your faction empties the owner's points in 8 s and fills its own in 16 s; another faction in the circle holds the claim. The new owner's guards come back 15 s (boss) and 30 s later.
- A faction that holds more than half of a zone's towers for `FactionWarfare:ControlSeconds` (560 s) controls the zone, which moves the front line. The minimap, the world map's tower flags and the capture circle follow it.
- `.outpost` lists the zone's towers; GMs have `.outpost owner <1-6>` and `.outpost clear`.
- Inferred, not captured: the front line rule (the official one follows the campaign's duchy conflicts), the guard respawn rule, the control time. The towers are not saved: a restart gives every tower back to its duchy's faction. Faction camps, supplies and fortresses are not done yet.

**Mounts** (issue #8 follow-up): a character that entered an expedition mounted could not dismount inside. The game data forbids mounts in 80 cluster types: every dungeon, the expeditions, the arenas, the corrupted, hell and mists dungeons, and the dragon areas. The server now reads that rule. A character that comes in mounted arrives on foot, and mounting there is refused.

**Smaller fixes**
- Bank "add to stacks": a full stack of 999 no longer keeps the rest of that item type out; it goes into free vault slots.
- Speed emblems: the cities' and the Roads of Avalon's speed zones give their buff (the Roads' one only on a mount, as the data says).
- Wardrobe skins: a worn skin's spells (the skull hood's laugh, say) take their vanity spell slots.
- PlayerCounts (event 276) every 10 s, as on the official server.

Thanks to JumpingY for the mount report.

## 2026-11-07 — Fix: the Bridgewatch fast-travel arrival

- **Fast travel** (issue #10): a journey to Bridgewatch put the character inside a shed next to the travel point, where it could not walk away. The client data places that travel point's arrival (its `spawnpos`, at (7.91, 20.98)) inside the shed's collision. An arrival that lies in a blocked cell is now moved to the nearest walkable cell of the cluster's walk grid, (4.5, 20.5) in Bridgewatch, for journeys, island returns and `.tele` alike. A check of all 161 travel points found no other arrival inside a wall. The official server's arrival there has not been captured.

Thanks to JumpingY for the report and the `.bot grid` output.

## 2026-11-06 (later) — Fixes: the 100 % global discount, the journey price; faster mobs and bots; tick profile

- **Global discount** (bug): every NPC service (repairs, crafting fees, enchanting, fast travel and the rest) showed a 100 % "Global discount" and cost nothing. The client weighs prices against a gold price index of 5000 and reads the index from Join key 83, which the server never sent. It now sends 5000, as every captured official Join does (no discount); `Economy:GoldPriceIndex` sets it (2500 = 50 % off). A price of 0 is no longer accepted.
- **Fast travel**: the server works out the journey price itself, the way the client does (route cost × the luggage value: count × weight × fasttravelfactor × 30 over the equipped and carried items), and refuses a client price more than 1 silver off it.
- **Mobs react as on the official server**: a mob's first swing starts 0.15 s after it enters a fight, not 1 s plus its windup (`MobReactionSeconds` in `content/combat.json`). In the captures a standing mob swung about 0.14 s after the first hit, and chasing mobs swung as soon as they reached their range.
- **Bots**: an idle patrol or drifter that a player sees decides at least every 0.25 s (`Bots:ScanSeconds`), so it attacks on sight; a bot's first swing after the first hit comes after 300 ms instead of 600 (`Bots:Combat:ReactionMs`).
- **Performance**: every mob's aggro scan walked all bots of its map to find hired companions. With 1000 bots on a map of 1090 mobs a tick now takes about 3 ms of CPU instead of 15 (2000 bots: about 5 ms).
- **Diagnostics**: `.tick` (GameMaster) shows the world tick's average and peak by section; a tick over 20 ms names its slowest sections in the log. `SelfTest --bench=<bots>` runs a stress test with that many bots (it needs the client data).

## 2026-11-06 — Fixes: the expedition Join channel, the way out of an island showroom

- **Expeditions** (issue #8): the Join channel at an expedition agent no longer breaks off at once. The client ends that channel on any answer to `EnteringExpeditionStart` (op 278) and then cancels it itself, so the server now sends no answer when it accepts; the player enters the expedition after the full 10 s.
- **Island showroom** (issue #7): Depart at the showroom's Travel Point takes the player back to the city's island merchant. There the client sends `TravelIslandShowroom` (op 533) with the travel point rather than `BuyJourney`, and the server now answers it.

Both checked in the game client. Thanks to JumpingY for the detailed reports.

## 2026-11-04 — Server modules (branch `modules`)

An AzerothCore-like module system: a module is a folder with C# sources that the server builds and loads, and reloads while it runs. Start with `docs/modules/README.md`; `docs/modules/HOOKS.md` lists every hook, and `modules/examples` holds working modules. Not merged into `main` yet.

- **Hooks** on players, combat, spells, buffs, items, mounts, mobs, maps and any network message, as `*Script` methods or lambdas, with cancel, priorities and per-id filters. A module that keeps failing is switched off without stopping the game.
- **Types, not text**: hooks and `Server` hand out `ItemType`, `SpellInfo`, `MobType` and `ZoneInfo`, and closed sets are enums.
- **What became of a hit**: `Combat.OnBeforeAbsorb` and `OnAfterAbsorb` (damage shields), `Combat.OnHitAvoided` (invincibility, spell immunity, a module's cancel), `Spells.OnBeforeTrigger` (reactions), `e.IsDirectHit`, `e.UnitsHit`, `Roll`.
- **A boss of your own**: `Server.DefineMob` (a copy of a mobs.xml type with your numbers: the clients still show the base type's model and name), `IMob.Attack`, `Threat`, `CurrentSpell`, `CastSpell` with its own cast time, instant and scripted spells for mobs, `Mobs.OnBeforeLeaveCombat`, areas the clients can see.
- Self-tests cover the hooks and a contract test guards the public API (`docs/modules/api-baseline.txt`). Not checked in the game client: `MobDefinition.Title`, `CreateArea`'s `visual`, and how the cast bar follows `castSeconds`.

## 2026-10-26 (evening) — PvP end-game (the parts the client code and data settle)

Everything below is on under `Preset: official`, where the official game has it; each system has its own switch.
Fields that are only inferred are sent as neutral values and marked `[H]` in the code and in REFERENCE.md; captures
of the official server will settle them. The database schema goes from v37 to v42 — back up your save first.

**PvP details**
- Death recap (op 492): mob and player hits, heals, crowd control and debuffs of the last 180 s (`DeathRecap:*`).
- Healing sickness (`Pvp:HealingSickness`): 2+ distinct healers within 10 s give `HEALINGSICKNESS_n`.
- Resilience / focus-fire protection (`Pvp:Resilience`): less damage from 2–30 attackers, with penetration.
- Zone entry (`Pvp:SpawnBuffs`, `Pvp:EnterSpellCooldowns`): the worldsettings spawn buffs (spawn shield, speed) and
  the 15 s spell cooldown on entering yellow, red and black zones.

**Arena**
- Official rules: item power caps (600 hard; 900 soft for 1v1 and the Crystal Arena), the double-healer penalty,
  `ARENA_*` entry buffs, camp turret and camp invulnerability, personal season points (`Arena:ItemPowerCap`,
  `DoubleHealerPenalty`, `EnterBuffs`, `WarcampDefense`, `Psp`).
- Runestones (`Arena:Orbs`): zone orbs in the unranked arena (active after 60 s, a claim costs the enemy 10 tickets),
  channel orbs in the Crystal Arena with the 120 s majority score.
- Crystal Arena as its own mode on CrystalRealm03 (`Arena:CrystalArena`), and rank points with the 17 ranks from Iron 1
  to Crystal and rank-up chests (`Arena:RankPoints`; the rank point formula is not in the client data, so it is a
  configurable Elo-like placeholder).
- The in-match HUD events stay off (`Arena:MatchHud` false) until captures confirm them.

**Alliances** (`Alliances:*`): create (5 M from the guild account), disband, leave, member and detail info, invitations
by mail, answer, cancel, kick, limits and cooldowns, the alliance chat channel; allies are friendly in PvP.

**Guild territories and castles** (`Territories:*`, `Castles:*`)
- The 268 watchtower territories, 23 castles and 95 castle outposts from the client data, with their monoliths and
  castles in the world; guild season history, participation and the full season update.
- Territory claims in the season's claimable window and prime time, giving up, access rights, monolith food, the
  guild's territory list, the season schedule with its Tower Guardian, and season points for holding territories.
- Castles and outposts: guards, elite guards and the lord in phases, the capture zone, ownership, and chests on the
  treasure schedule for the owner.
- The world-map territory info stays off (`Territories:WorldMapInfo` false): wrong array lengths crash the client's map.

**Guild hideouts** (`Hideouts:*`): placing the construction kit by the data's rules, construction, the hideout
instance with entry, exit and access rights, its own bank vault (separate from every city bank) and market, Make Home,
respawn and Journey Back, food upkeep, upgrades and tribute to the territory owner.

Not yet (they need captures): raids and conquest of territories, hideout sieges and destruction, power cores,
fortifications, the Crystal League.

Self-tests: 519 on `main`, 547 on `modifications`.

## 2026-10-26 (later) — bots travel and play support roles

Both features need `Bots:Enabled` (off under `official`, on under `pve`).

**Travel between clusters** (`Bots:Travel:*`, `Bots:Gatherer:TripMinutes`, `Bots:Gatherer:BankAtHome`)
- A world bot leaving through an exit appears 10-20 s later at the matching entrance next door, as the same character
  (name, gear, health, bag). It only travels to a map that has a player on it and a free bot slot of its role;
  otherwise it logs out at the exit as before. Bots never enter instances, islands, hellgates or the Roads.
- Gatherers have a home city with a bank. When the bag is full or the trip time (30 min) is up, a gatherer walks home
  cluster by cluster, goes to its city's bank door, pauses and banks its haul — per city, no global storage.

**Support roles in patrol groups** (`Bots:Support:*`, `Bots:Patrol:Makeups`)
- Patrol groups are drawn from weighted makeups per zone (`dps`, `healer`, `tank`); by default 9 in 10 stay all-dps
  (8 in 10 in red and black zones and on the Roads).
- Healers (holy or nature staff, cloth) heal and shield their own group, lowest health first, through the players'
  cast rules; they never target players or other groups' bots (area heals behave like a player's).
- Tanks (mace or hammer, shield, plate) open with taunts and turn on whoever hits their healer.
- A healer bot in a player's party heals the party.

Self-tests: 484 on `main`, 512 on `modifications`.

## 2026-10-26 — official by default, switchable player bots

The server now follows the course of emulators such as TrinityCore and AzerothCore: **`Preset: official` behaves like
the official game**, and everything the server adds on top is behind a config key. The `pve` preset keeps the
earlier PvE-friendly behaviour and is opt-in. All keys are documented in `config/world.jsonc` and in
[REFERENCE.md](REFERENCE.md).

### Official preset audit

Every config key and every server-made system was checked against the official game. 404 keys: 199 already official,
129 inert under official (their master switch is off), 30 deviating (now fixed), 46 whose official value is unknown
(left as they are and marked in their descriptions).

**Loading**
- A configuration without `Preset` now means `official` (it used to fall back to `pve`).
- A configuration without `Tutorial:Opening` now starts new characters in the opening tutorial.
- `Progression:NewCharacters` is `FairStart` under official: a new character's destiny board is empty and grows with
  fame. `pve` keeps `UnlockAll`.

**Server additions now off under official** (each has its own key; `pve` keeps them on):

| Key | Official | pve | What it controls |
|---|---|---|---|
| `Events:PlayerInvasions` | false | true | `.invasion`, invasion tickets in the shop and in drops |
| `Bounties:Enabled` | false | true | `.bounty` tasks |
| `PvpGoals:Enabled` | false | true | challenge points for PvP kills and duels |
| `Dungeons:DailyBonus` | 1 | content | the dungeon of the day multiplier |
| `Dungeons:GroupScaling` | false | true | random dungeons scaled to the party |
| `Guilds:BossSeasonPoints` | false | true | guild season points for boss kills |
| `Guilds:SeasonRewards` | Official | Content | bracket silver and items at season end, `.guild payout` |
| `Season:Rules` | Official | Content | the Adventurer's Challenge as in the client data vs `goals.json` overrides |
| `Expeditions:Rules` | Official | Content | solo group runs, auto-accept, hardcore carry, scaling, extra items |
| `WorldBosses:ExtraSpawns` | false | true | the server's own extra world bosses |
| `WorldBosses:Loot` | Killer | content | who gets a world boss's loot |
| `Loot:TierCap` | false | true | common loot at most one tier above the content |
| `Corrupted:LeaveCommand` | false | true | `.cd leave` |

**Defaults changed to the official values**

| Key | Official | pve |
|---|---|---|
| `Arena:CountdownSeconds` | 15 | 10 |
| `Arena:Tickets2v2` / `Tickets5v5` | 150 / 150 | 6 / 15 |
| `Arena:TicketsPerDeath` (new) | 2 | 1 |
| `Arena:MatchMinutes` | 25 | 15 |
| `Arena:Win/LossSilver`, `Win/LossFame` | 0 | 5000 / 1500, 30000 / 10000 |
| `Arena:Bots` | false | true |
| `Arena:DuelsRated` | false | true |
| `Loot:BlackMarketSoftCap` | 0 | 4 |
| `Roads:Rescue` | false | true |
| `Corrupted:MapsOpenDungeons` | true (maps open a corrupted dungeon) | false (maps are invasion tickets) |

Notes:
- An arena match no longer starts, or pays out, against empty slots when no bots are available.
- Orb ticket loss is not modelled yet, so official arena matches mostly run to the 25-minute limit. Arena season
  points are not modelled either.
- `Islands:BuyWithoutPremium` (new, true in both presets) is a temporary deviation: officially only premium players
  buy islands, and this server has no premium for gold yet.
- Cosmetics from content (`Vanity:*`) stay on in both presets by choice.

**Commands**: `.who` (every player's position) and `.save` are GameMaster-only now.

**Docs**: the commented examples in `config/world.jsonc` show official values with the `pve` value in the note; the
orphaned chat example moved to `config/chat.jsonc`; stale descriptions were fixed.

### Player bots — a switchable optional feature

Player bots are not an official feature. Like AzerothCore's playerbots module they stay in the server, are **off
under official** (master switch `Bots:Enabled` false) and on under `pve`.

**Switches**
- `Bots:Enabled` — master switch for every kind of bot.
- `Bots:World:Enabled` — the open-world population, with one switch per role: `Bots:City`, `Bots:Gatherer`,
  `Bots:Patrol`, `Bots:Drifter` (`…:Enabled`), and `Bots:Zones` (where world bots may spawn).
- `Bots:Arena:Enabled`, `Bots:Hellgate:Enabled`, `Bots:Invader:Enabled` — content bots, which now work without the
  world population.
- `Bots:Pvp:Enabled` — false: bots never start a fight, they only fight back.
- `Bots:Rewards:Enabled` — false: killing a bot gives no fame, silver, loot or goal credit.
- Former hard-coded behaviour is configurable: city wander share and radius, exit vs logout share, gatherer flee
  time and distance, patrol spacing, retarget and kite intervals.
- `.bot on|off` says that it lasts until the restart.

**Social presence and chat** (`Bots:Social:*`, `Bots:Chat:*`)
- World bots count as online players by name: whispers get a delayed canned reply, friend requests are declined
  after a delay, nothing is stored in the database.
- City bots chatter now and then, wave back at emotes, and sometimes say "gg" or a taunt after a fight, with a
  per-cluster rate limit. All lines are editable in `content/bots.json` (`Chat`).

**Bots in your party** (`Bots:Party:*`)
- A world bot near your tier can accept a party invite (`AcceptChance` 0.7, `TierRange` 1, `MaxPerParty` 2).
- It plays like a hired companion: follows you across clusters and into dungeons, expeditions, hellgates and the
  Mists, fights the party's mobs, takes its role (healer, tank, ranged, melee) from its gear.
- It never loots. It leaves and despawns when kicked, when the party disbands, when you are away too long, or after
  `MaxMinutes` (120).

**Roster, hours and gear** (`Bots:Roster:*`, `Bots:Schedule:*`, `Bots:Gear:*`)
- A stable roster built from a seed: the same names come back with the same look and gear across respawns and
  restarts, and never collide with real characters. Nothing is stored in the database.
- Population by hour of day: a 24-hour curve (or a few anchor points) scales each cluster's bot count; bots log in and
  out gradually. The default curve is flat, i.e. unchanged behaviour.
- More realistic gear: pieces of mixed tiers around the bot's tier, occasional .1–.3 enchantments by zone, artifacts
  more likely in red and black zones.

### Tests

The self-test suite grew from 460 to 475 tests (the modifications branch: 503), all passing.
