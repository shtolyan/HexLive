# Atomic Content: приёмка текущего мира

Дата проверки: 2026-08-26. Контур: изолированный staging
`http://62.146.235.120:5124/api/assets/v1`, production-порт `5123` не
изменялся.

Проверенный content source SHA:
`5e317e5745191e54af7bb8ee58077003357f97fb`.

## Итог

- `registryRevision`: `4517`.
- Активных macOS-записей: `2725`.
- Digest отсортированного inventory `type/id`:
  `731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1`.
- Распределение: `actor=5`, `audio=1128`, `building=7`, `config=754`,
  `hair=16`, `mob=2`, `object=51`, `prosthetic=8`, `vfx=69`, `wear=685`.
- Payload: `assetBundle=1596`, `file=1129`.
- Editor staging smoke: PASS — реальный remote bootstrap, 15
  репрезентативных payload, все модели текущего мира и owner-icons.
- Рабочий набор текущего snapshot: `85` model-payload — `actor=1`,
  `building=6`, `hair=3`, `mob=2`, `object=38`, `prosthetic=4`, `wear=31`.
- До снятия loading curtain готовы все 85 моделей и 46 настоящих owner-icons.
  Владельцы без нарисованной иконки немедленно показывают штатный emoji;
  отдельного icon-bundle нет.

Smoke строит список не вручную. Он читает текущий `WorldSnapshot`, собирает
актёров, волосы, одежду, протезы, содержимое инвентарей и контейнеров,
held/holstered/favorite предметы, world objects, build products, ингредиенты,
мобов и крабов. Для каждого ID проверяются активная record, совместимый macOS
variant, загрузка `main` и наличие renderable geometry. Поэтому новый предмет
в мире автоматически становится новой обязательной проверкой.

## Визуально проверено в загруженной сцене

- Дом: пол, стены, окна, дверь, опоры и крыша используют шесть независимых
  `building/architecture.*` bundle; primitive fallback отсутствует.
- Кровати: нативные брёвна, палки, верёвки и 50 листьев.
- Вешалка/шкаф: нативная рама, рейки, полки и верёвочные соединения.
- Верстак: нативные палки, доски и обвязки.
- Костёр внутри дома: нативный hearth из камней, брёвен, стоек и верёвок.
  Отдельный незавершённый `campfire.spot` в snapshot показывает доставленные
  девять палок — это корректная стадия строительства, не fallback.
- `station.drying_rack@4`: четыре восьмигранные деревянные детали и четыре
  отдельные верёвочные обвязки; старая модель `fence:96` устранена.
- `shelter.tent@2`: двустороннее полотно до земли, задняя стенка, стойки,
  коньковая балка и растяжки; прежние голые стойки устранены.
- `tool.spear@3`: на спине и в руке используется authored mesh
  `StoneSpear_AI_oriented`, а не процедурный примитив.
- `tool.bow@1`: отдельный recurved-bow FBX с цельными плечами, кожаной
  рукоятью и тетивой. `resource.arrow@1` опубликована отдельным объектом;
  физической зависимости между ними нет.

## Точный manifest текущего мира

```text
actor/Jana@2
building/architecture.door.wood@2
building/architecture.floor.board@2
building/architecture.roof.palm@2
building/architecture.support.wood@2
building/architecture.wall.wood@2
building/architecture.window.wood@2
hair/AsukaHair@2
hair/Hair07@2
hair/JelikaHair_32434@2
mob/crab@1
mob/dog@2
object/bed.basic@3
object/campfire.spot@3
object/food.coconut@2
object/food.coconut_open@2
object/food.coconut_pierced@2
object/forest.deadfall@2
object/furniture.wardrobe@3
object/herb.bush@2
object/item.bandage@2
object/item.medkit@2
object/item.pill@1
object/item.plaster@1
object/palm.coconut_branch@1
object/plant.yucca@2
object/resource.board@2
object/resource.cloth@1
object/resource.fiber@1
object/resource.herb_leaf@2
object/resource.mechanical_part@1
object/resource.palm_crown_small@1
object/resource.palm_leaf@2
object/resource.rope@2
object/resource.stick@2
object/resource.stone@2
object/shelter.tent@2
object/station.drying_rack@4
object/station.water_collector@3
object/station.workbench@3
object/stump.palm@1
object/tool.axe_stone@2
object/tool.bottle@3
object/tool.bow@1
object/tool.hammer@2
object/tool.knife@2
object/tool.machete@2
object/tool.pickaxe_stone@2
object/tool.saw@2
object/tool.spear@3
prosthetic/arm.mechanical.r@2
prosthetic/arm.wood.l@2
prosthetic/leg.mechanical.r@2
prosthetic/leg.wood.r@2
wear/FAO Harness Male@2
wear/FCO Belt Male@2
wear/FCO Boots Male@2
wear/FCO Gloves Male@2
wear/FCO Knee Straps Male@2
wear/FCO Legs Straps Male@2
wear/FCO Pants Male@2
wear/FCO Waist Strappy Male@2
wear/TonnyFlash@2
wear/clothing.ankleboots_amy@2
wear/clothing.boots_charm_velvet_purple@2
wear/clothing.boots_classic@2
wear/clothing.cap_stars@2
wear/clothing.croptop_fit_4_batik4@2
wear/clothing.cuffs_anarchy@2
wear/clothing.gloves_stars@2
wear/clothing.pants_ranger@2
wear/clothing.shorts_fit_3_blackmagenta@2
wear/clothing.shorts_summer@2
wear/clothing.skirt_primal@2
wear/clothing.thighboots_amy@2
wear/clothing.tshirt_big_tshirt_lost_angels@2
wear/gear.backpack_osiris@2
wear/gear.backpack_riot_denim@2
wear/underwear.bra_riot_red@2
wear/underwear.briefs_sweety_08panty@2
wear/underwear.briefs_vapor_bot02@2
wear/underwear.swimsuit_briefs_bottom_6@2
wear/underwear.swimsuit_briefs_bottom_white@2
wear/underwear.swimsuit_top_top_green@2
wear/underwear.top_vapor_11@2
```

`build.site`, `building.hut_plan`, вода, труп/могила и severed-limb anchors
намеренно не имеют визуального payload. Их видимая стадия строится из
`BuildProduct` и уже включена в список выше.

## Атомарные изменения этой приёмки

- `object/shelter.tent`: `revision 1 → 2`.
- `object/station.drying_rack`: `revision 3 → 4`.
- `object/tool.bow`: новая `revision 1`.
- `object/resource.arrow`: новая `revision 1`.

Повторная публикация всех четырёх кандидатов дала `0 changed`, registry остался
`4517`. Другие object revisions и blobs не менялись.
