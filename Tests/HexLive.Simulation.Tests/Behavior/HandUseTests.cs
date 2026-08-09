using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class HandUseTests
{
    [Test]
    public void ToolAndWeaponUse_RequiresStandingAndAtLeastOneUsableHand()
    {
        var body = new BodyState();

        Assert.Multiple(() =>
        {
            Assert.That(body.HasUsableHand, Is.True);
            Assert.That(body.CanUseToolsOrWeapons, Is.True);
            Assert.That(body.IntactHands, Is.EqualTo(2));
            Assert.That(body.CanUseTwoHanded, Is.True);
        });

        body.Sever(BodyPart.ArmR);
        Assert.Multiple(() =>
        {
            Assert.That(body.HasUsableHand, Is.True);
            Assert.That(body.CanUseToolsOrWeapons, Is.True);
            Assert.That(body.IntactHands, Is.EqualTo(1));
            Assert.That(body.CanUseTwoHanded, Is.False);
        });

        body.Sever(BodyPart.ArmL);
        Assert.Multiple(() =>
        {
            Assert.That(body.HasUsableHand, Is.False);
            Assert.That(body.CanUseToolsOrWeapons, Is.False);
            Assert.That(body.IntactHands, Is.Zero);
            Assert.That(body.WeaponHands, Is.Zero);
        });

        body.Condition(BodyPart.ArmR).Prosthetic = new ProstheticState
        {
            DefinitionId = ContentIds.WoodenArm,
            Part = BodyPart.ArmR,
            Function = Spec118.WoodenArmFunction,
            Condition = 0.5f,
            MaxCondition = 1f
        };
        Assert.Multiple(() =>
        {
            Assert.That(body.LimbFunction(BodyPart.ArmR), Is.EqualTo(0.225f).Within(0.0001f));
            Assert.That(body.HasUsableHand, Is.True);
            Assert.That(body.CanUseToolsOrWeapons, Is.True);
            Assert.That(body.IntactHands, Is.EqualTo(1));
        });

        body.Condition(BodyPart.ArmR).Prosthetic.Condition = 0.4f;
        Assert.Multiple(() =>
        {
            Assert.That(body.LimbFunction(BodyPart.ArmR), Is.EqualTo(0.18f).Within(0.0001f));
            Assert.That(body.HasUsableHand, Is.False);
            Assert.That(body.CanUseToolsOrWeapons, Is.False);
        });
    }

    [Test]
    public void WeaponSelection_FiltersTwoHandedAndAllWeaponsByUsableHands()
    {
        var items = new List<ItemInstance>
        {
            new(GearCatalog.Knife),
            new(GearCatalog.Axe),
            new(GearCatalog.Spear)
        };

        Assert.Multiple(() =>
        {
            Assert.That(GearCatalog.BestMeleeWeapon(items, 2), Is.EqualTo(GearCatalog.Spear));
            Assert.That(GearCatalog.BestMeleeWeapon(items, 1), Is.EqualTo(GearCatalog.Axe),
                "One-handed axe remains usable while the stronger spear is filtered out.");
            Assert.That(GearCatalog.BestMeleeWeapon(items, 0), Is.EqualTo(GearCatalog.Fist));
        });
    }

    [Test]
    public void CoconutBlade_RequiresAHandButNotStandingLegs()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Knife));

        npc.Body.Sever(BodyPart.LegR);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Body.CanUseToolsOrWeapons, Is.False,
                "Heavy tool/weapon work remains forbidden while prone.");
            Assert.That(DecisionSystem.HasCoconutBlade(npc), Is.True,
                "Light coconut work remains possible while prone with a hand.");
            Assert.That(DecisionSystem.CanPerformDeclared(
                world, npc, ContentIds.Coconut, InteractionType.Process, legacyOk: false), Is.True);
            Assert.That(DecisionSystem.CanPerformDeclared(
                world, npc, ContentIds.Log, InteractionType.Process, legacyOk: false), Is.False);
        });

        npc.Body.Sever(BodyPart.ArmL);
        npc.Body.Sever(BodyPart.ArmR);
        Assert.Multiple(() =>
        {
            Assert.That(DecisionSystem.HasCoconutBlade(npc), Is.False,
                "Owning a knife is insufficient when neither hand can hold it.");
            Assert.That(DecisionSystem.CanPerformDeclared(
                world, npc, ContentIds.Coconut, InteractionType.Process, legacyOk: false), Is.False);
        });
    }

    [Test]
    public void SnapshotActingHand_PrefersRightThenFallsBackToLeftAndProsthesis()
    {
        var conditions = new List<BodyPartConditionSnapshot>
        {
            Arm(BodyPart.ArmL, severed: false),
            Arm(BodyPart.ArmR, severed: false)
        };

        AssertActingHand(conditions, true, BodyPart.ArmR);

        conditions[0] = Arm(BodyPart.ArmL, severed: true);
        AssertActingHand(conditions, true, BodyPart.ArmR);

        conditions[0] = Arm(BodyPart.ArmL, severed: false);
        conditions[1] = Arm(BodyPart.ArmR, severed: true);
        AssertActingHand(conditions, true, BodyPart.ArmL);

        conditions[0] = Arm(BodyPart.ArmL, severed: true);
        AssertActingHand(conditions, false, BodyPart.ArmR);

        conditions[1].Prosthetic = new ProstheticSnapshot
        {
            DefinitionId = ContentIds.WoodenArm,
            Part = BodyPart.ArmR,
            Function = Spec118.WoodenArmFunction,
            Condition = 0.5f,
            MaxCondition = 1f
        };
        AssertActingHand(conditions, true, BodyPart.ArmR);

        conditions[1].Prosthetic.Condition = 0.4f;
        AssertActingHand(conditions, false, BodyPart.ArmR);

        conditions[0] = Arm(BodyPart.ArmL, severed: false);
        AssertActingHand(conditions, true, BodyPart.ArmL);
    }

    private static BodyPartConditionSnapshot Arm(BodyPart part, bool severed) => new()
    {
        Part = part,
        Health = severed ? 0f : 1f,
        Severed = severed
    };

    private static void AssertActingHand(
        IReadOnlyList<BodyPartConditionSnapshot> conditions, bool expectedUsable, BodyPart expectedHand)
    {
        var usable = BodyPartFunctionSnapshotMath.TryGetActingHand(conditions, out var hand);
        Assert.Multiple(() =>
        {
            Assert.That(usable, Is.EqualTo(expectedUsable));
            Assert.That(hand, Is.EqualTo(expectedHand));
        });
    }
}

}
