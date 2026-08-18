#nullable enable
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{

/// <summary>
/// §145.3: связь с сервером потеряна НАСОВСЕМ (реконнект сдался —
/// <see cref="LinkState.Failed"/>). Молчаливый вечный реконнект игрок читал
/// как «игра зависла»: ни ошибки, ни выхода. Теперь — модальный диалог с
/// одной кнопкой: ОК возвращает в главное меню тем же путём, что пункт меню
/// (<see cref="GameMenu.ReloadToMainMenu"/>); сохранять нечего — мир не наш.
/// <para>
/// Смотрит ТОЛЬКО на терминальный Failed. Мигания сети (Reconnecting,
/// Stalled) диалога не открывают — их показывает индикатор связи, и они
/// лечатся сами.
/// </para>
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class ConnectionLostDialog : MonoBehaviour
{
    private static readonly Color Backdrop = new(0f, 0f, 0f, 0.62f);
    private static readonly Color Card = new(0.05f, 0.08f, 0.10f, 0.97f);
    private static readonly Color Stroke = new(0.95f, 0.45f, 0.35f, 0.55f);
    private static readonly Color TextColor = new(0.92f, 0.94f, 0.95f, 1f);
    private static readonly Color TextDim = new(0.65f, 0.70f, 0.72f, 1f);

    private SimulationRunnerBehaviour? _runner;
    private UIDocument _document = null!;
    private VisualElement? _overlay;
    private Label? _detail;
    private bool _shown;

    public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

    private void Awake()
    {
        _document = GetComponent<UIDocument>();
        var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings != null)
        {
            var settings = Instantiate(baseSettings);
            settings.name = "ConnectionLostDialogSettings";
            // Выше всего остального UI: поверх меню, панелей и тостов —
            // модальность без верховенства была бы просто картинкой.
            settings.sortingOrder = 400;
            _document.panelSettings = settings;
        }
    }

    private void Update()
    {
        if (_shown || _runner == null)
        {
            return;
        }

        var link = _runner.Link;
        if (!link.IsRemote || link.State != LinkState.Failed)
        {
            return;
        }

        _shown = true;
        Show(link.Message);
    }

    private void Show(string? technicalMessage)
    {
        var root = _document.rootVisualElement;
        root.pickingMode = PickingMode.Ignore;

        // Подложка ловит ВСЕ клики: пока диалог открыт, мира для мыши нет.
        _overlay = new VisualElement
        {
            style =
            {
                position = Position.Absolute,
                left = 0, right = 0, top = 0, bottom = 0,
                backgroundColor = Backdrop,
                alignItems = Align.Center,
                justifyContent = Justify.Center,
            }
        };

        var card = new VisualElement
        {
            style =
            {
                minWidth = 360, maxWidth = 460,
                paddingTop = 18, paddingBottom = 16,
                paddingLeft = 22, paddingRight = 22,
                backgroundColor = Card,
                borderTopLeftRadius = 10, borderTopRightRadius = 10,
                borderBottomLeftRadius = 10, borderBottomRightRadius = 10,
                borderTopWidth = 1, borderBottomWidth = 1,
                borderLeftWidth = 1, borderRightWidth = 1,
                borderTopColor = Stroke, borderBottomColor = Stroke,
                borderLeftColor = Stroke, borderRightColor = Stroke,
            }
        };

        card.Add(new Label(Loc.Get("net.lost.title"))
        {
            style =
            {
                color = TextColor, fontSize = 17,
                unityFontStyleAndWeight = FontStyle.Bold,
                marginBottom = 8, whiteSpace = WhiteSpace.Normal,
            }
        });
        card.Add(new Label(Loc.Get("net.lost.body"))
        {
            style =
            {
                color = TextColor, fontSize = 13,
                marginBottom = 6, whiteSpace = WhiteSpace.Normal,
            }
        });

        // Техническая строка (последняя причина из бэкенда) — мелко и тускло:
        // игроку она не обязательна, а в отчёте о проблеме бесценна.
        if (!string.IsNullOrEmpty(technicalMessage))
        {
            _detail = new Label(technicalMessage)
            {
                style =
                {
                    color = TextDim, fontSize = 10.5f,
                    marginBottom = 10, whiteSpace = WhiteSpace.Normal,
                }
            };
            card.Add(_detail);
        }

        var ok = new Button(GameMenu.ReloadToMainMenu)
        {
            text = Loc.Get("net.lost.ok"),
            style =
            {
                marginTop = 6, height = 34, fontSize = 14,
                alignSelf = Align.Center, minWidth = 140,
            }
        };
        card.Add(ok);

        _overlay.Add(card);
        root.Add(_overlay);
        ok.Focus();
    }
}

}
