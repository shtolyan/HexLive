using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §105 r3: the number on the portrait. It must notice a smashed limb — r2
/// showed the worst vital zone alone, so a colonist with a shattered leg read
/// "100%" — without ever reading BETTER than the vital state, which is the
/// mistake that killed the plain seven-zone average in the first place.
/// </summary>
public sealed class DisplayHealthTests
{
    private static BodyState Body(
        float head = 1f, float torso = 1f, float pelvis = 1f,
        float armL = 1f, float armR = 1f, float legL = 1f, float legR = 1f)
    {
        var body = new BodyState();
        body.Parts[BodyPart.Head] = head;
        body.Parts[BodyPart.Torso] = torso;
        body.Parts[BodyPart.Pelvis] = pelvis;
        body.Parts[BodyPart.ArmL] = armL;
        body.Parts[BodyPart.ArmR] = armR;
        body.Parts[BodyPart.LegL] = legL;
        body.Parts[BodyPart.LegR] = legR;
        return body;
    }

    [Test]
    public void IntactBodyReadsFull()
    {
        Assert.That(Body().DisplayHealth(), Is.EqualTo(1f).Within(0.0001f));
    }

    [Test]
    public void SmashedLimbMovesTheNumber()
    {
        var oneLegGone = Body(legL: 0f).DisplayHealth();
        Assert.That(oneLegGone, Is.LessThan(0.999f),
            "A destroyed leg must not read as a healthy colonist.");
        Assert.That(oneLegGone, Is.EqualTo(0.90f).Within(0.0001f));
        Assert.That(Body(legL: 0f, legR: 0f, armL: 0f, armR: 0f).DisplayHealth(),
            Is.EqualTo(0.60f).Within(0.0001f));
    }

    [Test]
    public void IntactLimbsCannotHideAPiercedVital()
    {
        var body = Body(torso: 0.2f);
        Assert.That(body.DisplayHealth(), Is.EqualTo(body.VitalHealth()).Within(0.0001f),
            "Whole arms and legs must never lift the number above the vital state — " +
            "that is exactly what the seven-zone average got wrong.");
        Assert.That(body.DisplayHealth(), Is.EqualTo(0.2f).Within(0.0001f));
    }

    [Test]
    public void NeverReadsAboveTheWorstVitalZone()
    {
        foreach (var vital in new[] { 0f, 0.15f, 0.5f, 0.85f })
        {
            var body = Body(head: vital, legL: 0.3f);
            Assert.That(body.DisplayHealth(), Is.LessThanOrEqualTo(body.VitalHealth() + 0.0001f));
        }
    }

    [Test]
    public void LimbDamageOnlyEverLowersTheNumber()
    {
        var healthy = Body(torso: 0.7f).DisplayHealth();
        var mauled = Body(torso: 0.7f, armR: 0.4f).DisplayHealth();
        Assert.That(mauled, Is.LessThanOrEqualTo(healthy));
    }
}

}
