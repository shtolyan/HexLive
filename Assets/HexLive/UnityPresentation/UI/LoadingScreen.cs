using System.Collections;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

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

        // §Server: chosen "watch a server" instead of building a world here.
        // The addresses themselves live in ServerBook, which also remembers
        // which kind of session was last played so Continue can follow it.
        private bool _connectChosen;
        private VisualElement _connectBox;
        private TextField _serverField;
        private TextField _tokenField;

        // §146: which scenario "New game" starts. The row expands into the two
        // mode rows (the Connect-row pattern); the last pick is remembered so
        // the next new game defaults to it. Continue/Restart ignore this and
        // take the mode from the save header.
        private HexLive.Simulation.Bootstrap.GameMode _newGameMode;
        private VisualElement _newGameBox;
        private const string NewGameModePref = "HexLive.NewGameMode";

        private VisualElement _root;
        private VisualElement _menuBox;
        private VisualElement _progressFill;
        private VisualElement _progressStrip;
        private Label _status;
        // §41.3: big day/clock readout shown only while time winds forward —
        // the player watches days roll by, not raw ticks.
        private Label _timeReadout;

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
            // Корутина умирает молча (domain reload в игре), а воркер намотки —
            // нет: он крутил бы мёртвый мир в фоне. Флаг он проверяет каждый
            // тик, так что остановка занимает один шаг.
            _windAbort = true;
            // Safety: if the coroutine died mid-wind (an in-play domain reload
            // kills coroutines silently), don't leave the renderer muted.
            IsReplaying = false;
        }

        public void Begin(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
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

            // §146: "New game" opens the mode choice instead of starting
            // immediately — two worlds now live behind this row.
            card.Add(MakeMenuRow("plus", Loc.Get("menu.newgame"),
                primary: false, enabled: true, () => ToggleNewGameBox()));
            _newGameBox = BuildNewGameBox();
            card.Add(_newGameBox);

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
            _menuChosen = true;
        }

        // §146: the two scenarios behind "New game". Rows, not a dropdown —
        // the whole menu is rows, and two options do not earn a widget.
        private VisualElement BuildNewGameBox()
        {
            var box = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None,
                    paddingLeft = 24, paddingRight = 12, paddingBottom = 4
                }
            };

            var remembered = PlayerPrefs.GetInt(NewGameModePref, 0);
            _newGameMode = remembered == (int)HexLive.Simulation.Bootstrap.GameMode.BigIsland
                ? HexLive.Simulation.Bootstrap.GameMode.BigIsland
                : HexLive.Simulation.Bootstrap.GameMode.Feud;

            AddNewGameModeRow(box, "menu.newgame.mode.feud",
                HexLive.Simulation.Bootstrap.GameMode.Feud);
            AddNewGameModeRow(box, "menu.newgame.mode.bigisland",
                HexLive.Simulation.Bootstrap.GameMode.BigIsland);
            return box;
        }

        private void AddNewGameModeRow(
            VisualElement box, string term, HexLive.Simulation.Bootstrap.GameMode mode)
        {
            var row = MakeMenuRow("play", Loc.Get(term), primary: false, enabled: true, () =>
            {
                _newGameMode = mode;
                PlayerPrefs.SetInt(NewGameModePref, (int)mode);
                PlayerPrefs.Save();
                _continueChosen = false;
                _menuChosen = true;
            });
            row.style.height = 36;
            box.Add(row);
        }

        private void ToggleNewGameBox()
        {
            if (_newGameBox == null)
            {
                return;
            }

            var opening = _newGameBox.style.display == DisplayStyle.None;
            _newGameBox.style.display = opening ? DisplayStyle.Flex : DisplayStyle.None;
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

        private IEnumerator Run()
        {
            // Spec 41.4: menu first — nothing exists until the player picks.
            while (!_menuChosen)
            {
                yield return null;
            }

            _menuBox.style.display = DisplayStyle.None;
            _progressStrip.style.display = DisplayStyle.Flex;

            // §Server: watching someone else's world skips this whole block —
            // there is no seed to pick, no save to restore and no offline time
            // to wind, because the world never stopped running.
            if (_connectChosen)
            {
                yield return ConnectToServer();
                yield break;
            }

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
                // Spec 41.4: new game wipes the save and rolls a fresh world.
                // Restart keeps the CURRENT island — the save's seed rebuilds
                // the same topology from day 1. Dev one-shot override lets a
                // known island seed be restarted cleanly without keeping the
                // old mutable save.
                var restartSeed = _restartChosen && _save != null ? _save.seed : (int?)null;
                mode = _restartChosen && _save != null
                    ? (HexLive.Simulation.Bootstrap.GameMode)_save.mode
                    : _newGameMode;
                SaveGame.Delete();
                if (restartSeed.HasValue)
                {
                    seed = restartSeed.Value;
                }
                else if (!SaveGame.TryConsumeNewGameSeed(out seed))
                {
                    seed = System.Environment.TickCount;
                }
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

            // §41.3: прогрев арта забираем у Configure себе — он поедет рядом с
            // намоткой, а не перед ней. Флаг снимаем сразу: он статический, и
            // остальные вызывающие Configure (dev-сцены, подключение к серверу)
            // обязаны прогреться сами, как раньше.
            SimulationRunnerBehaviour.DeferContentPrewarm = true;
            try
            {
                _runner.Configure(
                    HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(seed, mode),
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
                        SaveGame.Delete();
                        seed = System.Environment.TickCount;
                        _targetTick = 0;
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
                    0.55f + 0.1f * Wearing.Garments.ContentQueue.Progress,
                    Loc.Get(Wearing.Garments.ContentQueue.MessageKey));
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
            // С переходом на Addressables одежда и причёска приезжают не
            // мгновенно, и выбор «первой» стал попадать в момент, когда
            // выбирать ещё некого: раньше он срабатывал по счастливой
            // случайности. Игрок не должен видеть, как это достраивается.
            yield return WaitForActors(npcs);

            // Выбор ДО занавеса, а не после: когда шторка уходит, персонаж уже
            // выбран и панель открыта. Clear первым — чтобы SelectionChanged
            // сработал даже если прогрев оставил её выбранной.
            NpcSelection.Clear();
            NpcSelection.Select(FindOpeningTarget(npcs));

            // Let the character bar finish one layout pass, then place the
            // camera synchronously. The world is paused, so its ordinary
            // SmoothDamp path cannot reach the selected NPC before fade-out.
            yield return null;
            // §131: стартовый кадр — слежение включено, камера у головы,
            // спереди-сбоку и низко (константы OpeningShot* контроллера).
            FindFirstObjectByType<RtsCameraController>()?.SnapToSelectedTarget(openingShot: true);

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
            NpcSelection.Select(FindOpeningTarget(npcs));
            yield return null;
            // §131: стартовый кадр — слежение включено, камера у головы,
            // спереди-сбоку и низко (константы OpeningShot* контроллера).
            FindFirstObjectByType<RtsCameraController>()?.SnapToSelectedTarget(openingShot: true);

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

        // Ждём, пока мир будет ГОТОВ ПОКАЗАТЬСЯ: тела построены и очередь
        // контента пуста. Без таймаута — и это не смелость, а следствие
        // устройства: каждая начатая задача обязана завершиться, потому что
        // Addressables завершает операцию всегда, и успехом, и провалом.
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

            // Queue first is load-bearing short-circuiting. ActorsReady applies
            // the now-cached wardrobe to a paused actor; calling it while an
            // Addressables prewarm is still running could fall through to the
            // synchronous GetVisuals path from inside this coroutine — the
            // exact WaitForCompletion deadlock fixed in 105199ce.
            while (!Wearing.Garments.ContentQueue.IsIdle || !renderer.ActorsReady(ids))
            {
                // Прогресс НАСТОЯЩИЙ: сделано из всего, что заказано. Полоска
                // на этом участке живёт в верхней четверти — терраген и прогрев
                // панелей уже позади.
                SetProgress(Mathf.Lerp(0.75f, 0.99f, Wearing.Garments.ContentQueue.Progress),
                    Loc.Get(Wearing.Garments.ContentQueue.MessageKey));
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

                // На ЖИВОМ мире ждать бесконечно нельзя, и это не отказ от
                // правила «каждая начатая задача обязана завершиться»: то
                // правило про Addressables, а здесь ждут ещё и колонистку,
                // которую мир меняет прямо во время ожидания. Занавес, который
                // пережил загрузку, — игра, в которую нельзя играть; недошитая
                // причёска — кадр, который догонит через секунду.
                if (liveWorld && waited >= LiveWorldGiveUpSeconds)
                {
                    Debug.LogError($"[Loading] занавес поднят силой после {waited:F0} с " +
                        "ожидания на живом мире — см. предыдущую строку, там имя " +
                        "виноватого.");
                    yield break;
                }
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
                if (npc.IsHostileToColony)
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
        private static int FindOpeningTarget(
            System.Collections.Generic.List<(int id, string name)> npcs)
        {
            return npcs.Count > 0 ? npcs[0].id : -1;
        }
    }
}
