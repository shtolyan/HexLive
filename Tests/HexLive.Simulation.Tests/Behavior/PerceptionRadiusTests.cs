using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§125.1: характеристика Восприятие → расширенный радиус в гексах.
/// Очки остаются прежними; формула сенсора и трёхгексовый пол проверяются
/// отдельно.</summary>
[NonParallelizable]
public sealed class PerceptionRadiusTests
{
    [TestCase(0f, 3)]     // §125.1: нулевой ролл всё равно видит три кольца
    [TestCase(0.5f, 8)]   // среднее тело: было 5, стало 8 (+60%)
    [TestCase(0.8f, 13)]
    [TestCase(1f, 16)]    // верх полосы ролла
    [TestCase(1.3f, 21)]  // тренировочный потолок §76.13
    public void AttributeMapsToHexRadius(float attribute, int expected)
    {
        var npc = new NPCState();
        npc.Attributes.Perception = attribute;

        Assert.That(PerceptionMath.RadiusTiles(npc), Is.EqualTo(expected));
    }

    [Test]
    public void NobodyIsBlindToWhoStandsRightNextToHer()
    {
        // Бюджет §76.2 обязан положить чью-то ось на дно: замер 200 сидов дал
        // ноль у 10.8% колонисток. Ноль означал бы «не вижу стоящую вплотную» —
        // выпадение из жизни колонии целиком. Заметить того, кто рядом, — это
        // присутствие, а не зоркость, и потому не роллится.
        var npc = new NPCState();
        npc.Attributes.Perception = 0f;

        Assert.That(PerceptionMath.RadiusTiles(npc),
            Is.EqualTo(PerceptionMath.MinRadiusTiles).And.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void UnrolledBodySeesTheMeanRadius()
    {
        Assert.That(PerceptionMath.RadiusTiles(new NPCState()), Is.EqualTo(8));
    }
}

}
