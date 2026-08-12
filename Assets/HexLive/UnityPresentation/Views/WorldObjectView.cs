#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121: связь «нарисованный объект ↔ его номер в симуляции», и всё, что нужно
/// для наведения мышью. Вешается на вид объекта при создании
/// (<c>HexWorldRenderer</c>), чтобы маркер находился по статическому списку,
/// а не поиском по сцене.
///
/// ⭐ Коллайдеров у объектов в этом проекте НЕТ и не заводится. Пикинг здесь
/// аналитический — луч против <see cref="Renderer.bounds"/>. Добавить
/// коллайдеры значило бы, среди прочего, начать
/// попадать в физические тела ткани MagicaCloth на актрисах.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldObjectView : MonoBehaviour
{
    private static readonly List<WorldObjectView> Live = new();

    /// <summary>Все живые виды объектов, в порядке появления.</summary>
    public static IReadOnlyList<WorldObjectView> All => Live;

    private Renderer[] _renderers = Array.Empty<Renderer>();
    private WorldObjectPickBounds[] _pickBounds = Array.Empty<WorldObjectPickBounds>();
    private bool _highlighted;

    /// <summary>Номер объекта в симуляции (<c>ObjectId.Value</c>).</summary>
    public int ObjectId { get; private set; } = -1;

    public string DefinitionId { get; private set; } = string.Empty;

    public IReadOnlyList<Renderer> Renderers => _renderers;

    public void Init(int objectId, string definitionId)
    {
        ObjectId = objectId;
        DefinitionId = definitionId ?? string.Empty;
        _renderers = GetComponentsInChildren<Renderer>(true);
        _pickBounds = GetComponentsInChildren<WorldObjectPickBounds>(true);
    }

    public void Init(int objectId, string definitionId, Renderer[] renderers)
    {
        ObjectId = objectId;
        DefinitionId = definitionId ?? string.Empty;
        _renderers = renderers ?? Array.Empty<Renderer>();
        _pickBounds = GetComponentsInChildren<WorldObjectPickBounds>(true);
    }

    /// <summary>
    /// Ближайшее пересечение луча с габаритами вида. Габариты, а не меши:
    /// попасть в кокос надо мышкой, а не пулей, и лишняя точность здесь стоила
    /// бы перебора треугольников каждый кадр.
    /// </summary>
    public bool TryIntersect(Ray ray, out float distance)
    {
        // Вид с переопределением зоны пикинга (пальма: только ствол, крона не
        // активатор) проверяется исключительно против него — рендереры такого
        // вида в наведении не участвуют вовсе.
        distance = float.MaxValue;
        var hit = false;
        var overridden = false;
        for (var i = 0; i < _pickBounds.Length; i++)
        {
            var pick = _pickBounds[i];
            if (pick == null || !pick.gameObject.activeInHierarchy)
            {
                continue;
            }

            overridden = true;
            if (pick.TryIntersect(ray, out var pickDistance) && pickDistance < distance)
            {
                distance = pickDistance;
                hit = true;
            }
        }

        if (overridden)
        {
            return hit;
        }
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (renderer.bounds.IntersectRay(ray, out var d) && d < distance)
            {
                distance = d;
                hit = true;
            }
        }

        return hit;
    }

    public void SetHighlighted(bool on)
    {
        if (_highlighted == on)
        {
            return;
        }

        _highlighted = on;
        if (on)
        {
            Rendering.HoverHighlight.Active.Apply(_renderers);
        }
        else
        {
            Rendering.HoverHighlight.Active.Clear(_renderers);
        }
    }

    private void OnEnable() => Live.Add(this);

    private void OnDisable()
    {
        // Гаснущий вид обязан снять подсветку сам: иначе объект, скрытый
        // туманом войны под курсором, вернётся уже светящимся.
        if (_highlighted)
        {
            Rendering.HoverHighlight.Active.Clear(_renderers);
            _highlighted = false;
        }

        Live.Remove(this);
    }
}

}
