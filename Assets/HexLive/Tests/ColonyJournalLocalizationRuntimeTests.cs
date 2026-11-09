#if UNITY_EDITOR
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.History;
using HexLive.UnityPresentation.Localization;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class ColonyJournalLocalizationRuntimeTests
{
    [TestCase("DoorKnocked",0,false)]
    [TestCase("DoorOpenedForGuest",0,true)] [TestCase("DoorOpenedForGuest",1,true)]
    [TestCase("DoorRefusedGuest",0,true)] [TestCase("DoorRefusedGuest",1,true)]
    [TestCase("DoorBreakInStarted",0,false)] [TestCase("DoorBreakInStarted",1,true)]
    [TestCase("DoorBroken",0,false)] [TestCase("DoorBroken",1,true)]
    public void DoorDiaryUsesImportedFirstPersonTermsAndActualFormatter(string type,int perspective,bool mentionsName) =>
        AssertFormatted(type,perspective,mentionsName);

    internal static void AssertFormatted(string type,int perspective,bool mentionsName)
    {
        var original=Loc.Current;
        try
        {
            foreach(var language in new[]{Language.English,Language.Russian})
            foreach(var authorMale in new[]{false,true})
            {
                Loc.Current=language;
                var key="journal."+type+(perspective==0?".did.v0":".got.v0");
                Assert.That(Loc.Has(key),Is.True,key);
                var entry=new JournalEntrySnapshot {Type=type,Tick=4000,Perspective=perspective,
                    SubjectNameId=mentionsName?(authorMale?"kshishtof":"jolly"):string.Empty};
                var body=JournalFormatter.Format(entry,authorMale).Body;
                Assert.That(body,Is.Not.Empty.And.Not.EqualTo(type));
                Assert.That(body,Does.Not.Contain("journal.").And.Not.Contain("[[").And.Not.Contain("{0}"));
                if(mentionsName) Assert.That(body,Does.Contain(Loc.NpcName(entry.SubjectNameId)));
                if(language==Language.Russian) Assert.That(body,Does.Match("[А-Яа-яЁё]"));
                if(perspective==1)
                {
                    entry.Perspective=0;
                    Assert.That(JournalFormatter.Format(entry,authorMale).Body,Is.Not.EqualTo(body),
                        "The two participants need distinct first-person memories.");
                }
            }
        }
        finally {Loc.Current=original;}
    }
}
}
#endif
