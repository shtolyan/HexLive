using System.Collections.Generic;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PeopleAppearanceRollTests
{
    [Test]
    public void SingleBodyKeepsDeterministicSkinVoiceAndUniqueColonyLooks()
    {
        var skins = new HashSet<string>();
        var voices = new HashSet<string>();
        for (int seed = 0; seed < 64; seed++)
        {
            var names = new HashSet<string>();
            var looks = new HashSet<string>();
            var hair = new HashSet<string>();
            for (int id = 1; id <= 16; id++)
            {
                var a = ColonistAppearance.Roll(seed, id, names, looks, hair);
                var b = ColonistAppearance.Roll(seed, id, names, looks, hair);
                Assert.That(a.Mesh, Is.EqualTo("Marta"));
                Assert.That((a.Mesh, a.SkinSet, a.VoiceBank, a.Hairstyle, a.NameId),
                    Is.EqualTo((b.Mesh, b.SkinSet, b.VoiceBank, b.Hairstyle, b.NameId)));
                Assert.That(names.Add(a.NameId), Is.True);
                Assert.That(hair.Add(a.Hairstyle), Is.True);
                Assert.That(looks.Add(ColonistAppearance.LookKey(a.Mesh, a.SkinSet, a.Hairstyle)), Is.True);
                skins.Add(a.SkinSet); voices.Add(a.VoiceBank);
            }
        }
        Assert.That(skins, Is.EquivalentTo(ColonistAppearance.SkinSets));
        Assert.That(voices, Is.EquivalentTo(ColonistAppearance.VoiceBanks));
    }
}
