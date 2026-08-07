using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class LocomotionTurnPolicyTests
{
    [TestCase(60f, false)]
    [TestCase(90f, false)]
    [TestCase(100f, false)]
    [TestCase(120f, true)]
    [TestCase(180f, true)]
    public void PlantedPivotUsesTheRequestedAngleBeforeRotation(float requestedAngle, bool expected)
    {
        Assert.That(LocomotionTurnPolicy.RequiresPlantedPivot(requestedAngle), Is.EqualTo(expected));
    }

    [Test]
    public void SixtyDegreeHexBendKeepsFullPaceAfterOneTurnTick()
    {
        // Default turn pace resolves 54° of a 60° bend in one simulation tick.
        var residual = 60f - 90f * SimBalance.BaseTurnSpeedFactor * 0.25f;
        Assert.That(LocomotionTurnPolicy.AlignmentFactor(60f, residual), Is.EqualTo(1f));
    }

    [Test]
    public void NinetyDegreeTurnSlowsWhileRotationIsStillVisible()
    {
        var residual = 90f - 90f * SimBalance.BaseTurnSpeedFactor * 0.25f;
        var factor = LocomotionTurnPolicy.AlignmentFactor(90f, residual);
        Assert.That(factor, Is.LessThan(1f));
        Assert.That(factor, Is.GreaterThanOrEqualTo(SimBalance.TurnMinSpeedFactor));
    }
}

}
