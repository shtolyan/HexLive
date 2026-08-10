using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§125.1: характеристика Восприятие → радиус в гексах. Число с листа
/// персонажа и есть радиус, поэтому формула проверяется поимённо.</summary>
[NonParallelizable]
public sealed class PerceptionRadiusTests
{
    [TestCase(0f, 0)]     // слепая — осознанно допустима
    [TestCase(0.5f, 5)]   // среднее тело §76
    [TestCase(0.8f, 8)]   // «Восприятие 8» с листа = 8 гексов
    [TestCase(1f, 10)]    // верх полосы ролла
    [TestCase(1.3f, 13)]  // тренировочный потолок §76.13
    public void AttributeMapsToHexRadius(float attribute, int expected)
    {
        var npc = new NPCState();
        npc.Attributes.Perception = attribute;

        Assert.That(PerceptionMath.RadiusTiles(npc), Is.EqualTo(expected));
    }

    [Test]
    public void UnrolledBodySeesTheMeanRadius()
    {
        // Дефолт 0.5 = «мир до §125»: у теста-сцены и старого сейва радиус 5.
        Assert.That(PerceptionMath.RadiusTiles(new NPCState()), Is.EqualTo(5));
    }
}

}
