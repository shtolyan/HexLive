#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>Каким смыслом подсвечен объект под курсором.</summary>
public enum HoverTone
{
    /// <summary>Обычная вещь — бери и пользуйся.</summary>
    Normal,

    /// <summary>§121: ЧУЖОЕ. Кровать соседки, её фляга. Виден до клика, чтобы
    /// «нельзя» не выяснялось отказом уже после выбора пункта меню.</summary>
    Foreign,
}

/// <summary>
/// §121: «объект под курсором» — как он подсвечивается. Интерфейс существует
/// ровно затем, чтобы способ подсветки можно было заменить, не трогая ни ввод,
/// ни меню: у игрока куплен полноценный аутлайнер, и когда он приедет в
/// репозиторий, здесь появится вторая реализация, а <see cref="HoverHighlight.Active"/>
/// станет на неё указывать. Один вызов, ни одной правки на местах.
/// </summary>
public interface IHoverHighlighter
{
    void Apply(IReadOnlyList<Renderer> renderers, HoverTone tone);

    void Clear(IReadOnlyList<Renderer> renderers);
}

/// <summary>Какой подсветкой пользуется игра сейчас.</summary>
public static class HoverHighlight
{
    private static IHoverHighlighter? _active;

    public static IHoverHighlighter Active
    {
        get => _active ??= new TintHoverHighlighter();
        set => _active = value;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _active = null;
}

/// <summary>
/// Дешёвая подсветка «пока нет аутлайнера»: подмешивает цвет в базовый через
/// <see cref="MaterialPropertyBlock"/>.
///
/// Четыре решения, за каждым — грабли:
/// <list type="bullet">
/// <item>БЛОК, а не смена материала: материал общий на все кокосы острова, и
/// подмена засветила бы разом все.</item>
/// <item>Базовый цвет, а не эмиссия: ключевое слово <c>_EMISSION</c> живёт на
/// МАТЕРИАЛЕ, и включить его пришлось бы через <c>renderer.material</c> — то
/// есть породить копию материала на каждый подсвеченный объект и потерять
/// батчинг. Блоку же <c>_BaseColor</c> URP/Lit подчиняется как есть.</item>
/// <item>⭐ Подмешивается ЦВЕТ, а не белизна. Первая версия делала
/// <c>Lerp(tint, Color.white, 0.45)</c> — и НИЧЕГО не подсвечивала у всей
/// текстурной графики: у одежды, камней, палок, кокосов и пальм
/// <c>_BaseColor</c> и так белый, то есть блок записывал ровно то, что там
/// уже стояло. Светились только объекты с кодовым цветом. Именно это игрок
/// видел как «подсвечивается не всё».</item>
/// <item>Блок пишется ПО ИНДЕКСУ МАТЕРИАЛА, а не на рендерер целиком:
/// <c>GarmentWorldCondition</c> пишет одежде поиндексно, и блок уровня
/// рендерера ему проигрывает — подсветка одежды исчезала бы под собственной
/// грязью.</item>
/// </list>
/// </summary>
public sealed class TintHoverHighlighter : IHoverHighlighter
{
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColor = Shader.PropertyToID("_Color");

    // Тёплое золото — тот же акцент, которым панель метит выбранное.
    private static readonly Color NormalTint = new(1f, 0.78f, 0.36f);

    // Чужое — холодный красноватый. Не «ошибка» (ошибка ничего не значит до
    // клика), а «принадлежит другому»: читается сразу и не спорит с золотым.
    private static readonly Color ForeignTint = new(1f, 0.42f, 0.38f);

    // Насколько подмешивать. Достаточно, чтобы белое стало явно цветным, и
    // недостаточно, чтобы предмет перестал быть собой.
    private const float Mix = 0.55f;

    private readonly Dictionary<Renderer, MaterialPropertyBlock[]> _saved = new();
    private readonly MaterialPropertyBlock _scratch = new();

    public void Apply(IReadOnlyList<Renderer> renderers, HoverTone tone)
    {
        var target = tone == HoverTone.Foreign ? ForeignTint : NormalTint;

        for (var i = 0; i < renderers.Count; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                continue;
            }

            if (!_saved.ContainsKey(renderer))
            {
                var before = new MaterialPropertyBlock[materials.Length];
                for (var m = 0; m < materials.Length; m++)
                {
                    var block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, m);
                    before[m] = block;
                }

                _saved[renderer] = before;
            }

            for (var m = 0; m < materials.Length; m++)
            {
                var material = materials[m];
                var property = material != null && material.HasProperty(BaseColor)
                    ? BaseColor
                    : LegacyColor;
                var tint = material != null && material.HasProperty(property)
                    ? material.GetColor(property)
                    : Color.white;

                renderer.GetPropertyBlock(_scratch, m);
                _scratch.SetColor(property, Color.Lerp(tint, target, Mix));
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
                for (var m = 0; m < before.Length; m++)
                {
                    renderer.SetPropertyBlock(before[m], m);
                }

                _saved.Remove(renderer);
            }
        }
    }
}

}
