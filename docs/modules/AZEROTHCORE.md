# From AzerothCore modules

| AzerothCore | Here |
|---|---|
| `modules/mod-x/` | The same: `modules/mod-x/`, each with its own git if you like |
| `conf/mod_x.conf.dist`, copied to `configs/modules/` | `conf/mod-x.jsonc.dist`, copied to `config/modules/mod-x.jsonc` |
| `sConfigMgr->GetOption<bool>("Mod.Enable", false)` | `Config.Get("Key", false)` or `Config.Bind<Settings>()`; a key `"Enabled": false` is handled by the server |
| `void Addmod_xScripts() { new MyScript(); }` | Nothing: every class deriving from a `*Script` class is found |
| Rebuild the server to add a module | The server builds only the module; `.modules reload mod-x` while it runs |
| `PlayerScript`, `UnitScript`, `SpellSC`, `CreatureScript`, `WorldScript`, `ServerScript`, `AllMapScript` | `PlayerScript`, `CombatScript`, `UnitScript`, `SpellScript`, `BuffScript`, `MobScript`, `ItemScript`, `FormulaScript`, `ServerScript`, `MapScript`, `NetworkScript` ([HOOKS.md](HOOKS.md)) |
| `CreatureScript`'s `UpdateAI`, `JustEngagedWith`, `EnterEvadeMode` | `MobScript`'s `OnBeforeThink` (`e.Cancel()` skips the engine's AI), `OnAggro`, `OnLeaveCombat`; `e.Mob.CastSpell`, `SetTarget`, `AddThreat`, `MoveTo`, `Despawn` ([example](../../modules/examples/mod-scripted-boss)) |
| `PlayerScript("Name", {PLAYERHOOK_ON_LOGIN})` | Nothing: only the methods you override are connected |
| `void OnPlayerLogin(Player* p)` | `public override void OnLogin(PlayerLoginArgs e)` |
| `bool CanX(...)` returning false | `OnBeforeX(args)` with `args.Cancel()` |
| `void OnDamage(Unit* a, Unit* v, uint32& damage)` | `OnBeforeDamage(BeforeDamageArgs e)` with `e.Amount`, or `e.Cancel()` to stop the hit |
| `player->GetItemByPos(...)`, `item->GetTemplate()->SubClass == ITEM_SUBCLASS_WEAPON_SWORD` | `player.MainHand is { Weapon: WeaponType.Sword }`, `player.Worn(EquipSlot.Head)` ([types](README.md#types-not-text)) |
| `roll_chance_f(15.0f)`, `Acore::Containers::SelectRandomContainerElement(list)` | `Roll.Chance(0.15)`, `Roll.Pick(list)` |
| `AuraScript`'s `OnEffectAbsorb`, `AfterEffectAbsorb` | `Combat.OnBeforeAbsorb`, `Combat.OnAfterAbsorb` |
| `ProcEventInfo::GetHitMask()`: `PROC_HIT_IMMUNE`, `PROC_HIT_EVADE`, `PROC_HIT_ABSORB`, `PROC_HIT_REFLECT` | `Combat.OnHitAvoided` with `e.Reason`, `Combat.OnAfterAbsorb`, `e.Origin == DamageOrigin.Reflected` |
| A `creature_template` row of your own | `Server.DefineMob("MY_BOSS", "T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", m => m.MaxHealth = 250000)`; the model and name stay the base type's |
| `DoMeleeAttackIfReady()`, `me->GetThreatMgr()`, `me->HasUnitState(UNIT_STATE_CASTING)` | `mob.Attack(target)`, `mob.Threat`, `mob.IsCasting` |
| `EnterEvadeMode()` that stays in the fight | `Mobs.OnBeforeLeaveCombat` with `e.Cancel()` |
| `creature->isWorldBoss()`, `GetCreatureTemplate()->rank` | `mob.IsWorldBoss`, `mob.Category == MobCategory.Boss`, `mob.Type.Faction` |
| `player->GetMap()->IsDungeon()` | `player.Zone.Kind == ZoneKind.Dungeon`, `player.Zone.Color`, `player.Zone.Pvp` |
| `spellInfo->HasAura(...)`, `SpellInfo` fields | `e.Spell.Category`, `e.Spell.Target`, `Server.Spell("NAME")` |
| `ScriptName` of a creature or spell in the database | `[ForId("T8_MOB_BOSS")]` on the class or the method, masks work |
| `ChatCommandTable` | `Commands.Register("hello", PlayerLevel.Player, "help text", call => ...)` |
| `data/sql/db-*` | Not yet: a database API is planned. `Storage` and files for now |
| No priorities: load order decides | `[Priority(HookPriority.Early)]`, `loadAfter`, and `e.CancelledBy` to see who stopped it |
| A missing hook means a pull request to the core | Network hooks and `coreAccess` get you there first ([README](README.md#reaching-further)) |
| A crash in a script takes the server down | A handler that throws is logged, and the module is switched off after too many errors. An endless loop still hangs the world |
