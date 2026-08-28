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
/// аналитический: <see cref="Renderer.bounds"/> отбирает кандидатов, а
/// попадание решает луч против треугольников меша (<see cref="MeshRayPicker"/>).
/// Добавить коллайдеры значило бы, среди прочего, начать попадать в
/// физические тела ткани MagicaCloth на актрисах.
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

    /// <summary>
    /// Контекстная цель может отличаться от нарисованного объекта: отдельная
    /// секция недостроенного модульного дома проксирует меню на footprint-owner.
    /// Геометрия и подсветка при этом остаются у самой секции.
    /// </summary>
    public int ContextObjectId { get; private set; } = -1;

    public string ContextDefinitionId { get; private set; } = string.Empty;

    public IReadOnlyList<Renderer> Renderers => _renderers;

    public void Init(int objectId, string definitionId)
    {
        ObjectId = objectId;
        DefinitionId = definitionId ?? string.Empty;
        ClearContextProxy();
        RefreshGeometry();
    }

    public void Init(int objectId, string definitionId, Renderer[] renderers)
    {
        ObjectId = objectId;
        DefinitionId = definitionId ?? string.Empty;
        ClearContextProxy();
        _renderers = renderers ?? Array.Empty<Renderer>();
        _pickBounds = GetComponentsInChildren<WorldObjectPickBounds>(true);
    }

    public void SetContextProxy(int objectId, string definitionId)
    {
        ContextObjectId = objectId;
        ContextDefinitionId = definitionId ?? string.Empty;
    }

    public void ClearContextProxy()
    {
        ContextObjectId = ObjectId;
        ContextDefinitionId = DefinitionId;
    }

    /// <summary>
    /// Refresh the analytical picking surface after a streaming/dynamic view
    /// replaces its child geometry. Build sites can exist before their content
    /// prefab arrives, so the renderer list captured by Init may be empty even
    /// though the bed appears a few frames later.
    /// </summary>
    public void RefreshGeometry()
    {
        if (_highlighted)
        {
            Rendering.HoverHighlight.Active.Clear(_renderers);
        }

        _renderers = GetComponentsInChildren<Renderer>(true);
        _pickBounds = GetComponentsInChildren<WorldObjectPickBounds>(true);

        if (_highlighted)
        {
            Rendering.HoverHighlight.Active.Apply(_renderers);
        }
    }

    /// <summary>
    /// Ближайшее пересечение луча с видимой геометрией вида (§121.1).
    /// <see cref="Renderer.bounds"/> — только грубый отбор: осевой габарит
    /// накрывает пустые углы, и раньше костёр «съедал» клики по кокосу и
    /// колонистке рядом. Победу решает <see cref="MeshRayPicker"/> — луч
    /// против треугольников меша; рендерер без меш-данных (частицы пламени)
    /// участвует по-старому лишь когда меш-пригодных в виде нет вовсе.
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

        var meshCapableSeen = false;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            // #247: the distant square is a picture, not a new hitbox. Its
            // transparent corners and square bounds used to cover neighbours
            // and its lifted centre made the cursor feel vertically shifted.
            // Real renderers remain enabled while forceRenderingOff, so their
            // authored triangles provide exactly the same pick at every zoom.
            if (renderer.TryGetComponent<ObjectImpostorVisual>(out _))
            {
                continue;
            }

            if (!MeshRayPicker.HasMeshData(renderer))
            {
                continue;
            }

            meshCapableSeen = true;
            // Меш лежит внутри своего габарита, поэтому вход луча в габарит —
            // нижняя граница точной дистанции: дальше текущего победителя
            // треугольники можно не перебирать.
            if (!renderer.bounds.IntersectRay(ray, out var entry) || entry >= distance)
            {
                continue;
            }

            if (MeshRayPicker.TryIntersect(renderer, ray, out var d) && d < distance)
            {
                distance = d;
                hit = true;
            }
        }

        if (meshCapableSeen)
        {
            return hit;
        }

        // Ни одного меша (чисто эффектный вид) — прежнее поведение по габаритам.
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

    /// <summary>
    /// Объединённый мировой габарит видимых рендереров — для near-miss фазы
    /// пикинга (<see cref="WorldObjectPicker"/>) и оценки «мелкий проп».
    /// Переопределение зоны (<see cref="WorldObjectPickBounds"/>) здесь
    /// сознательно не учитывается: его носит только крупная пальма, а в
    /// near-miss попадают лишь мелкие виды.
    /// </summary>
    public bool TryGetWorldBounds(out Bounds bounds)
    {
        bounds = default;
        var found = false;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (renderer == null || !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (renderer.TryGetComponent<ObjectImpostorVisual>(out _))
            {
                continue;
            }

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    /// <summary>§121.4: вид под курсором. Читает GarmentWorldCondition, чтобы
    /// его покадровый Sync не затирал property block подсветки.</summary>
    public bool Highlighted => _highlighted;

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
