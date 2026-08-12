using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class ProstheticJumpTests
{
    [Test]
    public void FunctionalWoodenLeg_RestoresJumpSupport()
    {
        var body = new BodyState();
        body.Sever(BodyPart.LegL);

        Assert.That(body.CanJump, Is.False, "A bare stump cannot support a jump.");

        body.Condition(BodyPart.LegL).Prosthetic = Prosthetic(
            ContentIds.WoodenLeg,
            BodyPart.LegL,
            Spec118.WoodenLegFunction,
            Spec118.WoodenProstheticDurability);

        Assert.Multiple(() =>
        {
            Assert.That(body.LimbFunction(BodyPart.LegL), Is.LessThan(0.75f),
                "The wooden leg keeps its intended movement penalty.");
            Assert.That(body.CanJump, Is.True,
                "A fitted functional prosthesis must restore access to jump edges.");
        });
    }

    [Test]
    public void JumpStillRequiresAWorkingSupportOnBothSides()
    {
        var body = new BodyState();
        body.Sever(BodyPart.LegL);
        body.Condition(BodyPart.LegL).Prosthetic = Prosthetic(
            ContentIds.MechanicalLeg,
            BodyPart.LegL,
            Spec118.MechanicalLegFunction,
            condition: 0f);

        Assert.That(body.CanJump, Is.False,
            "A broken prosthesis must behave like an unsupported stump.");

        body.Condition(BodyPart.LegL).Prosthetic = Prosthetic(
            ContentIds.MechanicalLeg,
            BodyPart.LegL,
            Spec118.MechanicalLegFunction,
            Spec118.MechanicalProstheticDurability);
        body.Parts[BodyPart.LegR] = 0.60f;

        Assert.That(body.CanJump, Is.False,
            "The prosthesis does not bypass a seriously injured natural leg.");
    }

    private static ProstheticState Prosthetic(
        string definitionId,
        BodyPart part,
        float function,
        float condition) => new()
    {
        DefinitionId = definitionId,
        Part = part,
        Function = function,
        Condition = condition,
        MaxCondition = definitionId == ContentIds.MechanicalLeg
            ? Spec118.MechanicalProstheticDurability
            : Spec118.WoodenProstheticDurability,
        Mechanical = definitionId == ContentIds.MechanicalLeg
    };
}

}
