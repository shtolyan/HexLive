#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// §121: «объект под курсором» — как он подсвечивается. Интерфейс существует
/// ровно затем, чтобы способ подсветки можно было заменить, не трогая ни ввод,
/// ни меню: у игрока куплен полноценный аутлайнер, и когда он приедет в
/// репозиторий, здесь появится вторая реализация, а <see cref="HoverHighlight.Active"/>
/// станет на неё указывать. Один вызов, ни одной правки на местах.
/// </summary>
public interface IHoverHighlighter
{
    void Apply(IReadOnlyList<Renderer> renderers);

    void Clear(IReadOnlyList<Renderer> renderers);
}

/// <summary>Какой подсветкой пользуется игра сейчас.</summary>
public static class HoverHighlight
{
    private static IHoverHighlighter? _active;

    public static IHoverHighlighter Active
    {
        get => _active ??= new EmissionHoverHighlighter();
        set => _active = value;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _active = null;
}

/// <summary>
/// Дешёвая подсветка «пока нет аутлайнера»: поднимает базовый цвет через
/// <see cref="MaterialPropertyBlock"/>.
///
/// Решения, за каждым — грабли:
/// <list type="bullet">
/// <item>БЛОК, а не смена материала: материал общий на все кокосы острова, и
/// подмена засветила бы разом все.</item>
/// <item>Базовый цвет, а не эмиссия: ключевое слово <c>_EMISSION</c> живёт на
/// МАТЕРИАЛЕ, и включить его пришлось бы через <c>renderer.material</c> — то
/// есть породить копию материала на каждый подсвеченный объект и потерять
/// батчинг. Блоку же <c>_BaseColor</c> URP/Lit подчиняется как есть.</item>
/// <item>⭐ Подъём — «умножить и добавить», НЕ лерп к белому: у половины
/// каталога (_AiGen-группа, FBX с цветом в текстуре) <c>_BaseColor</c> белый,
/// и лерп белого к белому давал НЕВИДИМУЮ подсветку при работающем клике
/// (юка). URP/Lit умножает текстуру на цвет, так что множитель &gt; 1
/// осветляет и белые текстурированные материалы.</item>
/// <item>Блоки пишутся ПО ИНДЕКСУ материала: одежда на земле держит свои
/// per-index блоки (GarmentWorldCondition), которые перекрывают общий блок
/// рендерера — общий тинт на ней не проявлялся вовсе.</item>
/// <item>Восстанавливается ИСХОДНЫЙ блок, а не «белый»: у части видов в блоке
/// уже что-то лежит (мокрая одежда, кровь), и сброс в умолчание стёр бы это.</item>
/// </list>
/// </summary>
public sealed class EmissionHoverHighlighter : IHoverHighlighter
{
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColor = Shader.PropertyToID("_Color");

    // Осветление, не перекраска: предмет обязан остаться собой, просто
    // «поднятым» — иначе подсветка читается как другой объект.
    private const float LiftScale = 1.25f;
    private const float LiftAdd = 0.18f;

    private readonly Dictionary<Renderer, MaterialPropertyBlock[]> _saved = new();
    private readonly MaterialPropertyBlock _scratch = new();

    public void Apply(IReadOnlyList<Renderer> renderers)
    {
        for (var i = 0; i < renderers.Count; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            var materials = renderer.sharedMaterials;
            if (!_saved.ContainsKey(renderer))
            {
                var before = new MaterialPropertyBlock[materials.Length];
                for (var m = 0; m < materials.Length; m++)
                {
                    before[m] = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(before[m], m);
                }

                _saved[renderer] = before;
            }

            for (var m = 0; m < materials.Length; m++)
            {
                var material = materials[m];
                var property = material != null && material.HasProperty(BaseColor)
                    ? BaseColor
                    : LegacyColor;

                renderer.GetPropertyBlock(_scratch, m);
                // Цвет мог уже лежать в блоке (мокрая одежда) — он и есть
                // текущая правда; материал — только запасной источник.
                var tint = _scratch.HasColor(property)
                    ? _scratch.GetColor(property)
                    : material != null && material.HasProperty(property)
                        ? material.GetColor(property)
                        : Color.white;
                var lifted = new Color(
                    tint.r * LiftScale + LiftAdd,
                    tint.g * LiftScale + LiftAdd,
                    tint.b * LiftScale + LiftAdd,
                    tint.a);
                _scratch.SetColor(property, lifted);
                renderer.SetPropertyBlock(_scratch, m);
            }
        }
    }

    public void Clear(IReadOnlyList<Renderer> renderers)
    {
        for (var i = 0; i < renderers.Count; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            if (_saved.TryGetValue(renderer, out var before))
            {
                var count = Mathf.Min(before.Length, renderer.sharedMaterials.Length);
                for (var m = 0; m < count; m++)
                {
                    renderer.SetPropertyBlock(before[m], m);
                }

                _saved.Remove(renderer);
            }
            else
            {
                renderer.SetPropertyBlock(null);
            }
        }
    }
}

}
