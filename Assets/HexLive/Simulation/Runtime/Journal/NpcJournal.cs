using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime.Journal
{

/// <summary>
/// Spec §136: личный дневник одной NPC — кольцо закрытых записей плюс кандидат
/// текущего часа.
///
/// <para>
/// Кандидат — это ОДНА структура, а не список: за час прилетает до нескольких
/// сотен событий, и копить их, чтобы в конце выбрать максимум, значило бы
/// аллоцировать на горячем пути эмита ради одной строки. Побеждающий вес
/// сравнивается на месте.
/// </para>
/// <para>
/// Порядок в <see cref="Entries"/> — хронологический (старое первым). Вид
/// показывает его перевёрнутым: игрок не должен листать вниз, чтобы узнать,
/// что случилось только что.
/// </para>
/// </summary>
public sealed class NpcJournal
{
    private readonly List<JournalEntry> _entries = new();

    public IReadOnlyList<JournalEntry> Entries => _entries;

    /// <summary>Номер часа, к которому относится накопленный кандидат.</summary>
    public int PendingHour { get; set; } = int.MinValue;

    public int PendingWeight { get; set; }
    public string PendingType { get; set; }
    public JournalPerspective PendingPerspective { get; set; }

    /// <summary>
    /// Id второй участницы — нужен ОТДЕЛЬНО от её имени: тепло к ней читается
    /// из <c>Social.Relationships</c> по id, а в текст уходит id имени (§74).
    /// </summary>
    public EntityId? PendingSubjectId { get; set; }

    public string PendingSubjectNameId { get; set; }
    public string PendingExtra { get; set; }

    /// <summary>Бытовые дела часа — то, что перечислит тихая запись.</summary>
    public string Chore0 { get; set; }
    public string Chore1 { get; set; }
    public string Chore2 { get; set; }

    public bool HasPending => PendingWeight > 0;

    public void ClearPending()
    {
        PendingHour = int.MinValue;
        PendingWeight = 0;
        PendingType = null;
        PendingPerspective = JournalPerspective.Did;
        PendingSubjectId = null;
        PendingSubjectNameId = null;
        PendingExtra = null;
        Chore0 = null;
        Chore1 = null;
        Chore2 = null;
    }

    /// <summary>Записать бытовое дело, если такого в этом часу ещё не было.</summary>
    public void NoteChore(string type)
    {
        if (string.IsNullOrEmpty(type) || Chore0 == type || Chore1 == type || Chore2 == type)
        {
            return;
        }

        var slots = Spec136.QuietChoreSlots;
        if (slots >= 1 && Chore0 == null) Chore0 = type;
        else if (slots >= 2 && Chore1 == null) Chore1 = type;
        else if (slots >= 3 && Chore2 == null) Chore2 = type;
    }

    public void Add(JournalEntry entry)
    {
        _entries.Add(entry);
        var capacity = Spec136.Capacity < 1 ? 1 : Spec136.Capacity;
        if (_entries.Count > capacity)
        {
            _entries.RemoveRange(0, _entries.Count - capacity);
        }
    }

    /// <summary>
    /// Продлить последнюю тихую запись ещё на один час — вместо новой строки.
    /// Возвращает false, если продлевать нечего или упёрлись в
    /// <see cref="Spec136.MaxQuietRun"/>.
    /// </summary>
    public bool TryExtendQuiet(int tick)
    {
        if (_entries.Count == 0)
        {
            return false;
        }

        var last = _entries[_entries.Count - 1];
        if (!last.IsQuiet || last.QuietHours >= Spec136.MaxQuietRun)
        {
            return false;
        }

        last.QuietHours++;
        last.Tick = tick;
        _entries[_entries.Count - 1] = last;
        return true;
    }

    /// <summary>Восстановление из сейва и из снапшота — порядок сохраняется.</summary>
    public void LoadFrom(List<JournalEntry> entries)
    {
        _entries.Clear();
        if (entries != null)
        {
            _entries.AddRange(entries);
        }
    }

    public void Clear() => _entries.Clear();
}

}
