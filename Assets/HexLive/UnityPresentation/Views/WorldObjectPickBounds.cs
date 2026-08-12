#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121: замена зоны мышиного пикинга для одного вида. Когда компонент висит
/// под <see cref="WorldObjectView"/>, луч наведения проверяется ТОЛЬКО против
/// перечисленных локальных габаритов, а не против <c>Renderer.bounds</c> всех
/// рендереров. Нужен стоящей пальме: её крона — сабмеш того же рендерера, что
/// и ствол, поэтому баунд рендерера накрывает пол-гекса и съедает наведение на
/// всё, что лежит под кроной. Активатором остаётся только ствол.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldObjectPickBounds : MonoBehaviour
{
    private readonly List<Bounds> _localBounds = new();

    public void Add(Bounds localBounds) => _localBounds.Add(localBounds);

    public bool TryIntersect(Ray ray, out float distance)
    {
        distance = float.MaxValue;
        var hit = false;
        var matrix = transform.localToWorldMatrix;
        for (var i = 0; i < _localBounds.Count; i++)
        {
            var world = TransformBounds(matrix, _localBounds[i]);
            if (world.IntersectRay(ray, out var d) && d < distance)
            {
                distance = d;
                hit = true;
            }
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
