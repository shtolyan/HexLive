using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: чужую вещь надевают только спросив — и каждый раз заново. Отказ по
/// делу («самой нужна») обиды не несёт, личный — стоит отношений.
/// </summary>
public sealed class WearPermissionTests
{
    private const string Bra = "underwear.bra_riot";

    private static (WorldState world, NPCState owner, NPCState asker) TwoColonists()
    {
        var world = TestWorld.CreateWorld(12345);
        var colonists = world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        Assert.That(colonists.Length, Is.EqualTo(2), "Нужны две колонистки.");
        // Чистый лист: владение считается по вещам, а не по стартовому наряду.
        foreach (var npc in colonists)
        {
            npc.WornItems.Clear();
            npc.Inventory.Items.Clear();
        }

        return (world, colonists[0], colonists[1]);
    }

    /// <summary>Надеть вещь тем же путём, что и игра, — через CompleteDress.</summary>
    private static bool Dress(WorldState world, NPCState npc, WorldObjectState garment)
    {
        var definition = world.Content.ObjectDefinitions[garment.DefinitionId];
        var interaction = definition.Interactions.First(i => i.Type == InteractionType.Dress);
        return ExecutionSystem.CompleteDress(world, npc, garment, definition, interaction, string.Empty);
    }

    /// <summary>Вещь хозяйки, лежащая в мире (её и просят).</summary>
    private static WorldObjectState GarmentOf(WorldState world, NPCState owner, string id = Bra)
    {
        var tile = owner.Tile;
        var junction = world.Tiles.Items[tile].Junctions[0];
        var obj = WorldObjectMutations.SpawnObject(world, id, owner.Fragment, tile, junction);
        obj.Owner = owner.Id;
        return obj;
    }

    /// <summary>⭐ Последние трусы не отдают — иначе хозяйка сама останется голой.</summary>
    [Test]
    public void HerLastCoveringPieceIsNeverLent()
    {
        var (world, owner, asker) = TwoColonists();
        var garment = GarmentOf(world, owner);

        Assert.That(WearPermissionMath.Decide(world, owner, asker, garment),
            Is.EqualTo(WearPermissionMath.Verdict.RefuseNeeded),
            "Отдала единственную вещь, прикрывающую эту часть тела.");
    }

    /// <summary>«И так трусы надеты, есть ещё — почему не дать» (формулировка игрока).</summary>
    [Test]
    public void ASpareIsLentToANeutralHousemate()
    {
        var (world, owner, asker) = TwoColonists();
        owner.WornItems.Add(new ItemInstance(Bra) { OwnerId = owner.Id.Value });
        var spare = GarmentOf(world, owner);

        Assert.That(WearPermissionMath.Decide(world, owner, asker, spare),
            Is.EqualTo(WearPermissionMath.Verdict.Grant),
            "Запасную вещь пожалели нейтральной соседке.");
    }

    /// <summary>Неприязнь перевешивает одну запасную, но не две.</summary>
    [Test]
    public void DislikeCostsASpareAndTwoSparesWinItBack()
    {
        var (world, owner, asker) = TwoColonists();
        owner.Social.GetOrCreate(asker.Id).Affinity = -0.5f;
        owner.WornItems.Add(new ItemInstance(Bra) { OwnerId = owner.Id.Value });
        var spare = GarmentOf(world, owner);

        Assert.That(WearPermissionMath.Decide(world, owner, asker, spare),
            Is.EqualTo(WearPermissionMath.Verdict.RefuseDislike),
            "Нелюбимой соседке отдали последнюю запасную.");

        owner.Inventory.Items.Add(new ItemInstance(Bra) { OwnerId = owner.Id.Value });
        Assert.That(WearPermissionMath.Decide(world, owner, asker, spare),
            Is.EqualTo(WearPermissionMath.Verdict.Grant),
            "С двумя запасными жадничать уже не из чего.");
    }

    /// <summary>Мёрзнущая хозяйка не отдаёт то, что греет, — и это не обида.</summary>
    [Test]
    public void AColdOwnerKeepsWhatWarmsHer()
    {
        var (world, owner, asker) = TwoColonists();
        // Запас есть (жадности нет), но САМА она раздета и мёрзнет: эта вещь
        // дала бы ей настоящую прибавку тепла — значит, нужна ей самой.
        owner.Inventory.Items.Add(new ItemInstance("clothing.jacket_autumn") { OwnerId = owner.Id.Value });
        var warm = GarmentOf(world, owner, "clothing.jacket_autumn");
        owner.Needs.ThermalDiscomfort = 1f;

        Assert.That(WearPermissionMath.Decide(world, owner, asker, warm),
            Is.EqualTo(WearPermissionMath.Verdict.RefuseNeeded),
            "Отдала тёплое, замерзая сама.");
    }

    /// <summary>Вещь подруги без разрешения не надевается даже вплотную.</summary>
    [Test]
    public void DressingAFriendsGarmentWithoutPermissionIsRefusedByExecution()
    {
        var (world, owner, asker) = TwoColonists();
        var garment = GarmentOf(world, owner);

        var worn = asker.WornItems.Count;
        var completed = Dress(world, asker, garment);

        Assert.That(completed, Is.False, "Чужое надели без спроса.");
        Assert.That(asker.WornItems.Count, Is.EqualTo(worn));
        Assert.That(world.Entities.Objects.ContainsKey(garment.Id), Is.True,
            "Вещь исчезла из мира при неудачном надевании.");
    }

    /// <summary>С разрешением — надевается, разрешение сгорает, хозяйка прежняя.</summary>
    [Test]
    public void APermittedGarmentIsWornOnceAndStaysItsOwners()
    {
        var (world, owner, asker) = TwoColonists();
        var garment = GarmentOf(world, owner);
        asker.Mind.WearGrants.Add(new HexLive.Simulation.AI.WearGrant
        {
            Item = garment.Id,
            Owner = owner.Id,
            ExpiresTick = world.Tick + 600
        });

        var completed = Dress(world, asker, garment);

        Assert.That(completed, Is.True, "С разрешением надеть не дали.");
        var wornItem = asker.WornItems.First(i => i.DefinitionId == Bra);
        Assert.That(wornItem.OwnerId, Is.EqualTo(owner.Id.Value),
            "Одолженная вещь сменила хозяйку — это присвоение, а не заём.");
        Assert.That(asker.Mind.WearGrants, Is.Empty,
            "Разрешение не сгорело — второй раз наденет без спроса.");
    }
}

}
