# Тлумач (Tlumach, раніше Unity Translation Launcher) — контекст проєкту для Claude Code

Програма для перекладу Unity-ігор. Автори: команда **SwitchUA**, учасниця **CallMeBeepMe** (Наталія).
Мова спілкування з авторкою — українська. Інтерфейс програми — українська та англійська.
Програма може бути поширена публічно — не залишати в збірці персональних даних.

## Бренд
Назва «Тлумач» / «Tlumach» (затверджено 27.10.2026). Знак — літера Ї на плитці шрифтового атласу із загнутим кутом,
права крапка червона (#D8453A). Палітра: чорнило #221D45, пергамент #F3ECDD, сургуч #D8453A, гліф #E9B44C, атлас #3FA394
(тема `ink` / «Чорнило»). Шрифти інтерфейсу — Fixel Text / Fixel Display (українські, MacPaw + AlfaBravo, OFL), вшиті в www/.
Не використовувати шрифти російського походження (Golos, Onest, Manrope, JetBrains Mono тощо).
Внутрішні імена namespace і DLL `TranslationLauncher` лишилися старими навмисно. Папки даних перейменовано (2026-10-27):
`Документи\Tlumach\Workspaces` (старий шлях за замовчуванням у налаштуваннях автоматично замінюється, проєкти зберігають свої папки),
резервна папка даних `%AppData%\Tlumach` (стара `UnityTranslationLauncher` використовується, якщо вже існує).
Чистота пакета: Release без pdb і з PathMap (у DLL немає шляхів машини збірки); якщо app\data скопійовано з іншого ПК
(WorkspaceOwner інший), програма скидає налаштування, проєкти, журнал і профіль вікна.
Бренд-матеріали: project files `brand/` (сторінка, icons/, fonts/).

## Структура
```
Launcher/              основна програма (.NET 8, C#, AnyCPU — x86 і x64)
  App/Program.cs       старт: data-папка, single-instance (data/instance.json), відкриття вікна Edge/Chrome --app, автовихід
  App/HttpServer.cs    власний HTTP-сервер на 127.0.0.1 (TcpListener), API захищене токеном (X-Token / ?t=)
  App/Api.cs           усі /api/* ендпоінти (проєкти, пошук, ассети, експорт/імпорт, шрифти, атласи, open-url…)
  App/Batch.cs         «Імпорт у гру»: індекс гри, пошук місця файлу, порівняння, оновлення до актуальної версії, звіти
  App/Projects.cs      Settings, Project (original/ modified/ exports/ reports/ fonts/ incoming/), Store, Docs
  App/Jobs.cs          фонові задачі (пауза/продовження/стоп), AppLog → data/log.txt
  Core/UnityDoc.cs     відкриття .assets / UnityFS-бандлів / старих UnityWeb/UnityRaw (AssetsTools.NET), збереження
  Core/Extract.cs      таблиці рядків: TextAsset, Unity Localization StringTable, I2, generic, raw; експорт/імпорт TXT/JSON/CSV
  Core/Dump.cs         JSON-дамп як в UABEA (ToJson / FromJson)
  Core/Merge.cs        структурне злиття JSON (порядок гри, тексти з перекладу), MergeLog, схожість назв
  Core/Scanner.cs      автопошук перекладу за ключовими словами
  Core/Atlas.cs        атласи шрифтів: зв'язок шрифт→Texture2D, декодування Alpha8/RGBA/DXT1/DXT5…, PNG
  www/                 інтерфейс (index.html, app.css, app.js, i18n.js, icon.png) — вшито в DLL;
                       файл app\www\i18n.js поруч із програмою перекриває вбудований (тексти можна міняти без збірки)
TMPFontReplacer/       створення SDF-атласу з TTF і вбудовування TMP-шрифту (FontPatcher, AtlasBuilder, TrueTypeFont,
                       SchemaFallback + tmp_schemas.txt, FontPairs, Report). Також окрема CLI-програма.
exe/                   Tlumach.exe = стандартний .NET apphost (win-x64, GUI) самої програми, без власного коду лаунчера.
                       Збірка: cd Launcher && dotnet build -c Release -r win-x64 -o out-win, потім
                       python3 ../exe/make_exe.py out-win/TranslationLauncher.exe <пакет>/Tlumach.exe (міняє вшитий шлях
                       на app\TranslationLauncher.dll). Іконка й версія вшиваються SDK (працює і на Linux). Якщо .NET немає,
                       apphost сам показує вікно з посиланням на завантаження.
                       Історія хибних спрацювань (Trojan:Win32/Wacatac!ml): mingw-C, mono mcs і net48-лаунчер, що запускав
                       прихований процес dotnet, — не повертати. Справжнє рішення — підпис коду (сертифікат).
tools/                 schemagen — генератор tmp_schemas.txt (лише для розробки)
lib/                   AssetsTools.NET, Cpp2IL, Mono.Cecil … DLL з UABEANext + classdata.tpk (шлях: властивість UabeaDir)
```

## Ліцензія
Код — GPL-3.0-only (LICENSE, рішення автора 2026-10-28). Назва, знак Ї, іконки — TRADEMARKS.md, не GPL.
У README і «Про програму» чесно вказано: зроблено людиною разом із ШІ (Claude).

## Збірка
```
cd Launcher
dotnet build -c Release -o out
```
Запуск для розробки: `dotnet out/TranslationLauncher.dll --port 47200 --token TEST --no-browser --stay`,
інтерфейс: http://127.0.0.1:47200/?t=TEST

Пакет для користувача: `Tlumach.exe` (apphost, див. exe/) + `app\` (DLL з win-x64 збірки, classdata.tpk, runtimeconfig, deps, www\i18n.js,
THIRD_PARTY_NOTICES.txt) + `Запуск з консоллю (діагностика).bat` + `README_UA.txt` + `LICENSE` + `TRADEMARKS.md` (без `src\`: код на GitHub). Збирає GitHub Actions (.github/workflows/build.yml). Потрібен .NET Runtime 8+.
Папку `app\data\` (settings.json, projects.json, log.txt, window-profile) у пакет НЕ класти.

## Що вже зроблено (версія 1.2)
- Проєкти ігор, окремі папки оригіналів / змінених файлів, «У гру», «Оригінали», змінені файли.
- Автопошук перекладу (ключові слова; рос./білоруська прибрані), ручний пошук як в UABEA, пошук тексту, сортування таблиць.
- Редактор рядків, undo/redo, Ctrl+S — швидке збереження, Ctrl+Shift+S — збереження з запитом, Ctrl+Z / Ctrl+Y.
- Експорт/імпорт TXT/JSON/CSV/дамп/DAT, перетягування файлів у вікно.
- «Імпорт у гру»: файли самі знаходять місце; схожі назви → попередження; однакова структура → заміна;
  інша структура → 1) оновити до актуальної версії + звіт, 2) лише перевірити, 3) нічого не робити.
- Шрифти (TMP Font Replacer): пошук пар MonoBehaviour + Texture2D (усі пари / за словами), перегляд атласу,
  порівняння з оригіналом, створення й вбудовування шрифту, звіт про символи.
- Вітальне вікно, «Про програму», кнопки Донат / Bugs (Discord), теми: біла, чорна, UA/EN.

## Зроблено у версії 1.2 (підготовка до публікації)
- Посилання Донат / Discord вшиті в програму: константи `Api.DonateUrl` / `Api.BugsUrl` (App/Api.cs),
  у «Налаштуваннях» їх більше немає.
- Папка робочих файлів за замовчуванням — `Документи\Unity Translation Launcher\Workspaces` поточного користувача.
  У settings.json за замовчуванням зберігається "" + `WorkspaceOwner` (хеш ПК+користувача, без імен);
  шлях з іншого ПК / профілю відкидається, робочі папки проєктів переносяться під нову папку (Store.Load).
- Ліцензії: усі DLL у lib\ — MIT (AssetsTools.NET, Cpp2IL, Mono.Cecil, AssetRipper.Primitives), атрибуція
  в THIRD_PARTY_NOTICES.txt і рядок у «Про програму». Код UABEA/TMP не копіювався: Dump.cs лише сумісний
  формат JSON-дампу UABEA; tmp_schemas.txt — тільки назви/типи полів TMP_FontAsset, без коду TextMeshPro.
- Теми: CSS-змінні в app.css (`--on-accent`, `--accent3`, `--love`, `--drop` для кожної теми); список тем — `THEMES` в app.js.

## Редизайн (2026-10-27, за прикладом дашборда)
- Розмітка: бічна панель на «рамці» вікна (лого, «Навігація», пункти-«пігулки» з круглими іконками),
  справа — заокруглена панель вмісту (topbar + main + statusbar), картки з тінню, головні кнопки темні (`--btn-dark`).
- Теми: `light` (Біла), `dark` (Чорна, за замовчуванням), `ink` (Чорнило, від треду бренду; авторка вирішила залишити). Інші (рожева, зелена, сіра, «папір») прибрані на прохання авторки. Нові змінні: `--frame`, `--side-*`, `--btn-dark*`, `--on-dark`, `--card-line`, `--card-shadow`.
- UX (2026-10-27): бічна панель — 5 кроків (`STEPS` в app.js: Гра → Знайти тексти → Перекласти → Шрифти → Вбудувати в гру).
  Ручний пошук — вкладка кроку 2, «Імпорт у гру» (batch) — вкладка кроку 5 (`STEP_TABS`). Над кожною сторінкою кроку — `stepHead()`
  (Крок N з 5, вкладки, Назад / Далі). Тулбар: лише проєкт, Зберегти (+ Відмінити/Повторити тільки в редакторі).
  Пауза/Продовжити/Стоп — у рядку стану під час операції; тема й мова — у Налаштуваннях і гарячих клавішах;
  Журнал, Про програму, Донат, Discord, Вихід — іконки внизу бічної панелі. Усі дії — в одному об'єкті `ACTS`.
- Сортування колонок: `th()` / `sortRows()` / `bindSort()` в app.js у всіх таблицях; редактор сортує на сервері
  (`/api/asset/table?sort=<колонка>&desc=1`, `EntryTable.ToJson` + `Natural`), бо рядки підвантажуються сторінками. Порожні клітинки завжди внизу.
- Гарячі клавіші налаштовуються в «Налаштуваннях»: список і типові клавіші — `HOTKEYS` в app.js, зміни зберігаються в `Settings.Hotkeys`
  (лише змінені; "" = без клавіші). Клавіші беруться з фізичної кнопки (e.code), тож працюють і на українській розкладці.
  Ctrl+Z / Ctrl+X у текстовому полі після набору чи виділення лишаються браузерними (відміна / вирізати).
- Вузьке вікно (<1460px): кнопки тулбара лише з іконками (назви — у підказках).

## Каталог Addressables і нова мова (2026-10-27, за гайдами ukrainizer.netlify.app / hikarosato.github.io/guide)
- `Core/Catalog.cs`: після збереження UnityFS-бандла (`Docs.SaveDoc`, «Імпорт у гру», створення шрифту) `Store.PatchCatalogs`
  шукає `catalog*.json` / `catalog*.bin` у папці бандла або найближчій батьківській (зазвичай `StreamingAssets\aa`) і ставить
  m_Crc = 0 для всіх бандлів (як `patchcrc` в AddressablesTools; код не копіювався). Патч на місці: довжина й офсети не міняються.
  Змінений каталог іде в modified/ (оригінал — в original/) і додається до списку «saved» → «У гру» копіює його разом із бандлом.
  Немає каталогу → нічого не робиться. Каталог усередині бандла (catalog.bundle) не підтримується.
- `Extract.AddLanguage` + `POST /api/asset/addlang` + кнопка «Додати українську мову» в редакторі: I2 (новий запис у mLanguages,
  нова клітинка в кожному терміні, Flags доповнюються) і «загальні» багатомовні масиви (`Extract.FindLanguages`: масив рядків,
  у кожному з яких масив рядків однакової довжини ≥ 2; назви мов — верхній масив рядків із «lang/locale/culture» у назві).
  Нові клітинки заповнюються текстом вибраної мови (за замовчуванням English). StringTable (окремий ассет на мову) — не підтримується.
- Режим «Замінити мову» (без правки .dll): кнопка «🇺🇦 Українська мова» → діалог (заміна за замовчуванням, мова — English або
  будь-яка з таблиці; «Додати» — лише для I2 / загальних масивів). `POST /api/asset/replacelang` зберігає в `<WorkDir>\languages.json`
  колонку, мову, regex рядків (для загальних масивів — `Extract.LangRowPattern`) і знімок оригінального тексту (з original/, якщо є).
  `/api/asset/table` тоді віддає `replace` і `orig` для рядків; редактор показує Ключ | Оригінал | Українська (у колонку тієї мови).
  StringTable теж підтримується (одна мова — локаль таблиці).
- Мови в коді гри (`Core/DllLangs.cs`, Mono.Cecil; лише Mono — для IL2CPP повідомлення «використайте заміну»): кнопка
  «Шукати мови в коді гри (.dll)» у діалозі «Українська мова» → job `POST /api/dll/langscan` по Managed\*.dll (без Unity*/System*
  тощо) знаходить enum із ≥3 мовами (або ≥2 + «lang» у назві), `new string[]{…}` і `new List<string>{…}` з літералами мов, і
  методи зі згадками ≥3 мов (лише показ). `POST /api/dll/langadd` {id, value} додає член enum (max+1) / елемент масиву / Add у список;
  id масиву/списку = номер ініціалізатора в методі (не зсув IL). Значення пропонується в стилі списку (Ukrainian / Українська / uk / UK / uk-UA).
  Змінена .dll іде в modified/ (оригінал — original/), далі «У гру». Перевірено на тестовій .dll: змінений IL виконується.
- Якщо правки в Projects.cs / app.js / i18n.js загубилися: `python3 /mnt/project-files/patches/reapply_catalog_lang.py` з папки Launcher
  (скрипт повертає лише каталог + «Додати мову»; режим заміни — в app.js: `languageDialog`, `drawLangBtn`, `replMode` в i18n.js).

## Правки 2026-10-27 (вечір)
- Тема/мова в Налаштуваннях застосовуються одразу (onchange → saveSettings). Чорна тема: помаранчевий акцент #ff8a3d, текст на акценті #1a0c02; крапка лого — `svg.logo .dot-mark` з `--logo-dot`.
- Імпорт «Готових файлів»: блок вибору дії — над таблицею, радіокнопки `name=bMode` без вибору за замовчуванням, бадж «рекомендовано» на варіанті 1, кнопка «Виконати» (`S.batch.mode`).
- Каталог Addressables: `GET /api/catalogs` (Store.CatalogStatus), `POST /api/catalogs/patch` (Store.PatchAllCatalogs); у JS `askCatalogs(force)` після «У гру»/збереження/імпорту + картка внизу кроку 5.
- Папка експорту на проєкт: `Project.ExportDir` (порожньо = WorkDir/exports, `Exports` повертає актуальну), `POST /api/projects/export-dir`, JS `chooseExportDir()` питає під час першого експорту; змінити — меню ⋯ проєкту. Експорт більше не дублюється завантаженням браузера.
- Шрифти: скан показує й окремі .ttf/.otf гри (kind "TTF", pathId ""). `GET /api/fonts/ttf` віддає байти m_FontData (Unity Font) або файл; `save=1` пише в папку експорту. У картці атласу для TTF — перегляд через FontFace і кнопка «💾 TTF».
- Помилки «TypeInfoResolver» (JsonSerializerOptions) — усім options додано resolver + `JsonSerializerIsReflectionEnabledByDefault` у csproj; «onLog of null» — гонка pollJob, виправлено.
- Імпорт приймає будь-які розширення та текстові дампи UABEA (Dump.FromText / ParseAny).
- Детальний README: /mnt/project-files/docs/README_UA.md (у пакеті як README_UA.txt).
- Підказки при наведенні: `window.TIPS` (uk/en) унизу i18n.js, ключі = ключі I18N. app.js (`addTips`/`tipFor`) сам знаходить елементи з таким текстом (або placeholder) через MutationObserver і ставить `data-tip`; `title` теж перетворюється на стилізовану підказку (`.tipbox`). Кнопкові дії лише на кнопках (TIP_BUTTON_ONLY), назви кроків — лише в бічній панелі.
- Крок 6 «Зібрати архів» (page `pack`): `GET /api/pack` (прев'ю), `POST /api/pack` (job). Архів `<Гра>_Data.zip` у папці експорту: усі файли з modified/ під папкою *_Data, за шляхами гри (зразок — Discounty_Data.zip від авторки). Файли поза *_Data не додаються (показуються як пропущені).

## Версія 1.3: ручна заміна і прискорення (2026-10-29)
- Ручний пошук: галочки на файлах дерева (`S.manual.checked`) → `replaceFiles(rels)`: один файл — `pickPath` файлу, кілька — папка +
  `POST /api/file/replace/match` (та сама назва; спершу сама папка, далі підпапки). `POST /api/file/replace` (job, `ReplaceFiles` у Batch.cs):
  Docs.Close, BackupOriginal, копія в modified/, PatchCatalogs, далі askApply як у збереженні.
- Галочки на ассетах (`S.manual.picked`, «вибрати все» — видимі рядки) → `importMany` → `POST /api/manual/import`: пари файл↔ассет за назвою /
  PathID (`Score` ≥ 0.98 або PidHint), 1 файл + 1 ассет — напряму; застосування через `ApplyBatch(..., items, fromBatch:false)` зі звітом.
- Індикатор зайнятості: `busy()` в app.js (лічильник запитів `api()` + `S.job`), `#busy` у рядку стану, смуга на прогресі.
- Індекс гри (`IndexFor`) — один на проєкт одночасно (`BuildLock`), кешується в `<WorkDir>\game-index.json`. `/api/batch/find` не чекає на
  побудову: віддає {items, building, indexed}, діалог «Інша ціль» оновлюється кожні 3 с.
- «Інша ціль» (баг до 1.2): пошук блокувався повною індексацією гри, а ручний вибір перезаписував аналіз (`Analyze` ставив Target заново)
  і не застосовувався без «Виконати». Тепер `BItem.Manual` зберігається в аналізі, після вибору — «Замінити зараз» (`batchApply("update", [id])`,
  через `afterJob`, якщо триває інша операція).
- Крок 5 перейменовано на «Вбудувати в гру» / «Embed into game».

## TODO від авторки — виконано (див. вище)
1. **Прибрати налаштування для розробників** перед поширенням: поля посилань на Донат і Discord у «Налаштуваннях» —
   це інформація лише для розробників (вшити посилання в програму, а не давати користувачам їх міняти).
   Загалом перевірити, щоб у збірці не було персональної інформації.
2. **Папка для робочих файлів**: зараз у налаштуваннях зберігається шлях, прив'язаний до ПК авторки
   (через збережений settings.json у app\data). Треба автоматично ставити шлях у «Документи» користувача,
   на чиєму ПК відкрито програму (напр. Documents\Unity Translation Launcher\Workspaces), і не переносити
   абсолютні шляхи з чужого ПК.

## Відомі обмеження
- Старий .dat з іншою структурою не оновлюється автоматично (потрібен JSON-дамп).
- Нові ключі StringTable з TXT/CSV не додаються (лише через JSON-дамп).
- Текстовий (.txt) дамп UABEA розпізнається (Dump.FromText → той самий JSON, що й Dump.ToJson) і в «Імпорті у гру», і в редакторі.
- Атласи у стиснених мобільних форматах (ETC/ASTC/Crunch) не показуються.
