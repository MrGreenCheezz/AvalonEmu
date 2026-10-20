"""Makes Reverse/CHECKLIST.md from server/content/checks.json (the client checklist; the server's .check reads the same file).

Usage (from anywhere): python tools/checklist/gen_checklist.py [checks.json] [CHECKLIST.md]
Defaults: content/checks.json of this repository and CHECKLIST.md beside the repository (Reverse/CHECKLIST.md).
The setup lines are checked against the server's commands by the self-test (CheckerTest), not here.
"""
import json, os, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, '..', '..'))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'content', 'checks.json')
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(REPO, '..', 'CHECKLIST.md')

def strip_comments(text):
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == '\\': out.append(text[i + 1]); i += 2; continue
            if c == '"': in_str = False
            i += 1; continue
        if c == '"': in_str = True; out.append(c); i += 1; continue
        if text.startswith('//', i):
            while i < n and text[i] != '\n': i += 1
            continue
        out.append(c); i += 1
    return ''.join(out)

checks = json.loads(strip_comments(open(SRC, encoding='utf-8').read()))

ids = set(); errs = []
for c in checks:
    for k in ['id','order','area','title','priority','players','minutes','setup','steps','expect','ifBroken','source']:
        if k not in c: errs.append(f"{c.get('id')}: missing {k}")
    if c['id'] in ids: errs.append(f"duplicate {c['id']}")
    ids.add(c['id'])
orders = sorted(c.get('order', 0) for c in checks)
if orders != list(range(1, len(checks) + 1)): errs.append('order is not 1..n, each once')
if errs:
    print(chr(10).join(errs)); sys.exit(1)

areas = collections.OrderedDict()
for c in checks: areas.setdefault(c['area'], []).append(c)

# the full needs (descriptions that are not "(see ...)")
needs = collections.OrderedDict()
for c in checks:
    for n in c.get('needs', []):
        if '(see ' in n: continue
        needs.setdefault(n, []).append(c['id'])
for c in checks:
    for n in c.get('needs', []):
        if '(see ' in n:
            key = n.split(' (see')[0].split()[0]
            for full in needs:
                if full.startswith(key): needs[full].append(c['id'])

prio = collections.Counter(c['priority'] for c in checks)
two = sum(1 for c in checks if c['players'] == 2)
mins = sum(c['minutes'] for c in checks)

ROUTE = """1. **До игры (~25 мин):** ACC-01…04 — регистрация в клиенте и в веб-панели (http://127.0.0.1:8080/), админка.
2. **Новый персонаж в Тетфорде:** START-01, SOC-01 (каналы чата), QOL-01 (эмоции).
3. **Тетфорд (0000) — всё, что в городе:** CITY-01 (станки), MOUNT-01 (конюшня), CITY-09 (ремонт и разбор),
   CRAFT-01…05 (кузница, здание слияния, ремонтная мастерская), CRAFT-07, GOAL-01…04, VAN-01/02, FW-01/03 (торговец
   фракции), GUILD-01/03/04, COMP-01, BOT-03, PVP-01/02, ARENA-01…03 (агентство), EXP-01/02/05 (агент экспедиций),
   SOC-03; банк 0006 и рынок 0007: CITY-04, CITY-05, QOL-02; в конце CITY-08 — путешествие в Лимхерст.
4. **Лимхерст (1000) и остров:** ISL-01, затем ISL-02…ISL-10 подряд (ISL-09 после ISL-08), ISL-11 в конце.
5. **Обход городов по `.tele`:** CITY-02 (Бриджуотер 2000, Мартлок 3004 + CITY-03, Форт-Стерлинг 4000, Карлеон 3003 +
   CITY-07), MIST-06 (Брецилиен 5000).
6. **Windripple Fen (0204, зелёная, выход из Тетфорда):** START-02/03, GATH-03…05, MOUNT-02/03, MOB-01…05, MOB-07,
   COMB-01…04, LOOT-01…03, DEATH-01, QOL-03, PARTY-02, COMP-02, FW-02, WB-03/04, CRAFT-06.
7. **Случайные подземелья из 0204:** RD-01, RD-02, MOB-03, RD-04…RD-08, LOOT-05, RD-03 (с компаньонами).
8. **Туманы из 0204:** MIST-01…05 (MIST-05 вдвоём), MIST-07. **Дороги из 0204:** ROAD-01…05.
9. **Sleetwater Basin (0201, жёлтая, рядом с 0204):** GATH-01/02, LOOT-04/06, OW-01/02, MOB-06, MOB-08, WB-01,
   HUNT-01…03.
10. **Tharcal Fissure (4206):** SD-01…03.
11. **Hell Den Тетфорда (вход из 0000):** CD-01…05, HG-01…03, затем EXP-03.
12. **Darkground Swamp (0221, красная):** BOT-01/02/04/05; RD-09 (2326, чёрная зона T6).
13. **Вдвоём (второй клиент), одной сессией:** CITY-06, EXP-04, MIST-05, WB-02, GUILD-02, PARTY-01/03/04, PVP-03…08,
   ARENA-04, VAN-03, SOC-02/04/05."""

L = []
L.append('# Чек-лист проверки в клиенте')
L.append('')
L.append('Всё, что ещё нужно проверить в настоящем клиенте (сервер делался по функциям и проверялся в основном самотестами). '
         'Система модов (ModKit, редактор, иконки) сюда не входит. Источник истины — `server/content/checks.json` '
         '(этот файл сделан из него скриптом `server/tools/checklist/gen_checklist.py`). Команды из «Подготовки» сервер выполняет сам '
         '(`.check <id>`); их можно ввести и вручную в чат (канал Say) по порядку; `{me}` — имя твоего персонажа.')
L.append('')
L.append(f'Всего проверок: **{len(checks)}** (приоритет 1: {prio[1]}, 2: {prio[2]}, 3: {prio[3]}); вдвоём: {two}; '
         f'примерно {mins // 60} ч {mins % 60} мин всего. Приоритет 1 — основное и скорее всего сломанное (догадки в SPEC, '
         'исправления, ждущие подтверждения), 3 — косметика.')
L.append('')
L.append('Если что-то не так: напиши id проверки, что видно, и приложи то, что указано в «Если сломано». '
         'Журнал сервера — `server/data` (лог-файл) или страница /admin/log веб-панели.')
L.append('')
L.append('## Как пользоваться проверяльщиком')
L.append('')
L.append('Нужен аккаунт уровня GameMaster или Admin (консоль сервера: `.account level <имя> GameMaster`). Все команды — в чат игры:')
L.append('')
L.append('- `.check next` — следующая непройденная проверка по рекомендуемому порядку: сервер выполняет её подготовку от твоего имени '
         '(телепорт, предметы, мобы — по одной команде, после смены кластера ждёт, пока загрузишься), затем пишет в чат шаги и что должно получиться.')
L.append('- `.check <id>` — то же для конкретной проверки (например `.check MOB-01`); `.check <id> as2` — только текст, без подготовки (для второго игрока).')
L.append('- `.check ok [id] [заметка]` — прошла; `.check fail [id] <что не так>` — сломано (сервер сохранит 50 последних строк журнала); '
         '`.check skip [id] [почему]` — пропустить. Без id — та проверка, что запускалась последней.')
L.append('- `.check list [todo|fail|ok|skip|p1|2p|MOB|часть названия раздела] [страница]` — список со статусами; `.check status` — итоги по разделам; '
         '`.check reset <id|all>` — вернуть в «ждёт».')
L.append('- Веб-панель: http://127.0.0.1:8080/admin/checks — все проверки по разделам с цветом статуса, фильтры, подробности, '
         'заметки и журнал сбоев; статус можно поставить и там.')
L.append('- Результаты пишутся в `server/data/checks-results.json` (статус, заметка, кто, когда, сборка сервера, журнал для сбоев).')
L.append('')
L.append('## Рекомендуемый порядок')
L.append('')
L.append('`.check next` идёт по этому порядку (поле `order` в checks.json).')
L.append('')
L.append(ROUTE)
L.append('')
if needs:
    L.append('## Новые команды, которые нужны для подготовки')
    L.append('')
    L.append('Их ещё нет на сервере; проверки с ними можно пройти и без них, подготовив ситуацию вручную.')
    L.append('')
    for n, users in needs.items():
        L.append(f'- `{n.split(" — ")[0]}` — {n.split(" — ",1)[1] if " — " in n else ""}  ')
        L.append(f'  Нужна для: {", ".join(sorted(set(users)))}')
    L.append('')
L.append('## Содержание')
L.append('')
for a, cs in areas.items():
    anchor = a
    L.append(f'- {a} — {len(cs)} ({", ".join(c["id"] for c in cs)})')
L.append('')
for a, cs in areas.items():
    L.append(f'## {a}')
    L.append('')
    for c in cs:
        L.append(f'### {c["id"]} — {c["title"]}')
        meta = f'Приоритет **{c["priority"]}** · игроков: {c["players"]} · ~{c["minutes"]} мин · порядок {c["order"]} · источник: {c["source"]}'
        L.append(meta)
        L.append('')
        if c['setup']:
            L.append(f'**Подготовка** (`.check {c["id"]}` выполнит её сам):')
            L.append('```')
            L.extend(c['setup'])
            L.append('```')
        if c.get('needs'):
            L.append('*Нужна новая команда:* ' + '; '.join(n.split(' — ')[0].split(' (see')[0] for n in c['needs']))
            L.append('')
        L.append('**Шаги:**')
        for i, s in enumerate(c['steps'], 1): L.append(f'{i}. {s}')
        L.append('')
        L.append('**Ожидается:**')
        for s in c['expect']: L.append(f'- {s}')
        L.append('')
        L.append(f'**Если сломано:** {c["ifBroken"]}')
        L.append('')
open(OUT, 'w', encoding='utf-8').write('\n'.join(L))

per = collections.Counter(c['area'] for c in checks)
print(len(checks), dict(prio), 'two', two, 'min', mins)
for a, n in per.items(): print(n, a)
for n, u in needs.items(): print('NEED', n.split(' — ')[0], sorted(set(u)))
