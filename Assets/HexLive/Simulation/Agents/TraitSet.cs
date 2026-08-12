namespace HexLive.Simulation.Agents
{

// §126: черта характера — КАКОЙ она человек. Третий слой личности рядом с
// §76: характеристики говорят, что тело может, навыки — что руки умеют,
// черта — что человек СТАНЕТ делать, когда выбор за ним.
//
// ⭐ Черта — не фракция. До §126 «абьюзер» и «не моется» были свойствами бита
// Faction: колонистка не могла быть неряхой в принципе, а чужак — чистюлей, и
// поведение переехало бы вместе с фракцией, если бы кто-то когда-нибудь сдался
// в плен. Теперь фракция отвечает только на вопрос «кто кому враг»
// (FactionRelations), а черта — на «как он себя ведёт».
//
// Ординал = НОМЕР БИТА в TraitSet.Bits, и он уходит в сейв одним числом:
// перечисление СТРОГО ДОПИСЫВАЕТСЯ В КОНЕЦ, как GoalType и Faction. Вставка в
// середину переименовала бы черты у всех сохранённых колоний разом.
public enum TraitKind
{
    // §81: гнобит чужих. Жертву по-прежнему выбирает вражда (§126.3), а не
    // черта: своих он не трогает никогда.
    Abuser = 0,

    // §89: на быт плевать — не моется и не стирает.
    Slob = 1,

    // Обратный полюс Slob: моется и стирает раньше и охотнее.
    Neat = 2,

    // Досуг дороже дел: реже берётся за работу «пока руки свободны».
    Lazy = 3,

    // Обратный полюс Lazy: свободные руки почти всегда находят дело.
    Diligent = 4,

    // §49: ложится раньше. Спит всё равно до полного — «дольше» выходит само.
    Sleepyhead = 5,

    // §62: избегает опасности дольше и шире, первой не нападает.
    Coward = 6,

    // Обратный полюс Coward: раньше принимает бой и меньше топчется в патовой.
    Brave = 7
}

// §126: набор черт одного человека. Битовая маска, а не список: проверка
// Has() стоит одну инструкцию и лежит прямо на горячем пути аукциона целей, а
// в сейв и в провод уходит ОДНО число, которое нельзя «забыть дописать»
// наполовину.
//
// Класс, а не структура, — по образцу AttributeSet и по той же причине:
// NPCState отдаёт его get-only свойством, и мутирующий метод на копии
// структуры молча не сделал бы ничего.
public sealed class TraitSet
{
    // Порядок обхода для экспортёра и листа персонажа — тот же приём, что
    // AttributeSet.All. Значение имеет только порядок ПОКАЗА: место в сейве
    // определяет ординал, а не эта таблица.
    public static readonly TraitKind[] All =
    {
        TraitKind.Abuser,
        TraitKind.Slob,
        TraitKind.Neat,
        TraitKind.Lazy,
        TraitKind.Diligent,
        TraitKind.Sleepyhead,
        TraitKind.Coward,
        TraitKind.Brave
    };

    // Пустая маска = «человек без особых черт» = игра до §126 байт-в-байт.
    // Именно поэтому дефолт нулевой, а не «средний»: у черты нет середины.
    public ulong Bits { get; set; }

    public bool Has(TraitKind kind) => (Bits & (1UL << (int)kind)) != 0UL;

    public void Add(TraitKind kind) => Bits |= 1UL << (int)kind;

    public void Remove(TraitKind kind) => Bits &= ~(1UL << (int)kind);

    public void Clear() => Bits = 0UL;

    public int Count
    {
        get
        {
            var count = 0;
            foreach (var kind in All)
            {
                if (Has(kind))
                {
                    count++;
                }
            }

            return count;
        }
    }

    // База ключа локализации. Вид дописывает ".title" и ".desc" — два суффикса
    // в одном месте вида, вместо двух строк на каждую черту в снапшоте.
    public static string LocKey(TraitKind kind) =>
        "trait." + kind.ToString().ToLowerInvariant();

    // Разбор авторской строки бутстрапа ("Abuser") в черту. Незнакомое имя —
    // false, а не исключение: бутстрап тест-сцены не должен ронять мир из-за
    // опечатки, но и молча дарить не ту черту тоже нельзя (вызывающий логирует).
    public static bool TryParse(string name, out TraitKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), name,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = TraitKind.Abuser;
        return false;
    }
}

}
