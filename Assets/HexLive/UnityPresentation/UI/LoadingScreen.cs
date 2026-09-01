using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;
using PresentationSimulationMode = HexLive.UnityPresentation.Bootstrap.SimulationMode;

namespace HexLive.UnityPresentation.UI
{
    // Spec 41.1/41.4: full-screen loading overlay. Opens as a MENU (Continue /
    // New game) over the island art — the world is bootstrapped only after
    // the choice. Then: restore the saved model (41.2 v2), wind the offline
    // time (chunked so the bar animates, 41.3),
    // let the renderer build every view behind the curtain,
    // pre-warm the character panel/portrait for each girl (kills the
    // first-click hitch), fade out, unpause, focus the camera on Jana.
    public sealed class LoadingScreen : MonoBehaviour
    {
        /// <summary>True while the title/loading screen owns the display —
        /// the in-game Escape menu must never open over it.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>True from the moment the world is created until it stands
        /// at its FINAL tick — i.e. it covers the restore and the offline wind,
        /// not just the wind loop. The renderer skips its whole Update in this
        /// window: the runner reports IsReady the instant Configure returns, so
        /// without this the very next frame builds the entire scene (terrain,
        /// actresses, props) on the pre-wind state, only for the wind to make it
        /// stale. Winding is pure headless simulation; views build ONCE
        /// afterward, in loading phase 3.</summary>
        public static bool IsReplaying { get; private set; }

        // Бюджета кадра у намотки больше НЕТ и он не нужен: она крутится на
        // своём потоке, а главный в это время грузит арт и рисует полосу. Был
        // ReplayBudgetMsPerFrame = 120 мс — компромисс между «мотать быстро» и
        // «не морозить экран», который стоил и того и другого.
        private const float FadeSeconds = 0.7f;

        // Диагностика зависшего занавеса. Раз в столько секунд ожидание
        // ПЕЧАТАЕТ, кого именно ждёт: «экран не пропал» без имени виноватого —
        // симптом, на который отвечают догадками (§109.16).
        private const float StallReportSeconds = 5f;

        // Живой мир (сервер) не останавливается ради загрузчика, поэтому
        // ожидание готовности тел здесь ограничено: после этого занавес
        // поднимается силой, с ошибкой в лог. Локальный путь ждёт мир на
        // ПАУЗЕ и остаётся безлимитным, как и был.
        private const float LiveWorldGiveUpSeconds = 30f;

        // §41.3: единственные две ячейки, которые главный поток делит с
        // воркером намотки. Тик — только на чтение (полоса и часы), стоп-флаг —
        // только на запись. volatile здесь не для атомарности (int и bool
        // атомарны и так), а чтобы значение не осело в регистре: цикл воркера
        // не трогает ничего, что заставило бы JIT перечитать флаг.
        private volatile int _windTick;
        private volatile bool _windAbort;

        private SimulationRunnerBehaviour _runner;
        private int _targetTick;
        private SaveGameData _save;
        private bool _menuChosen;
        private bool _continueChosen;
        private bool _restartChosen;

        // §41.8: every local colony is its own catalog entry. The active slot
        // only changes when Continue or a card in the world library chooses it.
        private IReadOnlyList<SaveWorldInfo> _worlds;
        private VisualElement _worldLibraryBox;
        private readonly List<Texture2D> _worldPreviewTextures = new();

        // §Server: chosen "watch a server" instead of building a world here.
        // The addresses themselves live in ServerBook, which also remembers
        // which kind of session was last played so Continue can follow it.
        private bool _connectChosen;
        private VisualElement _connectBox;
        private TextField _serverField;
        private TextField _tokenField;

        // §146/§146.9: New game opens a dedicated scalable mode panel. The last
        // pick is remembered; Continue/Restart still take mode from the save.
        private HexLive.Simulation.Bootstrap.GameMode _newGameMode;
        private VisualElement _newGameBox;
        private VisualElement _newGameModeArtwork;
        private Label _newGameModeTitle;
        private Label _newGameModeDescription;
        private readonly Dictionary<HexLive.Simulation.Bootstrap.GameMode, Button>
            _newGameModeButtons = new();
        private const string NewGameModePref = "HexLive.NewGameMode";

        private readonly struct NewGameModeOption
        {
            public readonly HexLive.Simulation.Bootstrap.GameMode Mode;
            public readonly string TitleTerm;
            public readonly string DescriptionTerm;
            public readonly string ArtClass;

            public NewGameModeOption(
                HexLive.Simulation.Bootstrap.GameMode mode,
                string titleTerm, string descriptionTerm, string artClass)
            {
                Mode = mode;
                TitleTerm = titleTerm;
                DescriptionTerm = descriptionTerm;
                ArtClass = artClass;
            }
        }

        private static readonly NewGameModeOption[] NewGameModeOptions =
        {
            new(HexLive.Simulation.Bootstrap.GameMode.Feud,
                "menu.newgame.mode.feud", "menu.newgame.mode.feud.description", "art-feud"),
            new(HexLive.Simulation.Bootstrap.GameMode.BigIsland,
                "menu.newgame.mode.bigisland", "menu.newgame.mode.bigisland.description",
                "art-bigisland"),
            new(HexLive.Simulation.Bootstrap.GameMode.HugeIsland,
                "menu.newgame.mode.hugeisland", "menu.newgame.mode.hugeisland.description",
                "art-hugeisland"),
            new(HexLive.Simulation.Bootstrap.GameMode.Maniac,
                "menu.newgame.mode.maniac", "menu.newgame.mode.maniac.description",
                "art-maniac"),
        };

        private VisualElement _root;
        private VisualElement _menuBox;
        private VisualElement _progressFill;
        private VisualElement _progressStrip;
        private Label _status;
        // §41.3: big day/clock readout shown only while time winds forward —
        // the player watches days roll by, not raw ticks.
        private Label _timeReadout;

        // За шторкой кадры никому не нужны — важна пропускная способность
        // асинхронной загрузки. Дефолтный BelowNormal ограничивает интеграцию
        // AssetBundle/Asset-загрузок парой миллисекунд на кадр; при ~180 wear-
        // бандлах это минуты чистого ожидания. High поднимает бюджет на время
        // загрузочного экрана и возвращается на выходе.
        private ThreadPriority _previousLoadingPriority;

        private void OnEnable()
        {
            IsActive = true;

            // Unity's timeScale survives Enter Play Mode when domain reload is
            // disabled, and can also be stranded at zero by an in-play script
            // reload while the Escape menu is open. The simulation uses its
            // own unscaled clock, so that stale zero produces the exact split
            // the player sees: roots move, scaled-time Animators stay frozen.
            // Loading owns the whole display now; no gameplay menu pause needs
            // preserving across this boundary.
            Time.timeScale = 1f;
        }

        private void OnDestroy()
        {
            IsActive = false;
            Application.backgroundLoadingPriority = _previousLoadingPriority;
            // Корутина умирает молча (domain reload в игре), а воркер намотки —
            // нет: он крутил бы мёртвый мир в фоне. Флаг он проверяет каждый
            // тик, так что остановка занимает один шаг.
            _windAbort = true;
            // Safety: if the coroutine died mid-wind (an in-play domain reload
            // kills coroutines silently), don't leave the renderer muted.
            IsReplaying = false;

            foreach (var texture in _worldPreviewTextures)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }
            _worldPreviewTextures.Clear();
        }

        public void Begin(SimulationRunnerBehaviour runner)
        {
            // Не в OnEnable: экран создаётся бутстрапом ещё до конца
            // инициализации play mode, и Unity успевает вернуть приоритету
            // асинхронной загрузки дефолт ПОСЛЕ раннего OnEnable (замер
            // 2026-08-27: OnEnable ставил High, к первому кадру снова
            // BelowNormal).
            _previousLoadingPriority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            _runner = runner;
            _worlds = SaveGame.ListWorlds();
            _save = SaveGame.TryReadHeader();
            BuildUi();
            StartCoroutine(Run());
        }

        private void BuildUi()
        {
            var document = gameObject.AddComponent<UIDocument>();
            // Own settings clone: 1080p-referenced scaling (the shared debug
            // asset uses a tiny physical-size canvas that blew the menu up to
            // half the screen) and a panel sort above every HUD layer,
            // including the Escape menu (220).
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "LoadingScreenPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 400;
                document.panelSettings = settings;
            }

            document.sortingOrder = 1000; // above co-panel documents

            _root = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    left = 0, top = 0, right = 0, bottom = 0,
                    backgroundColor = new Color(0.05f, 0.07f, 0.09f, 1f),
                    justifyContent = Justify.FlexEnd,
                    alignItems = Align.Center
                }
            };

            // Spec 41.1: fal.ai island art; the flat color above is the
            // fallback when the texture is absent.
            var art = Resources.Load<Texture2D>("HexLive/UI/loading_island");
            if (art != null)
            {
                _root.style.backgroundImage = new StyleBackground(art);
                _root.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            }

            // The game logo floats over the art in the top-left corner.
            var logo = Resources.Load<Texture2D>("HexLive/UI/logo");
            if (logo != null)
            {
                var logoImage = new VisualElement
                {
                    style =
                    {
                        position = Position.Absolute,
                        left = 36, top = 20,
                        width = 360,
                        height = 360f * logo.height / logo.width,
                        backgroundImage = new StyleBackground(logo)
                    }
                };
                _root.Add(logoImage);
            }

            // Spec 41.4: the main menu — a compact dark card docked to the
            // bottom-left corner: icon rows over a dimmed rounded panel. The
            // world doesn't exist yet; buttons decide which world to build.
            _menuBox = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    left = 40, bottom = 40,
                    alignItems = Align.FlexStart
                }
            };

            var card = new VisualElement
            {
                style =
                {
                    width = 300,
                    backgroundColor = new Color(0.055f, 0.070f, 0.085f, 0.86f),
                    borderTopLeftRadius = 18, borderTopRightRadius = 18,
                    borderBottomLeftRadius = 18, borderBottomRightRadius = 18,
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopWidth = 1, borderBottomWidth = 1,
                    borderLeftColor = new Color(1f, 1f, 1f, 0.10f),
                    borderRightColor = new Color(1f, 1f, 1f, 0.10f),
                    borderTopColor = new Color(1f, 1f, 1f, 0.10f),
                    borderBottomColor = new Color(1f, 1f, 1f, 0.10f),
                    paddingLeft = 16, paddingRight = 16,
                    paddingTop = 16, paddingBottom = 14
                }
            };

            // Continue means "put me back where I was", and where you were may
            // have been someone else's world. So it follows the LAST session:
            // a server if that is what you were watching, the local save
            // otherwise. The two paths differ completely further down — a local
            // resume winds the offline days forward, a server resume winds
            // nothing, because the colony never stopped.
            var resumeServer = ServerBook.LastSessionWasRemote ? ServerBook.LastUrl : null;
            var continueLabel = resumeServer != null
                ? Loc.Get("menu.continue") + "  ·  " + ServerBook.ShortLabel(resumeServer)
                : Loc.Get("menu.continue");

            card.Add(MakeMenuRow("play", continueLabel,
                primary: true, enabled: resumeServer != null || _save != null, () =>
            {
                if (resumeServer != null)
                {
                    BeginConnect(resumeServer);
                    return;
                }

                _continueChosen = true;
                _menuChosen = true;
            }));

            // Restart: the SAME island (the save's seed) from day 1 — only
            // meaningful while a save exists, greyed out otherwise.
            card.Add(MakeMenuRow("restart", Loc.Get("menu.restart"),
                primary: false, enabled: _save != null, () =>
            {
                _continueChosen = false;
                _restartChosen = true;
                _menuChosen = true;
            }));

            // Local worlds stay reachable even when Continue points at the
            // last remote server. This is the durable way back from online
            // play into any single-player colony (§41.8).
            card.Add(MakeMenuRow("worlds", Loc.Get("menu.worlds"),
                primary: false, enabled: true, () => ToggleWorldLibrary()));
            _worldLibraryBox = BuildWorldLibraryBox();

            // §146: New game opens the dedicated scenario browser.
            card.Add(MakeMenuRow("plus", Loc.Get("menu.newgame"),
                primary: false, enabled: true, () => ToggleNewGameBox()));
            _newGameBox = BuildNewGameBox();

            // Watch a world running on a server instead of building one here.
            // The row expands into an address field rather than opening another
            // screen — one field is not worth a screen, and it keeps the whole
            // choice visible at once.
            card.Add(MakeMenuRow("play", Loc.Get("menu.connect"),
                primary: false, enabled: true, () => ToggleConnectRow(card)));
            _connectBox = BuildConnectRow();
            card.Add(_connectBox);

            // Placeholders for now — visible but not wired up yet.
            card.Add(MakeMenuRow("gear", Loc.Get("menu.settings"),
                primary: false, enabled: false, null));
            card.Add(MakeMenuRow("person", Loc.Get("menu.characters"),
                primary: false, enabled: false, null));

            card.Add(MakeMenuRow("exit", Loc.Get("menu.quit"),
                primary: false, enabled: true, () =>
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }));

            var divider = new VisualElement
            {
                style =
                {
                    height = 1,
                    marginTop = 10, marginBottom = 10,
                    backgroundColor = new Color(1f, 1f, 1f, 0.12f)
                }
            };
            card.Add(divider);

            var tagline = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingLeft = 8, paddingRight = 4
                }
            };
            tagline.Add(MakeIcon("bulb", new Color(0.72f, 0.75f, 0.77f)));
            tagline.Add(new Label(Loc.Get("menu.tagline"))
            {
                style =
                {
                    fontSize = 12,
                    color = new Color(0.72f, 0.75f, 0.77f),
                    whiteSpace = WhiteSpace.Normal,
                    marginLeft = 10
                }
            });
            card.Add(tagline);

            _menuBox.Add(card);
            _root.Add(_menuBox);
            _root.Add(_worldLibraryBox);
            _root.Add(_newGameBox);

            // Bottom gradient strip carrying the title/status/progress, so
            // text stays readable over any art. Hidden while the menu is up.
            var strip = new VisualElement
            {
                style =
                {
                    width = Length.Percent(100),
                    paddingBottom = 28, paddingTop = 18,
                    paddingLeft = 32, paddingRight = 32,
                    backgroundColor = new Color(0f, 0f, 0f, 0.55f),
                    alignItems = Align.Center,
                    display = DisplayStyle.None
                }
            };
            _progressStrip = strip;
            _root.Add(strip);

            // The centerpiece during the time-wind: a large, letter-spaced
            // day + clock. Hidden until replay starts, so the other phases
            // (island/warmup) keep the compact single-line status.
            _timeReadout = new Label(string.Empty)
            {
                style =
                {
                    fontSize = 44,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    letterSpacing = 3,
                    color = new Color(0.96f, 0.93f, 0.86f, 1f),
                    marginBottom = 6,
                    display = DisplayStyle.None
                }
            };
            strip.Add(_timeReadout);

            _status = new Label("...")
            {
                style =
                {
                    fontSize = 14,
                    unityFontStyleAndWeight = FontStyle.Normal,
                    letterSpacing = 4,
                    color = new Color(0.72f, 0.75f, 0.72f, 1f),
                    marginBottom = 12
                }
            };
            strip.Add(_status);

            var barBack = new VisualElement
            {
                style =
                {
                    width = Length.Percent(60),
                    height = 8,
                    backgroundColor = new Color(1f, 1f, 1f, 0.15f),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    overflow = Overflow.Hidden
                }
            };
            strip.Add(barBack);

            _progressFill = new VisualElement
            {
                style =
                {
                    width = Length.Percent(0),
                    height = Length.Percent(100),
                    backgroundColor = new Color(0.95f, 0.75f, 0.35f, 1f)
                }
            };
            barBack.Add(_progressFill);

            document.rootVisualElement.Add(_root);
        }

        // Menu rows in the reference style: icon + label on a transparent
        // row, gold for the primary action, subtle light wash on hover.
        // Disabled rows stay visible but dimmed and unclickable.
        // §Server: the address book, folded away until "Connect" is clicked.
        // Servers you have watched before are one click; a new one is one field.
        private VisualElement BuildConnectRow()
        {
            var box = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None,
                    paddingLeft = 12, paddingRight = 12,
                    paddingBottom = 8
                }
            };

            foreach (var url in ServerBook.Recent())
            {
                var remembered = url;
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
                };

                var connect = MakeMenuRow("play", ServerBook.ShortLabel(remembered),
                    primary: false, enabled: true, () => BeginConnect(remembered));
                connect.style.flexGrow = 1;
                connect.style.height = 36;
                row.Add(connect);

                // A dead address you keep having to scroll past is worse than no
                // history at all, so every entry can be dropped.
                var forget = MakeMenuRow("exit", string.Empty, primary: false, enabled: true, () =>
                {
                    ServerBook.Forget(remembered);
                    row.RemoveFromHierarchy();
                });
                forget.style.height = 36;
                forget.style.paddingLeft = 6;
                forget.style.paddingRight = 6;
                row.Add(forget);

                box.Add(row);
            }

            _serverField = new TextField
            {
                // Falls back to a local server — what anyone trying this first will want.
                value = ServerBook.LastUrl ?? ServerBook.DefaultUrl,
                style =
                {
                    marginLeft = 0, marginRight = 0, marginTop = 6, marginBottom = 8,
                    fontSize = 13
                }
            };
            box.Add(_serverField);

            // §145.3: токен управления. Пустое поле — честный зритель; с
            // токеном сервер разрешит этому подключению приказы NPC. Помнится,
            // как URL: набирать 55 знаков при каждом входе никто не станет.
            var tokenLabel = new Label(Loc.Get("menu.connect.token"))
            {
                style = { fontSize = 11, opacity = 0.75f, marginTop = 2 }
            };
            box.Add(tokenLabel);
            _tokenField = new TextField
            {
                // §145.3: токен из командной строки (-hexlive-token) главнее
                // запомненного — он свежее. Один запуск через скрипт засевает
                // поле; первое «подключиться» запоминает токен в ServerBook,
                // и дальше игра работает даже с двойного клика без аргументов.
                value = SessionConfig.ControlToken ?? ServerBook.LastToken ?? string.Empty,
                style =
                {
                    marginLeft = 0, marginRight = 0, marginTop = 2, marginBottom = 8,
                    fontSize = 13
                }
            };
            box.Add(_tokenField);

            box.Add(MakeMenuRow("play", Loc.Get("menu.connect.go"), primary: true, enabled: true,
                () => BeginConnect(_serverField.value)));

            return box;
        }

        // §41.8: dedicated local-world browser. UXML/USS own the reusable
        // shell; cards are data-driven because every directory under saves/
        // is a world, not a fixed slot count.
        private VisualElement BuildWorldLibraryBox()
        {
            var template = Resources.Load<VisualTreeAsset>("HexLive/UI/WorldLibraryPanel");
            if (template == null)
            {
                Debug.LogError("[HexLive] Missing Resources/HexLive/UI/WorldLibraryPanel.uxml");
                return new VisualElement { name = "missingWorldLibraryPanel" };
            }

            var host = template.CloneTree();
            host.AddToClassList("world-library-host");
            host.Q<Label>("panelKicker").text = Loc.Get("menu.worlds.kicker");
            host.Q<Label>("panelTitle").text = Loc.Get("menu.worlds.title");
            host.Q<Label>("panelDescription").text = Loc.Get("menu.worlds.description");
            host.Q<Button>("closeButton").clicked += () =>
                host.RemoveFromClassList("is-open");

            var list = host.Q<ScrollView>("worldList");
            var empty = host.Q<VisualElement>("emptyState");
            host.Q<Label>("emptyTitle").text = Loc.Get("menu.worlds.empty.title");
            host.Q<Label>("emptyDescription").text = Loc.Get("menu.worlds.empty.description");

            if (_worlds == null || _worlds.Count == 0)
            {
                list.AddToClassList("is-empty");
                empty.AddToClassList("is-visible");
                return host;
            }

            foreach (var world in _worlds)
            {
                list.Add(BuildWorldCard(world, list, empty));
            }

            return host;
        }

        private VisualElement BuildWorldCard(
            SaveWorldInfo world, ScrollView list, VisualElement empty)
        {
            var card = new VisualElement();
            card.AddToClassList("world-card");

            var previewFrame = new VisualElement();
            previewFrame.AddToClassList("world-card-preview-frame");
            var preview = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            preview.AddToClassList("world-card-preview");
            preview.image = LoadWorldPreview(world) ??
                Resources.Load<Texture2D>("HexLive/UI/loading_island");
            previewFrame.Add(preview);
            card.Add(previewFrame);

            var copy = new VisualElement();
            copy.AddToClassList("world-card-copy");

            var title = new Label(WorldModeTitle(world.Header.mode));
            title.AddToClassList("world-card-title");
            copy.Add(title);

            var day = HexLive.Simulation.Runtime.EnvironmentSystem.CalendarDay(world.Header.tick);
            var meta = new Label(string.Format(
                Loc.Get("menu.worlds.meta"), day, world.Header.seed));
            meta.AddToClassList("world-card-meta");
            copy.Add(meta);

            var dateCulture = Loc.Current == Language.Russian
                ? CultureInfo.GetCultureInfo("ru-RU")
                : CultureInfo.GetCultureInfo("en-US");
            var savedLocal = DateTimeOffset.FromUnixTimeSeconds(world.LastSavedUnixSeconds)
                .ToLocalTime().ToString("g", dateCulture);
            var date = new Label(string.Format(Loc.Get("menu.worlds.saved"), savedLocal));
            date.AddToClassList("world-card-date");
            copy.Add(date);
            card.Add(copy);

            var actions = new VisualElement();
            actions.AddToClassList("world-card-actions");

            var load = new Button(() => ResumeWorld(world))
            {
                text = Loc.Get("menu.worlds.load")
            };
            load.AddToClassList("world-card-action");
            load.AddToClassList("world-card-load");
            actions.Add(load);

            var warning = new Label(Loc.Get("menu.worlds.delete.warning"));
            warning.AddToClassList("world-card-delete-warning");
            actions.Add(warning);

            var confirmingDelete = false;
            var delete = new Button();
            delete.text = Loc.Get("menu.worlds.delete");
            delete.AddToClassList("world-card-delete");
            actions.Add(delete);

            var cancel = new Button();
            cancel.text = Loc.Get("menu.worlds.delete.cancel");
            cancel.AddToClassList("world-card-delete-cancel");
            actions.Add(cancel);

            cancel.clicked += () =>
            {
                confirmingDelete = false;
                actions.RemoveFromClassList("is-confirming");
                delete.text = Loc.Get("menu.worlds.delete");
            };
            delete.clicked += () =>
            {
                if (!confirmingDelete)
                {
                    confirmingDelete = true;
                    actions.AddToClassList("is-confirming");
                    delete.text = Loc.Get("menu.worlds.delete.confirm");
                    return;
                }

                if (!SaveGame.DeleteWorld(world.Id))
                {
                    confirmingDelete = false;
                    actions.RemoveFromClassList("is-confirming");
                    delete.text = Loc.Get("menu.worlds.delete.failed");
                    return;
                }

                _worlds = SaveGame.ListWorlds();
                card.RemoveFromHierarchy();
                if (_worlds.Count == 0)
                {
                    list.AddToClassList("is-empty");
                    empty.AddToClassList("is-visible");
                }
            };

            card.Add(actions);

            return card;
        }

        private Texture2D LoadWorldPreview(SaveWorldInfo world)
        {
            if (!world.HasPreview)
            {
                return null;
            }

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!ImageConversion.LoadImage(
                        texture, File.ReadAllBytes(world.PreviewPath), false))
                {
                    Destroy(texture);
                    return null;
                }

                texture.name = "WorldPreview_" + world.Id;
                _worldPreviewTextures.Add(texture);
                return texture;
            }
            catch (Exception e)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
                Debug.LogWarning($"[HexLive] World preview unreadable ({world.Id}): {e.Message}");
                return null;
            }
        }

        private static string WorldModeTitle(int mode)
        {
            foreach (var option in NewGameModeOptions)
            {
                if ((int)option.Mode == mode)
                {
                    return Loc.Get(option.TitleTerm);
                }
            }

            return Loc.Get("menu.newgame.mode.feud");
        }

        private void ResumeWorld(SaveWorldInfo world)
        {
            if (!SaveGame.SelectWorld(world.Id))
            {
                return;
            }

            _save = world.Header;
            _connectChosen = false;
            _restartChosen = false;
            _continueChosen = true;
            if (SessionConfig.Mode == PresentationSimulationMode.Remote)
            {
                SessionConfig.UseServer(null);
            }
            _worldLibraryBox?.RemoveFromClassList("is-open");
            _menuChosen = true;
        }

        private void ToggleWorldLibrary()
        {
            if (_worldLibraryBox == null)
            {
                return;
            }

            _newGameBox?.RemoveFromClassList("is-open");
            _worldLibraryBox.ToggleInClassList("is-open");
        }

        /// <summary>
        /// Accepts whatever was typed or clicked and leaves the menu. The URL is
        /// only REMEMBERED here, not yet proven — a bad address is remembered
        /// too, so the player can edit it instead of retyping from scratch.
        /// </summary>
        private void BeginConnect(string rawUrl)
        {
            var url = ServerBook.Normalize(rawUrl);
            if (url.Length == 0)
            {
                return;
            }

            ServerBook.Remember(url);
            ServerBook.RememberToken(_tokenField?.value);
            SessionConfig.UseServer(url, _tokenField?.value);

            _connectChosen = true;
            _continueChosen = false;
            _worldLibraryBox?.RemoveFromClassList("is-open");
            _menuChosen = true;
        }

        // §146/§146.9: one separate, data-driven panel for every scenario.
        // Adding the next mode is one descriptor + localization/art, not a new
        // main-menu branch.
        private VisualElement BuildNewGameBox()
        {
            var template = Resources.Load<VisualTreeAsset>("HexLive/UI/GameModePanel");
            if (template == null)
            {
                Debug.LogError("[HexLive] Missing Resources/HexLive/UI/GameModePanel.uxml");
                return new VisualElement { name = "missingGameModePanel" };
            }

            var host = template.CloneTree();
            host.AddToClassList("game-mode-host");
            host.Q<Label>("panelTitle").text = Loc.Get("menu.newgame.mode.panel.kicker");
            host.Q<Label>("panelSubtitle").text = Loc.Get("menu.newgame.mode.panel.title");
            host.Q<Button>("closeButton").clicked += () =>
                host.RemoveFromClassList("is-open");

            _newGameModeArtwork = host.Q<VisualElement>("modeArtwork");
            _newGameModeTitle = host.Q<Label>("modeTitle");
            _newGameModeDescription = host.Q<Label>("modeDescription");

            var list = host.Q<ScrollView>("modeList");
            _newGameModeButtons.Clear();
            foreach (var option in NewGameModeOptions)
            {
                var captured = option;
                var button = new Button(() => SelectNewGameMode(captured.Mode))
                {
                    text = Loc.Get(option.TitleTerm)
                };
                button.AddToClassList("game-mode-button");
                list.Add(button);
                _newGameModeButtons[option.Mode] = button;
            }

            var start = host.Q<Button>("startButton");
            start.text = Loc.Get("menu.newgame.mode.start");
            start.clicked += StartSelectedNewGame;

            var remembered = PlayerPrefs.GetInt(NewGameModePref, 0);
            _newGameMode = remembered switch
            {
                (int)HexLive.Simulation.Bootstrap.GameMode.BigIsland =>
                    HexLive.Simulation.Bootstrap.GameMode.BigIsland,
                (int)HexLive.Simulation.Bootstrap.GameMode.HugeIsland =>
                    HexLive.Simulation.Bootstrap.GameMode.HugeIsland,
                (int)HexLive.Simulation.Bootstrap.GameMode.Maniac =>
                    HexLive.Simulation.Bootstrap.GameMode.Maniac,
                _ => HexLive.Simulation.Bootstrap.GameMode.Feud
            };
            SelectNewGameMode(_newGameMode);
            return host;
        }

        private void SelectNewGameMode(HexLive.Simulation.Bootstrap.GameMode mode)
        {
            _newGameMode = mode;
            foreach (var pair in _newGameModeButtons)
            {
                pair.Value.EnableInClassList("is-selected", pair.Key == mode);
            }

            foreach (var option in NewGameModeOptions)
            {
                _newGameModeArtwork.RemoveFromClassList(option.ArtClass);
                if (option.Mode != mode) continue;
                _newGameModeArtwork.AddToClassList(option.ArtClass);
                _newGameModeTitle.text = Loc.Get(option.TitleTerm);
                _newGameModeDescription.text = Loc.Get(option.DescriptionTerm);
            }

            PlayerPrefs.SetInt(NewGameModePref, (int)mode);
            PlayerPrefs.Save();
        }

        private void StartSelectedNewGame()
        {
            _continueChosen = false;
            _restartChosen = false;
            _newGameBox?.RemoveFromClassList("is-open");
            _menuChosen = true;
        }

        private void ToggleNewGameBox()
        {
            if (_newGameBox == null)
            {
                return;
            }

            _worldLibraryBox?.RemoveFromClassList("is-open");
            _newGameBox.ToggleInClassList("is-open");
        }

        private void ToggleConnectRow(VisualElement card)
        {
            if (_connectBox == null)
            {
                return;
            }

            var opening = _connectBox.style.display == DisplayStyle.None;
            _connectBox.style.display = opening ? DisplayStyle.Flex : DisplayStyle.None;
            if (opening)
            {
                _worldLibraryBox?.RemoveFromClassList("is-open");
                _newGameBox?.RemoveFromClassList("is-open");
                _serverField?.Focus();
            }
        }

        private static Button MakeMenuRow(
            string icon, string text, bool primary, bool enabled, System.Action onClick)
        {
            var gold = new Color(0.941f, 0.706f, 0.361f);
            var textColor = new Color(0.906f, 0.925f, 0.937f);
            var dim = new Color(0.45f, 0.49f, 0.52f);
            var color = !enabled ? dim : primary ? gold : textColor;

            var button = new Button(onClick)
            {
                text = string.Empty,
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    height = 46,
                    marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 2,
                    paddingLeft = 12, paddingRight = 12,
                    backgroundColor = Color.clear,
                    borderLeftWidth = 0, borderRightWidth = 0,
                    borderTopWidth = 0, borderBottomWidth = 0,
                    borderTopLeftRadius = 12, borderTopRightRadius = 12,
                    borderBottomLeftRadius = 12, borderBottomRightRadius = 12
                }
            };

            button.Add(MakeIcon(icon, color));
            button.Add(new Label(text)
            {
                style =
                {
                    fontSize = 17,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = color,
                    marginLeft = 12
                },
                pickingMode = PickingMode.Ignore
            });

            if (enabled)
            {
                button.RegisterCallback<MouseEnterEvent>(_ =>
                    button.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f));
                button.RegisterCallback<MouseLeaveEvent>(_ =>
                    button.style.backgroundColor = Color.clear);
            }
            else
            {
                button.SetEnabled(false);
            }

            return button;
        }

        // Tiny vector icons painted with Painter2D — no textures needed.
        private static VisualElement MakeIcon(string kind, Color color)
        {
            const float s = 20f;
            var el = new VisualElement
            {
                style = { width = s, height = s, flexShrink = 0 },
                pickingMode = PickingMode.Ignore
            };
            el.generateVisualContent += ctx =>
            {
                var p = ctx.painter2D;
                p.fillColor = color;
                p.strokeColor = color;
                p.lineWidth = 2.4f;
                p.lineCap = LineCap.Round;
                var c = new Vector2(s * 0.5f, s * 0.5f);
                switch (kind)
                {
                    case "play":
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.30f, s * 0.16f));
                        p.LineTo(new Vector2(s * 0.88f, s * 0.50f));
                        p.LineTo(new Vector2(s * 0.30f, s * 0.84f));
                        p.ClosePath();
                        p.Fill();
                        break;

                    case "plus":
                        p.lineWidth = 3.2f;
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.5f, s * 0.14f));
                        p.LineTo(new Vector2(s * 0.5f, s * 0.86f));
                        p.MoveTo(new Vector2(s * 0.14f, s * 0.5f));
                        p.LineTo(new Vector2(s * 0.86f, s * 0.5f));
                        p.Stroke();
                        break;

                    case "restart":
                        // Circular arrow: near-full ring with a gap at the
                        // upper-right, arrowhead pointing into the gap.
                        p.lineWidth = 3.0f;
                        p.BeginPath();
                        p.Arc(c, s * 0.34f, 60, 330);
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.74f, s * 0.06f));
                        p.LineTo(new Vector2(s * 0.82f, s * 0.30f));
                        p.LineTo(new Vector2(s * 0.58f, s * 0.30f));
                        p.Stroke();
                        break;

                    case "worlds":
                        // Two saved-world cards: a compact stack with a small
                        // horizon mark, readable at the menu's 20 px icon size.
                        p.lineWidth = 2.2f;
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.22f, s * 0.12f));
                        p.LineTo(new Vector2(s * 0.86f, s * 0.12f));
                        p.LineTo(new Vector2(s * 0.86f, s * 0.64f));
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.12f, s * 0.30f));
                        p.LineTo(new Vector2(s * 0.76f, s * 0.30f));
                        p.LineTo(new Vector2(s * 0.76f, s * 0.86f));
                        p.LineTo(new Vector2(s * 0.12f, s * 0.86f));
                        p.ClosePath();
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.22f, s * 0.70f));
                        p.LineTo(new Vector2(s * 0.38f, s * 0.52f));
                        p.LineTo(new Vector2(s * 0.51f, s * 0.65f));
                        p.LineTo(new Vector2(s * 0.66f, s * 0.47f));
                        p.Stroke();
                        break;

                    case "gear":
                        p.lineWidth = 3.4f;
                        for (var i = 0; i < 8; i++)
                        {
                            var a = i * Mathf.PI / 4f;
                            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                            p.BeginPath();
                            p.MoveTo(c + dir * (s * 0.26f));
                            p.LineTo(c + dir * (s * 0.44f));
                            p.Stroke();
                        }

                        p.lineWidth = 3.6f;
                        p.BeginPath();
                        p.Arc(c, s * 0.22f, 0, 360);
                        p.Stroke();
                        break;

                    case "person":
                        p.BeginPath();
                        p.Arc(new Vector2(s * 0.5f, s * 0.30f), s * 0.17f, 0, 360);
                        p.Fill();
                        p.BeginPath();
                        p.Arc(new Vector2(s * 0.5f, s * 0.92f), s * 0.32f, 180, 360);
                        p.ClosePath();
                        p.Fill();
                        break;

                    case "exit":
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.52f, s * 0.16f));
                        p.LineTo(new Vector2(s * 0.16f, s * 0.16f));
                        p.LineTo(new Vector2(s * 0.16f, s * 0.84f));
                        p.LineTo(new Vector2(s * 0.52f, s * 0.84f));
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.42f, s * 0.5f));
                        p.LineTo(new Vector2(s * 0.88f, s * 0.5f));
                        p.MoveTo(new Vector2(s * 0.72f, s * 0.34f));
                        p.LineTo(new Vector2(s * 0.88f, s * 0.5f));
                        p.LineTo(new Vector2(s * 0.72f, s * 0.66f));
                        p.Stroke();
                        break;

                    case "bulb":
                        p.BeginPath();
                        p.Arc(new Vector2(s * 0.5f, s * 0.38f), s * 0.22f, 0, 360);
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(s * 0.40f, s * 0.70f));
                        p.LineTo(new Vector2(s * 0.60f, s * 0.70f));
                        p.MoveTo(new Vector2(s * 0.43f, s * 0.82f));
                        p.LineTo(new Vector2(s * 0.57f, s * 0.82f));
                        p.Stroke();
                        break;
                }
            };
            return el;
        }

        // §41.3: tick -> "Day N · HH:MM" using the sim's own day length and
        // clock formatter, so the readout matches in-game time exactly.
        private static string FormatDayTime(int tick)
        {
            var dayLen = HexLive.Simulation.Runtime.EnvironmentSystem.DayLengthTicks;
            var day = HexLive.Simulation.Runtime.EnvironmentSystem.CalendarDay(tick);
            var progress = (tick % dayLen) / (float)dayLen;
            var clock = HexLive.Simulation.Runtime.EnvironmentSystem.FormatClock(progress);
            return $"{Loc.Get("loading.day")} {day}   {clock}";
        }

        private void SetProgress(float overall, string status)
        {
            if (_progressFill != null)
            {
                _progressFill.style.width = Length.Percent(Mathf.Clamp01(overall) * 100f);
            }

            if (_status != null)
            {
                _status.text = status;
            }
        }

        private static float ContentProgress()
        {
            var queue = Wearing.Garments.ContentQueue.Progress;
            var service = ContentAssetService.Instance;
            if (service.DownloadTotalBytes <= 0 || service.DownloadedBytes == 0)
            {
                return queue;
            }

            var bytes = Mathf.Clamp01(
                (float)(service.DownloadedBytes / (double)service.DownloadTotalBytes));
            return Mathf.Min(queue, bytes);
        }

        private static string ContentStatus()
        {
            var service = ContentAssetService.Instance;
            return string.IsNullOrWhiteSpace(service.Status)
                ? Loc.Get(Wearing.Garments.ContentQueue.MessageKey)
                : service.Status;
        }

        private IEnumerator Run()
        {
            // Spec 41.4: menu first — nothing exists until the player picks.
            while (!_menuChosen)
            {
                yield return null;
            }

            _menuBox.style.display = DisplayStyle.None;
            _progressStrip.style.display = DisplayStyle.Flex;

            // §21/§152: visual movement tuning is atomic content too. Apply it
            // before choosing either backend: a remote world still renders its
            // actors locally and must not fall back to stale code constants.
            var hexTuningReady = false;
            var hexTuningApplied = false;
            Config.HexTuning.LoadAtomic(success =>
            {
                hexTuningApplied = success;
                hexTuningReady = true;
            });
            while (!hexTuningReady)
            {
                SetProgress(0.01f, ContentStatus());
                yield return null;
            }
            if (!hexTuningApplied)
            {
                ShowContentFailure(ContentAssetService.Instance.LastError);
                yield break;
            }

            // §Server: watching someone else's world skips this whole block —
            // there is no seed to pick, no save to restore and no offline time
            // to wind, because the world never stopped running.
            if (_connectChosen)
            {
                yield return ConnectToServer();
                yield break;
            }

            // A previous session may have been remote. Any local choice —
            // Continue, Restart or New Game — must switch the backend back to
            // Local before Configure creates it (§41.8).
            if (SessionConfig.Mode == PresentationSimulationMode.Remote)
            {
                SessionConfig.UseServer(null);
            }

            var simDataReady = false;
            var simDataApplied = false;
            Config.ExternalBalanceTuning.LoadAtomic(success =>
            {
                simDataApplied = success;
                simDataReady = true;
            });
            while (!simDataReady)
            {
                SetProgress(0.02f, ContentStatus());
                yield return null;
            }
            if (!simDataApplied)
            {
                ShowContentFailure(ContentAssetService.Instance.LastError);
                yield break;
            }

            Wearing.Garments.WardrobeMeta.Load();

            // Explicit developer override wins over the verified production
            // object, but is never discovered implicitly beside the Player.
            Config.ExternalBalanceTuning.LoadAndApply();

            int seed;
            // §146.2: the mode is part of the world's identity. Continue and
            // Restart take it from the save header; only New game asks the menu.
            var mode = HexLive.Simulation.Bootstrap.GameMode.Feud;
            if (_continueChosen && _save != null)
            {
                seed = _save.seed;
                mode = (HexLive.Simulation.Bootstrap.GameMode)_save.mode;
                _targetTick = _save.tick + SaveGame.OfflineTicks(_save);
                Debug.Log($"[HexLive] Resuming save: seed {seed} ({mode}), tick {_save.tick}" +
                    $" + offline {_targetTick - _save.tick}");
            }
            else
            {
                // §41.8: New Game and Restart allocate a NEW world directory.
                // Restart keeps the current island's seed/mode, but its older
                // progress remains in the catalog and can always be loaded.
                var restartSeed = _restartChosen && _save != null ? _save.seed : (int?)null;
                mode = _restartChosen && _save != null
                    ? (HexLive.Simulation.Bootstrap.GameMode)_save.mode
                    : _newGameMode;
                if (restartSeed.HasValue)
                {
                    seed = restartSeed.Value;
                }
                else if (!SaveGame.TryConsumeNewGameSeed(out seed))
                {
                    seed = System.Environment.TickCount;
                }
                SaveGame.CreateWorld(seed, mode);
                _targetTick = 0;
                Debug.Log(restartSeed.HasValue
                    ? $"[HexLive] Restart same island: seed {seed} ({mode})"
                    : $"[HexLive] New game: seed {seed} ({mode})");
            }

            // This session is local — so Continue offers the local save next
            // time, not the server we happened to watch before it.
            ServerBook.RememberLocalSession();

            SetProgress(0.02f, Loc.Get("loading.world"));
            yield return null;

            // Spec 41.3: curtain the views BEFORE the world exists. Configure()
            // makes the runner IsReady synchronously, so every frame from here
            // to the end of the wind would otherwise let the renderer build the
            // whole scene on a state that the wind is about to invalidate.
            IsReplaying = true;

            // Spec 41.1: bootstrap PAUSED; the loader unpauses after the fade.
            var speed = _continueChosen && _save is { speed: > 0f } ? _save.speed : 1f;

            // §41.3 (r3): ГЕНЕРАЦИЯ МИРА — НА ВОРКЕРЕ. Worldgen — чистая
            // симуляция (тот же довод, что у намотки ниже и у удалённого
            // BuildInitialWorld): на большом острове синхронный Create в
            // главном потоке замораживал редактор на минуты сразу после
            // «Реестр контента готов» — UI, музыка и MCP умирали, хотя это
            // просто честная работа. Здесь главный поток только крутит шторку.
            HexLive.Simulation.Bootstrap.WorldBootstrapDefinition builtDefinition = null;
            HexLive.Simulation.Core.WorldState builtWorld = null;
            var buildSeed = seed;
            var buildMode = mode;
            var buildTask = System.Threading.Tasks.Task.Run(() =>
            {
                builtDefinition = HexLive.Simulation.Bootstrap
                    .PrototypeWorldDefinitionFactory.Create(buildSeed, buildMode);
                builtWorld = new HexLive.Simulation.Bootstrap.WorldStateFactory()
                    .Create(builtDefinition);
            });
            while (!buildTask.IsCompleted)
            {
                SetProgress(0.03f, Loc.Get("loading.world"));
                yield return null;
            }
            if (buildTask.IsFaulted)
            {
                // Молча упасть нельзя (Task проглотит исключение, экран ждал бы
                // вечно). Причина в лог; дальше прежний синхронный путь как
                // последний шанс — он упадёт с той же ошибкой, но на виду.
                Debug.LogError("[HexLive] Worldgen на воркере упал: " +
                    buildTask.Exception?.GetBaseException());
                builtDefinition = HexLive.Simulation.Bootstrap
                    .PrototypeWorldDefinitionFactory.Create(seed, mode);
                builtWorld = null;
            }

            // §41.3: прогрев арта забираем у Configure себе — он поедет рядом с
            // намоткой, а не перед ней. Флаг снимаем сразу: он статический, и
            // остальные вызывающие Configure (dev-сцены, подключение к серверу)
            // обязаны прогреться сами, как раньше.
            SimulationRunnerBehaviour.DeferContentPrewarm = true;
            try
            {
                _runner.Configure(
                    builtDefinition, builtWorld,
                    startPaused: true, initialSpeed: speed);

                // Spec 41.2 v2: apply the saved MODEL onto the freshly built
                // world (static topology comes from the seed, everything mutable
                // from the blob). A corrupt save falls back to a new world.
                if (_continueChosen && _save != null && _runner.Engine is { } restoreEngine)
                {
                    SetProgress(0.05f, Loc.Get("loading.world"));
                    yield return null;

                    if (!SaveGame.TryRestore(restoreEngine.World))
                    {
                        Debug.LogWarning("[HexLive] Save restore failed — starting a new world.");
                        seed = System.Environment.TickCount;
                        _targetTick = 0;
                        SaveGame.CreateWorld(seed, mode);
                        _runner.Configure(
                            HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(seed, mode),
                            startPaused: true, initialSpeed: 1f);
                    }
                    else if (restoreEngine.World.Completed)
                    {
                        _targetTick = restoreEngine.World.Tick;
                    }
                }
            }
            finally
            {
                SimulationRunnerBehaviour.DeferContentPrewarm = false;
            }

            var hasReplay = _runner.Engine is { } configured &&
                !configured.World.Completed &&
                configured.World.Tick < _targetTick;
            yield return null;

            // §41.3: намотка офлайна уходит НА ВОРКЕР, а главный поток в это
            // время тянет арт. Раньше и то и другое стояло в очереди к одному
            // потоку: сначала Configure читал с диска 8.3 МБ префабов и все
            // сэмплы FMOD, и только потом начинался первый тик — а сама намотка
            // шла порциями по ReplayBudgetMsPerFrame, отдавая кадр Unity.
            //
            // Почему это безопасно, хотя мир один на двоих:
            //  • симуляция не знает про UnityEngine вовсе (та же сборка
            //    собирается headless под .NET 9), так что с воркера ей нечего
            //    трогать из запретного, а буферы патфайндера уже [ThreadStatic];
            //  • на время намотки мир принадлежит воркеру ИСКЛЮЧИТЕЛЬНО:
            //    CreateSnapshot отдаёт null, пока IsReplaying (это перекрывает
            //    всех потребителей разом), рендер и звук выходят первой строкой
            //    Update, а LocalEngineBackend.Tick на паузе не делает ничего и
            //    не сливает события;
            //  • главный поток читает у воркера ровно один int — номер тика.
            System.Threading.Tasks.Task windTask = null;
            System.Exception windError = null;
            HexLive.Simulation.Runtime.SimulationEngine windEngine = null;
            HexLive.Simulation.Runtime.FlightRecorder windRecorder = null;
            var windTrace = HexLive.Simulation.Runtime.SimTrace.Enabled;
            var windStart = 0;
            var windTarget = _targetTick;
            var windClock = System.Diagnostics.Stopwatch.StartNew();
            var windStepMs = 0.0;

            if (hasReplay && _runner.Engine is { } engine)
            {
                windEngine = engine;
                windStart = engine.World.Tick;
                _windTick = windStart;
                _windAbort = false;
                _timeReadout.style.display = DisplayStyle.Flex;

                // §30.14/30.17: намотку никто не разбирает — самописец и трасса
                // на ней только жгут время. Замер (Mono, edit mode, сид 12345):
                // 6.51 мс/тик без самописца против 12.07 с ним.
                windRecorder = engine.World.FlightRecorder;
                engine.World.FlightRecorder = null;
                HexLive.Simulation.Runtime.SimTrace.Enabled = false;

                windTask = System.Threading.Tasks.Task.Run(() =>
                {
                    var stepping = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        while (!_windAbort &&
                               !engine.World.Completed &&
                               !IsColonyExtinct(engine.World) &&
                               engine.World.Tick < windTarget)
                        {
                            engine.Step();
                            _windTick = engine.World.Tick;
                        }
                    }
                    catch (System.Exception e)
                    {
                        // Молча упасть здесь нельзя: Task проглотит исключение,
                        // а экран останется ждать вечно. Причина уезжает на
                        // главный поток и печатается там.
                        windError = e;
                    }

                    windStepMs = stepping.Elapsed.TotalMilliseconds;
                });
            }

            // Пока мир мотается — тянем арт. Вызов блокирующий (это чтения с
            // диска), но блокирует он теперь только КАДР, а не намотку.
            _runner.WarmContent();

            try
            {
                // Ждём намотку. Бандлы в это время едут сами — их дождёмся ниже,
                // но только ПОСЛЕ того, как домотанный мир скажет, что ему
                // вообще нужно.
                while (windTask is { IsCompleted: false })
                {
                    var tick = _windTick;
                    var done = (tick - windStart) /
                        (float)Mathf.Max(1, windTarget - windStart);
                    _timeReadout.text = FormatDayTime(tick);
                    SetProgress(0.05f + done * 0.5f, Loc.Get("loading.time"));
                    yield return null;
                }
            }
            finally
            {
                // Корутину убивает domain reload — тогда воркер обязан
                // остановиться сам, а самописец вернуться на место, иначе он
                // останется выключенным на всю игровую сессию.
                _windAbort = true;
                windTask?.Wait(2000);
                if (windEngine != null)
                {
                    windEngine.World.FlightRecorder = windRecorder;
                }

                HexLive.Simulation.Runtime.SimTrace.Enabled = windTrace;
                _timeReadout.style.display = DisplayStyle.None;
            }

            if (windEngine != null)
            {
                if (windError != null)
                {
                    UnityEngine.Debug.LogError(
                        "[HexLive] Offline wind threw and stopped at tick " +
                        $"{windEngine.World.Tick}: {windError}");
                }

                if (IsColonyExtinct(windEngine.World) && windEngine.World.Tick < windTarget)
                {
                    UnityEngine.Debug.Log(
                        "[HexLive] Offline wind stopped early: the colony died out at tick " +
                        $"{windEngine.World.Tick} (target was {windTarget}).");
                }

                // Две РАЗНЫЕ величины, и путать их нельзя: скорость намотки
                // считается по времени воркера, а не по всему окну загрузки —
                // иначе долгая подгрузка бандлов «замедлит» намотку в отчёте.
                // Отношение одного к другому и говорит, кто кого ждал.
                var wound = windEngine.World.Tick - windStart;
                var windowMs = System.Math.Max(1L, windClock.ElapsedMilliseconds);
                var workerMs = System.Math.Max(1.0, windStepMs);
                UnityEngine.Debug.Log(
                    $"[HexLive] Replayed {wound} ticks to {windEngine.World.Tick} on a worker in {workerMs:0} ms — " +
                    $"{wound * 1000.0 / workerMs:0} tick/s, {workerMs / System.Math.Max(1, wound):0.00} ms/tick; " +
                    $"the load window was {windowMs} ms, the wind {workerMs * 100.0 / windowMs:0}% of it " +
                    $"(the rest is content: bundles, prefabs, FMOD)");
            }

            // Мир домотан и снова наш — теперь и только теперь известно, ЧТО в
            // нём есть. За игровые сутки колонистки переодеваются, умирают в
            // другом, теряют конечности и получают протезы, а в колонию
            // приходят новые девушки со своими причёсками. Заявка уходит на
            // разницу; уже приехавшее молчит.
            if (hasReplay)
            {
                _runner.WarmForWorld();
            }

            // Шторка ждёт бандлы: одежду по итоговому миру и иконки.
            while (hasReplay && !Wearing.Garments.ContentQueue.IsIdle)
            {
                SetProgress(
                    0.55f + 0.1f * ContentProgress(), ContentStatus());
                yield return null;
            }

            // The world now stands at its final tick — drop the curtain so the
            // scene is built exactly once, on the state the player will see.
            IsReplaying = false;

            // Spec 41.1 phase 3: the renderer builds terrain/actors/wardrobe
            // from the (paused) snapshot over the next frames.
            SetProgress(hasReplay ? 0.7f : 0.3f, Loc.Get("loading.island"));
            for (var i = 0; i < 6; i++)
            {
                yield return null;
            }

            // Spec 41.1 phase 4: pre-warm the character UI for every girl —
            // panel tree, portrait camera + RenderTexture, shader variants all
            // build behind the overlay, so the first real click is instant.
            var npcs = ListNpcIds();
            for (var i = 0; i < npcs.Count; i++)
            {
                SetProgress(Mathf.Lerp(0.75f, 0.95f, (i + 1) / (float)npcs.Count),
                    Loc.Get("loading.warmup"));
                NpcSelection.Select(npcs[i].id);
                yield return null;
                yield return null;
            }

            // ⭐ Тела должны быть ПОСТРОЕНЫ до того, как поднимется шторка.
            // С переходом на атомарный кэш одежда и причёска приезжают не
            // мгновенно, и выбор «первой» стал попадать в момент, когда
            // выбирать ещё некого: раньше он срабатывал по счастливой
            // случайности. Игрок не должен видеть, как это достраивается.
            yield return WaitForActors(npcs);

            // Выбор ДО занавеса, а не после: когда шторка уходит, персонаж уже
            // выбран и панель открыта. Clear первым — чтобы SelectionChanged
            // сработал даже если прогрев оставил её выбранной.
            NpcSelection.Clear();
            var openingTarget = FindOpeningTarget(npcs);
            ReportOpeningTarget(openingTarget, npcs);
            NpcSelection.Select(openingTarget);

            // Let the character bar finish one layout pass, then place the
            // camera synchronously. The world is paused, so its ordinary
            // SmoothDamp path cannot reach the selected NPC before fade-out.
            yield return null;
            // §131: стартовый кадр — слежение включено, камера у головы,
            // спереди-сбоку и низко (константы OpeningShot* контроллера).
            FindFirstObjectByType<RtsCameraController>()?.SnapToSelectedTarget(openingShot: true);

            // #339: прогрев шейдерных вариантов за занавесом — см. хелпер.
            yield return WarmShaderVariantsSweep();

            SetProgress(1f, Loc.Get("loading.done"));
            yield return null;

            // Fade the curtain, then start life.
            var t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.unscaledDeltaTime;
                if (_root != null)
                {
                    _root.style.opacity = 1f - Mathf.Clamp01(t / FadeSeconds);
                }

                yield return null;
            }

            if (!_runner.IsCompleted)
            {
                // Explicit hand-off invariant: model clock and Unity's visual
                // clock both leave loading alive. Escape -> Continue used to
                // repair a stale zero here accidentally.
                Time.timeScale = 1f;
                _runner.Resume();
            }

            _runner.AutosaveEnabled = true;
            Destroy(gameObject);
        }

        /// <summary>
        /// Connect, wait for the world, then hand over to the same presentation
        /// warm-up the local path uses.
        /// <para>
        /// Two things this must NOT do, both of which the local path does: wind
        /// offline time (the server never stopped, so there is nothing to catch
        /// up) and unpause at the end (the clock is not ours). It also has to
        /// stay honest while waiting — a connect that is failing should say so,
        /// not sit on a progress bar forever.
        /// </para>
        /// </summary>
        private IEnumerator ConnectToServer()
        {
            IsReplaying = true;
            SetProgress(0.05f, Loc.Get("loading.connecting"));

            // Build the runner in remote mode. Configure() is what picks the
            // backend, reading SessionConfig, which the menu just set.
            _runner.Configure(
                HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(0),
                startPaused: true, initialSpeed: 1f);

            // Вехи удалённой загрузки печатаются В ЛОГ: занавес, который не
            // ушёл, снаружи выглядит одинаково на любой из пяти фаз, и без
            // этих строк следующая догадка снова будет ставкой (§109.16).
            var waited = 0f;
            var nextConnectReport = StallReportSeconds;
            while (!_runner.IsReady)
            {
                var link = _runner.Link;
                if (link.State == LinkState.Failed)
                {
                    // Unrecoverable — a different build, or an address that will
                    // never resolve. Say why and go back to the menu rather than
                    // spinning on a bar that will never fill.
                    ShowConnectFailure(link.Message);
                    yield break;
                }

                waited += Time.unscaledDeltaTime;
                SetProgress(Mathf.Min(0.35f, 0.05f + waited * 0.05f),
                    link.State == LinkState.Reconnecting
                        ? Loc.Get("loading.reconnecting")
                        : Loc.Get("loading.connecting"));
                if (waited >= nextConnectReport)
                {
                    nextConnectReport += StallReportSeconds;
                    Debug.LogWarning($"[Loading] подключение {waited:F0} с: " +
                        $"состояние связи {link.State} {link.Message}");
                }

                yield return null;
            }

            Debug.Log($"[Loading] сервер подключён за {waited:F1} с, тик {_runner.CurrentTick}");

            // The clock needs a couple of frames buffered before it will present
            // anything; showing the island mid-fill would stutter on entry.
            SetProgress(0.45f, Loc.Get("loading.syncing"));
            var settle = 0f;
            while (settle < 1.0f && _runner.CurrentTick <= 0)
            {
                settle += Time.unscaledDeltaTime;
                yield return null;
            }

            IsReplaying = false;

            // §67/§41.4: блокирующий прогрев (FMOD-сэмплы, раны/кровь, мобы) —
            // как на локальном пути. Его здесь не было ВОВСЕ: FmodSfx.Prewarm
            // зовётся только из WarmContent, и на серверной сессии _ready
            // оставался false — вся озвучка (SFX, голоса) молчала при живой
            // музыке, которая греется отдельной веткой ещё из меню. Замер
            // 2026-08-28: _ready=false, soundGroups=0 на подключённом клиенте.
            _runner.WarmContent();

            // §41/§152: a remote client owns no WorldState, so the first live
            // snapshot is the only authoritative list of objects which belong
            // to this opening world. Queue their owner bundles now; every main
            // load reads icon from the same handle before the curtain leaves.
            Wearing.ScenePrewarm.ForSnapshot(_runner.CreateSnapshot());

            SetProgress(0.6f, Loc.Get("loading.island"));
            for (var i = 0; i < 6; i++)
            {
                yield return null;
            }

            var npcs = ListNpcIds();
            Debug.Log($"[Loading] мир сервера показан, греем панели: {npcs.Count} колонисток");
            for (var i = 0; i < npcs.Count; i++)
            {
                SetProgress(Mathf.Lerp(0.75f, 0.95f, (i + 1) / (float)npcs.Count),
                    Loc.Get("loading.warmup"));
                NpcSelection.Select(npcs[i].id);
                yield return null;
                yield return null;
            }

            yield return WaitForActors(npcs, liveWorld: true);
            Debug.Log("[Loading] занавес уходит");

            NpcSelection.Clear();
            var openingTarget = FindOpeningTarget(npcs);
            ReportOpeningTarget(openingTarget, npcs);
            NpcSelection.Select(openingTarget);
            yield return null;
            // §131: стартовый кадр — слежение включено, камера у головы,
            // спереди-сбоку и низко (константы OpeningShot* контроллера).
            FindFirstObjectByType<RtsCameraController>()?.SnapToSelectedTarget(openingShot: true);

            // #339: холодный серверный вход дёргался, пока URP компилировал
            // варианты шейдеров на первом показе каждого материала. Пять
            // скрытых кадров с разных ракурсов оплачивают эту цену за
            // занавесом, а не первыми секундами игры.
            yield return WarmShaderVariantsSweep();

            SetProgress(1f, Loc.Get("loading.done"));
            yield return null;

            var t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.unscaledDeltaTime;
                if (_root != null)
                {
                    _root.style.opacity = 1f - Mathf.Clamp01(t / FadeSeconds);
                }

                yield return null;
            }

            // The server owns simulation pause, but the local presentation
            // still owns Unity's Animator clock.
            Time.timeScale = 1f;

            // No Resume(), no autosave: the server owns both.
            Destroy(gameObject);
        }

        private void ShowConnectFailure(string message)
        {
            IsReplaying = false;
            SetProgress(0f, string.IsNullOrEmpty(message)
                ? Loc.Get("loading.connect.failed")
                : Loc.Get("loading.connect.failed") + "\n" + message);

            // Back to the menu: the player can fix the address and try again, or
            // just play locally.
            //
            // The address stays in the book (it is probably a typo away from
            // right), but this session no longer counts as remote — otherwise
            // Continue would keep marching back into a server that is gone and
            // there would be no way out but the connect list.
            _menuChosen = false;
            _connectChosen = false;
            ServerBook.RememberLocalSession();
            SessionConfig.UseServer(null);
            if (_menuBox != null)
            {
                _menuBox.style.display = DisplayStyle.Flex;
            }

            StartCoroutine(Run());
        }

        private void ShowContentFailure(string message)
        {
            IsReplaying = false;
            SetProgress(0f, Loc.Get("loading.content.failed") +
                (string.IsNullOrWhiteSpace(message) ? string.Empty : "\n" + message));
            _menuChosen = false;
            if (_menuBox != null)
            {
                _menuBox.style.display = DisplayStyle.Flex;
            }
            StartCoroutine(Run());
        }

        // #339: прогрев шейдерных вариантов. URP компилирует вариант при
        // ПЕРВОМ попадании материала в кадр; на холодном кэше (свежий билд,
        // первый вход) каждая новая порция мира стоила видимого рывка. Пока
        // занавес ещё непрозрачен, камера скрыто смотрит на стартовую сцену с
        // четырёх сторон и одним общим планом сверху (общий план дополнительно
        // прогревает дальний профиль: туман, импосторы, LOD-панораму) — рывки
        // компиляции случаются за занавесом. Контроллер камеры на время свипа
        // выключен (он пишет transform каждый кадр), исходная поза — та, что
        // выставил SnapToSelectedTarget, и она же возвращается в конце.
        private IEnumerator WarmShaderVariantsSweep()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                yield break;
            }

            var controller = FindFirstObjectByType<RtsCameraController>();
            var cameraTransform = cam.transform;
            var savedPosition = cameraTransform.position;
            var savedRotation = cameraTransform.rotation;
            var focus = savedPosition + savedRotation * Vector3.forward * 8f;
            if (controller != null)
            {
                controller.enabled = false;
            }

            for (var side = 0; side < 4; side++)
            {
                var around = Quaternion.Euler(0f, side * 90f, 0f);
                cameraTransform.position = focus + around * new Vector3(0f, 3f, -8f);
                cameraTransform.rotation =
                    Quaternion.LookRotation(focus + Vector3.up - cameraTransform.position);
                yield return null;
            }

            cameraTransform.position = focus + new Vector3(0f, 70f, -40f);
            cameraTransform.rotation = Quaternion.LookRotation(focus - cameraTransform.position);
            yield return null;

            cameraTransform.SetPositionAndRotation(savedPosition, savedRotation);
            if (controller != null)
            {
                controller.enabled = true;
            }
        }

        // Ждём, пока мир будет ГОТОВ ПОКАЗАТЬСЯ: тела построены и очередь
        // контента пуста. Без таймаута — и это не смелость, а следствие
        // устройства: каждая начатая задача обязана завершиться, потому что
        // атомарный загрузчик завершает операцию всегда, и успехом, и провалом.
        // Задача, начатая без завершения, — ошибка в загрузчике, и лечить её
        // страховкой на экране значит прятать её от себя.
        //
        // Подпись при этом человеческая: игрок видит «шьём одежду», а не
        // проценты в пустоту.
        private IEnumerator WaitForActors(
            System.Collections.Generic.List<(int id, string name)> npcs,
            bool liveWorld = false)
        {
            if (npcs.Count == 0)
            {
                yield break;
            }

            var renderer = FindFirstObjectByType<Rendering.HexWorldRenderer>();
            if (renderer == null)
            {
                yield break;
            }

            var ids = new System.Collections.Generic.List<int>(npcs.Count);
            foreach (var npc in npcs)
            {
                ids.Add(npc.id);
            }

            var waited = 0f;
            var nextReport = StallReportSeconds;
            var nextBuildPass = 1f;

            // Queue first is load-bearing short-circuiting. ActorsReady applies
            // the now-cached wardrobe to a paused actor; calling it while an
            // atomic-content prewarm is still running could fall through to the
            // synchronous GetVisuals path from inside this coroutine — the
            // exact WaitForCompletion deadlock fixed in 105199ce.
            while (!Wearing.Garments.ContentQueue.IsIdle || !renderer.ActorsReady(ids))
            {
                // ⭐ Под шторкой сим на паузе (тик 0), а синк рендерера идёт
                // только на НОВЫЙ тик: первый проход заказал тела асинхронно,
                // и без пинка второго прохода не наступало никогда — все
                // девушки «вида нет» до перезапуска. Раз в секунду просим
                // рендерер пройтись ещё раз по тому же тику.
                if (waited >= nextBuildPass)
                {
                    nextBuildPass = waited + 1f;
                    renderer.RequestActorBuildPass();
                }

                // Прогресс НАСТОЯЩИЙ: сделано из всего, что заказано. Полоска
                // на этом участке живёт в верхней четверти — терраген и прогрев
                // панелей уже позади.
                SetProgress(Mathf.Lerp(0.75f, 0.99f, ContentProgress()), ContentStatus());
                yield return null;

                waited += Time.unscaledDeltaTime;
                if (liveWorld)
                {
                    // Мир сервера не ждёт загрузчика: пока греются панели,
                    // колонистка может умереть — её вид уезжает в трупный
                    // реестр, и «готова ли она» уже никогда не станет правдой.
                    renderer.KeepLiveNpcIds(ids);
                    if (ids.Count == 0)
                    {
                        yield break;
                    }
                }

                if (waited < nextReport)
                {
                    continue;
                }

                nextReport += StallReportSeconds;
                Debug.LogWarning($"[Loading] жду {waited:F0} с: " +
                    $"очередь контента {Wearing.Garments.ContentQueue.Remaining} шт " +
                    $"({Wearing.Garments.ContentQueue.MessageKey}); " +
                    $"тела — {Describe(renderer.DescribeActorsNotReady(ids))}");

                // A live world may change while content is arriving, therefore
                // KeepLiveNpcIds above prunes actors which really disappeared.
                // It is never valid to reveal the world while ContentQueue is
                // still non-empty: that made the progress curtain lie and let
                // half-stitched actors appear. A stuck queue is a loader bug
                // and must remain visible (and fail the smoke), not be hidden
                // by a time-based escape hatch.
            }
        }

        private static string Describe(string blockers) =>
            string.IsNullOrEmpty(blockers) ? "готовы" : blockers;

        /// <summary>Spec 41.3: единственный стоп-кран снятого потолка офлайна —
        /// колония вымерла. `World.Completed` этого не значит (это спуск плота,
        /// §40.15, и он выключен), а мёртвая девушка не исчезает, а переезжает в
        /// `Entities.Corpses` — то есть живые это ровно `Entities.Npcs`.
        /// Чужак-враг (§70) в счёт не идёт: с ним одним мир мотался бы дальше
        /// уже без колонии. Вопрос задан положительно (AreAllies), как требует
        /// §72: «жива ли хоть одна СВОЯ», а не «не враг ли».</summary>
        private static bool IsColonyExtinct(HexLive.Simulation.Core.WorldState world)
        {
            foreach (var pair in world.Entities.Npcs)
            {
                if (HexLive.Simulation.Runtime.FactionRelations.AreAllies(
                        pair.Value.Faction, HexLive.Simulation.Agents.Faction.Colony))
                {
                    return false;
                }
            }

            return true;
        }

        private System.Collections.Generic.List<(int id, string name)> ListNpcIds()
        {
            var result = new System.Collections.Generic.List<(int, string)>();
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null)
            {
                return result;
            }

            foreach (var npc in snapshot.Npcs)
            {
                // §72: the opening camera frames one of OURS. The outsider is
                // the highest id, so an ungated roster could open the run
                // orbiting the man hunting them.
                //
                // §149: «наша» по ПРАВАМ идёт первым слагаемым. Вне режима
                // соло-лагерей (§146.3) чужой лагерь враждебен `Colony`, так
                // что выданная сервером девушка из соседнего лагеря отсеялась
                // бы этим самым гейтом — её бы не прогрели, не дождались тела и
                // не выбрали на старте.
                if (npc.IsHostileToColony &&
                    !(_runner != null && _runner.CanControlNpc(npc.Id)))
                {
                    continue;
                }

                result.Add((npc.Id.Value, npc.DisplayName));
            }

            return result;
        }

        // §74: the opening shot used to hunt for "Jana" by name. There is no
        // Jana any more — names are rolled per seed — so it frames the FIRST
        // colonist on the roster instead. The list is already gated to our own
        // faction (§72) and comes in snapshot order, i.e. ascending id, so this
        // is the girl the world was built around on every seed.
        //
        // ⭐ §149: но «первая по списку» — это НЕ «моя». На сервере выданная
        // девушка может быть из любого лагеря (§149.2), а список идёт по
        // возрастанию id, так что первой оказывалась чужая колонистка. Дальше
        // всё сходилось одно к одному: камера уезжала к ней, вырез §150 стоял
        // вокруг СВОЕЙ (глаза выреза — только управляемые), и игрок получал
        // тёмное пятно «где-то в центре карты»; а `PruneInvisibleSelection`
        // тем же кадром снимал выделение с невидимой чужой — панель открывалась
        // на ком угодно, только не на подопечной, и тумблер управления был
        // погашен. Поэтому стартовый кадр — на той, КЕМ ИГРОК УПРАВЛЯЕТ.
        // Локально ответ прежний: там управляемые и есть `Faction.Colony`.
        private int FindOpeningTarget(
            System.Collections.Generic.List<(int id, string name)> npcs)
        {
            for (var i = 0; i < npcs.Count; i++)
            {
                if (_runner != null && _runner.CanControlNpc(
                        new HexLive.Simulation.Common.EntityId(npcs[i].id)))
                {
                    return npcs[i].id;
                }
            }

            return npcs.Count > 0 ? npcs[0].id : -1;
        }

        /// <summary>§109.16: одна строка, по которой видно, КОГО выбрал старт и
        /// почему. Пустой ростер, чужая девушка и мёртвый тумблер снаружи
        /// выглядят одинаково — тёмный экран без выделения.</summary>
        private void ReportOpeningTarget(
            int targetId, System.Collections.Generic.List<(int id, string name)> npcs)
        {
            var roster = new System.Text.StringBuilder();
            for (var i = 0; i < npcs.Count; i++)
            {
                var owned = _runner != null && _runner.CanControlNpc(
                    new HexLive.Simulation.Common.EntityId(npcs[i].id));
                if (roster.Length > 0) roster.Append(", ");
                roster.Append(npcs[i].id).Append(owned ? "+" : "-");
            }

            var targetOwned = targetId >= 0 && _runner != null &&
                _runner.CanControlNpc(new HexLive.Simulation.Common.EntityId(targetId));
            Debug.Log($"[Loading] стартовый выбор: npc={targetId} " +
                $"{(targetOwned ? "СВОЯ" : "ЧУЖАЯ/нет прав")}; " +
                $"приказы {(_runner != null && _runner.SupportsNpcCommands ? "разрешены" : "запрещены")}; " +
                $"ростер [{roster}]");
        }
    }
}
