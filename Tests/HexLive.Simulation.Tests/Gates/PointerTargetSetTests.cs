using System.Linq;
using HexLive.UnityPresentation.Input;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Gates;
public sealed class PointerTargetSetTests
{
    [Test] public void OverlappingPersonAndItemAreBothChoicesEvenWhenIdsMatch()
    {
        var set=new PointerTargetSet();
        set.Add(PointerTargetKind.Person,26,3);set.Add(PointerTargetKind.Object,26,2);
        set.Sort();Assert.That(set.Items.Select(x=>x.Kind),Is.EqualTo(new[]{PointerTargetKind.Object,PointerTargetKind.Person}));
    }
    [Test] public void MultipleHouseSectionsProxyToOneChoiceWithNearestDepth()
    {
        var set=new PointerTargetSet();set.Add(PointerTargetKind.Object,10,8);set.Add(PointerTargetKind.Object,10,2);
        set.Add(PointerTargetKind.Person,26,3);set.Sort();
        Assert.That(set.Items.Count,Is.EqualTo(2));Assert.That(set.Items[0].Distance,Is.EqualTo(2));
    }
    [Test] public void AllCoincidentItemsArePreservedAndTiesHaveStableIdentityOrder()
    {
        var set=new PointerTargetSet();for(int i=20;i>0;i--)set.Add(PointerTargetKind.Object,i,2);
        set.Sort();Assert.That(set.Items.Select(x=>x.Id),Is.EqualTo(Enumerable.Range(1,20)));
        set.Clear();Assert.That(set.Items,Is.Empty);
    }
    [Test] public void InvalidDepthDoesNotLeakInvisibleBehindCameraTargetsAndNegativeCrabIdIsValid()
    {
        var set=new PointerTargetSet();set.Add(PointerTargetKind.Object,1,-1);set.Add(PointerTargetKind.Object,2,float.NaN);
        set.Add(PointerTargetKind.Object,3,float.PositiveInfinity);set.Add(PointerTargetKind.Wildlife,-9,2);
        Assert.That(set.Items.Count,Is.EqualTo(1));Assert.That(set.Items[0].Id,Is.EqualTo(-9));
    }
}
