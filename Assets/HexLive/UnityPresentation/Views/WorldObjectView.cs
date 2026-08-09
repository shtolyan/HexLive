#nullable enable
using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121: связь «нарисованный объект ↔ его номер в симуляции», и всё, что нужно
/// для наведения мышью. Вешается на вид объекта при создании
/// (<c>HexWorldRenderer</c>) — ровно как §112 вешает FoliageOccluder, и по той
/// же причине: маркер найдут по статическому списку, а не поиском по сцене.
///
/// ⭐ Коллайдеров у объектов в этом проекте НЕТ и не заводится. Пикинг здесь
/// аналитический. Добавить коллайдеры значило бы, среди прочего, начать
/// попадать в физические тела ткани MagicaCloth на актрисах.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldObjectView : MonoBehaviour
{
    private static readonly List<WorldObjectView> Live = new();

    /// <summary>Все живые виды объектов, в порядке появления.</summary>
    public static IReadOnlyList<WorldObjectView> All => Live;

    // Во что целиться у вида, за которым вообще нет геометрии (якоря тела,
    // могилы, воды, пустой стройплощадки). Полметра — примерно ладонь на
    // экране при обычном отдалении: попасть можно, промахнуться мимо соседа
    // тоже.
    private const float AnchorProxyRadius = 0.25f;

    // Земля под подошвой не идеально ровная (интерполяция вида, наклон тайла),
    // поэтому низ столба чуть ниже якоря.
    private const float PillarFootSlack = 0.2f;

    private Renderer[] _renderers = Array.Empty<Renderer>();
    private FoliageOccluder? _occluder;
    private int _childStamp = -1;
    private bool _highlighted;
    private HoverTone _tone = HoverTone.Normal;

    /// <summary>Номер объекта в симуляции (<c>ObjectId.Value</c>).</summary>
    public int ObjectId { get; private set; } = -1;

    public string DefinitionId { get; private set; } = string.Empty;

    /// <summary>
    /// §121: настоящий радиус препятствия из каталога, в мировых единицах.
    /// Больше нуля — по нему и целятся, а не по габаритам меша.
    /// </summary>
    public float ObstacleRadius { get; private set; }

    public IReadOnlyList<Renderer> Renderers => _renderers;

    public void Init(int objectId, string definitionId, float obstacleRadius)
    {
        ObjectId = objectId;
        DefinitionId = definitionId ?? string.Empty;
        ObstacleRadius = obstacleRadius;
        _occluder = GetComponent<FoliageOccluder>();
        CollectRenderers();
    }

    /// <summary>
    /// Вид уходит из-под управления рендерера (падающее дерево забирает его
    /// себе на пару секунд). Номер объекта в симуляции при этом протухает,
    /// поэтому маркер обязан замолчать: иначе меню действовало бы на объект,
    /// которого уже нет.
    /// </summary>
    public void Detach()
    {
        SetHighlighted(false, HoverTone.Normal);
        ObjectId = -1;
        enabled = false;
    }

    /// <summary>
    /// Попадает ли луч в объект — и на каком расстоянии. Три случая, по
    /// убыванию точности:
    /// <list type="number">
    /// <item>Скрытая листва (§112) не ловится ВООБЩЕ. Пальму убирают из кадра
    /// ровно затем, чтобы взять то, что под ней.</item>
    /// <item>Есть радиус препятствия — целимся в вертикальный цилиндр этого
    /// радиуса вокруг подошвы. ⭐ Это главное: у пальмы один рендерер на ствол
    /// и крону разом, его габаритная коробка 4.0 × 3.9 × 4.1 wu — ШИРЕ гекса, и
    /// накрывает всю землю вокруг. Камера смотрит сверху, входит в «потолок»
    /// этой коробки за метры до кокоса на песке — и пальма выигрывала всегда.
    /// Настоящий ствол при этом 0.45 wu. Репозиторий уже знает этот приём:
    /// <c>CameraFoliageCuller</c> тоже судит по стволу, а не по кроне.</item>
    /// <item>Иначе — габариты рендереров, как и было. Мелкие предметы и так
    /// мелкие, там коробка честная.</item>
    /// </list>
    /// Совсем без геометрии (якоря тела, могилы, воды, пустой стройплощадки) —
    /// маленький шар у подошвы, иначе такие объекты нельзя навести в принципе.
    /// </summary>
    public bool TryIntersect(Ray ray, out float distance)
    {
        distance = float.MaxValue;

        if (ObjectId < 0)
        {
            return false;
        }

        if (_occluder != null && _occluder.Hidden)
        {
            return false;
        }

        RefreshRenderersIfStale();

        if (ObstacleRadius > 0f)
        {
            return TryIntersectPillar(ray, ObstacleRadius, out distance);
        }

        var hit = false;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (!IsPickable(renderer))
            {
                continue;
            }

            if (renderer.bounds.IntersectRay(ray, out var d) && d < distance)
            {
                distance = d;
                hit = true;
            }
        }

        if (hit)
        {
            return true;
        }

        // Ни одного пригодного рендерера — значит вид «пустой якорь».
        return TryIntersectPillar(ray, AnchorProxyRadius, out distance);
    }

    public void SetHighlighted(bool on, HoverTone tone)
    {
        if (!on)
        {
            if (_highlighted)
            {
                HoverHighlight.Active.Clear(_renderers);
                _highlighted = false;
            }

            return;
        }

        // ⭐ Красим КАЖДЫЙ кадр, пока наведено, а не только на входе. Подсвечен
        // всегда ровно один объект, так что это бесплатно, — зато переживает
        // всех, кто переписывает property-блок своим тиком: грязь одежды
        // (GarmentWorldCondition), пересборку кучи на стройплощадке, мясо на
        // вертеле. Без этого одежда светилась бы один кадр из тика.
        if (_highlighted && _tone != tone)
        {
            HoverHighlight.Active.Clear(_renderers);
        }

        RefreshRenderersIfStale();
        _tone = tone;
        _highlighted = true;
        HoverHighlight.Active.Apply(_renderers, tone);
    }

    // Вертикальный столб радиуса r вокруг подошвы вида. Параметр луча один и
    // тот же для всех трёх координат, поэтому ближайшую точку ищем в плоскости
    // XZ, а высоту проверяем уже на найденном параметре — иначе в пальму можно
    // было бы попасть, целясь в небо над ней.
    private bool TryIntersectPillar(Ray ray, float radius, out float distance)
    {
        distance = float.MaxValue;

        var axis = transform.position;
        var toAxis = new Vector2(axis.x - ray.origin.x, axis.z - ray.origin.z);
        var dir = new Vector2(ray.direction.x, ray.direction.z);
        var dirLenSq = dir.sqrMagnitude;
        if (dirLenSq < 1e-6f)
        {
            return false; // луч смотрит строго вниз — столб вырождается в точку
        }

        var t = Vector2.Dot(toAxis, dir) / dirLenSq;
        if (t <= 0f)
        {
            return false; // столб за спиной
        }

        if ((toAxis - dir * t).sqrMagnitude > radius * radius)
        {
            return false;
        }

        // Направление луча от камеры нормировано, поэтому параметр t и есть
        // расстояние в мировых единицах — сравнимо с bounds.IntersectRay.
        var hitY = ray.origin.y + ray.direction.y * t;
        var top = axis.y + PillarHeight();
        if (hitY < axis.y - PillarFootSlack || hitY > top)
        {
            return false;
        }

        distance = t;
        return true;
    }

    // Докуда столб. Берём верх габаритов — ствол пальмы кончается там же, где
    // её крона, и целиться выше неё бессмысленно.
    private float PillarHeight()
    {
        var top = 0f;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (!IsPickable(renderer))
            {
                continue;
            }

            var height = renderer.bounds.max.y - transform.position.y;
            if (height > top)
            {
                top = height;
            }
        }

        return top > 0.01f ? top : AnchorProxyRadius * 2f;
    }

    private static bool IsPickable(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
        {
            return false;
        }

        // Частицы — это дым и искры. У ПОГАСШЕГО костра их рендереры остаются
        // включёнными, и курсор ловил бы пустоту над кострищем.
        return renderer is not ParticleSystemRenderer;
    }

    // Кэш рендереров одноразовый по построению, а часть видов пересобирает
    // детей на ходу: куча на стройплощадке (BuildSitePile) и мясо на вертеле
    // уничтожают и создают их заново на каждой доставке. Такой вид переставал
    // наводиться навсегда. Проверки дешёвые — пересборка происходит редко.
    private void RefreshRenderersIfStale()
    {
        if (transform.childCount != _childStamp)
        {
            CollectRenderers();
            return;
        }

        for (var i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null)
            {
                CollectRenderers();
                return;
            }
        }
    }

    private void CollectRenderers()
    {
        var wasHighlighted = _highlighted;
        if (wasHighlighted)
        {
            HoverHighlight.Active.Clear(_renderers);
        }

        _renderers = GetComponentsInChildren<Renderer>(true);
        _childStamp = transform.childCount;

        if (wasHighlighted)
        {
            HoverHighlight.Active.Apply(_renderers, _tone);
        }
    }

    private void OnEnable() => Live.Add(this);

    private void OnDisable()
    {
        // Гаснущий вид обязан снять подсветку сам: иначе объект, скрытый
        // туманом войны под курсором, вернётся уже светящимся.
        SetHighlighted(false, HoverTone.Normal);
        Live.Remove(this);
    }
}

}
