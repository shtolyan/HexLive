using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Спек §59.3, и это не опция: без экспортированных каталогов тесты мерили бы
/// код-дефолты, а не оттюненную игру. <c>Require</c> бросает, если экспорта нет.
/// <para>
/// Один раз на всю сборку: он кладёт значения поверх ПРОЦЕСС-ГЛОБАЛЬНЫХ статиков,
/// так что повтор на каждый тест был бы и лишним, и опасным.
/// </para>
/// </summary>
[SetUpFixture]
public sealed class SimDataFixture
{
    [OneTimeSetUp]
    public void ApplySimData()
    {
        SimDataFile.Require(RepoPaths.SimData);
    }
}

}
