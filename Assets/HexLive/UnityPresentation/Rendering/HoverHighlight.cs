#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// §118: «объект под курсором» — как он подсвечивается. Интерфейс существует
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
/// Дешёвая подсветка «пока нет аутлайнера»: осветляет базовый цвет через
/// <see cref="MaterialPropertyBlock"/>.
///
/// Три решения, за каждым — грабли:
/// <list type="bullet">
/// <item>БЛОК, а не смена материала: материал общий на все кокосы острова, и
/// подмена засветила бы разом все.</item>
/// <item>Базовый цвет, а не эмиссия: ключевое слово <c>_EMISSION</c> живёт на
/// МАТЕРИАЛЕ, и включить его пришлось бы через <c>renderer.material</c> — то
/// есть породить копию материала на каждый подсвеченный объект и потерять
/// батчинг. Блоку же <c>_BaseColor</c> URP/Lit подчиняется как есть.</item>
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
    private const float Lift = 0.45f;

    private readonly Dictionary<Renderer, MaterialPropertyBlock> _saved = new();
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

            if (!_saved.ContainsKey(renderer))
            {
                var before = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(before);
                _saved[renderer] = before;
            }

            var material = renderer.sharedMaterial;
            var property = material != null && material.HasProperty(BaseColor)
                ? BaseColor
                : LegacyColor;
            var tint = material != null && material.HasProperty(property)
                ? material.GetColor(property)
                : Color.white;

            renderer.GetPropertyBlock(_scratch);
            _scratch.SetColor(property, Color.Lerp(tint, Color.white, Lift));
            renderer.SetPropertyBlock(_scratch);
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
                renderer.SetPropertyBlock(before);
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
