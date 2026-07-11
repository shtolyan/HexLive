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

        private const float ReplayBudgetMsPerFrame = 10f;
        private const float FadeSeconds = 0.7f;
        private const string JanaName = "Jana";

        private SimulationRunnerBehaviour _runner;
        private int _targetTick;
        private SaveGameData _save;
        private bool _menuChosen;
        private bool _continueChosen;

        private VisualElement _root;
        private VisualElement _menuBox;
        private VisualElement _progressFill;
        private VisualElement _progressStrip;
        private Label _status;

        private void OnEnable() => IsActive = true;

        private void OnDestroy() => IsActive = false;

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

            // The big game title floats over the art, up high.
            var gameTitle = new Label("HexLive")
            {
                style =
                {
                    position = Position.Absolute,
                    left = 0, right = 0,
                    top = Length.Percent(14),
                    fontSize = 58,
                    color = new Color(0.97f, 0.94f, 0.86f, 0.97f),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    unityTextAlign = TextAnchor.MiddleCenter
                }
            };
            _root.Add(gameTitle);

            // Spec 41.4: the main menu — a compact dark card docked to the
            // left, same design language as the in-game Escape menu. The world
            // doesn't exist yet; buttons decide which world to build.
            _menuBox = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    left = 48, top = 0, bottom = 0,
                    justifyContent = Justify.Center,
                    alignItems = Align.FlexStart
                }
            };

            var card = new VisualElement
            {
                style =
                {
                    width = 300,
                    backgroundColor = new Color(0.075f, 0.094f, 0.110f, 0.98f),
                    borderTopLeftRadius = 16, borderTopRightRadius = 16,
                    borderBottomLeftRadius = 16, borderBottomRightRadius = 16,
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopWidth = 1, borderBottomWidth = 1,
                    borderLeftColor = new Color(1f, 1f, 1f, 0.12f),
                    borderRightColor = new Color(1f, 1f, 1f, 0.12f),
                    borderTopColor = new Color(1f, 1f, 1f, 0.12f),
                    borderBottomColor = new Color(1f, 1f, 1f, 0.12f),
                    paddingLeft = 24, paddingRight = 24,
                    paddingTop = 22, paddingBottom = 16
                }
            };

            var caption = new Label(Loc.Get("menu.title"))
            {
                style =
                {
                    fontSize = 22,
                    color = new Color(0.906f, 0.925f, 0.937f),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    marginBottom = 18
                }
            };
            card.Add(caption);

            // Continue is always present; without a save it sits disabled
            // (greyed out, not clickable) so the menu shape never changes.
            var continueButton = MakeMenuButton(Loc.Get("menu.continue"), primary: _save != null, () =>
            {
                _continueChosen = true;
                _menuChosen = true;
            });
            if (_save == null)
            {
                continueButton.SetEnabled(false);
                continueButton.style.backgroundColor = new Color(0.10f, 0.12f, 0.14f, 0.9f);
                continueButton.style.color = new Color(0.40f, 0.447f, 0.478f);
            }

            card.Add(continueButton);

            card.Add(MakeMenuButton(Loc.Get("menu.newgame"), primary: _save == null, () =>
            {
                _continueChosen = false;
                _menuChosen = true;
            }));

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

            var title = new Label("HexLive")
            {
                style =
                {
                    fontSize = 30,
                    color = new Color(0.95f, 0.92f, 0.85f, 1f),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginBottom = 6
                }
            };
            strip.Add(title);

            _status = new Label("...")
            {
                style =
                {
                    fontSize = 14,
                    color = new Color(0.8f, 0.8f, 0.78f, 1f),
                    marginBottom = 10
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

        // Buttons in the Escape-menu style: gold primary with dark ink text,
        // dark secondary with a subtle stroke that lights up gold on hover.
        private static Button MakeMenuButton(string text, bool primary, System.Action onClick)
        {
            var gold = new Color(0.941f, 0.706f, 0.361f);
            var goldDim = new Color(0.541f, 0.416f, 0.204f);
            var raised = new Color(0.133f, 0.165f, 0.192f);
            var stroke = new Color(1f, 1f, 1f, 0.12f);
            var ink = new Color(0.06f, 0.086f, 0.102f);
            var textColor = new Color(0.906f, 0.925f, 0.937f);

            var button = new Button(onClick)
            {
                text = text,
                style =
                {
                    width = 252,
                    height = 46,
                    marginLeft = 0, marginRight = 0, marginTop = 0,
                    marginBottom = 10,
                    fontSize = 15,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = primary ? ink : textColor,
                    backgroundColor = primary ? gold : raised,
                    borderTopLeftRadius = 10, borderTopRightRadius = 10,
                    borderBottomLeftRadius = 10, borderBottomRightRadius = 10,
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopWidth = 1, borderBottomWidth = 1,
                    borderLeftColor = stroke, borderRightColor = stroke,
                    borderTopColor = stroke, borderBottomColor = stroke
                }
            };

            if (!primary)
            {
                button.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    button.style.borderLeftColor = goldDim;
                    button.style.borderRightColor = goldDim;
                    button.style.borderTopColor = goldDim;
                    button.style.borderBottomColor = goldDim;
                });
                button.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    button.style.borderLeftColor = stroke;
                    button.style.borderRightColor = stroke;
                    button.style.borderTopColor = stroke;
                    button.style.borderBottomColor = stroke;
                });
            }

            return button;
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

            int seed;
            if (_continueChosen && _save != null)
            {
                seed = _save.seed;
                _targetTick = _save.tick + SaveGame.OfflineTicks(_save);
                Debug.Log($"[HexLive] Resuming save: seed {seed}, tick {_save.tick}" +
                    $" + offline {_targetTick - _save.tick}");
            }
            else
            {
                // Spec 41.4: new game wipes the save and rolls a fresh world.
                SaveGame.Delete();
                seed = System.Environment.TickCount;
                _targetTick = 0;
                Debug.Log($"[HexLive] New game: seed {seed}");
            }

            SetProgress(0.02f, Loc.Get("loading.world"));
            yield return null;

            // Spec 41.1: bootstrap PAUSED; the loader unpauses after the fade.
            var speed = _continueChosen && _save is { speed: > 0f } ? _save.speed : 1f;
            _runner.Configure(
                HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(seed),
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
                        HexLive.Simulation.Bootstrap.PrototypeWorldDefinitionFactory.Create(seed),
                        startPaused: true, initialSpeed: 1f);
                }
            }

            var hasReplay = _runner.Engine is { } configured &&
                configured.World.Tick < _targetTick;
            yield return null;

            // Spec 41.3: wind the loaded world forward by the offline ticks,
            // ~10 ms of stepping per frame so the bar visibly moves.
            if (hasReplay && _runner.Engine is { } engine)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var start = engine.World.Tick;
                while (engine.World.Tick < _targetTick)
                {
                    var frame = System.Diagnostics.Stopwatch.StartNew();
                    while (engine.World.Tick < _targetTick &&
                           frame.ElapsedMilliseconds < ReplayBudgetMsPerFrame)
                    {
                        engine.Step();
                    }

                    var done = (engine.World.Tick - start) /
                        (float)Mathf.Max(1, _targetTick - start);
                    SetProgress(0.05f + done * 0.6f,
                        Loc.Get("loading.time") + $" {engine.World.Tick}/{_targetTick}");
                    yield return null;
                }

                UnityEngine.Debug.Log(
                    $"[HexLive] Replayed to tick {engine.World.Tick} in {clock.ElapsedMilliseconds} ms");
            }

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

            // Spec 41.1: the game opens looking at Jana, panel up (the RTS
            // camera enters orbit on selection). LAST action — nothing may
            // steal the selection after this; Clear first so SelectionChanged
            // re-fires even if the warm-up pass left her selected.
            NpcSelection.Clear();
            NpcSelection.Select(FindJana(npcs));

            _runner.Resume();
            _runner.AutosaveEnabled = true;
            Destroy(gameObject);
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
                result.Add((npc.Id.Value, npc.DisplayName));
            }

            return result;
        }

        private static int FindJana(
            System.Collections.Generic.List<(int id, string name)> npcs)
        {
            foreach (var (id, name) in npcs)
            {
                if (name == JanaName)
                {
                    return id;
                }
            }

            return npcs.Count > 0 ? npcs[npcs.Count - 1].id : -1;
        }
    }
}
