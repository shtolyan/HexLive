#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Input
{

/// <summary>
/// §118: почему приказ не выполнен. Симуляция отвечает трассой
/// <c>ManualOrderRejected Reason=…</c>; здесь она превращается в короткую
/// подпись игроку.
///
/// Отказ обязан быть ВИДЕН: молчащий приказ игрок читает как поломку игры, а
/// не как «туда не дойти» — и идёт искать баг там, где его нет.
///
/// Причина ловится в общем сливе событий (SimulationRunnerBehaviour), а не
/// вторым проходом по кольцу: событий ~200 за тик, и второй читатель с
/// собственной отметкой стоил бы столько же, сколько первый.
/// </summary>
public static class ManualOrderFeedback
{
    private const float VisibleSeconds = 2.5f;

    public static int NpcId { get; private set; } = -1;

    /// <summary>Ключ локализации причины, например <c>toast.order_rejected.Unreachable</c>.</summary>
    public static string ReasonKey { get; private set; } = string.Empty;

    private static float _stampedAt = float.NegativeInfinity;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        NpcId = -1;
        ReasonKey = string.Empty;
        _stampedAt = float.NegativeInfinity;
    }

    public static bool IsFresh(int npcId) =>
        npcId >= 0 && npcId == NpcId &&
        ReasonKey.Length > 0 &&
        Time.unscaledTime - _stampedAt < VisibleSeconds;

    /// <summary>
    /// Разбирает сообщение вида <c>Order=Interact Reason=MissingTool</c>.
    /// Парсинг, а не своё поле в событии: формат <c>Ключ=Значение</c> — общий
    /// для всей трассы (его же читают история колонии и звук), и заводить
    /// ради одной подписи вторую форму значило бы её раздвоить.
    /// </summary>
    public static void Report(int npcId, string message)
    {
        var reason = ValueOf(message, "Reason=");
        if (reason.Length == 0)
        {
            return;
        }

        NpcId = npcId;
        ReasonKey = "toast.order_rejected." + reason;
        _stampedAt = Time.unscaledTime;
    }

    public static void Clear()
    {
        ReasonKey = string.Empty;
        _stampedAt = float.NegativeInfinity;
    }

    private static string ValueOf(string message, string key)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var start = message.IndexOf(key, System.StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += key.Length;
        var end = message.IndexOf(' ', start);
        return end < 0 ? message.Substring(start) : message.Substring(start, end - start);
    }
}

}
