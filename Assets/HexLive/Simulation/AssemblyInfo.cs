using System.Runtime.CompilerServices;

// Гейты симуляции живут в Tests/HexLive.Simulation.Tests (вне Assets/, чтобы им
// были позволены PackageReference). Часть проверяемого — internal по делу:
// `Trace` — механизм трассировки, `InteractionReach` и `MeleeSwing` — расчёт
// дистанций, и делать их public ради тестов значило бы расширить публичную
// поверхность сборки под инструмент.
//
// Unity эту строку исполняет как обычный атрибут сборки, а сборки с таким
// именем в игре просто нет — так что в билде она не значит ничего.
[assembly: InternalsVisibleTo("HexLive.Simulation.Tests")]
