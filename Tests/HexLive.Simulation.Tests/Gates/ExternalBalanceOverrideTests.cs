using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

public sealed class ExternalBalanceOverrideTests
{
    [Test]
    public void FlatOverrideAppliesEveryPopulationKnob()
    {
        var oldTotal = WorldBalance.MaxLivingNpcs;
        var oldColony = WorldBalance.MaxColonyNpcs;
        var oldOutsiders = WorldBalance.MaxOutsiderNpcs;
        var oldColonyDays = WorldBalance.ColonyArrivalIntervalDays;
        var oldRaidDays = Spec72.RaidWaveIntervalDays;
        try
        {
            var json = "{" +
                       "\"WorldBalance.MaxLivingNpcs\":12," +
                       "\"WorldBalance.MaxColonyNpcs\":7," +
                       "\"WorldBalance.MaxOutsiderNpcs\":5," +
                       "\"WorldBalance.ColonyArrivalIntervalDays\":9," +
                       "\"Spec72.RaidWaveIntervalDays\":4}";

            var ok = SimDataFile.TryApplyBalanceOverrides(
                json, out var applied, out var error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True, error);
                Assert.That(applied, Is.EqualTo(5));
                Assert.That(WorldBalance.MaxLivingNpcs, Is.EqualTo(12));
                Assert.That(WorldBalance.MaxColonyNpcs, Is.EqualTo(7));
                Assert.That(WorldBalance.MaxOutsiderNpcs, Is.EqualTo(5));
                Assert.That(WorldBalance.ColonyArrivalIntervalDays, Is.EqualTo(9));
                Assert.That(Spec72.RaidWaveIntervalDays, Is.EqualTo(4));
            });
        }
        finally
        {
            WorldBalance.MaxLivingNpcs = oldTotal;
            WorldBalance.MaxColonyNpcs = oldColony;
            WorldBalance.MaxOutsiderNpcs = oldOutsiders;
            WorldBalance.ColonyArrivalIntervalDays = oldColonyDays;
            Spec72.RaidWaveIntervalDays = oldRaidDays;
        }
    }

    [Test]
    public void InvalidKeyRejectsWholeFileWithoutPartialApply()
    {
        var oldTotal = WorldBalance.MaxLivingNpcs;
        try
        {
            var ok = SimDataFile.TryApplyBalanceOverrides(
                "{\"WorldBalance.MaxLivingNpcs\":99,\"WorldBalance.Typo\":1}",
                out var applied, out var error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(applied, Is.Zero);
                Assert.That(error, Does.Contain("WorldBalance.Typo"));
                Assert.That(WorldBalance.MaxLivingNpcs, Is.EqualTo(oldTotal),
                    "Валидная первая строка не должна примениться, если ниже в файле ошибка.");
            });
        }
        finally
        {
            WorldBalance.MaxLivingNpcs = oldTotal;
        }
    }

    [Test]
    public void FractionalIntegerIsRejected()
    {
        var ok = SimDataFile.TryApplyBalanceOverrides(
            "{\"WorldBalance.MaxLivingNpcs\":10.5}",
            out var applied, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(applied, Is.Zero);
            Assert.That(error, Does.Contain("Int32"));
        });
    }
}

}
