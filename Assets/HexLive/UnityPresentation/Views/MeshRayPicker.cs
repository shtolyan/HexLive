#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §121.1: точная фаза мышиного пикинга — луч против треугольников настоящего
/// меша. <see cref="Renderer.bounds"/> остаётся грубым отбором кандидатов: он
/// осевой и накрывает пустые углы, из-за чего крупный объект съедал клики по
/// всему рядом (костёр против кокоса, колонистка вплотную к предмету).
/// Физических коллайдеров по-прежнему нет — проверка аналитическая, по
/// кэшированным вершинам, и выполняется только для кандидатов, чей габарит луч
/// уже задел (единицы за кадр наведения).
///
/// Скелетные меши запекаются <see cref="SkinnedMeshRenderer.BakeMesh(Mesh)"/>
/// не чаще раза в <see cref="BakeIntervalSeconds"/> на рендерер: поза за 0.1 с
/// уходит меньше, чем на толщину пальца, а покадровая запечка — это ровно та
/// цена, что уже жгла кадр в бою.
/// </summary>
public static class MeshRayPicker
{
    private const float BakeIntervalSeconds = 0.1f;
    // Настоящий предохранитель утечки — чистка мёртвых ключей в EnsureBaked;
    // порог лишь задаёт, с какого размера словаря она начинает работать.
    private const int BakeCacheSweepThreshold = 64;

    private sealed class MeshData
    {
        public Vector3[] Vertices = System.Array.Empty<Vector3>();
        public int[] Triangles = System.Array.Empty<int>();
    }

    private sealed class BakeEntry
    {
        public readonly Mesh Baked;
        public readonly List<Vector3> Vertices = new();
        public readonly List<int> Triangles = new();
        public Mesh? SourceMesh;
        public float BakedAt = float.NegativeInfinity;

        public BakeEntry()
        {
            Baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        }
    }

    private static readonly Dictionary<Mesh, MeshData> StaticCache = new();
    private static readonly Dictionary<(Mesh mesh, int submesh), MeshData> SubmeshCache = new();
    private static readonly Dictionary<SkinnedMeshRenderer, BakeEntry> BakeCache = new();
    private static readonly List<Vector3> VertexScratch = new();
    private static readonly List<int> TriangleScratch = new();
    private static readonly List<SkinnedMeshRenderer> DeadKeyScratch = new();

    /// <summary>
    /// Есть ли у рендерера меш, по которому можно считать точное попадание.
    /// Рендерер без него (частицы пламени, trail) в точном пикинге не участвует
    /// вовсе — светящийся воздух не должен быть кликабельным.
    /// </summary>
    public static bool HasMeshData(Renderer renderer)
    {
        if (renderer is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh != null;
        }

        return renderer is MeshRenderer &&
            renderer.TryGetComponent<MeshFilter>(out var filter) &&
            filter.sharedMesh != null && IsReadable(filter.sharedMesh);
    }

    /// <summary>
    /// Ближайшее пересечение луча с треугольниками рендерера, в мировых
    /// единицах вдоль нормированного направления луча. Ложь — и когда меша
    /// нет, и когда луч прошёл мимо: пустой угол габарита прозрачен.
    /// </summary>
    public static bool TryIntersect(Renderer renderer, Ray ray, out float distance)
        => TryIntersect(renderer, -1, ray, out distance);

    /// <summary>
    /// То же, но только по треугольникам одного сабмеша (submesh &lt; 0 — весь
    /// меш). Нужно пальме: активатор — ствол, крона остаётся прозрачной.
    /// </summary>
    public static bool TryIntersect(
        Renderer renderer, int submesh, Ray ray, out float distance)
    {
        distance = float.MaxValue;
        if (renderer is SkinnedMeshRenderer skinned)
        {
            var entry = EnsureBaked(skinned);
            if (entry == null)
            {
                return false;
            }

            // BakeMesh(useScale: true) кладёт масштаб трансформа в вершины,
            // поэтому мировая матрица — только поворот и позиция.
            var transform = skinned.transform;
            var matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            if (submesh < 0)
            {
                return TryIntersectLists(
                    entry.Vertices, entry.Triangles, 0, entry.Triangles.Count,
                    matrix, ray, out distance);
            }

            if (submesh >= entry.Baked.subMeshCount)
            {
                return false;
            }

            var desc = entry.Baked.GetSubMesh(submesh);
            return TryIntersectLists(
                entry.Vertices, entry.Triangles, (int)desc.indexStart,
                (int)desc.indexCount, matrix, ray, out distance);
        }

        if (!(renderer is MeshRenderer) ||
            !renderer.TryGetComponent<MeshFilter>(out var filter) ||
            filter.sharedMesh == null)
        {
            return false;
        }

        var data = submesh < 0
            ? EnsureStatic(filter.sharedMesh)
            : EnsureSubmesh(filter.sharedMesh, submesh);
        if (data == null)
        {
            return false;
        }

        return TryIntersectArrays(
            data.Vertices, data.Triangles, renderer.transform.localToWorldMatrix,
            ray, out distance);
    }

    // Никакого editor-исключения: GetVertices/GetTriangles блокируются на
    // нечитаемом меше И В РЕДАКТОРЕ (проверено кустом Leaf/log_01 — спам
    // ошибок в консоль на каждый сабмеш). Нечитаемый меш честно выпадает из
    // точного пикинга; лекарство — Read/Write в импортёре ассета.
    private static bool IsReadable(Mesh mesh) => mesh.isReadable;

    private static MeshData? EnsureStatic(Mesh mesh)
    {
        if (StaticCache.TryGetValue(mesh, out var cached))
        {
            return cached.Triangles.Length > 0 ? cached : null;
        }

        var data = new MeshData();
        if (IsReadable(mesh))
        {
            mesh.GetVertices(VertexScratch);
            data.Vertices = VertexScratch.ToArray();
            TriangleScratch.Clear();
            AppendAllSubmeshes(mesh, TriangleScratch);
            data.Triangles = TriangleScratch.ToArray();
        }

        // Нечитаемый меш кэшируется пустым, чтобы не спрашивать его каждый кадр.
        StaticCache[mesh] = data;
        return data.Triangles.Length > 0 ? data : null;
    }

    private static MeshData? EnsureSubmesh(Mesh mesh, int submesh)
    {
        var key = (mesh, submesh);
        if (SubmeshCache.TryGetValue(key, out var cached))
        {
            return cached.Triangles.Length > 0 ? cached : null;
        }

        var data = new MeshData();
        if (IsReadable(mesh) && submesh >= 0 && submesh < mesh.subMeshCount &&
            mesh.GetSubMesh(submesh).topology == MeshTopology.Triangles)
        {
            mesh.GetVertices(VertexScratch);
            data.Vertices = VertexScratch.ToArray();
            TriangleScratch.Clear();
            mesh.GetTriangles(TriangleScratch, submesh, applyBaseVertex: true);
            data.Triangles = TriangleScratch.ToArray();
        }

        SubmeshCache[key] = data;
        return data.Triangles.Length > 0 ? data : null;
    }

    // GetTriangles(List<int>, …) замещает список, поэтому сабмеши собираются
    // аллоцирующей перегрузкой — это единожды на меш, дальше живёт кэш.
    private static void AppendAllSubmeshes(Mesh mesh, List<int> destination)
    {
        for (var i = 0; i < mesh.subMeshCount; i++)
        {
            if (mesh.GetSubMesh(i).topology != MeshTopology.Triangles)
            {
                continue;
            }

            destination.AddRange(mesh.GetTriangles(i, applyBaseVertex: true));
        }
    }

    private static BakeEntry? EnsureBaked(SkinnedMeshRenderer renderer)
    {
        var source = renderer.sharedMesh;
        if (source == null)
        {
            return null;
        }

        if (!BakeCache.TryGetValue(renderer, out var entry))
        {
            SweepDeadBakes();
            entry = new BakeEntry();
            BakeCache[renderer] = entry;
        }

        var now = Time.unscaledTime;
        if (now - entry.BakedAt >= BakeIntervalSeconds ||
            !ReferenceEquals(entry.SourceMesh, source))
        {
            entry.Baked.Clear();
            renderer.BakeMesh(entry.Baked, true);
            entry.Baked.GetVertices(entry.Vertices);
            // Топология меняется только со сменой исходного меша (ампутация
            // подменяет sharedMesh) — индексы перечитываются лишь тогда.
            if (!ReferenceEquals(entry.SourceMesh, source) ||
                entry.Triangles.Count == 0)
            {
                entry.Triangles.Clear();
                for (var i = 0; i < entry.Baked.subMeshCount; i++)
                {
                    if (entry.Baked.GetSubMesh(i).topology != MeshTopology.Triangles)
                    {
                        continue;
                    }

                    entry.Triangles.AddRange(
                        entry.Baked.GetTriangles(i, applyBaseVertex: true));
                }
            }

            entry.SourceMesh = source;
            entry.BakedAt = now;
        }

        return entry.Triangles.Count > 0 ? entry : null;
    }

    private static void SweepDeadBakes()
    {
        if (BakeCache.Count < BakeCacheSweepThreshold)
        {
            return;
        }

        DeadKeyScratch.Clear();
        foreach (var pair in BakeCache)
        {
            if (pair.Key == null)
            {
                DeadKeyScratch.Add(pair.Key);
                Object.Destroy(pair.Value.Baked);
            }
        }

        foreach (var key in DeadKeyScratch)
        {
            BakeCache.Remove(key);
        }

        DeadKeyScratch.Clear();
    }

    private static bool TryIntersectArrays(
        Vector3[] vertices, int[] triangles, Matrix4x4 localToWorld, Ray ray,
        out float distance)
    {
        // Луч переводится в локальные координаты. Bug #299: направление здесь
        // НОРМИРУЕТСЯ. Крошечный авторский меш (доска — сантиметры в файле,
        // ObjectFit компенсирует масштабом ×77) сжимал и направление луча, и
        // рёбра треугольников, и детерминант Мёллера-Трумбора проваливался под
        // абсолютный эпсилон «луч параллелен» на КАЖДОМ треугольнике — доска
        // была непикаемой при идеальном попадании в габарит. С нормированным
        // направлением локальный t — в локальных единицах; мировая дистанция
        // восстанавливается делением на длину локального направления.
        var inverse = localToWorld.inverse;
        var origin = inverse.MultiplyPoint3x4(ray.origin);
        var direction = inverse.MultiplyVector(ray.direction);
        var directionScale = direction.magnitude;
        if (directionScale <= 0f)
        {
            distance = float.MaxValue;
            return false;
        }

        direction /= directionScale;
        distance = float.MaxValue;
        var hit = false;
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            if (RayTriangle(origin, direction,
                    vertices[triangles[i]], vertices[triangles[i + 1]],
                    vertices[triangles[i + 2]], out var t) && t / directionScale < distance)
            {
                distance = t / directionScale;
                hit = true;
            }
        }

        return hit;
    }

    private static bool TryIntersectLists(
        List<Vector3> vertices, List<int> triangles, int indexStart, int indexCount,
        Matrix4x4 localToWorld, Ray ray, out float distance)
    {
        // Bug #299: та же нормировка, что в TryIntersectArrays — запечённый
        // скиннинг живёт без масштаба (TRS без scale), так что здесь она
        // тождественна, но пусть оба пути держат один инвариант.
        var inverse = localToWorld.inverse;
        var origin = inverse.MultiplyPoint3x4(ray.origin);
        var direction = inverse.MultiplyVector(ray.direction);
        var directionScale = direction.magnitude;
        if (directionScale <= 0f)
        {
            distance = float.MaxValue;
            return false;
        }

        direction /= directionScale;
        distance = float.MaxValue;
        var hit = false;
        var end = Mathf.Min(indexStart + indexCount, triangles.Count);
        for (var i = indexStart; i + 2 < end; i += 3)
        {
            if (RayTriangle(origin, direction,
                    vertices[triangles[i]], vertices[triangles[i + 1]],
                    vertices[triangles[i + 2]], out var t) && t / directionScale < distance)
            {
                distance = t / directionScale;
                hit = true;
            }
        }

        return hit;
    }

    // Мёллер–Трумбор без отсечения задних граней: в открытый меш (юбка, навес)
    // луч может войти и с изнанки, а мышь — не пуля, промах здесь дороже.
    private static bool RayTriangle(
        in Vector3 origin, in Vector3 direction,
        in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0f;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var det = Vector3.Dot(edge1, p);
        // Bug #299: эпсилон абсолютный, а det масштабируется квадратом длины
        // ребра — у сантиметрового авторского меша он ~1e-6..1e-8 даже при
        // нормированном направлении. 1e-12 оставляет отсев честно вырожденных
        // треугольников; почти-параллельный луч с ненулевым det отфильтруют
        // проверки u/v/t ниже.
        if (det > -1e-12f && det < 1e-12f)
        {
            return false;
        }

        var invDet = 1f / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        var q = Vector3.Cross(s, edge1);
        var v = Vector3.Dot(direction, q) * invDet;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        t = Vector3.Dot(edge2, q) * invDet;
        return t > 0f;
    }
}

}
