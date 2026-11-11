#nullable enable

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7 / §120.13: отдельного HTML-лотка у входа в стройку больше нет.
    /// Режим строительства в веб-клиенте — один экран «blueprint», который
    /// рисует сам редактор живого мира (<c>HutLayoutDesigner.Html</c>): каталог,
    /// выбранная карточка, «Выбор», «Отменить/Вернуть», «Выйти». Дверь в режим —
    /// кнопка «Строить» в HUD (<c>SimSpeedBar</c> → <see cref="Toggle"/>).
    /// </summary>
    public sealed partial class BuildModePanel
    {
    }
}
