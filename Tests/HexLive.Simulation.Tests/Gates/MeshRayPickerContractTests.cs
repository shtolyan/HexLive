using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Bug #299 («доски не подсвечиваются», третий заход игрока): аналитический
/// пикинг считает Мёллера-Трумбора в ЛОКАЛЬНОМ пространстве меша. Доска
/// авторски крошечная (~1 см в файле, ObjectFit компенсирует масштабом ×77),
/// и без нормировки направления луча детерминант каждого треугольника падал
/// под абсолютный эпсилон «луч параллелен» — идеальное попадание в габарит
/// не находило ни одного треугольника, вид был непикаемым и без подсветки.
/// Замер в редакторе: TryIntersect=False до нормировки, True после, на одном
/// и том же луче в центр габарита.
/// </summary>
public sealed class MeshRayPickerContractTests
{
    private static string Picker() => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
        "Views", "MeshRayPicker.cs"));

    [Test]
    public void LocalRayDirectionIsNormalizedForTinyAuthoredMeshes()
    {
        var picker = Picker();

        Assert.Multiple(() =>
        {
            Assert.That(picker, Does.Contain("direction /= directionScale;"),
                "Локальное направление луча нормируется: у сантиметрового " +
                "авторского меша det иначе тонет под эпсилоном параллельности.");
            Assert.That(picker, Does.Contain("t / directionScale < distance"),
                "Дистанция возвращается в мировых единицах — локальный t " +
                "делится на длину локального направления.");
            Assert.That(picker, Does.Not.Contain("det > -1e-8f && det < 1e-8f"),
                "Абсолютный эпсилон 1e-8 по det отвергал все треугольники " +
                "крошечных мешей; порог обязан оставаться заметно ниже.");
        });
    }
}

}
