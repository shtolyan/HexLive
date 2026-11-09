#nullable enable
using System;
using System.Collections.Generic;

namespace HexLive.UnityPresentation.UI
{

/// <summary>Один пункт меню: что написать и что сделать по клику.</summary>
public sealed class ContextMenuEntry
{
    public ContextMenuEntry(string label, Action activate, bool enabled = true, string? disabledHint = null)
    {
        Label = label;
        Activate = activate;
        Enabled = enabled;
        DisabledHint = string.Equals(label, disabledHint, StringComparison.Ordinal) ? null : disabledHint;
    }

    /// <summary>§167.3: пункт-родитель — вместо действия раскрывает вложенный
    /// список справа («Указание ▸»). Сам по клику ничего не делает.</summary>
    public ContextMenuEntry(string label, IReadOnlyList<ContextMenuEntry> children,
        bool enabled = true, string? disabledHint = null)
    {
        Label = label;
        Activate = () => { };
        Children = children;
        Enabled = enabled;
        DisabledHint = string.Equals(label, disabledHint, StringComparison.Ordinal) ? null : disabledHint;
    }

    public string Label { get; }

    public Action Activate { get; }

    /// <summary>Вложенные пункты; null — обычная строка.</summary>
    public IReadOnlyList<ContextMenuEntry>? Children { get; }

    /// <summary>Ложь — пункт виден, но серый. ВИДЕН намеренно: «топором нельзя»
    /// — это сведение о мире, а исчезнувший пункт учит только тому, что меню
    /// каждый раз разное.</summary>
    public bool Enabled { get; }

    public string? DisabledHint { get; }

    /// <summary>Bug #312: «опасный» пункт (кража) красится красным.
    /// set, а не init: Unity-профиль netstandard2.1 не несёт IsExternalInit.</summary>
    public bool Danger { get; set; }
}

}
