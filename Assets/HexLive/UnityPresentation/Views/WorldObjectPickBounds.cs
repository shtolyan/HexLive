#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121: замена зоны мышиного пикинга для одного вида. Когда компонент висит
/// под <see cref="WorldObjectView"/>, луч наведения проверяется ТОЛЬКО против
/// перечисленных зон, а не против рендереров вида. Нужен стоящей пальме: крона
/// затеняет пол-гекса сверху, и будь она активатором, съедала бы наведение на
/// всё, что лежит под ней, — активатором остаётся только ствол.
///
/// Зона задаётся сабмешем рендерера, на котором висит компонент: габарит
/// сабмеша служит грубым отбором, а попадание решают его треугольники
/// (<see cref="MeshRayPicker"/>). Старый вариант «только габарит» остаётся
/// запасным для зон без меш-данных.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldObjectPickBounds : MonoBehaviour
{
    private readonly struct Zone
    {
        public readonly Bounds LocalBounds;
        public readonly int Submesh; // -1 — попадание решает сам габарит

        public Zone(Bounds localBounds, int submesh)
        {
            LocalBounds = localBounds;
            Submesh = submesh;
        }
    }

    private readonly List<Zone> _zones = new();
    private Renderer? _renderer;

    public void Add(Bounds localBounds) => _zones.Add(new Zone(localBounds, -1));

    /// <summary>Точная зона: треугольники одного сабмеша здешнего рендерера.</summary>
    public void AddSubmesh(Bounds localBounds, int submesh) =>
        _zones.Add(new Zone(localBounds, submesh));

    public bool TryIntersect(Ray ray, out float distance)
    {
        distance = float.MaxValue;
        var hit = false;
        var matrix = transform.localToWorldMatrix;
        for (var i = 0; i < _zones.Count; i++)
        {
            var zone = _zones[i];
            var world = TransformBounds(matrix, zone.LocalBounds);
            if (!world.IntersectRay(ray, out var entry) || entry >= distance)
            {
                continue;
            }

            if (zone.Submesh >= 0)
            {
                if (_renderer == null)
                {
                    TryGetComponent(out _renderer);
                }

                if (_renderer != null && MeshRayPicker.HasMeshData(_renderer))
                {
                    // Точный тест состоялся: его промах — промах зоны, в
                    // габарит не проваливаемся, иначе пустой угол снова кликался бы.
                    if (MeshRayPicker.TryIntersect(
                            _renderer, zone.Submesh, ray, out var d) && d < distance)
                    {
                        distance = d;
                        hit = true;
                    }

                    continue;
                }
            }

            distance = entry;
            hit = true;
        }

        return hit;
    }

    private static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
    {
        var center = matrix.MultiplyPoint3x4(localBounds.center);
        var extents = localBounds.extents;
        var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        var worldExtents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
        return new Bounds(center, worldExtents * 2f);
    }
}

}
