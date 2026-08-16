using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Отпечаток топологии (§83.2 правило 8) — это отпечаток МИРА, а не рантайма,
/// на котором мир построили.
/// <para>
/// Стоит здесь потому, что первая же реальная связка клиент-сервер разошлась
/// ровно на этом: сумма хешировала сырые биты позиций джанкшенов, а редактор
/// (Mono) и сервер (.NET 9) считают <c>HexPointLayout.ToLocalOffset</c> с
/// разницей в 1 ULP — Mono ведёт цепочку float через double и округляет один
/// раз при записи. Тайлы совпадали побитово, остров был тот же, но handshake
/// отказывал со словами «разные сборки», и посмотреть сервер из редактора было
/// нельзя вообще.
/// </para>
/// <para>
/// Проверить это внутри одного рантайма нельзя «сравнив два рантайма» — зато
/// можно смоделировать саму разницу: сдвинуть каждую позицию на один ULP и
/// потребовать, чтобы сумма не дрогнула. Настоящий другой остров двигает id,
/// тайлы и соседей, а не последний бит мантиссы.
/// </para>
/// </summary>
public sealed class TopologyChecksumGateTests
{
    [Test]
    public void OneUlpDriftInJunctionPositionsDoesNotChangeTheChecksum()
    {
        var world = new WorldStateFactory().Create(PrototypeWorldDefinitionFactory.Create(12345));
        var before = TopologyChecksum.Compute(world);

        var moved = 0;
        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            junction.WorldPosition = new Float2(
                NextUlp(junction.WorldPosition.X),
                NextUlp(junction.WorldPosition.Y));
            moved++;
        }

        Assert.That(moved, Is.GreaterThan(1000), "мир без джанкшенов ничего не доказывает");
        Assert.That(TopologyChecksum.Compute(world), Is.EqualTo(before),
            "сумма зависит от битов float — она снова отпечаток рантайма, а не мира (§83.2 правило 8)");
    }

    /// <summary>
    /// А вот НАСТОЯЩЕЕ расхождение геометрии обязано быть слышно: убрать одно
    /// ребро — и сумма другая. Без этой половины первый тест проходил бы и на
    /// сумме-константе.
    /// </summary>
    [Test]
    public void ARealTopologyDifferenceStillFails()
    {
        var world = new WorldStateFactory().Create(PrototypeWorldDefinitionFactory.Create(12345));
        var before = TopologyChecksum.Compute(world);

        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            if (junction.Neighbors.Count == 0)
            {
                continue;
            }

            junction.Neighbors.RemoveAt(junction.Neighbors.Count - 1);
            break;
        }

        Assert.That(TopologyChecksum.Compute(world), Is.Not.EqualTo(before),
            "потерянное ребро прошло мимо суммы — она больше ничего не сторожит");
    }

    /// <summary>
    /// И тайлы: высота острова — то самое место, ради которого сумма вообще
    /// заведена (одно округление на границе .5 меняет воду и проходимость).
    /// </summary>
    [Test]
    public void ATileElevationDifferenceStillFails()
    {
        var world = new WorldStateFactory().Create(PrototypeWorldDefinitionFactory.Create(12345));
        var before = TopologyChecksum.Compute(world);

        foreach (var pair in world.Tiles.Items)
        {
            pair.Value.Elevation += 1;
            break;
        }

        Assert.That(TopologyChecksum.Compute(world), Is.Not.EqualTo(before),
            "сдвинутая высота тайла прошла мимо суммы");
    }

    private static float NextUlp(float value)
    {
        var bits = System.BitConverter.SingleToInt32Bits(value);
        return System.BitConverter.Int32BitsToSingle(bits >= 0 ? bits + 1 : bits - 1);
    }
}

}
