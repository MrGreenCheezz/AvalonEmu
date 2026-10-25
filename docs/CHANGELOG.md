# Changelog

The `modifications` branch carries everything in `main` plus modding. This update also brings it the five-file
JSONC configuration from PR #2 (`config/*.jsonc` in place of `config/albionlocal.json`).

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

The self-test suite has 503 tests on this branch (475 on `main`), all passing; the ones that need capture fixtures are
skipped in this repository.
