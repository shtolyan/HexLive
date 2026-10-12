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
- Все 191 исходно грязных tracked файлов остались побайтно прежними.

## Интеграция и выпуск

Изменения пока проверены локально. Этот запрос не активировал production
Player, server или content. New York не затрагивался.

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
