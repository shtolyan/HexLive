using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// §135: конечность В ЗУБАХ зверя. Геометрия — та же, что у упавшей конечности
/// (<see cref="SeveredLimbFactory"/>, §50.5): срез с канонического FBX хозяйки.
/// Разница только в якоре — не узел земли, а пасть.
/// <para>
/// ⭐ Держатель висит на КОРНЕ вида зверя, а не на кости морды, и каждый кадр
/// сам берёт позицию от кости, а поворот — от корня. Причина та же, что у
/// каблуков (HEEL_POSE_SPEC §2): локальные оси кости после импорта FBX
/// переставлены как угодно, так что «прицепить с локальным поворотом» — это
/// ставка, а не работа. Позиция от кости даёт покачивание головы, поворот от
/// корня — предсказуемую ориентацию.
/// </para>
/// </summary>
public sealed class MobCarriedLimbView : MonoBehaviour
{
    // Сколько кадров подряд пробуем собрать конечность, пока вид хозяйки не
    // появится в сцене. Её могло ещё не быть при первом кадре (реконнект,
    // порядок синхронизации видов), но ждать вечно тоже незачем.
    private const int ResolveAttempts = 120;

    // Смещение от кости морды в локальных осях КОРНЯ зверя, в долях мерки
    // размера (вид передаёт HexRadius — волк примерно с гекс длиной): вперёд
    // от головы и немного вниз, как ноша висит в пасти.
    private static readonly Vector3 MouthOffsetSizeUnits = new(0f, -0.06f, 0.10f);

    private int _ownerNpcId = -1;
    private string _variant = string.Empty;
    private float _referenceScale = 1f;
    private float _sizeUnit = 1f;

    private Transform _mouth;
    private Transform _holder;
    private GameObject _visual;
    private Mesh _ownedMesh;
    private Material _ownedMaterial;
    private int _attemptsLeft;

    /// <summary>Масштаб среза — тот же, что у упавшей конечности (§50.5).</summary>
    public void Configure(float referenceScale, float sizeUnit)
    {
        _referenceScale = referenceScale;
        _sizeUnit = sizeUnit > 0.001f ? sizeUnit : 1f;
    }

    /// <summary>
    /// Что зверь несёт сейчас: id хозяйки и зона (<c>LegL</c>…). -1/пусто —
    /// пасть свободна. Идемпотентно: тот же набор ничего не пересобирает.
    /// </summary>
    public void SetCarried(int ownerNpcId, string variant)
    {
        variant ??= string.Empty;
        if (ownerNpcId == _ownerNpcId && variant == _variant)
        {
            return;
        }

        Clear();
        _ownerNpcId = ownerNpcId;
        _variant = variant;
        _attemptsLeft = string.IsNullOrEmpty(variant) || ownerNpcId < 0 ? 0 : ResolveAttempts;
    }

    private void LateUpdate()
    {
        if (_visual == null && _attemptsLeft > 0)
        {
            _attemptsLeft--;
            TryBuild();
        }

        if (_holder == null)
        {
            return;
        }

        var anchor = _mouth != null ? _mouth : transform;
        _holder.SetPositionAndRotation(
            anchor.position + transform.TransformDirection(
                MouthOffsetSizeUnits * _sizeUnit),
            transform.rotation * Quaternion.Euler(90f, 0f, 0f));
    }

    private void TryBuild()
    {
        if (!NpcActorView.TryGetLive(_ownerNpcId, out var owner) || owner == null)
        {
            return;
        }

        var visual = SeveredLimbFactory.BuildReference(owner, _variant);
        if (visual == null)
        {
            // FBX хозяйки не читается — рисовать чужую геометрию нельзя, а
            // капсулу в пасти видно как ошибку. Ноша остаётся невидимой, но
            // поведение зверя (§135) от этого не меняется.
            _attemptsLeft = 0;
            Debug.LogWarning($"[§135 limb] нет геометрии '{_variant}' " +
                             $"хозяйки NPC{_ownerNpcId} — зверь несёт её невидимо");
            return;
        }

        _holder = new GameObject($"CarriedLimb {_variant}").transform;
        _holder.SetParent(transform, false);
        visual.transform.SetParent(_holder, false);
        visual.transform.localScale = Vector3.one * _referenceScale;
        _visual = visual;
        _ownedMesh = visual.GetComponentInChildren<MeshFilter>()?.sharedMesh;
        _ownedMaterial = visual.GetComponentInChildren<MeshRenderer>()?.sharedMaterial;
        _mouth = FindMouth(transform);
    }

    // Морда по имени кости: сначала челюсть/пасть, потом голова, потом шея.
    // Ни одна не нашлась (примитивный фолбэк-префаб) — держим у корня.
    private static Transform FindMouth(Transform root)
    {
        Transform jaw = null;
        Transform head = null;
        Transform neck = null;
        foreach (var bone in root.GetComponentsInChildren<Transform>(true))
        {
            var name = bone.name.ToLowerInvariant();
            if (jaw == null && (name.Contains("jaw") || name.Contains("muzzle") ||
                                name.Contains("mouth") || name.Contains("snout")))
            {
                jaw = bone;
            }
            else if (head == null && name.Contains("head"))
            {
                head = bone;
            }
            else if (neck == null && name.Contains("neck"))
            {
                neck = bone;
            }
        }

        return jaw != null ? jaw : head != null ? head : neck;
    }

    private void Clear()
    {
        if (_visual != null)
        {
            Destroy(_visual);
        }

        if (_holder != null)
        {
            Destroy(_holder.gameObject);
        }

        // Меш и материал сделаны в рантайме — это не импортированные ассеты,
        // и уходят они только руками.
        if (_ownedMesh != null)
        {
            Destroy(_ownedMesh);
        }

        if (_ownedMaterial != null)
        {
            Destroy(_ownedMaterial);
        }

        _visual = null;
        _holder = null;
        _mouth = null;
        _ownedMesh = null;
        _ownedMaterial = null;
    }

    private void OnDestroy()
    {
        Clear();
    }
}

}
