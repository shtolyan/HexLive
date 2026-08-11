using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Каталог готовых выражений лица из DAZ-паков (Tools/daz_expression_extract.py).
// У пака нет собственных морфов: каждое выражение — рецепт «стандартный
// G3F-блендшейп → вес в процентах», и эти блендшейпы уже есть на наших
// экспортированных актрисах. Веса бывают отрицательными (расширить глаза =
// минус на EyesClosed) — клампинг легаси-весов в проекте выключен, так что
// отрицательный вес честно экстраполирует морф.
public sealed class FaceExpressionCatalog
{
    [Serializable]
    private sealed class CatalogJson
    {
        public EntryJson[] expressions;
    }

    [Serializable]
    private sealed class EntryJson
    {
        public string name;
        public string label;   // русская подпись (наша, у пака имён нет)
        public ShapeJson[] shapes;
    }

    [Serializable]
    private sealed class ShapeJson
    {
        public string shape;
        public float weight;
    }

    public sealed class Expression
    {
        public string Name;
        public string Label;   // пусто, если подпись не задана
        public (string shape, float weight)[] Shapes;
    }

    public IReadOnlyList<Expression> Expressions => _expressions;
    private readonly List<Expression> _expressions = new();

    public static FaceExpressionCatalog Load(string resourcePath = "HexLive/FaceExpressions/CuteFun")
    {
        var catalog = new FaceExpressionCatalog();
        var asset = Resources.Load<TextAsset>(resourcePath);
        if (asset == null)
        {
            Debug.LogWarning($"[FaceExpressionCatalog] '{resourcePath}' not found in Resources");
            return catalog;
        }

        var parsed = JsonUtility.FromJson<CatalogJson>(asset.text);
        if (parsed?.expressions == null)
        {
            Debug.LogWarning($"[FaceExpressionCatalog] '{resourcePath}' is empty or malformed");
            return catalog;
        }

        foreach (var entry in parsed.expressions)
        {
            var shapes = new (string, float)[entry.shapes?.Length ?? 0];
            for (var i = 0; i < shapes.Length; i++)
            {
                shapes[i] = (entry.shapes[i].shape, entry.shapes[i].weight);
            }

            catalog._expressions.Add(new Expression
            {
                Name = entry.name,
                Label = entry.label ?? "",
                Shapes = shapes,
            });
        }

        return catalog;
    }

    public int IndexOf(string name)
    {
        for (var i = 0; i < _expressions.Count; i++)
        {
            if (_expressions[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    // Поиск по короткому номеру пака ("08", "11-Left") или точному имени
    // (кастомы вида "x_angry"). Номер матчится как суффикс после пробела.
    public int FindIndex(string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName))
        {
            return -1;
        }

        for (var i = 0; i < _expressions.Count; i++)
        {
            var name = _expressions[i].Name;
            if (name == idOrName || name.EndsWith(" " + idOrName))
            {
                return i;
            }
        }

        return -1;
    }

    // Кастомный рецепт в том же формате, что выражения пака — для эмоций,
    // которых в паке нет (злость, плач, боль…). Добавлять ДО создания ригов:
    // FaceExpressionRig кэширует резолв по индексу.
    public void AddCustom(string name, string label, (string shape, float weight)[] shapes)
    {
        _expressions.Add(new Expression { Name = name, Label = label, Shapes = shapes });
    }
}

// Привязка каталога к конкретной актрисе: имена блендшейпов рецептов один раз
// резолвятся в (renderer, index) по тем же правилам, что NpcFaceAnimator —
// точное имя или суффикс после "__"/".", чтобы находились и голые
// "eCTRLSmile", и "Genesis3Female__eCTRLSmile" на каждом меше, где канал есть
// (лицо, ресницы...).
public sealed class FaceExpressionRig
{
    private sealed class Binding
    {
        public readonly List<(SkinnedMeshRenderer skin, int index)> Targets = new();
        public float Weight;
        public bool IsEyesClosed;   // канал моргания — блинк пишется поверх
    }

    // gameSafe-режим выкидывает из рецептов каналы, которыми в игре владеют
    // другие системы: направление взгляда (глазами рулит LookAt по костям —
    // запечённый морф поверх даёт двойной сдвиг, косоглазие) и визэмы eCTRLv*
    // (их каждый кадр пишет uLipSync §67.7 — порядок LateUpdate не определён,
    // была бы драка за рот). Превью в TwoPeopleTest показывает рецепт как есть.
    private static readonly string[] GazeShapes =
    {
        "eCTRLEyesSideSide", "eCTRLEyesUpDown", "eCTRLEyesCrossed",
    };

    private readonly FaceExpressionCatalog _catalog;
    // Per-expression resolved bindings, built lazily per expression.
    private readonly Dictionary<int, List<Binding>> _resolved = new();
    private readonly SkinnedMeshRenderer[] _skins;
    private readonly bool _gameSafe;
    private List<Binding> _active;

    public FaceExpressionRig(SkinnedMeshRenderer[] skins, FaceExpressionCatalog catalog,
        bool gameSafe = false)
    {
        _skins = skins ?? Array.Empty<SkinnedMeshRenderer>();
        _catalog = catalog;
        _gameSafe = gameSafe;
    }

    public int Count => _catalog?.Expressions.Count ?? 0;

    // Что текущее выражение положило в eCTRLEyesClosedL/R (после умножения на
    // интенсивность; отрицательное = распахнутые глаза). Моргание аниматора
    // пишется ПОВЕРХ этих каналов как база + вес блинка — блендшейпы линейны,
    // так что сумма и есть честная композиция.
    public float AppliedEyesClosed { get; private set; }

    // «11-Left „Восторженный смех“» — короткий номер + наша подпись; без
    // подписи возвращает полное имя дила.
    public string NameOf(int expressionIndex)
    {
        if (expressionIndex < 0 || expressionIndex >= Count)
        {
            return "";
        }

        var expression = _catalog.Expressions[expressionIndex];
        if (string.IsNullOrEmpty(expression.Label))
        {
            return expression.Name;
        }

        var cut = expression.Name.LastIndexOf(' ');
        var shortId = cut >= 0 ? expression.Name[(cut + 1)..] : expression.Name;
        return $"{shortId} «{expression.Label}»";
    }

    // Выставить выражение с интенсивностью 0..1. Смена выражения сама зануляет
    // каналы предыдущего, которых нет в новом.
    public void Apply(int expressionIndex, float intensity01)
    {
        if (expressionIndex < 0 || expressionIndex >= Count)
        {
            Clear();
            return;
        }

        var bindings = ResolveBindings(expressionIndex);
        if (!ReferenceEquals(bindings, _active))
        {
            ClearActive();
            _active = bindings;
        }

        AppliedEyesClosed = 0f;
        foreach (var binding in bindings)
        {
            var weight = binding.Weight * Mathf.Clamp01(intensity01);
            if (binding.IsEyesClosed)
            {
                AppliedEyesClosed = weight;
            }

            foreach (var (skin, index) in binding.Targets)
            {
                if (skin != null)
                {
                    skin.SetBlendShapeWeight(index, weight);
                }
            }
        }
    }

    public void Clear()
    {
        ClearActive();
        _active = null;
        AppliedEyesClosed = 0f;
    }

    private void ClearActive()
    {
        if (_active == null)
        {
            return;
        }

        foreach (var binding in _active)
        {
            foreach (var (skin, index) in binding.Targets)
            {
                if (skin != null)
                {
                    skin.SetBlendShapeWeight(index, 0f);
                }
            }
        }
    }

    private List<Binding> ResolveBindings(int expressionIndex)
    {
        if (_resolved.TryGetValue(expressionIndex, out var cached))
        {
            return cached;
        }

        var bindings = new List<Binding>();
        foreach (var (shape, weight) in _catalog.Expressions[expressionIndex].Shapes)
        {
            if (_gameSafe &&
                (Array.IndexOf(GazeShapes, shape) >= 0 || shape.StartsWith("eCTRLv")))
            {
                continue;
            }

            var binding = new Binding
            {
                Weight = weight,
                IsEyesClosed = shape is "eCTRLEyesClosedL" or "eCTRLEyesClosedR",
            };
            foreach (var skin in _skins)
            {
                var mesh = skin != null ? skin.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (name == shape || name.EndsWith("__" + shape) || name.EndsWith("." + shape))
                    {
                        binding.Targets.Add((skin, i));
                    }
                }
            }

            if (binding.Targets.Count > 0)
            {
                bindings.Add(binding);
            }
        }

        _resolved.Add(expressionIndex, bindings);
        return bindings;
    }
}

}
