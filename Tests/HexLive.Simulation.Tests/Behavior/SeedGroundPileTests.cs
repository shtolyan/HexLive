using System;
using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class SeedGroundPileTests
{
    [TestCase(ContentIds.HerbSeed)]
    [TestCase(ContentIds.YuccaSeed)]
    public void FullHandfulIsGroundedDistinctStableAndClearAtAnyYaw(string id)
    {
        Assert.That(GroundPileCatalog.TryGet(id, false, out var profile), Is.True);
        Assert.That(profile.TargetSize, Is.EqualTo(.12f).Within(.00001f));
        var count = GroundPileCatalog.Capacity(id);
        Assert.That(count, Is.EqualTo(20));
        var poses = Enumerable.Range(0, count).Select(i => profile.Slot(i, count, i + 1000)).ToArray();
        Assert.That(poses.All(p => p.Y == 0), Is.True, "Every seed must touch its support, not a virtual upper layer.");
        Assert.That(poses.Select(p => p.Yaw).Distinct().Count(), Is.GreaterThan(10));
        for (int i = 0; i < count; i++)
        {
            var again = profile.Slot(i, count, i + 1000);
            Assert.That((again.X, again.Y, again.Z, again.Yaw), Is.EqualTo((poses[i].X, poses[i].Y, poses[i].Z, poses[i].Yaw)));
            Assert.That(Math.Abs(poses[i].X), Is.LessThan(.43f));
            Assert.That(Math.Abs(poses[i].Z), Is.LessThan(.25f));
            for (int j = 0; j < i; j++)
            {
                var dx = poses[i].X - poses[j].X; var dz = poses[i].Z - poses[j].Z;
                Assert.That(Math.Sqrt(dx * dx + dz * dz), Is.GreaterThan(Math.Sqrt(2) * profile.TargetSize),
                    "Fitted XZ bounding circles must clear at every random turn.");
            }
        }
    }
}
