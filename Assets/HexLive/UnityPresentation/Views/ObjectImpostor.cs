#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Views
{

/// <summary>Marks the billboard as presentation-only. Hover/click keeps using
/// the hidden authored mesh, so zoom cannot change an object's hit area.</summary>
[DisallowMultipleComponent]
public sealed class ObjectImpostorVisual : MonoBehaviour { }

/// <summary>
/// §150.4: world-space импостор дальнего обзора. Квад (MeshRenderer +
/// MeshFilter с общим читаемым мешем — SpriteRenderer невидим для
/// <see cref="MeshRayPicker"/>) живёт дочерним объектом корня вида в том же
/// мировом якоре, поэтому рассинхрон позиции невозможен по построению.
///
/// Текстура печётся из НАСТОЯЩЕГО вида этого definition: однократно на
/// definitionId и игровой день, на переднем фронте включения дальнего обзора,
/// пока меши ещё видимы. Пекарня переводит иерархию вида на слой Portrait
/// (главная камера его не рисует) и снимает выделенной ортокамерой через
/// RenderPipeline.SubmitRenderRequest — camera.Render() в URP запрещён
/// (см. NpcPortraitCache). Испечённый материал общий на все экземпляры
/// definition, инстансинг включён.
///
/// Скрытие мешей — только forceRenderingOff со save/restore (§150.1). Набор
/// рендереров перечисляется В МОМЕНТ переключения, а не замораживается при
/// создании: одежда и асинхронные префабы, доехавшие позже, не выпадают из
/// профиля.
/// </summary>
[DisallowMultipleComponent]
public sealed class ObjectImpostor : MonoBehaviour
{
    private const int BakeTextureSize = 256;
    private const float BakePitchDegrees = 15f;
    private const float BakeMargin = 1.05f;
    private const float AlphaCutoff = 0.4f;
    private const float NpcDiscSize = 1.0f;
    private const float NpcDiscLift = 1.35f;

    private sealed class BakedImpostor
    {
        public Material? Material;
        public float WorldSize;
        public float CenterLift;
        public int BakedDay = int.MinValue;
    }

    private static readonly Dictionary<string, BakedImpostor> Baked = new();
    private static Mesh? _quadMesh;
    private static Camera? _bakeCamera;
    private static RenderTexture? _bakeTarget;
    private static readonly List<Transform> LayerScratch = new(64);
    private static readonly List<int> SavedLayerScratch = new(64);
    private static readonly List<Renderer> RendererScratch = new(16);

    private string _definitionId = string.Empty;
    private float _placeholderSize = 1f;
    private Color _placeholderTint = new(0.6f, 0.6f, 0.6f);
    private bool _npcMode;
    private Material? _npcMaterial;

    private Transform? _quad;
    private MeshRenderer? _quadRenderer;
    private bool _distant;
    private readonly List<Renderer> _hiddenRenderers = new();
    private readonly List<bool> _hiddenSaved = new();
    private readonly List<Behaviour> _hiddenBehaviours = new();
    private readonly List<bool> _hiddenBehavioursSaved = new();

    /// <summary>Импостор объекта: текстура испечётся из вида definitionId.</summary>
    public static ObjectImpostor Attach(
        GameObject viewRoot, string definitionId, float placeholderSize, Color placeholderTint)
    {
        var impostor = viewRoot.AddComponent<ObjectImpostor>();
        impostor._definitionId = definitionId;
        impostor._placeholderSize = placeholderSize;
        impostor._placeholderTint = placeholderTint;
        impostor.BuildQuad();
        return impostor;
    }

    /// <summary>§150.4: портретный диск человека — текстура приходит снаружи
    /// (NpcPortraitCache), пекарня definition для него не используется.</summary>
    public static ObjectImpostor AttachNpc(GameObject viewRoot, Color factionTint)
    {
        var impostor = viewRoot.AddComponent<ObjectImpostor>();
        impostor._npcMode = true;
        impostor._definitionId = "npc";
        impostor._placeholderSize = NpcDiscSize;
        impostor._placeholderTint = factionTint;
        impostor.BuildQuad();
        impostor._quad!.localPosition = Vector3.up * NpcDiscLift;
        impostor._quad.localScale = Vector3.one * NpcDiscSize;
        return impostor;
    }

    /// <summary>Активен ли сейчас дальний режим этого вида — читает отсечка
    /// кликов SmallProps (§150.4): вид с живым импостором кликабелен.</summary>
    public bool DistantActive => _distant;

    /// <summary>Портрет дня (или цвет фракции, пока снимка нет).</summary>
    public void SetNpcPortrait(Texture? portrait, Color factionTint)
    {
        if (!_npcMode || _quadRenderer == null)
        {
            return;
        }

        _placeholderTint = factionTint;
        _npcMaterial ??= CreateImpostorMaterial(null);
        _npcMaterial.SetTexture(BaseMapId, portrait);
        _npcMaterial.SetColor(BaseColorId, portrait != null ? Color.white : factionTint);
        _quadRenderer.sharedMaterial = _npcMaterial;
    }

    /// <summary>Переключить вид между мешами и импостором. Пекти можно только
    /// здесь, на фронте включения: меши ещё не скрыты.</summary>
    public void SetDistant(bool distant, int gameDay)
    {
        if (_distant == distant || _quad == null || _quadRenderer == null)
        {
            return;
        }

        _distant = distant;
        if (!distant)
        {
            for (var i = 0; i < _hiddenRenderers.Count; i++)
            {
                if (_hiddenRenderers[i] != null)
                {
                    _hiddenRenderers[i].forceRenderingOff = _hiddenSaved[i];
                }
            }

            for (var i = 0; i < _hiddenBehaviours.Count; i++)
            {
                if (_hiddenBehaviours[i] != null)
                {
                    _hiddenBehaviours[i].enabled = _hiddenBehavioursSaved[i];
                }
            }

            _hiddenRenderers.Clear();
            _hiddenSaved.Clear();
            _hiddenBehaviours.Clear();
            _hiddenBehavioursSaved.Clear();
            _quadRenderer.forceRenderingOff = true;
            return;
        }

        if (_npcMode)
        {
            _npcMaterial ??= CreateImpostorMaterial(null);
            if (_npcMaterial.GetTexture(BaseMapId) == null)
            {
                _npcMaterial.SetColor(BaseColorId, _placeholderTint);
            }

            _quadRenderer.sharedMaterial = _npcMaterial;
        }
        else
        {
            var baked = EnsureBaked(gameDay);
            if (baked.Material != null)
            {
                _quadRenderer.sharedMaterial = baked.Material;
                _quad.localScale = Vector3.one * baked.WorldSize;
                _quad.localPosition = Vector3.up * baked.CenterLift;
            }
        }

        HideRealRenderers();
        _quadRenderer.forceRenderingOff = false;
    }

    /// <summary>Один общий кватернион на все квады за кадр — вызывает цикл в
    /// HexWorldRenderer.LateUpdate, а не per-object Update.</summary>
    public void FaceCamera(Quaternion rotation)
    {
        if (_quad != null)
        {
            _quad.rotation = rotation;
        }
    }

    private void HideRealRenderers()
    {
        GetComponentsInChildren(true, RendererScratch);
        for (var i = 0; i < RendererScratch.Count; i++)
        {
            var renderer = RendererScratch[i];
            if (renderer == null || renderer == _quadRenderer)
            {
                continue;
            }

            _hiddenRenderers.Add(renderer);
            _hiddenSaved.Add(renderer.forceRenderingOff);
            renderer.forceRenderingOff = true;
        }

        RendererScratch.Clear();

        // Декали (раны/грязь на коже) — не Renderer: у скрытого тела проектор
        // размазал бы текстуру по террейну под ним.
        foreach (var projector in GetComponentsInChildren<
            UnityEngine.Rendering.Universal.DecalProjector>(true))
        {
            if (projector == null)
            {
                continue;
            }

            _hiddenBehaviours.Add(projector);
            _hiddenBehavioursSaved.Add(projector.enabled);
            projector.enabled = false;
        }
    }

    // ---- Пекарня ----

    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private BakedImpostor EnsureBaked(int gameDay)
    {
        if (!Baked.TryGetValue(_definitionId, out var baked))
        {
            baked = new BakedImpostor
            {
                WorldSize = _placeholderSize,
                CenterLift = _placeholderSize * 0.5f
            };
            Baked[_definitionId] = baked;
        }

        // Портретное правило освещения: свет сцены запечён в текстуру, поэтому
        // снимок переснимается раз в игровые сутки — ночная пальма не должна
        // остаться чёрной на весь день.
        if (baked.Material != null && baked.BakedDay == gameDay)
        {
            return baked;
        }

        if (TryBake(out var texture, out var worldSize, out var centerLift))
        {
            if (baked.Material == null)
            {
                baked.Material = CreateImpostorMaterial(texture);
            }
            else
            {
                var previous = baked.Material.GetTexture(BaseMapId) as Texture2D;
                baked.Material.SetTexture(BaseMapId, texture);
                if (previous != null)
                {
                    Destroy(previous);
                }
            }

            baked.WorldSize = worldSize;
            baked.CenterLift = centerLift;
            baked.BakedDay = gameDay;
        }
        else if (baked.Material == null)
        {
            // Меши ещё не доехали (async-префаб): честная цветная заглушка,
            // следующий фронт включения обзора переснимет по-настоящему.
            var placeholder = CreateImpostorMaterial(null);
            placeholder.SetColor(BaseColorId, _placeholderTint);
            baked.Material = placeholder;
        }

        return baked;
    }

    private bool TryBake(out Texture2D texture, out float worldSize, out float centerLift)
    {
        texture = null!;
        worldSize = 0f;
        centerLift = 0f;

        var bounds = new Bounds();
        var found = false;
        GetComponentsInChildren(true, RendererScratch);
        for (var i = 0; i < RendererScratch.Count; i++)
        {
            var renderer = RendererScratch[i];
            if (renderer == null || renderer == _quadRenderer ||
                renderer is ParticleSystemRenderer)
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

        RendererScratch.Clear();
        if (!found || bounds.size.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        var camera = EnsureBakeCamera(transform.root);
        if (camera == null)
        {
            return false;
        }

        var size = Mathf.Max(bounds.size.y, Mathf.Max(bounds.size.x, bounds.size.z));
        camera.orthographicSize = size * 0.5f * BakeMargin;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = size * 4f + 2f;
        var direction = Quaternion.Euler(BakePitchDegrees, 0f, 0f) * Vector3.forward;
        camera.transform.SetPositionAndRotation(
            bounds.center - direction * (size * 2f + 1f),
            Quaternion.LookRotation(direction));

        // Изоляция на ВЫДЕЛЕННОМ слое PhotoBake, не на Portrait (bug #244):
        // Portrait — жилой слой живой identity-карты, на нём ПОСТОЯННО висит
        // неоновый задник PortraitStage — квад в 20 wu перед лицом выделенной
        // девушки в мировых координатах. Пекарня, снимавшая маской Portrait
        // рядом с объектом, ловила его в кадр, и оба matte-прохода видели
        // одинаковый непрозрачный фон — альфа 255, задник запекался в
        // текстуру импостора. PhotoBake пуст всегда: сюда объект переезжает
        // только внутри этого же синхронного вызова и возвращается в finally.
        var bakeLayer = PhotoBakeLayer();
        LayerScratch.Clear();
        SavedLayerScratch.Clear();
        GetComponentsInChildren(true, LayerScratch);
        foreach (var child in LayerScratch)
        {
            SavedLayerScratch.Add(child.gameObject.layer);
            child.gameObject.layer = bakeLayer;
        }

        try
        {
            var request = new RenderPipeline.StandardRequest();
            if (_bakeTarget == null ||
                !RenderPipeline.SupportsRenderRequest(camera, request))
            {
                return false;
            }

            request.destination = _bakeTarget;
            // URP versions/build targets do not consistently preserve the
            // camera clear alpha in SubmitRenderRequest. Recover coverage from
            // two opaque mattes instead: white-black is exactly (1-alpha), so
            // genuinely black details stay opaque while the background vanishes.
            var black = CaptureMatte(camera, request, Color.black);
            var white = CaptureMatte(camera, request, Color.white);
            if (black == null || white == null || black.Length != white.Length)
            {
                return false;
            }

            var pixels = new Color32[black.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var matteDelta = Mathf.Max(
                    white[i].r - black[i].r,
                    Mathf.Max(white[i].g - black[i].g, white[i].b - black[i].b));
                var alpha = (byte)Mathf.Clamp(255 - matteDelta, 0, 255);
                if (alpha == 0)
                {
                    pixels[i] = new Color32(0, 0, 0, 0);
                    continue;
                }

                // The black-matte pass is premultiplied by coverage. Undo it
                // before mip generation to avoid a dark fringe around cutouts.
                pixels[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(black[i].r * 255f / alpha), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(black[i].g * 255f / alpha), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(black[i].b * 255f / alpha), 0, 255),
                    alpha);
            }

            texture = new Texture2D(BakeTextureSize, BakeTextureSize,
                TextureFormat.RGBA32, mipChain: true)
            {
                name = $"Impostor {_definitionId}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        }
        finally
        {
            for (var i = 0; i < LayerScratch.Count; i++)
            {
                if (LayerScratch[i] != null)
                {
                    LayerScratch[i].gameObject.layer = SavedLayerScratch[i];
                }
            }

            LayerScratch.Clear();
            SavedLayerScratch.Clear();
        }

        worldSize = size;
        centerLift = bounds.center.y - transform.position.y;
        return true;
    }

    private static Color32[]? CaptureMatte(
        Camera camera, RenderPipeline.StandardRequest request, Color background)
    {
        camera.backgroundColor = background;
        RenderPipeline.SubmitRenderRequest(camera, request);

        var previousActive = RenderTexture.active;
        Texture2D? readback = null;
        try
        {
            RenderTexture.active = _bakeTarget;
            readback = new Texture2D(BakeTextureSize, BakeTextureSize,
                TextureFormat.RGBA32, mipChain: false);
            readback.ReadPixels(new Rect(0, 0, BakeTextureSize, BakeTextureSize), 0, 0);
            readback.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return readback.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previousActive;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            if (readback != null)
            {
                Destroy(readback);
            }
        }
    }

    /// <summary>Слой офф-скрин съёмки (bug #244): всегда пустой, объекты
    /// живут на нём только внутри синхронного прохода пекарни. Fallback 30 —
    /// безымянный незанятый индекс на случай устаревшего TagManager.</summary>
    internal static int PhotoBakeLayer()
    {
        var layer = LayerMask.NameToLayer("PhotoBake");
        return layer >= 0 ? layer : 30;
    }

    private static Camera? EnsureBakeCamera(Transform owner)
    {
        if (_bakeCamera != null)
        {
            return _bakeCamera;
        }

        var go = new GameObject("ImpostorBakeCamera");
        go.transform.SetParent(owner, false);
        var camera = go.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.cullingMask = 1 << PhotoBakeLayer();
        camera.allowMSAA = false;
        _bakeTarget = new RenderTexture(BakeTextureSize, BakeTextureSize, 16,
            RenderTextureFormat.ARGB32)
        {
            name = "ImpostorBakeRT"
        };
        camera.targetTexture = _bakeTarget;
        _bakeCamera = camera;
        return camera;
    }

    private static Material CreateImpostorMaterial(Texture2D? texture)
    {
        var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        if (texture != null)
        {
            material.SetTexture(BaseMapId, texture);
        }

        // Cutout: пишет глубину, не сортируется как transparent, дружит с
        // инстансингом — сотни пальм остаются считанными батчами.
        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.One);
        material.SetFloat("_DstBlend", (float)BlendMode.Zero);
        material.SetFloat("_ZWrite", 1f);
        material.SetFloat("_AlphaClip", 1f);
        material.EnableKeyword("_ALPHATEST_ON");
        material.SetFloat("_Cutoff", AlphaCutoff);
        material.SetFloat("_Cull", 0f); // двусторонний квад — ориентация не важна
        material.enableInstancing = true;
        material.renderQueue = (int)RenderQueue.AlphaTest;
        return material;
    }

    private void BuildQuad()
    {
        var go = new GameObject("Impostor");
        go.transform.SetParent(transform, false);
        go.AddComponent<ObjectImpostorVisual>();
        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = EnsureQuadMesh();
        _quadRenderer = go.AddComponent<MeshRenderer>();
        _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _quadRenderer.receiveShadows = false;
        // Стартует скрытым; слой остаётся обычным (не SmallProps!) — иначе
        // layer-cull 45 wu убил бы импостор раньше его порога 64 wu.
        _quadRenderer.forceRenderingOff = true;
        var placeholder = CreateImpostorMaterial(null);
        placeholder.SetColor(BaseColorId, _placeholderTint);
        _quadRenderer.sharedMaterial = placeholder;
        _quad = go.transform;
        _quad.localScale = Vector3.one * _placeholderSize;
        _quad.localPosition = Vector3.up * (_placeholderSize * 0.5f);
    }

    private void OnDestroy()
    {
        // Компонент может сняться отдельно от вида (передача тела в реестр
        // трупов): вернуть рендереры и не оставить осиротевший квад.
        for (var i = 0; i < _hiddenRenderers.Count; i++)
        {
            if (_hiddenRenderers[i] != null)
            {
                _hiddenRenderers[i].forceRenderingOff = _hiddenSaved[i];
            }
        }

        for (var i = 0; i < _hiddenBehaviours.Count; i++)
        {
            if (_hiddenBehaviours[i] != null)
            {
                _hiddenBehaviours[i].enabled = _hiddenBehavioursSaved[i];
            }
        }

        _hiddenRenderers.Clear();
        _hiddenSaved.Clear();
        _hiddenBehaviours.Clear();
        _hiddenBehavioursSaved.Clear();
        if (_quad != null)
        {
            Destroy(_quad.gameObject);
        }
    }

    private static Mesh EnsureQuadMesh()
    {
        if (_quadMesh != null)
        {
            return _quadMesh;
        }

        // Свой меш, а не встроенный Quad: MeshRayPicker требует читаемые
        // треугольники, и так квад гарантированно readable.
        var mesh = new Mesh { name = "ImpostorQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f)
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f)
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _quadMesh = mesh;
        return mesh;
    }
}

}
