# Крафт и предметные иконки — Flashback 440 / 441

Исполнитель: codex-msi-visual-bugs-20261011. Исходный checkout, ветка
codex/msi-visual-bugs; отдельные worktree и работники не создавались.

## Что изменено

- CharacterPanel: категории, прокрутка рецептов, крестик справа и закрытие
  левым кликом снаружи. Кадр закрытия блокирует world input. Загрузка иконки
  обновляет интерфейс даже при неизменном tick; backpack не превращён в scroll.
- Все 78 prepared wear ID получили свой рецепт в metadata.simulation.craft:
  верстак, 24 тика, шкуры/верёвки по семейному bill (§154).
- AssetGarmentCatalog включает active wear recipes в effective SimData;
  retire исключает рецепт. Старая форма баланса сохраняет craft metadata.
- AtomicContentBatchBuild включает готовую PNG одежды в payload владельца.
  ItemIcons разрешает generic prosthetic ID через конкретный side owner.
- 77 новых PNG: 34 варианта переиспользованы, шесть рюкзаков сняты штатным
  Unity фотографировщиком, 29 объектов и восемь протезов — фиксированным
  Blender tool rig с реальной импортированной геометрией и материалами.
  Прозрачность бутылки отключена только в иконке. Существующее мясо сохранено.
- Фотографировщик импортирует Single Sprite; пустая Multiple-нарезка больше
  не превращает существующую PNG в null Sprite.

## Проверка

- Native Unity 6000.4.5f1 PlayMode: категории, прокрутка, detail selection,
  close/backdrop, блокировка кадра закрытия; Sprite импорт и точный путь
  сборщика для всех 78 garments; routing протезов.
- Сервер: 29 проверок registry/catalog/admin, включая материализацию всех
  78 рецептов, сохранение базового non-wear рецепта и retire.
- Симуляция/контракты/локализация/структура спеки: 80 проверок. Реальный
  CraftByOutputCommand изготовил один top_primal, skirt_primal_male и
  backpack_primal_gatherer на верстаке, списав точные ингредиенты.
- .NET запускался с process-only DOTNET_ROLL_FORWARD=Major: установлен
  runtime 10, тестовые проекты target net9. Отчёты и Unity лог находятся в
  Build/MSIRelease/20261012-seams/craft-tests (локальные, не evidence archive).
- До сборки Player все 191 исходно грязных tracked файлов оставались побайтно
  прежними. Последующий Unity импорт изменил сериализацию четырёх meat .mat
  и ProjectSettings.asset; обе стадии зафиксированы в build receipt, сбросов нет.

## Интеграция и выпуск

Первоначальный отчёт выше относился к локальной проверке до выпуска.

Для игрока нужны согласованные новый Player с меню/ItemIcons, сервер с
материализацией craft metadata и owner bundles с icon entry. Рецепты нового
каталога нужно применить к существующему миру штатным Apply pending catalog
§154.2, сохранив save и rollback. Одна публикация PNG не изменяет pinned мир.
Сборки и активация координируются со Светой и Mac, MSI content owner.

Пересекающиеся файлы для точечной интеграции: CharacterPanel.cs,
SimulationInputAdapter.cs, RtsCameraController.cs, I2Languages.asset,
AtomicContentBatchBuild.cs, ItemIcons.cs, AssetGarmentCatalog.cs,
AdminEndpoints.cs и People/catalog.json. Не переносить всю dirty рабочую копию.
После выпуска проверить в живом HugeIsland выдачу приказа из крафта,
ресурсы/время/выход, картинки после новой сессии, оба способа закрытия и отсутствие
игрового приказа от закрывающего клика. Локальные проверки не заменяют live QA.

## Выпуск на Сингапуре — 2026-10-12

По последующему прямому поручению Толи MSI собрал и активировал Player,
server и content. Маковский Unity не требовался; New York не изменялся.

- Исходный HEAD: `6ea38650bb7e74f9be9e2f68f2e8459389b98737`.
  Существующие dirty inputs сохранены и включены в execution receipts;
  это сборка данного checkout, а не утверждение о чистом git tree.
- Unity 6000.4.5f1: full content 229/229 bundles + raw simdata = 230 объектов;
  WebGL Player exit 0, 0 compiler errors, 349 warnings, 29,8 минут.
  Linux self-contained server: 520 файлов; полный Release suite 470/470.
- Registry `449 -> 564`: 78 wear, 29 object icons, 8 prosthetic icons.
  Остальные варианты, в том числе семантически прежний raw simdata, сохранены.
- Server: `/opt/hexlive/releases/msi-20261012-6ea38650b`.
  Player: `/var/lib/hexlive-web/releases/msi-20261012-6ea38650b`.
  Сайт: https://play.62-146-235-120.sslip.io/ .
- Все 230 публичных registry records проверены; скачаны и сверены SHA256
  всех 115 изменённых blobs и 19 Player файлов. Audio: 5668 файлов проверены
  против исходного commit 7cd и опубликованных FSB/vis; повторная конвертация
  Mac-only инструментом не выполнялась.
- Сохранён тот же HugeIsland `a309de74171d470385412bfab01ffdab`,
  seed `629209070`, topology `59DDDCBB`, семь живых NPC. Пять защищённых
  auth/account/env файлов при активации побайтно прежние. Старый release,
  230 rollback candidates и остановленный save сохранены.
- Pinned каталог мира обновлён `447 -> 564`; все 78 wear recipes присутствуют.
  HTTP 302 первого admin POST был login redirect и не считался подтверждением.
  Сохранённый dirty Program не подключает central bearer middleware; auth не
  изменялась. Помощник использовал опубликованные `AssetGarmentCatalog.Materialize`
  и `WorldLibrary.StoreCatalog` при остановленном сервисе, проверив загрузку save.
  Первая проверка ошибочно ожидала `/health`, вызвав rollback каталога;
  повторная по штатному `/` прошла. Все квитанции и backup сохранены.
  Save SHA перед/после успешного offline apply одинаковый:
  `8ad9b5c267cbba4cbf4ab3033c67857675cb211b59ea518b50e32d5b4d4c60cb`.
- Браузерная проверка: новое меню показывает Singapore Play и выключенный
  New York; Singapore загрузил мир и модель Elsa NPC1. Наблюдались две
  startup NullReferenceException без stack trace и предупреждение FSR;
  они не помешали загрузке, но отсутствие runtime ошибок не заявляется.
  Полный live крафт, голос, кровь/бинты из аккаунта игрока ещё требуют QA.
  Flashback остаётся ready_for_test; игроковые проверки не закрыты.

Квитанции MSI: `Build/MSIRelease/20261012-craft-release/` —
`public-verification.json`, `activation-receipt.json`,
`catalog-apply-receipt-2.json`, `player-build-receipt.json`,
`server-6ea-manifest.json`, `audio-source-proof.json` и `live-server-menu.png`.
Операционный rollback на сервере:
`/var/lib/hexlive/ops/msi-craft-release-20261012-3d6c4c467`.
Название ops удерживает первоначальный task ID; фактический release source — 6ea.
