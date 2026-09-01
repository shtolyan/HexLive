#nullable enable
using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{

/// <summary>Один пункт меню: что написать и что сделать по клику.</summary>
public sealed class ContextMenuEntry
{
    public ContextMenuEntry(string label, Action activate, bool enabled = true, string? disabledHint = null)
    {
        Label = label;
        Activate = activate;
        Enabled = enabled;
        DisabledHint = disabledHint;
    }

    public string Label { get; }

    public Action Activate { get; }

    /// <summary>Ложь — пункт виден, но серый. ВИДЕН намеренно: «топором нельзя»
    /// — это сведение о мире, а исчезнувший пункт учит только тому, что меню
    /// каждый раз разное.</summary>
    public bool Enabled { get; }

    public string? DisabledHint { get; }

    /// <summary>Bug #312: «опасный» пункт (кража) красится красным.
    /// set, а не init: Unity-профиль netstandard2.1 не несёт IsExternalInit.</summary>
    public bool Danger { get; set; }
}

/// <summary>
/// §121: меню действий над тем, на что кликнули. Содержимое приходит готовым —
/// панель ничего не знает ни про объекты, ни про приказы, она только рисует
/// список и зовёт <see cref="ContextMenuEntry.Activate"/>.
///
/// Порядок слоёв: 165 — выше инспектора гекса (155) и полосы скорости (160),
/// ниже отладочной панели (170) и модального меню игры (220).
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class ContextMenuPanel : MonoBehaviour
{
    [SerializeField] private float _uiScale = 1f;

    private UIDocument _document = null!;
    private VisualElement _root = null!;
    private VisualElement _card = null!;
    private Label _title = null!;
    private VisualElement _items = null!;

    private static ContextMenuPanel? _instance;

    public static bool IsOpen { get; private set; }

    /// <summary>Курсор над меню — мировой клик обязан его пропустить.</summary>
    public static bool PointerOverPanel { get; private set; }

    // UI Toolkit и мировой Input System обрабатывают одно нажатие независимо.
    // Пункт меню закрывает карточку уже на MouseDown, поэтому одного
    // PointerOverPanel недостаточно: поздний Update камеры увидит скрытое меню
    // и примет тот же press за начало приказа. Защёлка держит всю физическую
    // pointer-последовательность до кадра после отпускания кнопки.
    private static bool _worldPointerSuppressed;
    private static int _worldPointerReleaseFrame = -1;

    /// <summary>Меню владеет текущим pointer-жестом; миру его видеть нельзя.</summary>
    public static bool BlocksWorldPointer =>
        IsOpen || PointerOverPanel || _worldPointerSuppressed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        IsOpen = false;
        PointerOverPanel = false;
        _worldPointerSuppressed = false;
        _worldPointerReleaseFrame = -1;
    }

    private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
    private static readonly Color TextMute = new(0.400f, 0.447f, 0.478f);
    private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.97f);
    private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
    private static readonly Color Stroke = new(1f, 1f, 1f, 0.14f);
    private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);

    private void Awake()
    {
        _instance = this;
        _document = GetComponent<UIDocument>();

        var reference = new Vector2Int(
            Mathf.RoundToInt(1920f / Mathf.Max(0.25f, _uiScale)),
            Mathf.RoundToInt(1080f / Mathf.Max(0.25f, _uiScale)));

        var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings != null)
        {
            var settings = Instantiate(baseSettings);
            settings.name = "ContextMenuPanelSettings";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.referenceResolution = reference;
            settings.match = 1f;
            settings.sortingOrder = 165;
            _document.panelSettings = settings;
        }

        BuildUi();
        Hide(suppressWorldPointer: false);
    }

    private void Update()
    {
        if (!_worldPointerSuppressed)
        {
            return;
        }

        var mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed))
        {
            _worldPointerReleaseFrame = -1;
            return;
        }

        if (_worldPointerReleaseFrame < 0)
        {
            _worldPointerReleaseFrame = Time.frameCount;
            return;
        }

        if (Time.frameCount > _worldPointerReleaseFrame)
        {
            _worldPointerSuppressed = false;
            _worldPointerReleaseFrame = -1;
        }
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
            IsOpen = false;
            PointerOverPanel = false;
            _worldPointerSuppressed = false;
            _worldPointerReleaseFrame = -1;
        }
    }

    private void BuildUi()
    {
        _root = _document.rootVisualElement;
        _root.pickingMode = PickingMode.Ignore;

        _card = new VisualElement
        {
            style =
            {
                position = Position.Absolute,
                minWidth = 190,
                maxWidth = 320,
                paddingTop = 6,
                paddingBottom = 6,
                paddingLeft = 6,
                paddingRight = 6,
                backgroundColor = Panel,
            }
        };
        SetRadius(_card, 8f);
        SetBorder(_card, Stroke, 1f);
        _card.RegisterCallback<PointerEnterEvent>(_ => PointerOverPanel = true);
        _card.RegisterCallback<PointerLeaveEvent>(_ => PointerOverPanel = false);

        _title = new Label
        {
            style =
            {
                color = Gold,
                fontSize = 13,
                unityFontStyleAndWeight = FontStyle.Bold,
                marginBottom = 4,
                marginLeft = 6,
                marginTop = 2,
            }
        };
        _card.Add(_title);

        _items = new VisualElement();
        _card.Add(_items);

        _root.Add(_card);
    }

    /// <summary>Открыть меню у точки экрана (в пикселях, начало — левый низ,
    /// как отдаёт Input System).</summary>
    public static void Open(Vector2 screenPosition, string title, IReadOnlyList<ContextMenuEntry> entries)
    {
        if (_instance == null || entries.Count == 0)
        {
            return;
        }

        _instance.OpenInternal(screenPosition, title, entries);
    }

    public static void Close()
    {
        if (_instance != null)
        {
            _instance.Hide();
        }
    }

    private void OpenInternal(Vector2 screenPosition, string title, IReadOnlyList<ContextMenuEntry> entries)
    {
        _title.text = title;
        _items.Clear();

        foreach (var entry in entries)
        {
            _items.Add(MakeItem(entry));
        }

        _root.style.display = DisplayStyle.Flex;
        IsOpen = true;

        // Экранные пиксели → координаты панели. Ось Y переворачивается: у
        // Input System начало внизу, у UI Toolkit — вверху.
        var panelPoint = RuntimePanelUtils.ScreenToPanel(
            _root.panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
        _card.style.left = panelPoint.x;
        _card.style.top = panelPoint.y;

        // Кламп по краям экрана — после того, как раскладка посчитает размер:
        // до этого ширина меню неизвестна, и меню у правого края уезжало бы
        // за экран целиком.
        _card.RegisterCallback<GeometryChangedEvent>(ClampIntoPanel);
    }

    private void ClampIntoPanel(GeometryChangedEvent evt)
    {
        _card.UnregisterCallback<GeometryChangedEvent>(ClampIntoPanel);

        var panelSize = _root.layout.size;
        var card = _card.layout.size;
        if (panelSize.x <= 0f || card.x <= 0f)
        {
            return;
        }

        var left = Mathf.Clamp(_card.style.left.value.value, 4f, Mathf.Max(4f, panelSize.x - card.x - 4f));
        var top = Mathf.Clamp(_card.style.top.value.value, 4f, Mathf.Max(4f, panelSize.y - card.y - 4f));
        _card.style.left = left;
        _card.style.top = top;
    }

    private VisualElement MakeItem(ContextMenuEntry entry)
    {
        var row = new VisualElement
        {
            style =
            {
                paddingTop = 5,
                paddingBottom = 5,
                paddingLeft = 8,
                paddingRight = 8,
                marginBottom = 2,
                backgroundColor = Color.clear,
            }
        };
        SetRadius(row, 5f);

        var label = new Label(entry.Label)
        {
            style =
            {
                // Bug #312: кража горит красным — игрок должен видеть, что
                // это не просто «подобрать».
                color = !entry.Enabled ? TextMute
                    : entry.Danger ? new Color(0.910f, 0.365f, 0.365f)
                    : Text,
                fontSize = 13,
            }
        };
        row.Add(label);

        if (entry.Enabled)
        {
            row.RegisterCallback<PointerEnterEvent>(_ => row.style.backgroundColor = Raised);
            row.RegisterCallback<PointerLeaveEvent>(_ => row.style.backgroundColor = Color.clear);
            row.RegisterCallback<MouseDownEvent>(e =>
            {
                e.StopPropagation();
                var action = entry.Activate;
                Hide();
                action();
            });
        }
        else if (!string.IsNullOrEmpty(entry.DisabledHint))
        {
            var hint = new Label(entry.DisabledHint)
            {
                style = { color = TextMute, fontSize = 11, marginTop = 1 }
            };
            row.Add(hint);
        }

        return row;
    }

    private void Hide(bool suppressWorldPointer = true)
    {
        if (suppressWorldPointer && IsOpen)
        {
            _worldPointerSuppressed = true;
            _worldPointerReleaseFrame = -1;
        }

        _root.style.display = DisplayStyle.None;
        _items.Clear();
        IsOpen = false;
        PointerOverPanel = false;
    }

    private static void SetRadius(VisualElement e, float radius)
    {
        e.style.borderTopLeftRadius = radius;
        e.style.borderTopRightRadius = radius;
        e.style.borderBottomLeftRadius = radius;
        e.style.borderBottomRightRadius = radius;
    }

    private static void SetBorder(VisualElement e, Color color, float width)
    {
        e.style.borderLeftColor = color;
        e.style.borderRightColor = color;
        e.style.borderTopColor = color;
        e.style.borderBottomColor = color;
        e.style.borderLeftWidth = width;
        e.style.borderRightWidth = width;
        e.style.borderTopWidth = width;
        e.style.borderBottomWidth = width;
    }
}

}
