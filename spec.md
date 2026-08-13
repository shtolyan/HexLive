# Game Specification — Emergent NPC Simulation (Tick-Driven, Restored + Extended)

> **⚠️ IMPORTANT NOTE**
> This document was updated to introduce a tick-driven architecture, but ALL previously defined systems (AI, perception, social, content, debug, etc.) are preserved and extended — not removed.
> Tick system is now the foundation layer, not a replacement.

> ⭐ **Это ГЕНЕРИРУЕМОЕ оглавление. Руками не править.**
> Текст спеки живёт в `Spec/<N>.md` — по файлу на раздел.
> Перегенерировать: `python3 Tools/spec_index.py`
>
> **`§N` → `Spec/N.md`, механически, без поиска.** Увидел `§105.14` в
> комментарии C# — открывай `Spec/105.md`. Подпункты (`§54.14`, `§21.21B`)
> живут внутри файла своего раздела.
>
> **Новый раздел заводится ТОЛЬКО через `python3 Tools/spec_new.py "Название"`** —
> он выдаёт следующий свободный номер. Не выбирай номер глазами: §84 заведён
> в спеке дважды именно потому, что свободный номер выбирали вручную.
>
> Номера §N не меняются НИКОГДА: на них 3773 ссылки из C#, 364 из
> Tests/Server/Tools и 1275 внутренних. Дыры в нумерации (87, 88, 90, 92,
> 95, 96, 98, 103) — это история, а не ошибка.


## Обзор — исторический слой (§1-§18)

| § | Раздел | Строк |
|---|---|---|
| [§1](Spec/1.md) | Core Principle | 18 |
| [§2](Spec/2.md) | Tick System (FOUNDATION) | 36 |
| [§3](Spec/3.md) | Spatial System (Fragment / Tile / Junction) | 22 |
| [§4](Spec/4.md) | Perception System (RESTORED + TICK-AWARE) | 20 |
| [§5](Spec/5.md) | Decision System (RESTORED) | 11 |
| [§6](Spec/6.md) | Planning System | 18 |
| [§7](Spec/7.md) | Pathfinding (Junction Graph) | 14 |
| [§8](Spec/8.md) | Movement System | 6 |
| [§9](Spec/9.md) | Execution System | 4 |
| [§10](Spec/10.md) | Memory System (RESTORED) | 13 |
| [§11](Spec/11.md) | Social System (RESTORED) | 14 |
| [§12](Spec/12.md) | Content System (RESTORED) | 14 |
| [§13](Spec/13.md) | Debug System (RESTORED) | 16 |
| [§14](Spec/14.md) | Core Flow (IMPORTANT) | 6 |
| [§15](Spec/15.md) | Unity Layer | 9 |
| [§16](Spec/16.md) | ECS Philosophy | 9 |
| [§17](Spec/17.md) | What Was Preserved | 13 |
| [§18](Spec/18.md) | Final Summary | 9 |

## Глубокая архитектура (§19-§35)

| § | Раздел | Строк |
|---|---|---|
| [§19](Spec/19.md) | World State / Data Model | 356 |
| [§20](Spec/20.md) | Spatial System (Deep) | 428 |
| [§21](Spec/21.md) | Movement, Rotation & Navigation Execution | 880 |
| [§22](Spec/22.md) | Perception System (Deep) | 286 |
| [§23](Spec/23.md) | Decision System (Deep) | 552 |
| [§24](Spec/24.md) | Planning System (Deep) | 198 |
| [§25](Spec/25.md) | Pathfinding System (Deep) | 375 |
| [§26](Spec/26.md) | Execution System / Action Runtime | 630 |
| [§27](Spec/27.md) | Memory & Knowledge System (Deep) | 312 |
| [§28](Spec/28.md) | Social System (Deep) | 559 |
| [§29](Spec/29.md) | Content & Interaction Authoring System (Deep) | 437 |
| [§29A](Spec/29A.md) | Flora & Produce System (v1 Minimal) | 103 |
| [§29B](Spec/29B.md) | Inventory & Item Carrying (v1 Minimal) | 57 |
| [§29C](Spec/29C.md) | Wildlife, Health & Combat (v1 Minimal — Iteration 9) | 426 |
| [§29E](Spec/29E.md) | Thirst, Water & Fire (Iteration 13) | 58 |
| [§29F](Spec/29F.md) | Hunting & Crafting (Iteration 14) | 81 |
| [§30](Spec/30.md) | Debug, Observability & Development Tooling (Deep) | 572 |
| [§31](Spec/31.md) | Unity Integration & Presentation Layer (Deep) | 327 |
| [§31A](Spec/31A.md) | Clothing System (Model-Level Foundation) | 211 |
| [§31B](Spec/31B.md) | Actor Visualization & Wardrobe (iteration 23) | 633 |
| [§31C](Spec/31C.md) | World Object Visualization & Tropical Reskin (iteration 24) | 406 |
| [§32](Spec/32.md) | NPC Brain & AI Architecture Layer (GOAP / LLM / Hybrid) | 183 |
| [§33](Spec/33.md) | First Vertical Slice / Prototype Scope | 192 |
| [§34](Spec/34.md) | Vertical Slice Implementation Plan | 512 |
| [§35](Spec/35.md) | Open World Survival Expansion (Iterations 17-22 Master Plan) | 302 |

## Итерации (§40+)

| § | Раздел | Строк |
|---|---|---|
| [§40](Spec/40.md) | Survivor Arc — Roadmap (captured 2026-07-11) | 1619 |
| [§41](Spec/41.md) | Loading, Save & Offline Progression (iteration 37) | 203 |
| [§42](Spec/42.md) | Survival Realism Rebalance (iteration 38) | 58 |
| [§43](Spec/43.md) | Sun & cast shadows (iteration 39) | 22 |
| [§44](Spec/44.md) | Blood, rest & herbal bandages (iteration 40) | 44 |
| [§45](Spec/45.md) | Free hands — the progress drive (iteration 41) | 119 |
| [§46](Spec/46.md) | Difficulty pass — putting teeth back (iteration 44) | 82 |
| [§47](Spec/47.md) | The fire zone & the comfort chain (iteration 44) | 95 |
| [§48](Spec/48.md) | Status effects — the unified buff/debuff layer (iteration 46) | 137 |
| [§49](Spec/49.md) | Sleep, social & water overhaul (iteration 47) | 139 |
| [§50](Spec/50.md) | Limb loss — amputation (iteration 48) | 269 |
| [§51](Spec/51.md) | Character inventory — the backpack window (iteration 48) | 73 |
| [§52](Spec/52.md) | Slot inventory, garment containers & build-sites (iteration 52) | 530 |
| [§53](Spec/53.md) | Compassion & mutual aid (iteration 49) | 253 |
| [§54](Spec/54.md) | Stranded-Deep resource, processing & butchering loop (iteration 54) | 816 |
| [§55](Spec/55.md) | Rivers retired, drink from the coconut (iteration 55) | 55 |
| [§56](Spec/56.md) | Predation cannibalism — killing to eat (iteration 56) | 100 |
| [§57](Spec/57.md) | Limb-health window — the body doll (iteration 57) | 158 |
| [§58](Spec/58.md) | Localization — I2 is the single source of strings (iteration 58) | 57 |
| [§59](Spec/59.md) | Data-driven catalogs — SO-конфиги и headless-мост (iteration 59) | 102 |
| [§60](Spec/60.md) | Кома — глубокое бессознательное (iteration 60) | 177 |
| [§61](Spec/61.md) | Поэтапный крафт на месте — выкладка, работа, взятие (iteration 61) | 40 |
| [§62](Spec/62.md) | Дальнее обнаружение врага — ⚠️, атака первой или обход (iteration 62) | 68 |
| [§63](Spec/63.md) | Прибой приносит одежду + поведенческий аудит выживания (iteration 63) | 138 |
| [§64](Spec/64.md) | Мечта — цель-стремление колонии, строим по плану (iteration 64) | 151 |
| [§65](Spec/65.md) | Смертельно устал → спать у костра, а не падать на месте (iteration 65) | 64 |
| [§66](Spec/66.md) | Один гекс — одна постройка: центр, поворот, кровати боком к костру (iteration 66) | 82 |
| [§67](Spec/67.md) | Звук: FMOD + два канала событий (базовая версия) | 291 |
| [§68](Spec/68.md) | Сама себя перевязывает — цель «лечение» в аукционе (iteration 68) | 66 |
| [§69](Spec/69.md) | Ткань — юбка колышется (MagicaCloth2, iteration 69) | 81 |
| [§70](Spec/70.md) | Музыка — редкий гость, приходящий из тишины (iteration 70) | 90 |
| [§71](Spec/71.md) | Темп — колония наконец ходит быстро (iteration 71) | 446 |
| [§72](Spec/72.md) | Враг-человек — фракции, охотник и сплочённый отпор (iteration 72) | 261 |
| [§73](Spec/73.md) | Остров стал больше — границы карты в одном месте (iteration 73) | 45 |
| [§74](Spec/74.md) | Девушка — набор, а не персонаж: меш, кожа, причёска, голос, имя (iteration 74) | 118 |
| [§75A](Spec/75A.md) | Симпатия персонажа к предметам | 39 |
| [§75](Spec/75.md) | Состав — две ручки, а не два списка (iteration 75) | 35 |
| [§76](Spec/76.md) | Характеристики и навыки — кто она и что умеет (iteration 76) | 269 |
| [§77](Spec/77.md) | Выкладка материала — предмет уходит из руки в середине анимации (iteration 77) | 58 |
| [§78](Spec/78.md) | Мужская походка — актёру своя локомоция (iteration 78) | 101 |
| [§79](Spec/79.md) | Мачете — железный трофей и скорость рубки от инструмента (iteration 79) | 167 |
| [§80](Spec/80.md) | Три поломки, которые видно глазами (iteration 80) | 81 |
| [§81](Spec/81.md) | Абьюз — общение силой (iteration 81) | 286 |
| [§82](Spec/82.md) | Солнце и злость (iteration 82) | 62 |
| [§83](Spec/83.md) | Сервер и онлайн-режим (iteration 83) | 60 |
| [§84](Spec/84.md) | Юка рубится как дерево; крафт берёт то, что лежит под ногами (iteration 84) | 107 |
| [§85](Spec/85.md) | Цвет глаз — пятая ось внешности (iteration 85) | 102 |
| [§86](Spec/86.md) | Бой не до смерти, если нет ненависти (iteration 86) | 37 |
| [§89](Spec/89.md) | Гопник: он ищет, догоняет и не отпускает (iteration 89) | 48 |
| [§91](Spec/91.md) | Арена стала настоящей, удар — рукопашным (iteration 91) | 45 |
| [§93-94](Spec/93.md) | Настоящий удар, лестница ненависти, отношения на десять дней | 42 |
| [§97](Spec/97.md) | Абьюз входит в НАСТОЯЩИЙ бой и выходит из него рано (iteration 97) | 37 |
| [§99](Spec/99.md) | Удар должно быть ВИДНО (iteration 99) | 17 |
| [§100-101](Spec/100.md) | Пять секунд драки и трезвая оценка шансов | 26 |
| [§102](Spec/102.md) | Зависание перед жертвой: четыре причины одного симптома (iteration 102) | 59 |
| [§104](Spec/104.md) | Бой перестал быть копией самого себя (iteration 104) | 161 |
| [§105](Spec/105.md) | На грани смерти: умирание вместо мгновенной смерти (iteration 105) | 394 |
| [§106](Spec/106.md) | Вода — убежище (iteration 106) | 88 |
| [§107](Spec/107.md) | Над головой: собака, которой нет; и фотография вместо кадра (iteration 107) | 186 |
| [§108](Spec/108.md) | Групповая охота — сговор против чужака (iteration 108) | 260 |
| [§109](Spec/109.md) | Свидетельницы вписываются сами, а бьющийся отвечает (iteration 109) | 137 |
| [§110](Spec/110.md) | Стресс — это слёзы, а не обморок (iteration 110) | 91 |
| [§111](Spec/111.md) | Обобрать беспомощного врага (iteration 111) | 242 |
| [§112](Spec/112.md) | Пальма не заслоняет кадр (iteration 112) | 68 |
| [§113](Spec/113.md) | Геометрия тела на под-сетке (iteration 113, r3) | 140 |
| [§114](Spec/114.md) | Баг-трекер в игре (iteration 114) | 273 |
| [§115](Spec/115.md) | Волосы колышутся — всем причёскам, но не одним способом (iteration 115) | 57 |
| [§116](Spec/116.md) | Гардероб доехал до игры: цвет волос, стартовый набор, статы, кровь (iteration 116) | 86 |
| [§117](Spec/117.md) | Прогнать чужака из лагеря (iteration 117) | 19 |
| [§118](Spec/118.md) | Kenshi-core: травмы, спасение и протезы (iteration 118) | 352 |
| [§119](Spec/119.md) | Верстак, незавершённый предмет и протез для подруги (iteration 119) | 151 |
| [§120](Spec/120.md) | Первая жилая хижина на архитектурной сетке (iteration 120) | 410 |
| [§121](Spec/121.md) | Ручное управление персонажем (iteration 121) | 129 |
| [§122](Spec/122.md) | Защита от петель: реестр намерений и три подписи лайвлока (iteration 122) | 176 |
| [§123](Spec/123.md) | Ростер кланов, множественный выбор и групповые приказы (iteration 123) | 160 |
| [§124](Spec/124.md) | Мёртвые персонажи и ручной перенос (iteration 124) | 35 |
| [§125](Spec/125.md) | Радиус восприятия NPC (iteration 125) | 158 |
| [§126](Spec/126.md) | Черты характера (iteration 126) | 167 |
| [§127](Spec/127.md) | Романтические анимации для двоих (iteration 127) | 132 |
| [§128](Spec/128.md) | Обмен вещами с лежащим без сознания персонажем (iteration 128) | 66 |
| [§129](Spec/129.md) | Дверь открывают руки (iteration 129) | 97 |
| [§130](Spec/130.md) | Взгляд в камеру при близком зуме (iteration 130) | 46 |
| [§131](Spec/131.md) | Камера: пивот всегда у головы, единый ответ позы, стартовый кадр | 38 |
| [§132](Spec/132.md) | Пополнение двух лагерей и потолок населения | 55 |
| [§133](Spec/133.md) | Своя одежда: гардероб дома, разрешение и скромность | 162 |
| [§134](Spec/134.md) | Котелок удалён | 41 |
| [§135](Spec/135.md) | Волк уносит добычу (iteration 135) | 196 |

*Разделов: 130. Строк всего: 22828.*
