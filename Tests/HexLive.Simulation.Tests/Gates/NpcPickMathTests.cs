using System;
using HexLive.UnityPresentation.Input;
using NUnit.Framework;
using P=HexLive.UnityPresentation.Input.NpcPickMath.Point;
namespace HexLive.Simulation.Tests.Gates;
public sealed class NpcPickMathTests
{
    [TestCase(0f,.2f)] [TestCase(.18f,.4f)] [TestCase(-.18f,1.2f)]
    public void BodyInteriorAndNearMissRemainClickableAcrossMeshGaps(float x,float y)
    {
        Assert.That(NpcPickMath.Capsule(new P(x,y,-3),new P(0,0,1),new P(0,.25f,0),new P(0,1.15f,0),.22f,20,out var hit),Is.True);
        Assert.That(hit,Is.InRange(2.77f,3f));
    }
    [Test] public void SleepingCapsuleTracksHorizontalPoseInsteadOfStandingAtTheFeet()
    {
        Assert.That(NpcPickMath.Capsule(new P(.7f,2,0),new P(0,-1,0),new P(0,.1f,0),new P(1.2f,.1f,0),.2f,5,out var hit),Is.True);
        Assert.That(hit,Is.EqualTo(1.7f).Within(.0001));
        Assert.That(NpcPickMath.Capsule(new P(0,2,1),new P(0,-1,0),new P(0,.1f,0),new P(1.2f,.1f,0),.2f,5,out _),Is.False);
    }
    [Test] public void ParallelRayHitsCapsAndOriginInsideHasZeroDistance()
    {
        Assert.That(NpcPickMath.Capsule(new P(0,3,0),new P(0,-2,0),new P(0,0,0),new P(0,1,0),.2f,5,out var hit),Is.True);
        Assert.That(hit,Is.EqualTo(1.8f).Within(.0001));
        Assert.That(NpcPickMath.Capsule(new P(0,.5f,0),new P(0,1,0),new P(0,0,0),new P(0,1,0),.2f,5,out hit),Is.True);
        Assert.That(hit,Is.Zero);
    }
    [Test] public void ClosestBodyWinsAndTargetsBehindCameraOrBeyondWinnerNeverHit()
    {
        var origin=new P(0,.6f,-3);var direction=new P(0,0,1);
        Assert.That(NpcPickMath.Capsule(origin,direction,new P(0,0,0),new P(0,1,0),.2f,9,out var first),Is.True);
        Assert.That(NpcPickMath.Capsule(origin,direction,new P(0,0,1),new P(0,1,1),.2f,first,out _),Is.False);
        Assert.That(NpcPickMath.Capsule(origin,direction,new P(0,0,-5),new P(0,1,-5),.2f,9,out _),Is.False);
    }
    [Test] public void DegenerateCapsuleIsSphereAndZeroRayDoesNotHit()
    {
        Assert.That(NpcPickMath.Capsule(new P(0,0,-2),new P(0,0,1),new P(0,0,0),new P(0,0,0),.2f,5,out var hit),Is.True);
        Assert.That(hit,Is.EqualTo(1.8f).Within(.0001));
        Assert.That(NpcPickMath.Capsule(new P(0,0,-2),new P(0,0,0),new P(0,0,0),new P(0,1,0),.2f,5,out _),Is.False);
    }
}
