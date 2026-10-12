# Рюкзаки в крафте — проверка 2026-10-12

Толя запросил возможность крафтить рюкзаки и деплой Сингапура.
Рецепты уже реализованы в e008778991057a9223dcddbc119786faa78f233a:
Gatherer, Hunter, Trail и соответствующие male варианты, всего6 Primal backpacks.
Каждый: Workbench, baseWorkTicks24, resource.hide3 и resource.rope2.
Повторные или дублирующие рецепты не добавляются.

PreparedWearBillCompletesAtWorkbenchThroughGenericCommand расширен на все6.
Запуск8/8 (ещё top и male skirt) проверяет принятие реального CraftByOutputCommand,
завершение работы на верстаке, ровно один выход и списание всех ингредиентов.
TRX/log: Build/MSIRelease/20261012-player-only/backpacks-craft.*.

Read-only проверка Сингапура до деплоя: тот же HugeIsland
a309de74171d470385412bfab01ffdab, pinned registry564; все6 recipes присутствуют
в effective world simdata и active wear metadata. Browser Player6ea уже содержит
меню крафта, категорию Clothing для GarmentLibrary и owner icons; bundle rebuild
не требуется. Фактические игровые приказы от аккаунта игрока не выдавались.

Публикация использует подготовленный и проверенный Linux runtime1214a639a,
включающий server-side recipe materialization, voice/social fixes и PlayersOnly.
После этой сборки server/runtime исходники не менялись; последующие коммиты
сохраняют уже используемые dirt/Editor sources, документы и расширяют тесты.
Файлы524 проверены по SHA, полный Server suite475/475 passed.
Нужны свежий preflight и rollback; Player6ea/content564 остаются совместимыми.
Квитанция фактической активации появится в ops/msi-player-only-20261012-1214a639a.
