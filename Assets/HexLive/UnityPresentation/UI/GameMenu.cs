using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// The Escape menu. Escape while a character is selected still belongs to
    /// the camera (deselect); Escape with nothing selected opens this overlay:
    /// Continue (hide) or Quit (exit the game / stop play mode in the editor).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class GameMenu : MonoBehaviour
    {
        /// <summary>World input (NPC picking etc.) is blocked while open.</summary>
        public static bool IsOpen { get; private set; }

        [SerializeField] private SimulationRunnerBehaviour _runner;

        // Was the sim already paused (via the speed bar) before the menu
        // opened? Then closing the menu must NOT force-resume it.
        private bool _simWasPausedBefore;
        private float _timeScaleBefore = 1f;

        private UIDocument _document;
        private VisualElement _overlay;
        private Label _title;
        private Label _continueLabel;
        private Label _mainMenuLabel;
        private Label _quitLabel;
        private Foldout _soundSettings;
        private readonly Label[] _soundLabels = new Label[4];

        private static readonly Color Dim = new(0f, 0f, 0f, 0.55f);
        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.98f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Hover = new(0.180f, 0.220f, 0.255f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
        private static readonly Color TextDim = new(0.604f, 0.651f, 0.678f);
        private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
        private static readonly Color Ink = new(0.06f, 0.086f, 0.102f);
        private static readonly Color Danger = new(0.75f, 0.20f, 0.18f);

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "GameMenuPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 220; // above every other HUD layer
                _document.panelSettings = settings;
            }

            Build();
            ApplyLanguage();
            IsOpen = false; // fresh session; the overlay is built hidden
        }

        private void OnEnable() => Loc.LanguageChanged += ApplyLanguage;

        private void OnDisable()
        {
            Loc.LanguageChanged -= ApplyLanguage;
            PlayerPrefs.Save();
            if (IsOpen)
            {
                Time.timeScale = _timeScaleBefore > 0f ? _timeScaleBefore : 1f;
            }

            IsOpen = false;
        }

        private void Update()
        {
            // The title/loading screen owns the display — this menu stays
            // hidden there no matter what (and force-closes if it slipped open).
            if (LoadingScreen.IsActive)
            {
                if (IsOpen)
                {
                    SetOpen(false);
                }

                return;
            }

            if (AdminVoicePanel.BlocksGameInput) return;
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            if (IsOpen)
            {
                SetOpen(false);
            }
            else if (!NpcSelection.HasSelection)
            {
                // With a selection, this same Escape press deselects instead
                // (the camera handles it in LateUpdate, after this check).
                SetOpen(true);
            }
        }

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void SetOpen(bool open)
        {
            if (IsOpen == open)
            {
                return;
            }

            if (!open) PlayerPrefs.Save();
            IsOpen = open;
            if (_overlay != null)
            {
                _overlay.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (open)
            {
                // Preserve the scaled presentation clock before Pause freezes
                // it. This can legitimately be zero when the player already
                // paused through the speed bar.
                _timeScaleBefore = Time.timeScale;
                _simWasPausedBefore = _runner != null && _runner.IsPaused;
                if (_runner != null && !_simWasPausedBefore)
                {
                    _runner.Pause();
                }

                Time.timeScale = 0f;
            }
            else
            {
                Time.timeScale = _timeScaleBefore >= 0f ? _timeScaleBefore : 1f;
                // Resume only if WE paused it — a manual pause from the speed
                // bar survives the menu.
                if (_runner != null && !_simWasPausedBefore && _runner.IsPaused)
                {
                    _runner.TogglePause();
                }
            }
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            var soundStyles = Resources.Load<StyleSheet>("HexLive/AudioSettings");
            if (soundStyles != null) root.styleSheets.Add(soundStyles);
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;

            _overlay = new VisualElement();
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0f;
            _overlay.style.right = 0f;
            _overlay.style.top = 0f;
            _overlay.style.bottom = 0f;
            _overlay.style.backgroundColor = Dim;
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.pickingMode = PickingMode.Position; // swallows world clicks
            // Born hidden — SetOpen(false) early-outs when IsOpen is already
            // false, so the initial hide MUST happen here, not there.
            _overlay.style.display = DisplayStyle.None;
            root.Add(_overlay);

            var card = new VisualElement();
            card.style.width = 340f;
            card.style.backgroundColor = Panel;
            SetBorder(card, Stroke, 1f);
            SetRadius(card, 16f);
            card.style.paddingLeft = 28f;
            card.style.paddingRight = 28f;
            card.style.paddingTop = 26f;
            card.style.paddingBottom = 26f;
            card.style.alignItems = Align.Stretch;
            _overlay.Add(card);

            _title = new Label();
            _title.style.color = Text;
            _title.style.fontSize = 24;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.marginBottom = 22f;
            card.Add(_title);

            var continueButton = MakeButton(out _continueLabel);
            continueButton.style.backgroundColor = Gold;
            _continueLabel.style.color = Ink;
            continueButton.RegisterCallback<MouseDownEvent>(_ => SetOpen(false));
            card.Add(continueButton);
            card.Add(new AgentPairingPanel());

            _soundSettings = new Foldout { name = "sound-settings", value = false };
            _soundSettings.AddToClassList("audio-settings");
            card.Add(_soundSettings);
            AddSoundSlider(FmodSfx.VolumeCategory.Voices);
            AddSoundSlider(FmodSfx.VolumeCategory.AgentVoices);
            AddSoundSlider(FmodSfx.VolumeCategory.Music);
            AddSoundSlider(FmodSfx.VolumeCategory.Environment);

            // Save the current game, tear the world down, and return to the
            // boot menu (see ReturnToMainMenu).
            var mainMenuButton = MakeButton(out _mainMenuLabel);
            mainMenuButton.style.backgroundColor = Raised;
            mainMenuButton.style.marginTop = 10f;
            mainMenuButton.RegisterCallback<MouseEnterEvent>(_ => mainMenuButton.style.backgroundColor = Hover);
            mainMenuButton.RegisterCallback<MouseLeaveEvent>(_ => mainMenuButton.style.backgroundColor = Raised);
            mainMenuButton.RegisterCallback<MouseDownEvent>(_ => ReturnToMainMenu());
            card.Add(mainMenuButton);

            var quitButton = MakeButton(out _quitLabel);
            quitButton.style.backgroundColor = Raised;
            quitButton.style.marginTop = 10f;
            quitButton.RegisterCallback<MouseEnterEvent>(_ => quitButton.style.backgroundColor = Danger);
            quitButton.RegisterCallback<MouseLeaveEvent>(_ => quitButton.style.backgroundColor = Raised);
            quitButton.RegisterCallback<MouseDownEvent>(_ => Quit());
            card.Add(quitButton);
        }

        private void AddSoundSlider(FmodSfx.VolumeCategory category)
        {
            var row = new VisualElement();
            row.AddToClassList("audio-settings-row");
            var heading = new VisualElement();
            heading.AddToClassList("audio-settings-heading");
            var label = new Label();
            _soundLabels[(int)category] = label;
            heading.Add(label);
            var percent = new Label();
            percent.AddToClassList("audio-settings-percent");
            heading.Add(percent);
            row.Add(heading);
            var slider = new Slider(0f, 100f) { name = "volume-" + category.ToString().ToLowerInvariant() };
            slider.AddToClassList("audio-settings-slider");
            slider.SetValueWithoutNotify(FmodSfx.GetUserVolume(category) * 100f);
            percent.text = $"{Mathf.RoundToInt(slider.value)}%";
            slider.RegisterValueChangedCallback(evt =>
            {
                FmodSfx.SetUserVolume(category, evt.newValue / 100f);
                percent.text = $"{Mathf.RoundToInt(evt.newValue)}%";
            });
            row.Add(slider);
            _soundSettings.Add(row);
        }

        private static VisualElement MakeButton(out Label label)
        {
            var button = new VisualElement();
            button.style.height = 46f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            SetBorder(button, Stroke, 1f);
            SetRadius(button, 10f);

            label = new Label();
            label.style.color = Text;
            label.style.fontSize = 15;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.Add(label);
            return button;
        }

        private void ApplyLanguage()
        {
            if (_title == null)
            {
                return;
            }

            _title.text = Loc.Get("menu.title");
            _continueLabel.text = Loc.Get("menu.continue");
            _mainMenuLabel.text = Loc.Get("menu.mainmenu");
            _quitLabel.text = Loc.Get("menu.quit");
            _soundSettings.text = Loc.Get("audio.settings");
            _soundLabels[(int)FmodSfx.VolumeCategory.Voices].text = Loc.Get("audio.voices");
            _soundLabels[(int)FmodSfx.VolumeCategory.AgentVoices].text = Loc.Get("audio.agent_voices");
            _soundLabels[(int)FmodSfx.VolumeCategory.Music].text = Loc.Get("audio.music");
            _soundLabels[(int)FmodSfx.VolumeCategory.Environment].text = Loc.Get("audio.environment");
            _soundSettings.tooltip = Loc.Get("audio.settings.hint");
        }

        // Save the current game, then reload the active scene. The world,
        // runner and every HUD panel are destroyed with the old scene; the
        // fresh load re-runs PrototypeRuntimeBootstrap.Install (AfterSceneLoad),
        // which opens the LoadingScreen as the main menu again.
        private void ReturnToMainMenu()
        {
            var runner = _runner != null ? _runner : FindAnyObjectByType<SimulationRunnerBehaviour>();
            if (runner != null)
            {
                // Explicit user save — write unconditionally, independent of the
                // autosave gates. A no-op when the world is not ours to persist
                // (WriteSaveNow checks SupportsClientSave).
                runner.WriteSaveNow();
            }

            ReloadToMainMenu();
        }

        /// <summary>
        /// §145.3: общий хвост «в главное меню» — перезагрузка сцены начисто.
        /// Мир, раннер и все панели умирают вместе со сценой; свежая загрузка
        /// заново запускает PrototypeRuntimeBootstrap, и LoadingScreen снова
        /// становится главным меню. Зовут пункт меню (после сохранения) и
        /// диалог потери связи (там сохранять нечего — мир не наш).
        /// </summary>
        internal static void ReloadToMainMenu()
        {
            // Menu paused the world (timeScale 0); the reload must start from a
            // clean clock, and the overlay flag must not survive into boot.
            IsOpen = false;
            Time.timeScale = 1f;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadScene(scene.buildIndex);
        }

        private static void Quit()
        {
            // timeScale survives leaving play mode — don't poison the editor.
            Time.timeScale = 1f;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void SetBorder(VisualElement e, Color color, float width)
        {
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
        }
    }
}
