using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.WardrobeTest
{

// §31B.4F: runtime-гизмо «как в Unity» для подгонки посадки головных уборов в
// WardrobeTest. Стрелки — перемещение по оси, квадратики — по плоскости,
// кольца — вращение, кубики на осях — масштаб по компоненте, центральный куб —
// равномерный масштаб. Оси гизмо = локальные оси целевой кости (режим Local).
//
// Гизмо правит ЛОКАЛЬНУЮ позу целевой кости напрямую и после каждого кадра
// драга зовёт TargetEdited — панель считывает позу в HeadwearFit и пишет её в
// префаб-ассет. Рисуется поверх всего (HexLive/WardrobeGizmo, ZTest Always):
// кость head сидит внутри шлема, обычная глубина спрятала бы половину ручек.
//
// Пикинг — своя математика луч-против-ручки (порог в долях экранного размера
// гизмо), без коллайдеров: паттерн тот же, что у TickHitClick в бутстрапе.
public sealed class WardrobeFitGizmo : MonoBehaviour
{
    public enum Mode { None, Move, Rotate, Scale }

    public WardrobeTestBootstrap Owner;
    public Camera Cam;

    // После каждого кадра, в котором драг изменил целевую кость.
    public event Action TargetEdited;

    private enum Part
    {
        None,
        MoveX, MoveY, MoveZ,      // стрелки
        PlaneX, PlaneY, PlaneZ,   // квадратики; нормаль = ось имени
        RingX, RingY, RingZ,      // кольца вращения
        ScaleX, ScaleY, ScaleZ,   // кубики на осях
        ScaleUniform,             // центральный куб
    }

    private sealed class Handle
    {
        public Color BaseColor;
        public GameObject Root;
        public readonly List<Material> Materials = new();
    }

    // Габариты в долях размера гизмо (умножаются на экранно-постоянный s).
    private const float AxisLength = 1f;
    private const float ShaftRadius = 0.015f;
    private const float ConeLength = 0.2f;
    private const float ConeRadius = 0.06f;
    private const float PlaneMin = 0.28f;
    private const float PlaneMax = 0.48f;
    private const float RingRadius = 0.8f;
    private const float RingWidth = 0.02f;
    private const float ScaleAxisLength = 0.8f;
    private const float CubeSize = 0.12f;
    private const float PickAxis = 0.09f;   // порог захвата оси/стрелки
    private const float PickRing = 0.08f;   // порог захвата кольца
    private const float ScreenScale = 0.17f; // s = дистанция до камеры * это

    private static readonly Color AxisXColor = new(0.91f, 0.26f, 0.22f);
    private static readonly Color AxisYColor = new(0.42f, 0.83f, 0.25f);
    private static readonly Color AxisZColor = new(0.26f, 0.51f, 0.95f);
    private static readonly Color UniformColor = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Color HoverColor = new(1f, 0.85f, 0.2f);

    private readonly Dictionary<Part, Handle> _handles = new();
    private GameObject _moveRoot;
    private GameObject _rotateRoot;
    private GameObject _scaleRoot;
    private Shader _shader;

    private Transform _target;
    private Mode _mode = Mode.Move;

    private Part _hover = Part.None;
    private Part _drag = Part.None;

    // Снимок на старте драга: вся математика идёт от него, а не от текущей
    // (уже подвинутой) позы — иначе ошибка накапливается кадр за кадром.
    private Vector3 _startGizmoPos;
    private Quaternion _startFrame;
    private Vector3 _startBonePos;      // мировая
    private Quaternion _startBoneRot;   // мировая
    private Vector3 _startBoneScale;    // локальная
    private float _startSize;
    private float _startParam;          // t вдоль оси
    private Vector3 _startPlanePoint;
    private Vector2 _startMouse;

    public Mode CurrentMode => _mode;

    // Тест ран в бутстрапе не должен ловить клик, который достался гизмо.
    public bool PointerBusy => _target != null && _mode != Mode.None &&
        (_drag != Part.None || _hover != Part.None);

    private void Awake()
    {
        _shader = Shader.Find("HexLive/WardrobeGizmo");
        if (_shader == null)
        {
            // Без своего шейдера ручки прячутся за мешем, но гизмо работает.
            _shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        _moveRoot = MakeRoot("Move");
        _rotateRoot = MakeRoot("Rotate");
        _scaleRoot = MakeRoot("Scale");

        BuildMoveHandles();
        BuildRotateHandles();
        BuildScaleHandles();
        UpdateVisibility();
    }

    public void SetTarget(Transform target)
    {
        _target = target;
        if (target == null)
        {
            _drag = Part.None;
            SetHover(Part.None);
        }

        UpdateVisibility();
    }

    public void SetMode(Mode mode)
    {
        _mode = mode;
        _drag = Part.None;
        SetHover(Part.None);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var visible = _target != null && _mode != Mode.None;
        _moveRoot.SetActive(visible && _mode == Mode.Move);
        _rotateRoot.SetActive(visible && _mode == Mode.Rotate);
        _scaleRoot.SetActive(visible && _mode == Mode.Scale);
    }

    private void LateUpdate()
    {
        if (_target == null || _mode == Mode.None || Cam == null)
        {
            return;
        }

        // Гизмо едет вместе с костью (и вращается с ней — режим Local);
        // размер экранно-постоянный. Камера двигается тоже в LateUpdate, так
        // что дистанция может отстать на кадр — для ручной подгонки неважно.
        transform.SetPositionAndRotation(_target.position, _target.rotation);
        var size = Vector3.Distance(Cam.transform.position, transform.position) * ScreenScale;
        transform.localScale = Vector3.one * Mathf.Max(0.001f, size);

        TickPointer();
    }

    private void TickPointer()
    {
        var mouse = Mouse.current;
        if (mouse == null)
        {
            return;
        }

        var position = mouse.position.ReadValue();

        if (_drag != Part.None)
        {
            if (!mouse.leftButton.isPressed)
            {
                _drag = Part.None;
                return;
            }

            DragTo(position);
            return;
        }

        var overUi = Owner != null && Owner.IsPointerOverUi(position);
        var pick = overUi ? Part.None : Pick(Cam.ScreenPointToRay(position));
        SetHover(pick);

        if (pick != Part.None && mouse.leftButton.wasPressedThisFrame)
        {
            BeginDrag(pick, position);
        }
    }

    // ---- пикинг ----

    private Part Pick(Ray ray)
    {
        var s = transform.localScale.x;
        var origin = transform.position;
        var best = Part.None;
        var bestScore = 1f; // score = дистанция/порог; < 1 — попадание

        void Consider(Part part, float distance, float threshold)
        {
            var score = distance / Mathf.Max(1e-6f, threshold);
            if (score < bestScore)
            {
                bestScore = score;
                best = part;
            }
        }

        switch (_mode)
        {
            case Mode.Move:
                Consider(Part.MoveX, AxisDistance(ray, origin, CurrentAxis(0), AxisLength * s), PickAxis * s);
                Consider(Part.MoveY, AxisDistance(ray, origin, CurrentAxis(1), AxisLength * s), PickAxis * s);
                Consider(Part.MoveZ, AxisDistance(ray, origin, CurrentAxis(2), AxisLength * s), PickAxis * s);
                for (var i = 0; i < 3; i++)
                {
                    // Внутри квадратика — почти нулевой score, плоскость
                    // выигрывает у проходящих рядом осей (как в Unity).
                    if (PlaneQuadHit(ray, i, s))
                    {
                        Consider(PlanePart(i), 0f, 1f);
                    }
                }

                break;

            case Mode.Rotate:
                for (var i = 0; i < 3; i++)
                {
                    if (RingDistance(ray, i, s, out var distance))
                    {
                        Consider(RingPart(i), distance, PickRing * s);
                    }
                }

                break;

            case Mode.Scale:
                for (var i = 0; i < 3; i++)
                {
                    Consider(ScalePart(i),
                        AxisDistance(ray, origin, CurrentAxis(i), ScaleAxisLength * s), PickAxis * s);
                }

                Consider(Part.ScaleUniform, DistanceToRay(ray, origin), CubeSize * 1.2f * s);
                break;
        }

        return best;
    }

    // Дистанция от луча до отрезка оси [origin, origin + dir * length].
    private static float AxisDistance(Ray ray, Vector3 origin, Vector3 dir, float length)
    {
        ClosestParams(ray, origin, dir, out var t, out _);
        t = Mathf.Clamp(t, 0f, length);
        return DistanceToRay(ray, origin + dir * t);
    }

    // Параметры ближайших точек прямой (origin + dir*t) и луча.
    private static void ClosestParams(Ray ray, Vector3 origin, Vector3 dir, out float t, out float rayT)
    {
        var w0 = origin - ray.origin;
        var a = Vector3.Dot(dir, dir);
        var b = Vector3.Dot(dir, ray.direction);
        var c = Vector3.Dot(ray.direction, ray.direction);
        var d = Vector3.Dot(dir, w0);
        var e = Vector3.Dot(ray.direction, w0);
        var denom = a * c - b * b;
        if (Mathf.Abs(denom) < 1e-8f)
        {
            // Ось смотрит вдоль луча — двигать по ней из этого ракурса нельзя.
            t = 0f;
            rayT = e / Mathf.Max(1e-8f, c);
            return;
        }

        t = (b * e - c * d) / denom;
        rayT = (a * e - b * d) / denom;
    }

    private static float DistanceToRay(Ray ray, Vector3 point)
    {
        var t = Mathf.Max(0f, Vector3.Dot(point - ray.origin, ray.direction));
        return Vector3.Distance(ray.origin + ray.direction * t, point);
    }

    private bool PlaneQuadHit(Ray ray, int axis, float s)
    {
        if (!IntersectPlane(ray, transform.position, CurrentAxis(axis), out var point))
        {
            return false;
        }

        var local = point - transform.position;
        var u = Vector3.Dot(local, CurrentAxis((axis + 1) % 3));
        var v = Vector3.Dot(local, CurrentAxis((axis + 2) % 3));
        return u >= PlaneMin * s && u <= PlaneMax * s && v >= PlaneMin * s && v <= PlaneMax * s;
    }

    private bool RingDistance(Ray ray, int axis, float s, out float distance)
    {
        distance = float.MaxValue;
        if (!IntersectPlane(ray, transform.position, CurrentAxis(axis), out var point))
        {
            return false; // кольцо ребром к камере из этого ракурса не взять
        }

        distance = Mathf.Abs(Vector3.Distance(point, transform.position) - RingRadius * s);
        return true;
    }

    private static bool IntersectPlane(Ray ray, Vector3 origin, Vector3 normal, out Vector3 point)
    {
        point = default;
        var denom = Vector3.Dot(normal, ray.direction);
        if (Mathf.Abs(denom) < 1e-4f)
        {
            return false;
        }

        var t = Vector3.Dot(origin - ray.origin, normal) / denom;
        if (t < 0f)
        {
            return false;
        }

        point = ray.origin + ray.direction * t;
        return true;
    }

    // ---- драг ----

    private void BeginDrag(Part part, Vector2 mousePosition)
    {
        _drag = part;
        _startGizmoPos = transform.position;
        _startFrame = transform.rotation;
        _startBonePos = _target.position;
        _startBoneRot = _target.rotation;
        _startBoneScale = _target.localScale;
        _startSize = transform.localScale.x;
        _startMouse = mousePosition;

        var ray = Cam.ScreenPointToRay(mousePosition);
        switch (part)
        {
            case Part.MoveX:
            case Part.MoveY:
            case Part.MoveZ:
            case Part.ScaleX:
            case Part.ScaleY:
            case Part.ScaleZ:
                ClosestParams(ray, _startGizmoPos, StartAxis(AxisIndex(part)), out _startParam, out _);
                break;

            case Part.PlaneX:
            case Part.PlaneY:
            case Part.PlaneZ:
            case Part.RingX:
            case Part.RingY:
            case Part.RingZ:
                IntersectPlane(ray, _startGizmoPos, StartAxis(AxisIndex(part)), out _startPlanePoint);
                break;
        }
    }

    private void DragTo(Vector2 mousePosition)
    {
        var ray = Cam.ScreenPointToRay(mousePosition);
        var axis = AxisIndex(_drag);
        var moved = false;

        switch (_drag)
        {
            case Part.MoveX:
            case Part.MoveY:
            case Part.MoveZ:
            {
                var dir = StartAxis(axis);
                ClosestParams(ray, _startGizmoPos, dir, out var t, out _);
                _target.position = _startBonePos + dir * (t - _startParam);
                moved = true;
                break;
            }

            case Part.PlaneX:
            case Part.PlaneY:
            case Part.PlaneZ:
            {
                if (IntersectPlane(ray, _startGizmoPos, StartAxis(axis), out var point))
                {
                    _target.position = _startBonePos + (point - _startPlanePoint);
                    moved = true;
                }

                break;
            }

            case Part.RingX:
            case Part.RingY:
            case Part.RingZ:
            {
                var normal = StartAxis(axis);
                if (IntersectPlane(ray, _startGizmoPos, normal, out var point))
                {
                    var v0 = _startPlanePoint - _startGizmoPos;
                    var v1 = point - _startGizmoPos;
                    if (v0.sqrMagnitude > 1e-8f && v1.sqrMagnitude > 1e-8f)
                    {
                        var angle = Vector3.SignedAngle(v0, v1, normal);
                        _target.rotation = Quaternion.AngleAxis(angle, normal) * _startBoneRot;
                        moved = true;
                    }
                }

                break;
            }

            case Part.ScaleX:
            case Part.ScaleY:
            case Part.ScaleZ:
            {
                // Оси гизмо = оси кости, так что компонента i локального
                // масштаба соответствует ручке i без пересчёта базиса.
                var dir = StartAxis(axis);
                ClosestParams(ray, _startGizmoPos, dir, out var t, out _);
                var factor = 1f + (t - _startParam) / Mathf.Max(0.001f, ScaleAxisLength * _startSize);
                factor = Mathf.Clamp(factor, 0.05f, 20f);
                var scale = _startBoneScale;
                scale[axis] = _startBoneScale[axis] * factor;
                _target.localScale = scale;
                moved = true;
                break;
            }

            case Part.ScaleUniform:
            {
                var factor = Mathf.Clamp(
                    1f + (mousePosition.x - _startMouse.x) * 0.004f, 0.05f, 20f);
                _target.localScale = _startBoneScale * factor;
                moved = true;
                break;
            }
        }

        if (moved)
        {
            TargetEdited?.Invoke();
        }
    }

    // ---- подсветка ----

    private void SetHover(Part part)
    {
        if (part == _hover)
        {
            return;
        }

        Tint(_hover, false);
        _hover = part;
        Tint(_hover, true);
    }

    private void Tint(Part part, bool hot)
    {
        if (part == Part.None || !_handles.TryGetValue(part, out var handle))
        {
            return;
        }

        var color = hot
            ? new Color(HoverColor.r, HoverColor.g, HoverColor.b, handle.BaseColor.a)
            : handle.BaseColor;
        foreach (var material in handle.Materials)
        {
            material.color = color;
        }
    }

    // ---- оси ----

    private static int AxisIndex(Part part)
    {
        switch (part)
        {
            case Part.MoveX:
            case Part.PlaneX:
            case Part.RingX:
            case Part.ScaleX:
                return 0;
            case Part.MoveY:
            case Part.PlaneY:
            case Part.RingY:
            case Part.ScaleY:
                return 1;
            default:
                return 2;
        }
    }

    private static Part PlanePart(int axis) =>
        axis == 0 ? Part.PlaneX : axis == 1 ? Part.PlaneY : Part.PlaneZ;

    private static Part RingPart(int axis) =>
        axis == 0 ? Part.RingX : axis == 1 ? Part.RingY : Part.RingZ;

    private static Part ScalePart(int axis) =>
        axis == 0 ? Part.ScaleX : axis == 1 ? Part.ScaleY : Part.ScaleZ;

    private static Vector3 AxisVector(int axis) =>
        axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

    private Vector3 CurrentAxis(int axis) => transform.rotation * AxisVector(axis);

    private Vector3 StartAxis(int axis) => _startFrame * AxisVector(axis);

    // ---- геометрия ручек ----

    private GameObject MakeRoot(string rootName)
    {
        var go = new GameObject(rootName);
        go.transform.SetParent(transform, false);
        return go;
    }

    private void BuildMoveHandles()
    {
        var cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
        var quad = PrimitiveMesh(PrimitiveType.Quad);
        var cone = BuildCone(18, ConeRadius, ConeLength);

        BuildArrow(Part.MoveX, Vector3.right, AxisXColor, cylinder, cone);
        BuildArrow(Part.MoveY, Vector3.up, AxisYColor, cylinder, cone);
        BuildArrow(Part.MoveZ, Vector3.forward, AxisZColor, cylinder, cone);

        var planeCenter = (PlaneMin + PlaneMax) * 0.5f;
        var planeSize = PlaneMax - PlaneMin;
        BuildPlaneQuad(Part.PlaneX, new Vector3(0f, planeCenter, planeCenter),
            Quaternion.Euler(0f, 90f, 0f), planeSize, AxisXColor, quad);
        BuildPlaneQuad(Part.PlaneY, new Vector3(planeCenter, 0f, planeCenter),
            Quaternion.Euler(90f, 0f, 0f), planeSize, AxisYColor, quad);
        BuildPlaneQuad(Part.PlaneZ, new Vector3(planeCenter, planeCenter, 0f),
            Quaternion.identity, planeSize, AxisZColor, quad);
    }

    private void BuildRotateHandles()
    {
        var ring = BuildRing(64, RingRadius, RingWidth);
        BuildRingHandle(Part.RingX, Quaternion.Euler(0f, 90f, 0f), AxisXColor, ring);
        BuildRingHandle(Part.RingY, Quaternion.Euler(90f, 0f, 0f), AxisYColor, ring);
        BuildRingHandle(Part.RingZ, Quaternion.identity, AxisZColor, ring);
    }

    private void BuildScaleHandles()
    {
        var cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
        var cube = PrimitiveMesh(PrimitiveType.Cube);

        BuildScaleAxis(Part.ScaleX, Vector3.right, AxisXColor, cylinder, cube);
        BuildScaleAxis(Part.ScaleY, Vector3.up, AxisYColor, cylinder, cube);
        BuildScaleAxis(Part.ScaleZ, Vector3.forward, AxisZColor, cylinder, cube);

        var uniform = NewHandle(Part.ScaleUniform, _scaleRoot, UniformColor);
        AddMesh(uniform, cube, Vector3.zero, Quaternion.identity,
            Vector3.one * (CubeSize * 1.2f), UniformColor);
    }

    private void BuildArrow(Part part, Vector3 axis, Color color, Mesh cylinder, Mesh cone)
    {
        var handle = NewHandle(part, _moveRoot, color);
        var rotation = Quaternion.FromToRotation(Vector3.up, axis);
        var shaftLength = AxisLength - ConeLength;
        // Примитив-цилиндр: высота 2, радиус 0.5 — отсюда пересчёт масштаба.
        AddMesh(handle, cylinder, axis * (shaftLength * 0.5f), rotation,
            new Vector3(ShaftRadius * 2f, shaftLength * 0.5f, ShaftRadius * 2f), color);
        AddMesh(handle, cone, axis * shaftLength, rotation, Vector3.one, color);
    }

    private void BuildPlaneQuad(Part part, Vector3 center, Quaternion rotation, float size,
        Color color, Mesh quad)
    {
        var translucent = color;
        translucent.a = 0.35f;
        var handle = NewHandle(part, _moveRoot, translucent);
        AddMesh(handle, quad, center, rotation, new Vector3(size, size, 1f), translucent);
    }

    private void BuildRingHandle(Part part, Quaternion rotation, Color color, Mesh ring)
    {
        var handle = NewHandle(part, _rotateRoot, color);
        AddMesh(handle, ring, Vector3.zero, rotation, Vector3.one, color);
    }

    private void BuildScaleAxis(Part part, Vector3 axis, Color color, Mesh cylinder, Mesh cube)
    {
        var handle = NewHandle(part, _scaleRoot, color);
        var rotation = Quaternion.FromToRotation(Vector3.up, axis);
        var shaftLength = ScaleAxisLength - CubeSize * 0.5f;
        AddMesh(handle, cylinder, axis * (shaftLength * 0.5f), rotation,
            new Vector3(ShaftRadius * 2f, shaftLength * 0.5f, ShaftRadius * 2f), color);
        AddMesh(handle, cube, axis * ScaleAxisLength, rotation, Vector3.one * CubeSize, color);
    }

    private Handle NewHandle(Part part, GameObject parent, Color baseColor)
    {
        var handle = new Handle { BaseColor = baseColor };
        handle.Root = new GameObject(part.ToString());
        handle.Root.transform.SetParent(parent.transform, false);
        _handles[part] = handle;
        return handle;
    }

    private void AddMesh(Handle handle, Mesh mesh, Vector3 localPos, Quaternion localRot,
        Vector3 localScale, Color color)
    {
        var go = new GameObject("mesh");
        go.transform.SetParent(handle.Root.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = localScale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        var material = new Material(_shader);
        material.color = color;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        handle.Materials.Add(material);
    }

    // Примитив создаётся только ради sharedMesh и тут же удаляется.
    private static Mesh PrimitiveMesh(PrimitiveType type)
    {
        var go = GameObject.CreatePrimitive(type);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;
        Destroy(go);
        return mesh;
    }

    // Конус вдоль +Y с крышкой основания. Cull Off — обход не важен.
    private static Mesh BuildCone(int segments, float radius, float height)
    {
        var vertices = new Vector3[segments + 2];
        vertices[0] = new Vector3(0f, height, 0f);
        vertices[segments + 1] = Vector3.zero;
        for (var i = 0; i < segments; i++)
        {
            var a = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
        }

        var triangles = new int[segments * 6];
        for (var i = 0; i < segments; i++)
        {
            var a = i + 1;
            var b = (i + 1) % segments + 1;
            triangles[i * 6 + 0] = 0;
            triangles[i * 6 + 1] = b;
            triangles[i * 6 + 2] = a;
            triangles[i * 6 + 3] = segments + 1;
            triangles[i * 6 + 4] = a;
            triangles[i * 6 + 5] = b;
        }

        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        return mesh;
    }

    // Плоское кольцо в плоскости XY (нормаль +Z), шириной 2*width.
    private static Mesh BuildRing(int segments, float radius, float width)
    {
        var vertices = new Vector3[segments * 2];
        for (var i = 0; i < segments; i++)
        {
            var a = i * Mathf.PI * 2f / segments;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            vertices[i * 2] = dir * (radius - width);
            vertices[i * 2 + 1] = dir * (radius + width);
        }

        var triangles = new int[segments * 6];
        for (var i = 0; i < segments; i++)
        {
            var i0 = i * 2;
            var i1 = i * 2 + 1;
            var j0 = (i + 1) % segments * 2;
            var j1 = (i + 1) % segments * 2 + 1;
            triangles[i * 6 + 0] = i0;
            triangles[i * 6 + 1] = j0;
            triangles[i * 6 + 2] = i1;
            triangles[i * 6 + 3] = i1;
            triangles[i * 6 + 4] = j0;
            triangles[i * 6 + 5] = j1;
        }

        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        return mesh;
    }
}

}
