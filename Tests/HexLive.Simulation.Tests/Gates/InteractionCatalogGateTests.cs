using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Bug #285 / §121.1: у одного определения не бывает двух действий
/// «Подобрать». Каталог кода и тюнинг-ассет мержатся по ID интеракции, и
/// ассет с ДРУГИМ id того же действия (board.asset: pickup.board рядом с
/// каталожным pickup.resource.board) молча даёт два одинаковых пункта в
/// контекстном меню. Гейт ловит такой дубль после каждого экспорта simdata.
/// </summary>
public sealed class InteractionCatalogGateTests
{
    [Test]
    public void NoDefinitionCarriesTwoPickUpInteractions()
    {
        var world = TestWorld.CreateWorld();
        Assert.Multiple(() =>
        {
            foreach (var pair in world.Content.ObjectDefinitions)
            {
                var pickups = pair.Value.Interactions.Count(
                    interaction => interaction.Type ==
                        HexLive.Simulation.Content.InteractionType.PickUp);
                Assert.That(pickups, Is.LessThanOrEqualTo(1),
                    $"{pair.Key}: два действия «Подобрать» — дубль каталога " +
                    "кода и тюнинг-ассета (bug #285).");
            }
        });
    }
}

}
