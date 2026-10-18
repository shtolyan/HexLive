using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §58.9: грамматический род в переводах. Мужчина говорил «я устала», потому что
/// все термины были написаны в женском роде; теперь мужская форма живёт в самом
/// термине разметкой <c>[[слот:женская|мужская]]</c>. Битая разметка вылезла бы
/// игроку скобками — её ловит гейт по всему ассету.
/// </summary>
public sealed class GenderedTextGateTests
{
    [Test]
    public void ResolvesEachSlotByItsOwnPerson()
    {
        const string line = "Я [[s:ждала|ждал]] {0}. [[0:Она не пришла|Он не пришёл]].";
        Assert.Multiple(() =>
        {
            Assert.That(GenderedText.Resolve(line, new GenderSlots(false, false)), Is.EqualTo("Я ждала {0}. Она не пришла."));
            Assert.That(GenderedText.Resolve(line, new GenderSlots(true, false)), Is.EqualTo("Я ждал {0}. Она не пришла."));
            Assert.That(GenderedText.Resolve(line, new GenderSlots(false, true)), Is.EqualTo("Я ждала {0}. Он не пришёл."));
            Assert.That(GenderedText.Resolve(line, new GenderSlots(true, true)), Is.EqualTo("Я ждал {0}. Он не пришёл."));
            Assert.That(GenderedText.Feminine("{0} [[0:помогла|помог]] {1}."), Is.EqualTo("{0} помогла {1}."));
            Assert.That(GenderedText.Resolve("[[s:Её|Его]] словами", GenderSlots.Of(true)), Is.EqualTo("Его словами"));
            Assert.That(GenderedText.Resolve("без разметки", GenderSlots.Of(true)), Is.EqualTo("без разметки"));
            Assert.That(GenderedText.Resolve("[[x:a|b]] [[s:c|d]]", GenderSlots.Of(true)), Is.EqualTo("[[x:a|b]] d"),
                "unknown slot stays visible, like a missing key");
        });
    }

    [Test]
    public void MaleBodiesAreMale()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new NPCState { ActorMesh = "Kshishtof" }.Sex, Is.EqualTo(GarmentSex.Male));
            Assert.That(new NPCState { ActorMesh = "Tonny" }.Sex, Is.EqualTo(GarmentSex.Male));
            Assert.That(new NPCState { ActorMesh = "Jolly" }.Sex, Is.EqualTo(GarmentSex.Female));
        });
    }

    private static readonly Regex Markup = new(@"\[\[([^:\]\r\n]*):([^|\]\r\n]*)\|([^\]\r\n]*)\]\]");

    [Test]
    public void EveryI2MarkupIsWellFormed()
    {
        var asset = Path.Combine(RepoPaths.Root, "Assets", "Resources", "I2Languages.asset");
        var broken = File.ReadAllLines(asset)
            .Select((line, i) => (line, i))
            .Where(x => x.line.Contains("[[") || x.line.Contains("]]"))
            .Where(x =>
            {
                var rest = Markup.Replace(x.line, "");
                return rest.Contains("[[") || rest.Contains("]]") ||
                       Markup.Matches(x.line).Any(m => m.Groups[1].Value is not ("s" or "0" or "1" or "2"));
            })
            .Select(x => $"I2Languages.asset:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.That(broken, Is.Empty, "§58.9 markup is [[s|0|1|2:feminine|masculine]] on one line");
    }
}

}
