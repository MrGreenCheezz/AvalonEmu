using System.Text.Json.Nodes;

namespace AlbionLocal.Server.Rules;

/// <summary>Ready rules for the Studio's template gallery (server/plugins/RULES.md "Templates"): a label, a description and the rule JSON.</summary>
public static class RuleTemplates
{
    private sealed record Template(string Id, string En, string Ru, string DescEn, string DescRu, string Json);

    private static readonly Template[] All =
    [
        new("night_black_fame", "+50 % fame at night in black zones", "+50 % славы ночью в чёрных зонах",
            "Kills in black zones between 20:00 and 06:00 (server clock) give half again as much fame.",
            "Убийства в чёрных зонах с 20:00 до 06:00 (часы сервера) дают в полтора раза больше славы.",
            """
            { "name": "+50 % fame at night in black zones", "trigger": { "type": "calc.fame" },
              "conditions": [ { "type": "zone.color", "params": { "colors": ["black"] } }, { "type": "time", "params": { "from": "20:00", "to": "06:00" } } ],
              "actions": [ { "type": "modify", "params": { "op": "mul", "value": 1.5 } } ] }
            """),
        new("boss_party_drop", "Boss drops an item to the whole party", "Босс даёт предмет всей группе",
            "When a world boss dies, every party member of the killer who is online gets the item (pick yours).",
            "Когда умирает мировой босс, каждый член группы убийцы онлайн получает предмет (выберите свой).",
            """
            { "name": "Boss drops an item to the whole party", "trigger": { "type": "mob.died" },
              "conditions": [ { "type": "mob.boss" } ],
              "actions": [ { "type": "give.item", "target": "party", "params": { "item": "T4_BAG", "count": 1 } },
                           { "type": "message", "target": "party", "params": { "text": "{mob} dropped a trophy for your party!" } } ] }
            """),
        new("healer_potions", "Potions heal 20 % more for healers", "Зелья лечат на 20 % больше у хилеров",
            "A heal of a potion spell on a player who wields a holy staff is 20 % bigger.",
            "Лечение зелий у игрока с посохом света в руках на 20 % больше.",
            """
            { "name": "Potions heal 20 % more for healers", "trigger": { "type": "spell.damage", "id": "*POTION*" },
              "conditions": [ { "type": "flag", "params": { "flag": "heal" } }, { "type": "equipped", "target": "target", "params": { "item": "*HOLYSTAFF*" } } ],
              "actions": [ { "type": "modify", "params": { "op": "mul", "value": 1.2 } } ] }
            """),
        new("announce_boss_kill", "Announce a world boss kill", "Объявить убийство мирового босса",
            "Everyone online sees who killed a world boss.",
            "Все онлайн видят, кто убил мирового босса.",
            """
            { "name": "Announce a world boss kill", "trigger": { "type": "mob.died" },
              "conditions": [ { "type": "mob.boss" } ],
              "actions": [ { "type": "message", "params": { "text": "{player} has slain the world boss {mob}!", "channel": "announce" } } ] }
            """),
        new("daily_login", "Daily login reward", "Ежедневная награда за вход",
            "Once per 20 hours a login gives silver and a message (the cooldown is kept across restarts).",
            "Раз в 20 часов вход в игру даёт серебро и сообщение (перезарядка сохраняется между перезапусками).",
            """
            { "name": "Daily login reward", "trigger": { "type": "player.login" }, "cooldown": { "seconds": 72000, "per": "player" },
              "actions": [ { "type": "silver", "params": { "amount": 5000 } },
                           { "type": "message", "params": { "text": "Daily reward: 5000 silver. See you tomorrow, {player}!" } } ] }
            """),
        new("home_command", "Command .home (teleport, 10 min cooldown)", "Команда .home (телепорт, перезарядка 10 мин)",
            "A player types .home to go to a cluster's spawn point (set the cluster: empty = the current one). Once per 10 minutes.",
            "Игрок пишет .home и попадает в точку появления кластера (укажите кластер; пусто — текущий). Раз в 10 минут.",
            """
            { "name": "Command .home", "trigger": { "type": "command", "params": { "command": "home", "help": "teleports you home (10 min cooldown)" } },
              "cooldown": { "seconds": 600, "per": "player" },
              "conditions": [ { "type": "in.combat", "not": true } ], "failMessage": "Not in combat!",
              "actions": [ { "type": "message", "params": { "text": "Going home…" } }, { "type": "teleport", "params": { "cluster": "" } } ] }
            """),
        new("kill_counter", "Every 100 kills: a reward", "Каждые 100 убийств — награда",
            "Counts each player's kills in a variable; at 100 the counter goes back to 0 and the player gets fame.",
            "Считает убийства игрока в переменной; на 100 счётчик сбрасывается, игрок получает славу.",
            """
            { "name": "Every 100 kills: a reward", "trigger": { "type": "mob.died" },
              "actions": [ { "type": "var.set", "params": { "scope": "player", "name": "kills", "op": "add", "value": 1 } },
                           { "type": "run.chain", "params": { "chain": "kills_check" } } ] }
            """),
        new("kill_counter_reward", "…its reward (chain kills_check)", "…её награда (цепочка kills_check)",
            "The second half of the kill counter: runs when the counter reached 100.",
            "Вторая половина счётчика убийств: срабатывает, когда счётчик дошёл до 100.",
            """
            { "name": "Kill counter reward", "trigger": { "type": "chain", "id": "kills_check" },
              "conditions": [ { "type": "var", "params": { "scope": "player", "name": "kills", "op": ">=", "value": 100 } } ],
              "actions": [ { "type": "var.set", "params": { "scope": "player", "name": "kills", "op": "set", "value": 0 } },
                           { "type": "fame", "params": { "amount": 10000, "tier": 6 } },
                           { "type": "message", "params": { "text": "100 kills! +10000 fame." } } ] }
            """),
        new("no_mount_black", "No mounting in combat", "Нельзя сесть на маунта в бою",
            "Refuses a mount request while the player is in combat.",
            "Запрещает садиться на маунта во время боя.",
            """
            { "name": "No mounting in combat", "trigger": { "type": "mount.check" },
              "conditions": [ { "type": "in.combat" } ],
              "actions": [ { "type": "cancel", "params": { "reason": "You cannot mount in combat" } } ] }
            """),
        new("evening_event", "A world event every evening", "Мировое событие каждый вечер",
            "At 20:00 (server clock) an announcement, and an invasion starts where the first online player stands.",
            "В 20:00 (часы сервера) объявление и вторжение там, где стоит первый игрок онлайн.",
            """
            { "name": "A world event every evening", "trigger": { "type": "timer.daily", "params": { "time": "20:00" } }, "target": "all",
              "actions": [ { "type": "message", "params": { "text": "The evening invasion begins!", "channel": "announce" } },
                           { "type": "event.start", "params": { "kind": "invasion", "tier": 0 } } ] }
            """),
    ];

    public static JsonArray Json() => new([.. All.Select(t => (JsonNode)new JsonObject
    {
        ["id"] = t.Id, ["en"] = t.En, ["ru"] = t.Ru, ["descEn"] = t.DescEn, ["descRu"] = t.DescRu, ["rule"] = JsonNode.Parse(t.Json),
    })]);
}
