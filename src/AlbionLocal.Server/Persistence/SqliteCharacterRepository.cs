using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using Microsoft.Data.Sqlite;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// <see cref="ICharacterRepository"/> on SQLite (Microsoft.Data.Sqlite, ARCHITECTURE.md §5.5): the file
/// &lt;Paths:Saves&gt;/albionlocal.db, one connection used under the repository's lock. The schema has a version
/// (PRAGMA user_version) and an ordered list of steps (<see cref="Migrations"/>): opening an older file runs the
/// missing steps, a newer one is refused. Arrays that are only ever read and written whole (appearance, spell
/// names, destiny board) are JSON text columns.
/// </summary>
public sealed partial class SqliteCharacterRepository : IGameRepository
{
    /// <summary>The local account: the id the Login response carried (key 51) for every login before stage 7.</summary>
    public static readonly Guid LocalAccountId = global::AlbionLocal.Server.Persistence.Account.LocalId;

    private const int EquipmentContainer = 0, InventoryContainer = 1;

    /// <summary>
    /// Schema steps in order: step i takes user_version i to i + 1, in one transaction. A released step is never
    /// edited; a change of the schema is a new step at the end.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // v1 (step 7): the account, its characters, their items and destiny board state.
        """
        CREATE TABLE accounts (
            id          TEXT PRIMARY KEY,                   -- Guid
            level       INTEGER NOT NULL                    -- AccountLevel: 0 Player, 1 GameMaster, 2 Admin
        );
        CREATE TABLE characters (
            id                  TEXT PRIMARY KEY,           -- Guid (Join key 1)
            account             TEXT NOT NULL REFERENCES accounts (id),
            name                TEXT NOT NULL UNIQUE COLLATE NOCASE,
            template            INTEGER NOT NULL,           -- CreateCharacter h8
            appearance          INTEGER NOT NULL,           -- CreateCharacter h9
            customization       TEXT NOT NULL,              -- JSON int array (CreateCharacter ia)
            clothes             TEXT NOT NULL,              -- JSON int array (CreateCharacter ib)
            cluster             TEXT NOT NULL,              -- world.xml cluster id
            position_x          REAL NOT NULL,
            position_y          REAL NOT NULL,
            created             INTEGER NOT NULL,           -- UTC DateTime ticks
            equipment_container TEXT NOT NULL,              -- Guid (Join key 51)
            inventory_container TEXT NOT NULL               -- Guid (Join key 54)
        );
        CREATE TABLE items (
            character   TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            container   INTEGER NOT NULL CHECK (container IN (0, 1)), -- 0 equipment (slot = EquipmentSlot), 1 inventory
            slot        INTEGER NOT NULL,                   -- may lie beyond the container (kept, not sent)
            uniquename  TEXT NOT NULL,
            count       INTEGER NOT NULL,
            quality     INTEGER NOT NULL,
            durability  INTEGER,                            -- a7m raw; NULL = full
            active      TEXT,                               -- JSON array, spell uniquename per slot (null = none); NULL = never chosen
            passive     TEXT,
            PRIMARY KEY (character, container, slot)
        ) WITHOUT ROWID;
        CREATE TABLE progression (                          -- no row = everything unlocked (Character.Progression null)
            character   TEXT PRIMARY KEY REFERENCES characters (id) ON DELETE CASCADE,
            completed   TEXT NOT NULL,                      -- JSON int array in completion order (FullAchievementInfo key 1)
            levels      TEXT NOT NULL                       -- JSON object: achievement id -> level
        );
        """,
        // v2 (step 13, cluster changes; SPEC stage 3 §4.6): facing, the cluster history of the Join keys and the transfer
        // committed but not yet joined.
        """
        ALTER TABLE characters ADD COLUMN direction REAL NOT NULL DEFAULT 0;            -- degrees (Join key 10)
        ALTER TABLE characters ADD COLUMN previous_cluster TEXT NOT NULL DEFAULT '';    -- Join key 66
        ALTER TABLE characters ADD COLUMN last_left_at INTEGER NOT NULL DEFAULT 0;      -- UTC ticks (Join key 101)
        ALTER TABLE characters ADD COLUMN last_city TEXT NOT NULL DEFAULT '';           -- Join key 118
        ALTER TABLE characters ADD COLUMN pending_transfer TEXT;                        -- JSON PendingTransfer; NULL = none
        """,
        // v3 (stage 7, several players): one account per login name; the local account keeps its characters and gets the
        // first name that logs in (or Accounts:LegacyName).
        """
        ALTER TABLE accounts ADD COLUMN name TEXT COLLATE NOCASE;   -- login name (Login key 6); NULL = the local account, not claimed yet
        CREATE UNIQUE INDEX accounts_name ON accounts (name);
        """,
        // v4 (stage 5c, mob kills): the character's total fame.
        """
        ALTER TABLE characters ADD COLUMN fame INTEGER NOT NULL DEFAULT 0;              -- a7m raw (x10000), UpdateFame key 1
        """,
        // v5 (real accounts, analysis/work/accounts): password and login checks. The name becomes the login key (lower
        // case; non-ASCII letters are lowered after the step, NormalizeNames), the name as typed moves to display_name.
        // Accounts of before have no password: their next successful login sets the one typed.
        """
        ALTER TABLE accounts ADD COLUMN display_name TEXT;                              -- the name as typed (Login key 64 of refusals)
        ALTER TABLE accounts ADD COLUMN password TEXT;                                  -- Passwords.Hash form; NULL = none yet
        ALTER TABLE accounts ADD COLUMN failed_logins INTEGER NOT NULL DEFAULT 0;       -- wrong passwords in a row
        ALTER TABLE accounts ADD COLUMN locked_until INTEGER;                           -- UTC ticks; NULL = not locked
        ALTER TABLE accounts ADD COLUMN banned_until INTEGER;                           -- UTC ticks; NULL = not banned
        ALTER TABLE accounts ADD COLUMN ban_reason TEXT NOT NULL DEFAULT '';
        ALTER TABLE accounts ADD COLUMN created INTEGER NOT NULL DEFAULT 0;             -- UTC ticks
        UPDATE accounts SET display_name = name, name = lower(name),
            created = strftime('%s', 'now') * 10000000 + 621355968000000000;
        """,
        // v6 (fame and the destiny board, analysis/work/progression): the fame inside the current level of each node in progress.
        """
        ALTER TABLE progression ADD COLUMN progress TEXT NOT NULL DEFAULT '{}';         -- JSON object: achievement id -> a7m raw
        """,
        // v7 (stage 6, economy; analysis/work/stage6/SPEC.md): the character's silver and gold, its bank vaults, and the
        // marketplace: the open orders, the finished entries waiting to be collected, the silver of sales to an offline seller.
        // Everything belongs to one character (and goes with it).
        """
        ALTER TABLE characters ADD COLUMN silver INTEGER NOT NULL DEFAULT 0;            -- a7m raw (Join key 33)
        ALTER TABLE characters ADD COLUMN gold INTEGER NOT NULL DEFAULT 0;              -- a7m raw (Join key 34)
        CREATE TABLE bank_items (
            character   TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            location    TEXT NOT NULL,                      -- the bank's cluster id
            tab         TEXT NOT NULL,                      -- vault tab Guid (bgf.u0; tab 0 = 7deb25d2-...)
            slot        INTEGER NOT NULL,
            uniquename  TEXT NOT NULL,
            count       INTEGER NOT NULL,
            quality     INTEGER NOT NULL,
            durability  INTEGER,
            active      TEXT,
            passive     TEXT,
            PRIMARY KEY (character, location, tab, slot)
        ) WITHOUT ROWID;
        CREATE TABLE market_orders (
            id          INTEGER PRIMARY KEY,                -- AuctionDTO Id (from 1000000000)
            owner       TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            type        TEXT NOT NULL,                      -- 'offer' (sell order) or 'request' (buy order)
            location    TEXT NOT NULL,                      -- the marketplace's cluster id
            uniquename  TEXT NOT NULL,                      -- ItemTypeId (T4_X@n)
            quality     INTEGER NOT NULL,
            amount      INTEGER NOT NULL,                   -- still open
            unit_price  INTEGER NOT NULL,                   -- a7m raw
            created     INTEGER NOT NULL,                   -- UTC ticks
            expires     INTEGER NOT NULL,                   -- UTC ticks
            reference   TEXT NOT NULL,                      -- ReferenceId Guid
            item        TEXT                                -- offer: JSON of the stack it holds (durability, spells)
        );
        CREATE TABLE market_finished (
            id          INTEGER PRIMARY KEY,
            owner       TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            order_id    INTEGER NOT NULL,                   -- the order it came from (AuctionDTO Id)
            type        TEXT NOT NULL,
            location    TEXT NOT NULL,
            uniquename  TEXT NOT NULL,
            quality     INTEGER NOT NULL,
            amount      INTEGER NOT NULL,                   -- items to collect
            unit_price  INTEGER NOT NULL,
            silver      INTEGER NOT NULL,                   -- a7m raw to collect
            created     INTEGER NOT NULL,
            expires     INTEGER NOT NULL,
            reference   TEXT NOT NULL,
            item        TEXT
        );
        CREATE TABLE market_credits (
            character   TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            silver      INTEGER NOT NULL                    -- a7m raw, added to the character at its next Join
        );
        """,
        // v8 (stageD consumables, analysis/work/stageD/consumables/SPEC.md §6): the player's own timed spells (the food buff runs
        // on wall-clock time), the potion and food slot cooldowns, and the crafting focus.
        """
        ALTER TABLE characters ADD COLUMN effects TEXT NOT NULL DEFAULT '[]';          -- JSON array of SavedEffect
        ALTER TABLE characters ADD COLUMN cooldowns TEXT NOT NULL DEFAULT '[]';        -- JSON array of SavedCooldown
        ALTER TABLE characters ADD COLUMN focus REAL NOT NULL DEFAULT 0;              -- crafting focus points
        """,
        // v9 (stageD mounts, analysis/work/stageD/mounts/SPEC.md §3): mounted over a logout or cluster change, and the cooldown after
        // the mount died.
        """
        ALTER TABLE characters ADD COLUMN mounted_since INTEGER NOT NULL DEFAULT 0;   -- GameTimeStamp of the mount, 0 = dismounted
        ALTER TABLE characters ADD COLUMN mount_cooldown INTEGER NOT NULL DEFAULT 0;  -- GameTimeStamp it may mount again from
        """,
        // v10 (stageE pvp, analysis/work/stageE/pvp/SPEC.md §3.1, §3.4): the flag and the enlisted faction outlive a logout, and the
        // reputation with the time its daily recovery was last worked out.
        """
        ALTER TABLE characters ADD COLUMN flag INTEGER NOT NULL DEFAULT 0;           -- FlaggingStatus: 0 Blue, 1-6 a faction, 255 Red
        ALTER TABLE characters ADD COLUMN faction INTEGER NOT NULL DEFAULT 0;        -- the enlisted faction (a FlaggingStatus), 0 = none
        ALTER TABLE characters ADD COLUMN reputation REAL NOT NULL DEFAULT 0;        -- reputation points (-50000 … 50000)
        ALTER TABLE characters ADD COLUMN reputation_at INTEGER NOT NULL DEFAULT 0;  -- UTC ticks of the last recovery step, 0 = never
        """,
        // v11 (stageG dungeons, analysis/work/stageG/dungeons/SPEC.md §6.1): the way out of a random dungeon that is gone.
        """
        ALTER TABLE characters ADD COLUMN dungeon_return TEXT;                       -- JSON DungeonReturn; NULL = none
        """,
        // v12 (stageH ops-audit, analysis/work/stageH/ops-audit/SPEC.md §3.4): the Albion Journal achievements the tracker shows.
        """
        ALTER TABLE characters ADD COLUMN journal_tracked TEXT;                      -- JSON string[]; NULL = never set (suggestions)
        """,
        // v13 (stageH journals, analysis/work/stageH/journals/SPEC.md §A.5): the fame of a partly filled labourer journal.
        """
        ALTER TABLE items ADD COLUMN fame INTEGER;                                   -- a7m raw; NULL = 0 (_EMPTY) or maxfame (_FULL)
        ALTER TABLE bank_items ADD COLUMN fame INTEGER;
        """,
        // v14 (stageH expeditions, analysis/work/stageH/expeditions/SPEC.md §5.1, §8): the daily bonuses taken, the last expedition and
        // the reward items that did not fit the inventory.
        """
        ALTER TABLE characters ADD COLUMN expeditions TEXT;                          -- JSON ExpeditionLog; NULL = none
        """,
        // v15 (PVE-DESIGN step 11, analysis/work/stageH/journals/SPEC.md §B.4, §C.4): the Albion Journal, the Adventurer's Challenge and
        // the bounties of the character.
        """
        ALTER TABLE characters ADD COLUMN goals TEXT;                                -- JSON GoalLog; NULL = none
        """,
        // v16 (PVE-DESIGN step 12, analysis/work/stageH/blackmarket/SPEC.md §3): the day's sales to the Black Market's NPC buyer.
        """
        ALTER TABLE characters ADD COLUMN black_market TEXT;                         -- JSON BlackMarketLog; NULL = none
        """,
        // v17 (PVE-DESIGN step 13, analysis/work/stageH/guilds/SPEC.md §4): the guilds, their members (one guild per character; a
        // member row goes with its character) and when a character last left one (the guild change cooldown).
        """
        CREATE TABLE guilds (
            id          TEXT PRIMARY KEY,                   -- Guid (GuildUpdate key 0)
            name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created     INTEGER NOT NULL,                   -- UTC ticks
            logo        TEXT NOT NULL,                      -- JSON GuildLogo (CreateGuild keys 1-7)
            banner      TEXT NOT NULL,                      -- CreateGuild key 8
            silver      INTEGER NOT NULL DEFAULT 0,         -- a7m raw (GuildUpdate key 11)
            tax         INTEGER NOT NULL DEFAULT 0,         -- a7m raw (key 14)
            xp          INTEGER NOT NULL DEFAULT 0,         -- guild experience (SPEC §10)
            tags        TEXT NOT NULL                       -- JSON Guid[8], the role tag ids (GuildRoles.TagRoles order)
        );
        CREATE TABLE guild_members (
            character   TEXT PRIMARY KEY REFERENCES characters (id) ON DELETE CASCADE,
            guild       TEXT NOT NULL REFERENCES guilds (id) ON DELETE CASCADE,
            role        INTEGER NOT NULL,                   -- GuildRole 1-8
            joined      INTEGER NOT NULL,                   -- UTC ticks
            last_seen   INTEGER NOT NULL,                   -- UTC ticks
            position    INTEGER NOT NULL                    -- join order
        );
        CREATE INDEX guild_members_guild ON guild_members (guild);
        CREATE TABLE guild_left (
            character   TEXT PRIMARY KEY REFERENCES characters (id) ON DELETE CASCADE,
            left_at     INTEGER NOT NULL                    -- UTC ticks
        );
        """,
        // v18 (PVE-DESIGN step 13 part 2, analysis/work/stageH/guilds/SPEC.md §10.6): the guild contracts of the current period.
        """
        ALTER TABLE guilds ADD COLUMN contracts TEXT;                                -- JSON GuildContracts; NULL = none rolled
        """,
        // v19 (PVE-DESIGN step 14, analysis/work/stageH/islands/SPEC.md §0.5): the owned islands (a row goes with its owner).
        """
        CREATE TABLE islands (
            id            TEXT PRIMARY KEY,                 -- Guid (instance "@ISLAND@<id>", GetIslandInfos key 0)
            owner         TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            island_type   INTEGER NOT NULL,                 -- worldsettings.xml island, 1-based (key 2)
            level         INTEGER NOT NULL,                 -- 0-5 (key 6)
            access_point  TEXT NOT NULL,                    -- "<islandaccesspoint id>@<city>" (key 1)
            bought        INTEGER NOT NULL,                 -- UTC ticks (key 8)
            level_changed INTEGER NOT NULL DEFAULT 0,       -- UTC ticks, 0 = never (key 7)
            favorite      INTEGER NOT NULL DEFAULT 0,
            hidden        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX islands_owner ON islands (owner);
        """,
        // v20 (PVE-DESIGN step 14 phase 3, islands SPEC §0.5): the player buildings of the islands and the crops in them (rows go with the island).
        """
        CREATE TABLE island_buildings (
            id            TEXT PRIMARY KEY,                 -- Guid (NewBuilding key 1)
            island        TEXT NOT NULL REFERENCES islands (id) ON DELETE CASCADE,
            name          TEXT NOT NULL,                    -- buildings.xml uniquename (key 3)
            x             REAL NOT NULL,
            z             REAL NOT NULL,
            rotation      REAL NOT NULL DEFAULT 0,
            built         INTEGER NOT NULL                  -- UTC ticks (key 8)
        );
        CREATE INDEX island_buildings_island ON island_buildings (island);
        CREATE TABLE island_farmables (
            id            TEXT PRIMARY KEY,                 -- Guid (NewBuilding key 1)
            island        TEXT NOT NULL REFERENCES islands (id) ON DELETE CASCADE,
            building      TEXT NOT NULL REFERENCES island_buildings (id) ON DELETE CASCADE,
            item          TEXT NOT NULL,                    -- the seed's items.xml uniquename (key 3)
            x             REAL NOT NULL,
            z             REAL NOT NULL,
            planted       INTEGER NOT NULL,                 -- GameTimeStamp (UTC ticks)
            boosts        INTEGER NOT NULL DEFAULT 0,       -- focus waterings (FarmableObjectInfo key 14)
            boosted_at    INTEGER NOT NULL                  -- GameTimeStamp of the last, long.MinValue = never (key 13)
        );
        CREATE INDEX island_farmables_island ON island_farmables (island);
        """,
        // v21 (analysis/work/stageJ/chat/SPEC.md §2.7): the friends (one row per pair), the open friend requests and the mute lists;
        // every row goes with either of its characters.
        """
        CREATE TABLE friends (
            a             TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE, -- the smaller Guid string of the pair
            b             TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            since         INTEGER NOT NULL,                 -- UTC ticks
            PRIMARY KEY (a, b)
        );
        CREATE INDEX friends_b ON friends (b);
        CREATE TABLE friend_requests (
            from_char     TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            to_char       TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            created       INTEGER NOT NULL,                 -- UTC ticks
            PRIMARY KEY (from_char, to_char)
        );
        CREATE INDEX friend_requests_to ON friend_requests (to_char);
        CREATE TABLE mutes (
            owner         TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            muted         TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            muted_name    TEXT NOT NULL,                    -- MutePlayerUpdate key 3
            at            INTEGER NOT NULL,                 -- UTC ticks (key 5)
            PRIMARY KEY (owner, muted)
        );
        CREATE INDEX mutes_muted ON mutes (muted);
        """,
        // v22 (analysis/work/stageJ/mail/SPEC.md §4.1): the mailboxes (a character's or a guild's; a mail goes with its owner, an
        // attachment with its mail), the player mails sent per day (the daily limit) and the amount a market order was placed with
        // (the market mails' totals).
        """
        CREATE TABLE mails (
            id            INTEGER PRIMARY KEY AUTOINCREMENT, -- from 1000000, never reused (GetMailInfos key 3; DeleteMail's upper bound)
            character     TEXT REFERENCES characters (id) ON DELETE CASCADE, -- a personal mailbox's owner, or NULL
            guild         TEXT REFERENCES guilds (id) ON DELETE CASCADE,     -- a guild mailbox's owner, or NULL
            kind          INTEGER NOT NULL,                 -- MailKind (bd3.a)
            category      INTEGER NOT NULL,                 -- MailCategory (bd3.b) 1-6
            sender        TEXT NOT NULL,                    -- a name or a cluster id (key 7)
            sender_id     TEXT,                             -- the sending character's Guid (key 4); NULL = none
            from_guild    INTEGER NOT NULL DEFAULT 0,
            subject       TEXT NOT NULL,                    -- key 11
            body          TEXT NOT NULL,                    -- ReadMail key 1
            editor        TEXT,                             -- ReadMail key 2
            received      INTEGER NOT NULL,                 -- UTC ticks (key 12)
            expires       INTEGER,                          -- UTC ticks; NULL = never
            read          INTEGER NOT NULL DEFAULT 0,
            CHECK ((character IS NULL) <> (guild IS NULL))
        );
        INSERT INTO sqlite_sequence (name, seq) VALUES ('mails', 999999);
        CREATE INDEX mails_character ON mails (character, id DESC);
        CREATE INDEX mails_guild ON mails (guild, id DESC);
        CREATE TABLE mail_attachments (
            id            TEXT PRIMARY KEY,                 -- Guid (ReadMail key 3, ClaimAttachmentFromMail key 2)
            mail          INTEGER NOT NULL REFERENCES mails (id) ON DELETE CASCADE,
            item          TEXT NOT NULL                     -- JSON InventoryItem (as market_orders.item)
        );
        CREATE INDEX mail_attachments_mail ON mail_attachments (mail);
        CREATE TABLE mail_sends (
            character     TEXT NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
            at            INTEGER NOT NULL                  -- UTC ticks; Mail:DailyLimit counts those since 00:00 UTC
        );
        CREATE INDEX mail_sends_character ON mail_sends (character, at);
        ALTER TABLE market_orders ADD COLUMN initial_amount INTEGER;                 -- the amount placed; NULL (older orders) = amount
        """,
        // v23 (analysis/work/stageJ/focus-lp/SPEC.md §5.2): the focus regeneration stamp, the learning points with their payout stamp, the
        // client's use flags and the auto-learn nodes.
        """
        ALTER TABLE characters ADD COLUMN focus_stamp INTEGER NOT NULL DEFAULT 0;     -- GameTimeStamp of the focus value, 0 = never
        ALTER TABLE characters ADD COLUMN learning_points INTEGER NOT NULL DEFAULT 0; -- a7m raw
        ALTER TABLE characters ADD COLUMN lp_stamp INTEGER NOT NULL DEFAULT 0;        -- GameTimeStamp of the last point paid, 0 = never
        ALTER TABLE characters ADD COLUMN use_flags INTEGER NOT NULL DEFAULT 3;       -- UseFlag bits (0 learning points, 1 focus, 2 respec silver)
        ALTER TABLE characters ADD COLUMN autolearn TEXT;                             -- JSON AutoLearnSet; NULL = none
        """,
        // v24 (analysis/work/stageL/corrupted/SPEC.md §4.9): the infamy of the corrupted dungeons (current and peak), runs and completions.
        """
        ALTER TABLE characters ADD COLUMN infamy TEXT;                                -- JSON InfamyLog; NULL = none
        """,
        // v25 (analysis/work/stageI/guild-goals/SPEC.md §2.8): the guild goals of a guild (seasons, weeks, member logs), read and
        // written whole as the contracts (one JSON column instead of the SPEC's four tables; it goes with its guild row).
        """
        ALTER TABLE guilds ADD COLUMN progress TEXT;                                 -- JSON GuildProgress; NULL = none yet
        """,
        // v26 (analysis/work/stageL/roads/SPEC.md §5.7): the Roads of Avalon network: the links between two exit spots, and the outgoing
        // spots without one (idle or searching) with their lifetime. The free incoming spots need no row. Times are UTC ticks (GameTimeStamps).
        """
        CREATE TABLE road_links (
            id            INTEGER PRIMARY KEY,              -- the network's own number
            out_cluster   TEXT NOT NULL,                    -- world.xml cluster of the outgoing spot (whose lifetime the link has)
            out_path      TEXT NOT NULL,                    -- its criticalObjects path (instanceslot_00/tunnelexit_01)
            in_cluster    TEXT NOT NULL,
            in_path       TEXT NOT NULL,
            type          INTEGER NOT NULL,                 -- tunnelexittype index (SOLO 0, GROUP 1, RAID 2; NewTunnelExit key 5)
            opened        INTEGER NOT NULL,                 -- key 7
            closes        INTEGER NOT NULL,                 -- key 8
            charges       INTEGER NOT NULL,                 -- key 9
            next_charge   INTEGER NOT NULL DEFAULT 0,       -- key 11; 0 = full
            recent_until  INTEGER NOT NULL DEFAULT 0        -- key 12; 0 = never used
        );
        CREATE TABLE road_slots (
            cluster       TEXT NOT NULL,
            path          TEXT NOT NULL,
            state         INTEGER NOT NULL,                 -- 1 searching (with its keyword), 2 idle
            keyword       TEXT,
            closes        INTEGER NOT NULL,                 -- the end of its lifetime: then it rolls again
            PRIMARY KEY (cluster, path)
        );
        """,
        // v27 (analysis/work/stageL/mists/SPEC.md §6.7 phase B): the Brecilien standing (StandingType MistCity) and the favour spent at its City Trader.
        """
        ALTER TABLE characters ADD COLUMN mist_standing INTEGER NOT NULL DEFAULT 0;   -- a7m raw (UpdateStanding 87 key 1)
        ALTER TABLE characters ADD COLUMN mist_favor INTEGER NOT NULL DEFAULT 0;      -- a7m raw (the trader's FAVOR currency)
        """,
        // v28 (analysis/work/stageN/mounts/SPEC.md): island animals: the nutrition fed and the growth (or product progress) at a stamp.
        """
        ALTER TABLE island_farmables ADD COLUMN nutrition REAL NOT NULL DEFAULT 0;    -- food units left at fed_at
        ALTER TABLE island_farmables ADD COLUMN fed_at INTEGER NOT NULL DEFAULT 0;    -- GameTimeStamp of nutrition and growth (0 = planted)
        ALTER TABLE island_farmables ADD COLUMN growth INTEGER NOT NULL DEFAULT 0;    -- ticks grown (a baby) or produced (a grown animal) at fed_at
        """,
        // v29 (analysis/work/stageN/qol/SPEC.md §2): the character's saved loadouts.
        """
        ALTER TABLE characters ADD COLUMN loadouts TEXT;                             -- JSON Loadout[]; NULL = none
        """,
        // v30 (analysis/work/stageN/faction/SPEC.md §2): the faction standing and points per faction, and the last enlistment change.
        """
        ALTER TABLE characters ADD COLUMN faction_standing TEXT;                     -- JSON a7m[6] by FlaggingStatus - 1; NULL = none
        ALTER TABLE characters ADD COLUMN faction_points TEXT;                       -- JSON a7m[6] (the FACTION_* currencies); NULL = none
        ALTER TABLE characters ADD COLUMN faction_changed_at INTEGER NOT NULL DEFAULT 0; -- GameTimeStamp of the last enlistment change; 0 = never
        """,
        // v31 (analysis/work/stageN/econ/SPEC.md §1): the market's sales per
        // city, for the price-history chart (AuctionGetItemAverageStats). IF NOT EXISTS: safe at whichever position the merge gives it.
        """
        CREATE TABLE IF NOT EXISTS market_sales (
            location    TEXT NOT NULL,                      -- the marketplace's cluster id (per city, as the orders)
            uniquename  TEXT NOT NULL,                      -- ItemTypeId (T4_X@n)
            quality     INTEGER NOT NULL,
            amount      INTEGER NOT NULL,
            unit_price  INTEGER NOT NULL,                   -- a7m raw
            at          INTEGER NOT NULL                    -- UTC ticks (GameTimeStamp)
        );
        CREATE INDEX IF NOT EXISTS market_sales_item ON market_sales (location, uniquename, at);
        """,
        // v32 (analysis/work/stageN/arena/SPEC.md §5): the duel and arena ladders (Elo), one row per character and ladder.
        // IF NOT EXISTS: safe at whichever position the merge gives it.
        """
        CREATE TABLE IF NOT EXISTS ratings (
            character_id TEXT NOT NULL,                     -- Character.Id
            ladder       TEXT NOT NULL,                     -- duel, 1v1, 2v2, 5v5
            name         TEXT NOT NULL,                     -- the character's name at its last result
            rating       INTEGER NOT NULL,
            wins         INTEGER NOT NULL DEFAULT 0,
            losses       INTEGER NOT NULL DEFAULT 0,
            draws        INTEGER NOT NULL DEFAULT 0,
            updated      INTEGER NOT NULL DEFAULT 0,        -- UTC ticks of the last result
            PRIMARY KEY (character_id, ladder)
        );
        CREATE INDEX IF NOT EXISTS ratings_ladder ON ratings (ladder, rating);
        """,
        // v33 (analysis/work/stageN/labourers/SPEC.md §2.5): the labourers living in the houses of the islands (rows go with the house).
        // IF NOT EXISTS: safe at whichever position the merge gives it (the furniture of the houses are island_farmables rows).
        """
        CREATE TABLE IF NOT EXISTS island_labourers (
            id            TEXT PRIMARY KEY,                 -- Guid (NewBuilding key 1)
            island        TEXT NOT NULL REFERENCES islands (id) ON DELETE CASCADE,
            building      TEXT NOT NULL REFERENCES island_buildings (id) ON DELETE CASCADE, -- the house
            type          TEXT NOT NULL,                    -- buildings.xml labourer uniquename (T2_LABOURER_WOOD)
            name          TEXT NOT NULL,
            x             REAL NOT NULL,
            z             REAL NOT NULL,
            rotation      REAL NOT NULL DEFAULT 0,
            hired         INTEGER NOT NULL,                 -- GameTimeStamp
            fame          INTEGER NOT NULL DEFAULT 0,       -- labourer fame, a7m raw
            happiness     REAL NOT NULL DEFAULT 0,          -- whole happiness at happiness_at
            happiness_at  INTEGER NOT NULL DEFAULT 0,
            journal       TEXT,                             -- the _FULL journal of the job; NULL = none
            job_start     INTEGER NOT NULL DEFAULT 0,
            job_end       INTEGER NOT NULL DEFAULT 0,
            job_yield     REAL NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS island_labourers_island ON island_labourers (island);
        """,
        // v34 (analysis/work/stageN/awakening/SPEC.md §2): an awakened weapon's legendary soul (traits, attunement) per item row.
        // ADD COLUMN only, independent of the other steps: safe at whichever position the merge gives it.
        """
        ALTER TABLE items ADD COLUMN soul TEXT;                                      -- JSON ItemSoul; NULL = not awakened
        ALTER TABLE bank_items ADD COLUMN soul TEXT;
        """,
        // v35 (analysis/work/stageN/vanity/SPEC.md §2): the cosmetics of a character (unlocks, wardrobe, mount skins), one JSON column.
        """
        ALTER TABLE characters ADD COLUMN vanity TEXT;                               -- JSON VanityLog; NULL = none
        """,
        // v36: online duration for Login key 34. Existing saves have no reliable historic counter.
        """
        ALTER TABLE characters ADD COLUMN played_ms INTEGER NOT NULL DEFAULT 0 CHECK (played_ms >= 0);
        """,
        // v37: opt-in, bounded opening tutorial; NULL preserves existing characters.
        """
        ALTER TABLE characters ADD COLUMN tutorial_opening TEXT;
        """,
        // v38 (analysis/work/stageP/hideouts/SPEC.md §7 A1/A2): the alliances, their guilds (a guild's row goes with the guild), the open
        // invitations (the kind-2 mail in the invited guild's mailbox) and each guild's alliance history (the change and rejoin cooldowns).
        """
        CREATE TABLE IF NOT EXISTS alliances (
            id            TEXT PRIMARY KEY,                 -- Guid (GuildUpdate key 17)
            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
            tag           TEXT NOT NULL UNIQUE COLLATE NOCASE,
            founder       TEXT NOT NULL,                    -- the founder guild's Guid (AllianceGetMemberInfos key 0)
            created       INTEGER NOT NULL                  -- UTC ticks
        );
        CREATE TABLE IF NOT EXISTS alliance_guilds (
            guild         TEXT PRIMARY KEY REFERENCES guilds (id) ON DELETE CASCADE,
            alliance      TEXT NOT NULL REFERENCES alliances (id) ON DELETE CASCADE,
            joined        INTEGER NOT NULL,                 -- UTC ticks ("Member since")
            position      INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS alliance_guilds_alliance ON alliance_guilds (alliance, position);
        CREATE TABLE IF NOT EXISTS alliance_invites (
            alliance      TEXT NOT NULL REFERENCES alliances (id) ON DELETE CASCADE,
            guild         TEXT NOT NULL REFERENCES guilds (id) ON DELETE CASCADE,
            mail          INTEGER NOT NULL,                 -- the invitation mail's id (AllianceAnswerInvitation key 0); 0 = none
            invited       INTEGER NOT NULL,                 -- UTC ticks
            PRIMARY KEY (alliance, guild)
        );
        CREATE TABLE IF NOT EXISTS alliance_history (
            guild         TEXT PRIMARY KEY REFERENCES guilds (id) ON DELETE CASCADE,
            changed       INTEGER NOT NULL,                 -- UTC ticks of the last found or join
            last_alliance TEXT,                             -- the alliance it last left; NULL = none
            left_at       INTEGER                           -- UTC ticks; NULL = never left one
        );
        """,
        // v39 (analysis/work/stageP/territories/SPEC.md P5): the owners of the castles and castle outposts (and later the watchtower
        // territories), one row per owned world.xml territory; a guild's rows go with the guild.
        """
        CREATE TABLE IF NOT EXISTS territory_owners (
            territory     TEXT PRIMARY KEY,                 -- world.xml territory Guid (NewCastleObject key 3)
            guild         TEXT NOT NULL REFERENCES guilds (id) ON DELETE CASCADE,
            since         INTEGER NOT NULL                  -- UTC ticks of the capture
        );
        """,
        // v40 (analysis/work/stageP/hideouts/SPEC.md H1-H4): the guild hideouts (a guild's rows go with the guild) and the characters whose
        // home is a hideout (Make Home; the row goes with the character or the hideout).
        """
        CREATE TABLE IF NOT EXISTS hideouts (
            id              TEXT PRIMARY KEY,                 -- Guid (NewHideoutObject key 1, the instance "@HIDEOUT@<id>")
            guild           TEXT NOT NULL REFERENCES guilds (id) ON DELETE CASCADE,
            cluster         TEXT NOT NULL,                    -- the surface cluster (world.xml id)
            x               REAL NOT NULL,
            z               REAL NOT NULL,
            rotation        REAL NOT NULL DEFAULT 0,
            placed          INTEGER NOT NULL,                 -- GameTimeStamp
            phase           INTEGER NOT NULL DEFAULT 0,       -- construction phase index; -1 = built
            phase_ends      INTEGER NOT NULL DEFAULT 0,       -- GameTimeStamp
            level           INTEGER NOT NULL DEFAULT 0,       -- 0 = HIDEOUT-0001a … 2 = c
            upgrade_started INTEGER NOT NULL DEFAULT 0,       -- GameTimeStamp; 0 = no upgrade running
            nutrition       REAL NOT NULL DEFAULT 0,          -- whole nutrition stored
            shields         INTEGER NOT NULL DEFAULT 0,
            last_upkeep     INTEGER NOT NULL DEFAULT 0,       -- GameTimeStamp of the last daily upkeep
            tribute         INTEGER NOT NULL DEFAULT 0,       -- a7m per day
            access          INTEGER NOT NULL DEFAULT 1        -- preset: 0 closed, 1 private, 2 public
        );
        CREATE INDEX IF NOT EXISTS hideouts_guild ON hideouts (guild);
        CREATE TABLE IF NOT EXISTS hideout_homes (
            character       TEXT PRIMARY KEY REFERENCES characters (id) ON DELETE CASCADE,
            hideout         TEXT NOT NULL REFERENCES hideouts (id) ON DELETE CASCADE
        );
        """,
        // v41 (analysis/work/stageP/territories/SPEC.md P3/P4): a claimed watchtower territory's food, access and tower power points, one row
        // per owner row (it goes with it). IF NOT EXISTS: safe at whichever position the merge gives it.
        """
        CREATE TABLE IF NOT EXISTS territory_state (
            territory     TEXT PRIMARY KEY REFERENCES territory_owners (territory) ON DELETE CASCADE,
            nutrition     REAL NOT NULL DEFAULT 0,          -- whole nutrition at fed_at
            fed_at        INTEGER NOT NULL DEFAULT 0,       -- UTC ticks
            access        INTEGER NOT NULL DEFAULT 0,       -- ChangeTerritoryAccessRights key 1
            power         REAL NOT NULL DEFAULT 0,          -- tower power points at power_at
            power_at      INTEGER NOT NULL DEFAULT 0        -- UTC ticks
        );
        """,
        // v42 (analysis/work/stageP/league-pvp/SPEC.md §3.3, P7): the arena ranks of the ranked categories (1 the Crystal Arena, 2 the 1v1), one
        // row per character and category: the season, the rank index (0 = ARENA_IRON_1 … 16 = ARENA_CRYSTAL), the rank points and the highest
        // rank whose rank-up chest was given. IF NOT EXISTS: safe at whichever position the merge gives it.
        """
        CREATE TABLE IF NOT EXISTS arena_ranks (
            character_id TEXT NOT NULL,                     -- Character.Id
            category     INTEGER NOT NULL,                  -- arena.bin arenacategory index
            season       INTEGER NOT NULL DEFAULT 0,        -- arenaseason index
            rank         INTEGER NOT NULL DEFAULT 0,
            points       INTEGER NOT NULL DEFAULT 0,
            peak         INTEGER NOT NULL DEFAULT 0,
            updated      INTEGER NOT NULL DEFAULT 0,        -- UTC ticks of the last result
            PRIMARY KEY (character_id, category)
        );
        """,
        // v43: the most recently fame-fed Destiny Board HUD node. Existing saves select one on their next gain.
        """
        ALTER TABLE progression ADD COLUMN tracked INTEGER;
        """,
    ];

    private const string AccountColumns = "id, level, name, display_name, password, failed_logins, locked_until, banned_until, ban_reason, created";

    private const string CharacterColumns =
        "id, name, template, appearance, customization, clothes, cluster, position_x, position_y, created, equipment_container, inventory_container, "
        + "direction, previous_cluster, last_left_at, last_city, pending_transfer, fame, silver, gold, effects, cooldowns, focus, mounted_since, mount_cooldown, "
        + "flag, faction, reputation, reputation_at, dungeon_return, journal_tracked, expeditions, goals, black_market, "
        + "focus_stamp, learning_points, lp_stamp, use_flags, autolearn, infamy, mist_standing, mist_favor, loadouts, "
        + "faction_standing, faction_points, faction_changed_at, vanity, played_ms, tutorial_opening";

    private readonly object _sync = new();
    private readonly SqliteConnection _db;

    /// <param name="localLevel">The level the local account gets when this opens a new database (config Accounts:LocalAccountLevel);
    /// an existing local account keeps its stored level.</param>
    public SqliteCharacterRepository(string path, AccountLevel localLevel = AccountLevel.Admin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // No pooling: Dispose closes the file, so a test can delete its folder and a second server can open it.
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using (var fk = Command("PRAGMA foreign_keys = ON", null)) fk.ExecuteNonQuery(); // per connection; items follow their character
        Migrate(path);
        using (var add = Command("INSERT OR IGNORE INTO accounts (id, level, created) VALUES ($id, $level, $created)", null,
                   ("$id", LocalAccountId.ToString()), ("$level", (int)localLevel), ("$created", DateTime.UtcNow.Ticks)))
            add.ExecuteNonQuery();
        Account = FindAccount(LocalAccountId)!;
    }

    public Account Account { get; }

    public Account? FindAccount(Guid id)
    {
        lock (_sync) return ReadAccounts($"SELECT {AccountColumns} FROM accounts WHERE id = $id", null, ("$id", id.ToString())).FirstOrDefault();
    }

    public Account? FindAccount(string name)
    {
        lock (_sync) return ReadAccounts($"SELECT {AccountColumns} FROM accounts WHERE name = $name", null, ("$name", Account.KeyOf(name))).FirstOrDefault();
    }

    public IReadOnlyList<Account> Accounts()
    {
        lock (_sync) return ReadAccounts($"SELECT {AccountColumns} FROM accounts ORDER BY id != $local, created, rowid", null, ("$local", LocalAccountId.ToString()));
    }

    public Account? CreateAccount(string name, string? passwordHash, AccountLevel level)
    {
        var key = Account.KeyOf(name);
        if (key.Length == 0) return null;
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var taken = Command("SELECT count(*) FROM accounts WHERE name = $name", tx, ("$name", key)))
                if (Convert.ToInt64(taken.ExecuteScalar()) > 0) return null;
            var id = Guid.NewGuid();
            using (var add = Command("INSERT INTO accounts (id, level, name, display_name, password, created) VALUES ($id, $level, $name, $display, $password, $created)", tx,
                       ("$id", id.ToString()), ("$level", (int)level), ("$name", key), ("$display", name.Trim()), ("$password", passwordHash),
                       ("$created", DateTime.UtcNow.Ticks)))
                add.ExecuteNonQuery();
            var account = ReadAccounts($"SELECT {AccountColumns} FROM accounts WHERE id = $id", tx, ("$id", id.ToString())).First();
            tx.Commit();
            return account;
        }
    }

    public Account? ClaimLocalAccount(string name)
    {
        var key = Account.KeyOf(name);
        if (key.Length == 0) return null;
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var taken = Command("SELECT count(*) FROM accounts WHERE name = $name", tx, ("$name", key)))
                if (Convert.ToInt64(taken.ExecuteScalar()) > 0) return null;
            using (var claim = Command("UPDATE accounts SET name = $name, display_name = $display WHERE id = $id AND name IS NULL", tx,
                       ("$name", key), ("$display", name.Trim()), ("$id", LocalAccountId.ToString())))
                if (claim.ExecuteNonQuery() != 1) return null;
            var account = ReadAccounts($"SELECT {AccountColumns} FROM accounts WHERE id = $id", tx, ("$id", LocalAccountId.ToString())).First();
            tx.Commit();
            return account;
        }
    }

    public bool UpdateAccount(Account account)
    {
        lock (_sync)
        {
            using var update = Command("""
                UPDATE accounts SET level = $level, password = $password, failed_logins = $failed, locked_until = $locked,
                    banned_until = $banned, ban_reason = $reason
                WHERE id = $id
                """, null, ("$id", account.Id.ToString()), ("$level", (int)account.Level), ("$password", account.PasswordHash),
                ("$failed", account.FailedLogins), ("$locked", account.LockedUntil?.Ticks), ("$banned", account.BannedUntil?.Ticks),
                ("$reason", account.BanReason));
            return update.ExecuteNonQuery() == 1;
        }
    }

    public bool SetAccountLevel(Guid id, AccountLevel level)
    {
        lock (_sync)
        {
            using var update = Command("UPDATE accounts SET level = $level WHERE id = $id", null, ("$id", id.ToString()), ("$level", (int)level));
            return update.ExecuteNonQuery() == 1;
        }
    }

    public bool DeleteAccount(Guid id)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var characters = Command("DELETE FROM characters WHERE account = $id", tx, ("$id", id.ToString())))
                characters.ExecuteNonQuery(); // items and progression follow (ON DELETE CASCADE)
            using var account = id == LocalAccountId
                ? Command("""
                    UPDATE accounts SET name = NULL, display_name = NULL, password = NULL, failed_logins = 0, locked_until = NULL,
                        banned_until = NULL, ban_reason = ''
                    WHERE id = $id
                    """, tx, ("$id", id.ToString()))
                : Command("DELETE FROM accounts WHERE id = $id", tx, ("$id", id.ToString()));
            var found = account.ExecuteNonQuery() == 1;
            tx.Commit();
            return found;
        }
    }

    public IReadOnlyList<Character> List()
    {
        lock (_sync) return Query($"SELECT {CharacterColumns}, account FROM characters ORDER BY rowid");
    }

    public IReadOnlyList<Character> List(Guid account)
    {
        lock (_sync) return Query($"SELECT {CharacterColumns}, account FROM characters WHERE account = $account ORDER BY rowid", ("$account", account.ToString()));
    }

    public Character? Find(Guid id)
    {
        lock (_sync) return Query($"SELECT {CharacterColumns}, account FROM characters WHERE id = $id", ("$id", id.ToString())).FirstOrDefault();
    }

    public Character? Find(string name)
    {
        lock (_sync) return Query($"SELECT {CharacterColumns}, account FROM characters WHERE name = $name", ("$name", name)).FirstOrDefault();
    }

    public bool Create(Character c) => Import([c]) == 1;

    public int Import(IEnumerable<Character> characters)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            var created = 0;
            foreach (var c in characters)
            {
                if (c.Account == Guid.Empty) c.Account = Account.Id; // no account given: the local one
                using (var taken = Command("SELECT count(*) FROM characters WHERE id = $id OR name = $name", tx, ("$id", c.Id.ToString()), ("$name", c.Name)))
                    if (Convert.ToInt64(taken.ExecuteScalar()) > 0) continue;
                using (var insert = Command($"""
                        INSERT INTO characters (account, {CharacterColumns})
                        VALUES ($account, $id, $name, $template, $appearance, $customization, $clothes, $cluster, $x, $y, $created, $eq, $inv,
                            $direction, $previous, $left, $city, $transfer, $fame, $silver, $gold, $effects, $cooldowns, $focus, $mounted, $mountcd,
                            $flag, $faction, $reputation, $reputationat, $dungeon, $journal, $expeditions, $goals, $bm,
                            $focusstamp, $lp, $lpstamp, $useflags, $autolearn, $infamy, $miststanding, $mistfavor, $loadouts,
                            $fstanding, $fpoints, $fchanged, $vanity, $played, $tutorial)
                        """, tx, [("$account", c.Account.ToString()), .. CharacterValues(c)]))
                    insert.ExecuteNonQuery();
                WriteContents(c, tx);
                created++;
            }
            tx.Commit();
            return created;
        }
    }

    public bool Save(Character c)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // disposed without Commit = rolled back
            if (!Write(c, tx)) return false;
            tx.Commit();
            return true;
        }
    }

    public int SaveMany(IReadOnlyList<Character> cs)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction(); // one Commit for all: a crash or an exception writes none of them
            var stored = cs.Count(c => Write(c, tx));
            tx.Commit();
            return stored;
        }
    }

    /// <summary>The body of <see cref="Save"/> inside <paramref name="tx"/>; false (nothing written) when the character no longer exists.</summary>
    private bool Write(Character c, SqliteTransaction tx)
    {
        using (var update = Command("""
                UPDATE characters SET name = $name, template = $template, appearance = $appearance, customization = $customization,
                    clothes = $clothes, cluster = $cluster, position_x = $x, position_y = $y, created = $created,
                    equipment_container = $eq, inventory_container = $inv, direction = $direction, previous_cluster = $previous,
                    last_left_at = $left, last_city = $city, pending_transfer = $transfer, fame = $fame,
                    silver = $silver, gold = $gold, effects = $effects, cooldowns = $cooldowns, focus = $focus,
                    mounted_since = $mounted, mount_cooldown = $mountcd, flag = $flag, faction = $faction,
                    reputation = $reputation, reputation_at = $reputationat, dungeon_return = $dungeon,
                    journal_tracked = $journal, expeditions = $expeditions, goals = $goals, black_market = $bm,
                    focus_stamp = $focusstamp, learning_points = $lp, lp_stamp = $lpstamp, use_flags = $useflags, autolearn = $autolearn,
                    infamy = $infamy, mist_standing = $miststanding, mist_favor = $mistfavor, loadouts = $loadouts,
                    faction_standing = $fstanding, faction_points = $fpoints, faction_changed_at = $fchanged,
                    vanity = $vanity, played_ms = $played, tutorial_opening = $tutorial
                WHERE id = $id
                """, tx, CharacterValues(c)))
            if (update.ExecuteNonQuery() == 0) return false;
        using (var items = Command("DELETE FROM items WHERE character = $id", tx, ("$id", c.Id.ToString()))) items.ExecuteNonQuery();
        using (var progression = Command("DELETE FROM progression WHERE character = $id", tx, ("$id", c.Id.ToString()))) progression.ExecuteNonQuery();
        using (var bank = Command("DELETE FROM bank_items WHERE character = $id", tx, ("$id", c.Id.ToString()))) bank.ExecuteNonQuery();
        WriteContents(c, tx);
        return true;
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            using var delete = Command("DELETE FROM characters WHERE name = $name", null, ("$name", name));
            return delete.ExecuteNonQuery() > 0;
        }
    }

    public void Dispose()
    {
        lock (_sync) _db.Dispose();
    }

    /// <summary>Runs the schema steps the file has not had yet, each with its version bump in one transaction.</summary>
    private void Migrate(string path)
    {
        using var get = Command("PRAGMA user_version", null);
        var version = Convert.ToInt32(get.ExecuteScalar());
        if (version > Migrations.Length)
            throw new InvalidDataException($"{path}: schema version {version} is newer than this server's ({Migrations.Length})");
        for (; version < Migrations.Length; version++)
        {
            using var tx = _db.BeginTransaction();
            using (var step = Command(Migrations[version], tx)) step.ExecuteNonQuery();
            using (var set = Command($"PRAGMA user_version = {version + 1}", tx)) set.ExecuteNonQuery();
            tx.Commit();
            Log.Info("db", $"{path}: schema v{version + 1}");
            if (version + 1 == 5) NormalizeNames();
        }
    }

    private static (string, object?)[] CharacterValues(Character c) =>
    [
        ("$id", c.Id.ToString()), ("$name", c.Name), ("$template", c.Template), ("$appearance", (int)c.Appearance),
        ("$customization", JsonSerializer.Serialize(c.Customization)), ("$clothes", JsonSerializer.Serialize(c.Clothes)),
        ("$cluster", c.Cluster), ("$x", (double)c.PositionX), ("$y", (double)c.PositionY), ("$created", c.Created.Ticks),
        ("$eq", c.EquipmentContainer.ToString()), ("$inv", c.InventoryContainer.ToString()),
        ("$direction", (double)c.Direction), ("$previous", c.PreviousCluster), ("$left", c.LastLeftAt), ("$city", c.LastCity),
        ("$transfer", c.PendingTransfer is { } t ? JsonSerializer.Serialize(t) : null), ("$fame", c.Fame),
        ("$silver", c.Silver), ("$gold", c.Gold),
        ("$effects", JsonSerializer.Serialize(c.Effects)), ("$cooldowns", JsonSerializer.Serialize(c.Cooldowns)), ("$focus", c.CraftingFocus),
        ("$mounted", c.MountedSince), ("$mountcd", c.MountCooldownUntil),
        ("$flag", (int)c.Flag), ("$faction", (int)c.Faction), ("$reputation", c.Reputation), ("$reputationat", c.ReputationAt),
        ("$dungeon", c.DungeonReturn is { } d ? JsonSerializer.Serialize(d) : null),
        ("$journal", c.JournalTracked is { } j ? JsonSerializer.Serialize(j) : null),
        ("$expeditions", c.Expeditions is { } x ? JsonSerializer.Serialize(x) : null),
        ("$goals", c.Goals is { } g ? JsonSerializer.Serialize(g) : null),
        ("$bm", c.BlackMarket is { } bm ? JsonSerializer.Serialize(bm) : null),
        ("$focusstamp", c.FocusStamp), ("$lp", c.LearningPoints), ("$lpstamp", c.LpStamp), ("$useflags", c.UseFlags),
        ("$autolearn", c.AutoLearn is { } al ? JsonSerializer.Serialize(al) : null),
        ("$infamy", c.Infamy is { } inf ? JsonSerializer.Serialize(inf) : null),
        ("$miststanding", c.MistStanding), ("$mistfavor", c.MistFavor),
        ("$loadouts", c.Loadouts.Count > 0 ? JsonSerializer.Serialize(c.Loadouts) : null),
        ("$fstanding", c.FactionStanding.Any(x => x != 0) ? JsonSerializer.Serialize(c.FactionStanding) : null),
        ("$fpoints", c.FactionPoints.Any(x => x != 0) ? JsonSerializer.Serialize(c.FactionPoints) : null),
        ("$fchanged", c.FactionChangedAt),
        ("$vanity", c.Vanity is { } v ? JsonSerializer.Serialize(v) : null),
        ("$played", c.PlayedMilliseconds),
        ("$tutorial", c.Tutorial is null ? null : JsonSerializer.Serialize(c.Tutorial)),
    ];

    /// <summary>Inserts the items and the destiny board state of <paramref name="c"/> (the old rows are gone).</summary>
    private void WriteContents(Character c, SqliteTransaction tx)
    {
        using var insert = Command("""
            INSERT INTO items (character, container, slot, uniquename, count, quality, durability, active, passive, fame, soul)
            VALUES ($id, $container, $slot, $name, $count, $quality, $durability, $active, $passive, $fame, $soul)
            """, tx, ("$id", c.Id.ToString()), ("$container", 0), ("$slot", 0), ("$name", null), ("$count", 0), ("$quality", 0),
            ("$durability", null), ("$active", null), ("$passive", null), ("$fame", null), ("$soul", null));
        void Put(int container, int slot, InventoryItem it)
        {
            var p = insert.Parameters;
            p["$container"].Value = container;
            p["$slot"].Value = slot;
            p["$name"].Value = (object?)it.Name ?? DBNull.Value;
            p["$count"].Value = it.Count;
            p["$quality"].Value = it.Quality;
            p["$durability"].Value = (object?)it.Durability ?? DBNull.Value;
            p["$active"].Value = it.Active is null ? DBNull.Value : JsonSerializer.Serialize(it.Active);
            p["$passive"].Value = it.Passive is null ? DBNull.Value : JsonSerializer.Serialize(it.Passive);
            p["$fame"].Value = (object?)it.Fame ?? DBNull.Value;
            p["$soul"].Value = it.Soul is null ? DBNull.Value : JsonSerializer.Serialize(it.Soul);
            insert.ExecuteNonQuery();
        }
        for (var slot = 0; slot < c.Equipment.Length; slot++)
            if (c.Equipment[slot] is { } it) Put(EquipmentContainer, slot, it);
        for (var slot = 0; slot < c.Inventory.Count; slot++)
            if (c.Inventory[slot] is { } it) Put(InventoryContainer, slot, it);
        if (c.Vaults.Count > 0)
        {
            using var bank = Command("""
                INSERT INTO bank_items (character, location, tab, slot, uniquename, count, quality, durability, active, passive, fame, soul)
                VALUES ($id, $location, $tab, $slot, $name, $count, $quality, $durability, $active, $passive, $fame, $soul)
                """, tx, ("$id", c.Id.ToString()), ("$location", ""), ("$tab", ""), ("$slot", 0), ("$name", ""), ("$count", 0), ("$quality", 0),
                ("$durability", null), ("$active", null), ("$passive", null), ("$fame", null), ("$soul", null));
            foreach (var (key, slots) in c.Vaults)
            {
                var bar = key.LastIndexOf('|');
                for (var slot = 0; slot < slots.Count; slot++)
                {
                    if (slots[slot] is not { } it) continue;
                    var p = bank.Parameters;
                    p["$location"].Value = key[..bar];
                    p["$tab"].Value = key[(bar + 1)..];
                    p["$slot"].Value = slot;
                    p["$name"].Value = it.Name;
                    p["$count"].Value = it.Count;
                    p["$quality"].Value = it.Quality;
                    p["$durability"].Value = (object?)it.Durability ?? DBNull.Value;
                    p["$active"].Value = it.Active is null ? DBNull.Value : JsonSerializer.Serialize(it.Active);
                    p["$passive"].Value = it.Passive is null ? DBNull.Value : JsonSerializer.Serialize(it.Passive);
                    p["$fame"].Value = (object?)it.Fame ?? DBNull.Value;
                    p["$soul"].Value = it.Soul is null ? DBNull.Value : JsonSerializer.Serialize(it.Soul);
                    bank.ExecuteNonQuery();
                }
            }
        }
        if (c.Progression is not { } progression) return;
        using var put = Command("INSERT INTO progression (character, completed, levels, progress, tracked) VALUES ($id, $completed, $levels, $progress, $tracked)", tx,
            ("$id", c.Id.ToString()), ("$completed", JsonSerializer.Serialize(progression.Completed)),
            ("$levels", JsonSerializer.Serialize(progression.Levels)), ("$progress", JsonSerializer.Serialize(progression.Progress)), ("$tracked", progression.Tracked));
        put.ExecuteNonQuery();
    }

    /// <summary>The account rows a SELECT of <see cref="AccountColumns"/> returns.</summary>
    private List<Account> ReadAccounts(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var select = Command(sql, tx, args);
        using var r = select.ExecuteReader();
        var list = new List<Account>();
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateTime? Time(int i) => r.IsDBNull(i) ? null : new DateTime(r.GetInt64(i), DateTimeKind.Utc);
        while (r.Read())
            list.Add(new Account(Guid.Parse(r.GetString(0)), (AccountLevel)r.GetInt32(1))
            {
                Name = Text(2), DisplayName = Text(3), PasswordHash = Text(4), FailedLogins = r.GetInt32(5), LockedUntil = Time(6),
                BannedUntil = Time(7), BanReason = r.GetString(8), Created = new DateTime(r.GetInt64(9), DateTimeKind.Utc),
            });
        return list;
    }

    /// <summary>
    /// After schema step v5: SQLite's lower() lowers ASCII only, so a login key with other letters is lowered here
    /// (<see cref="Account.KeyOf"/>); one that would then clash with another account's stays as it was, with a warning.
    /// </summary>
    private void NormalizeNames()
    {
        var names = new List<(string Id, string Name)>();
        using (var select = Command("SELECT id, name FROM accounts WHERE name IS NOT NULL", null))
        using (var r = select.ExecuteReader())
            while (r.Read()) names.Add((r.GetString(0), r.GetString(1)));
        foreach (var (id, name) in names)
        {
            var key = Account.KeyOf(name);
            if (key == name) continue;
            try
            {
                using var update = Command("UPDATE accounts SET name = $name WHERE id = $id", null, ("$name", key), ("$id", id));
                update.ExecuteNonQuery();
            }
            catch (SqliteException) { Log.Warn("db", $"account {id}: its login key clashes with another account's in lower case; kept as it was"); }
        }
    }

    /// <summary>The characters a SELECT of <see cref="CharacterColumns"/> and account returns, each with its items and progression.</summary>
    private List<Character> Query(string sql, params (string Name, object? Value)[] args)
    {
        var list = new List<Character>();
        using (var select = Command(sql, null, args))
        using (var r = select.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new Character
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Name = r.GetString(1),
                    Template = r.GetInt32(2),
                    Appearance = (byte)r.GetInt32(3),
                    Customization = JsonSerializer.Deserialize<int[]>(r.GetString(4)) ?? [],
                    Clothes = JsonSerializer.Deserialize<int[]>(r.GetString(5)) ?? [],
                    Cluster = r.GetString(6),
                    PositionX = (float)r.GetDouble(7),
                    PositionY = (float)r.GetDouble(8),
                    Created = new DateTime(r.GetInt64(9), DateTimeKind.Utc),
                    EquipmentContainer = Guid.Parse(r.GetString(10)),
                    InventoryContainer = Guid.Parse(r.GetString(11)),
                    Direction = (float)r.GetDouble(12),
                    PreviousCluster = r.GetString(13),
                    LastLeftAt = r.GetInt64(14),
                    LastCity = r.GetString(15),
                    PendingTransfer = r.IsDBNull(16) ? null : JsonSerializer.Deserialize<PendingTransfer>(r.GetString(16)),
                    Fame = r.GetInt64(17),
                    Silver = r.GetInt64(18),
                    Gold = r.GetInt64(19),
                    Effects = JsonSerializer.Deserialize<List<SavedEffect>>(r.GetString(20)) ?? [],
                    Cooldowns = JsonSerializer.Deserialize<List<SavedCooldown>>(r.GetString(21)) ?? [],
                    CraftingFocus = r.GetDouble(22),
                    MountedSince = r.GetInt64(23),
                    MountCooldownUntil = r.GetInt64(24),
                    Flag = (byte)r.GetInt32(25),
                    Faction = (byte)r.GetInt32(26),
                    Reputation = r.GetDouble(27),
                    ReputationAt = r.GetInt64(28),
                    DungeonReturn = r.IsDBNull(29) ? null : JsonSerializer.Deserialize<DungeonReturn>(r.GetString(29)),
                    JournalTracked = r.IsDBNull(30) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(30)),
                    Expeditions = r.IsDBNull(31) ? null : JsonSerializer.Deserialize<ExpeditionLog>(r.GetString(31)),
                    Goals = r.IsDBNull(32) ? null : JsonSerializer.Deserialize<GoalLog>(r.GetString(32)),
                    BlackMarket = r.IsDBNull(33) ? null : JsonSerializer.Deserialize<BlackMarketLog>(r.GetString(33)),
                    FocusStamp = r.GetInt64(34),
                    LearningPoints = r.GetInt64(35),
                    LpStamp = r.GetInt64(36),
                    UseFlags = r.GetInt32(37),
                    AutoLearn = r.IsDBNull(38) ? null : JsonSerializer.Deserialize<AutoLearnSet>(r.GetString(38)),
                    Infamy = r.IsDBNull(39) ? null : JsonSerializer.Deserialize<InfamyLog>(r.GetString(39)),
                    MistStanding = r.GetInt64(40),
                    MistFavor = r.GetInt64(41),
                    Loadouts = r.IsDBNull(42) ? [] : JsonSerializer.Deserialize<List<Loadout>>(r.GetString(42)) ?? [],
                    FactionStanding = Six(r, 43),
                    FactionPoints = Six(r, 44),
                    FactionChangedAt = r.GetInt64(45),
                    Vanity = r.IsDBNull(46) ? null : JsonSerializer.Deserialize<VanityLog>(r.GetString(46)),
                    PlayedMilliseconds = r.GetInt64(47),
                    Tutorial = r.IsDBNull(48) ? null : JsonSerializer.Deserialize<OpeningTutorialProgress>(r.GetString(48)),
                    Account = Guid.Parse(r.GetString(49)),

                });
            }
        }
        foreach (var c in list) ReadContents(c);
        return list;
    }

    /// <summary>A JSON a7m[6] column (v30 faction standing / points); NULL or another length reads as six zeros.</summary>
    private static long[] Six(SqliteDataReader r, int column)
    {
        var six = new long[6];
        if (!r.IsDBNull(column) && JsonSerializer.Deserialize<long[]>(r.GetString(column)) is { } saved)
            Array.Copy(saved, six, Math.Min(6, saved.Length));
        return six;
    }

    private void ReadContents(Character c)
    {
        var equipment = new List<InventoryItem?>();
        var inventory = new List<InventoryItem?>();
        using (var select = Command("""
                   SELECT container, slot, uniquename, count, quality, durability, active, passive, fame, soul
                   FROM items WHERE character = $id ORDER BY container, slot
                   """, null, ("$id", c.Id.ToString())))
        using (var r = select.ExecuteReader())
        {
            while (r.Read())
            {
                var slots = r.GetInt32(0) == EquipmentContainer ? equipment : inventory;
                var slot = r.GetInt32(1);
                while (slots.Count <= slot) slots.Add(null);
                slots[slot] = new InventoryItem(r.GetString(2), r.GetInt32(3))
                {
                    Quality = r.GetInt32(4),
                    Durability = r.IsDBNull(5) ? null : r.GetInt64(5),
                    Active = r.IsDBNull(6) ? null : JsonSerializer.Deserialize<string?[]>(r.GetString(6)),
                    Passive = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<string?[]>(r.GetString(7)),
                    Fame = r.IsDBNull(8) ? null : r.GetInt64(8),
                    Soul = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<ItemSoul>(r.GetString(9)),
                };
            }
        }
        while (equipment.Count < Entities.Inventory.EquipmentSlots) equipment.Add(null);
        c.Equipment = [.. equipment];
        c.Inventory = inventory;
        using (var select = Command("""
                   SELECT location, tab, slot, uniquename, count, quality, durability, active, passive, fame, soul
                   FROM bank_items WHERE character = $id ORDER BY location, tab, slot
                   """, null, ("$id", c.Id.ToString())))
        using (var r = select.ExecuteReader())
        {
            while (r.Read())
            {
                var key = $"{r.GetString(0)}|{r.GetString(1)}";
                if (!c.Vaults.TryGetValue(key, out var slots)) c.Vaults[key] = slots = [];
                var slot = r.GetInt32(2);
                while (slots.Count <= slot) slots.Add(null);
                slots[slot] = new InventoryItem(r.GetString(3), r.GetInt32(4))
                {
                    Quality = r.GetInt32(5),
                    Durability = r.IsDBNull(6) ? null : r.GetInt64(6),
                    Active = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<string?[]>(r.GetString(7)),
                    Passive = r.IsDBNull(8) ? null : JsonSerializer.Deserialize<string?[]>(r.GetString(8)),
                    Fame = r.IsDBNull(9) ? null : r.GetInt64(9),
                    Soul = r.IsDBNull(10) ? null : JsonSerializer.Deserialize<ItemSoul>(r.GetString(10)),
                };
            }
        }
        using var progression = Command("SELECT completed, levels, progress, tracked FROM progression WHERE character = $id", null, ("$id", c.Id.ToString()));
        using var p = progression.ExecuteReader();
        if (p.Read())
            c.Progression = new Progression
            {
                Completed = JsonSerializer.Deserialize<List<int>>(p.GetString(0)) ?? [],
                Levels = JsonSerializer.Deserialize<SortedDictionary<int, int>>(p.GetString(1)) ?? [],
                Progress = JsonSerializer.Deserialize<SortedDictionary<int, long>>(p.GetString(2)) ?? [],
                Tracked = p.IsDBNull(3) ? null : p.GetInt32(3),
            };
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
