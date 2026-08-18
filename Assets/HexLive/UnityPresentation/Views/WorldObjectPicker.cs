#nullable enable
using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121.1 r3: выбор объекта мира под лучом. Раньше это был один цикл
/// «ближайший треугольник побеждает», и на сушилке (§35.5B) он был неправ:
/// каркас и вещь на нём — равноправные виды с ОБЩИМ якорем джанкшена, а палка
/// толщиной в сантиметр запросто оказывается ближе к камере, чем ткань в
/// дальнем слоте. Клик уходил на сушилку, у которой в меню одно «повесить», и
/// снять вещь было нечем.
///
/// Порядок теперь трёхфазный:
/// 1. ⭐ содержимое важнее вешалки — вид, умеющий «повесить», уступает луч
///    всему, во что тот же луч попал точно;
/// 2. промах по ткани у вешалки добирается near-miss'ом: МЕЛКИЙ вид рядом с
///    лучом ловится габаритом, расширенным на <see cref="NearMissMargin"/>;
/// 3. и только если рядом ничего нет — сама вешалка.
///
/// Замеры, из которых взяты числа (2026-08-18, редактор): вещь на сушилке
/// 0.24 × 0.21 × 0.026 wu, каркас 0.88 × 0.78, слоты с шагом 0.22 по X, ряды
/// ткани на z = +0.10 / −0.08 при рейках на ±0.055.
/// </summary>
public static class WorldObjectPicker
{
    /// <summary>
    /// Насколько раздувается габарит мелкого вида в фазе near-miss, wu.
    /// 0.04 выбрано по замеру: ряды ткани разнесены по глубине на 0.18, так
    /// что даже раздутые (0.026 + 0.08 = 0.106) они не смыкаются, а передний
    /// ряд не начинает ловить клики заднего.
    /// </summary>
    public const float NearMissMargin = 0.04f;

    /// <summary>
    /// Диагональ габарита, ниже которой вид считается мелким и участвует в
    /// near-miss. Замер: вещь ≈ 0.32 wu, сушилка ≈ 1.18 — порог 0.5 разводит
    /// их с запасом, и раздувать каркасы/мебель он не даёт.
    /// </summary>
    public const float SmallPropDiagonal = 0.5f;

    /// <summary>
    /// Вид под лучом. <paramref name="isEligible"/> — обычный фильтр вызова
    /// (есть взаимодействия, не отсечён culling'ом), <paramref name="defers"/>
    /// — «это вешалка, она уступает своему содержимому». Оба предиката
    /// приходят из вызывающего: каталог знает только он.
    /// </summary>
    public static WorldObjectView? Pick(
        Ray ray,
        Func<WorldObjectView, bool> isEligible,
        Func<WorldObjectView, bool> defers,
        out float distance)
    {
        distance = float.MaxValue;
        WorldObjectView? direct = null;
        var directDistance = float.MaxValue;
        WorldObjectView? deferred = null;
        var deferredDistance = float.MaxValue;

        var all = WorldObjectView.All;
        for (var i = 0; i < all.Count; i++)
        {
            var view = all[i];
            if (view == null || !isEligible(view) || !view.TryIntersect(ray, out var hit))
            {
                continue;
            }

            if (defers(view))
            {
                if (hit < deferredDistance)
                {
                    deferredDistance = hit;
                    deferred = view;
                }
            }
            else if (hit < directDistance)
            {
                directDistance = hit;
                direct = view;
            }
        }

        // Фаза 1: точное попадание в НЕ-вешалку выигрывает всегда, на любой
        // глубине. Именно это и значит «сначала рекастятся вещи»: ткань позади
        // палки больше не проигрывает ей по дистанции.
        if (direct != null)
        {
            distance = directDistance;
            return direct;
        }

        if (deferred == null)
        {
            return null;
        }

        // Фаза 2: луч попал в вешалку — значит игрок целился в её содержимое.
        // Мелкие виды поблизости получают второй шанс по раздутому габариту.
        var nearMiss = PickNearMiss(ray, isEligible, defers, out var nearMissDistance);
        if (nearMiss != null)
        {
            distance = nearMissDistance;
            return nearMiss;
        }

        distance = deferredDistance;
        return deferred;
    }

    private static WorldObjectView? PickNearMiss(
        Ray ray,
        Func<WorldObjectView, bool> isEligible,
        Func<WorldObjectView, bool> defers,
        out float distance)
    {
        distance = float.MaxValue;
        WorldObjectView? best = null;
        // ⭐ Победителя выбирает ПЕРПЕНДИКУЛЯР от луча до центра вещи, а не
        // глубина. Раздутые габариты соседок по рейке перекрываются (шаг 0.22
        // против ширины 0.24), и «кто ближе к камере» прыгал бы между ними от
        // пикселя к пикселю; расстояние до оси луча меняется монотонно, так
        // что выбирается ровно та вещь, НАД которой курсор.
        var bestOffset = float.MaxValue;

        var all = WorldObjectView.All;
        for (var i = 0; i < all.Count; i++)
        {
            var view = all[i];
            if (view == null || !isEligible(view) || defers(view) ||
                !view.TryGetWorldBounds(out var bounds))
            {
                continue;
            }

            if (bounds.size.magnitude > SmallPropDiagonal)
            {
                continue;
            }

            var grown = bounds;
            grown.Expand(NearMissMargin * 2f); // Expand — это ПОЛНЫЙ размер
            if (!grown.IntersectRay(ray, out var entry))
            {
                continue;
            }

            var offset = RayToPointDistance(ray, bounds.center);
            if (offset >= bestOffset)
            {
                continue;
            }

            bestOffset = offset;
            distance = entry;
            best = view;
        }

        return best;
    }

    private static float RayToPointDistance(Ray ray, Vector3 point)
    {
        var toPoint = point - ray.origin;
        var along = Mathf.Max(Vector3.Dot(toPoint, ray.direction), 0f);
        return Vector3.Distance(point, ray.origin + ray.direction * along);
    }
}

}
