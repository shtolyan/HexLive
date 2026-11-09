using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HexLive.Simulation.Content;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Gates;
public sealed class LeatherFlaskArtContractTests
{
    [Test] public void NewVesselHasRealFbxRealOwnerSpriteGearRecipeAndBothTranslations()
    {
        var folder=Path.Combine(RepoPaths.Root,"Assets/HexLiveContent");
        var fbx=File.ReadAllBytes(Path.Combine(folder,"RuntimeSource/Objects/tool.flask_leather.fbx"));
        Assert.That(fbx.Length,Is.GreaterThan(1024));
        Assert.That(Encoding.ASCII.GetString(fbx,0,20),Does.StartWith("Kaydara FBX Binary"));
        var icon=Path.Combine(folder,"Icons/tool.flask_leather.png");var png=File.ReadAllBytes(icon);
        Assert.That(png.Take(8),Is.EqualTo(new byte[]{137,80,78,71,13,10,26,10}));
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)),Is.EqualTo(512));
        Assert.That(File.ReadAllText(icon+".meta"),Does.Contain("textureType: 8"));
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root,"Tools/world_prop_manifest.json")));
        var owner=manifest.RootElement.GetProperty("entries").EnumerateArray().Single(x=>x.GetProperty("id").GetString()==ContentIds.LeatherFlask);
        Assert.That(owner.GetProperty("native").GetString(),Is.EqualTo("tool.flask_leather.fbx"));
        Assert.That(owner.GetProperty("materialSlots").GetArrayLength(),Is.EqualTo(3));
        var gear=File.ReadAllText(Path.Combine(folder,"RuntimeSource/Gear/flask_leather.asset"));
        Assert.That(gear,Does.Contain("gearId: "+ContentIds.LeatherFlask).And.Contain("craftable: 1").And.Contain("craftStation: 2"));
        Assert.That(gear,Does.Contain("itemId: resource.hide").And.Contain("itemId: resource.rope"));
        var terms=RepoPaths.ReadText(Path.Combine(RepoPaths.Root,"Assets/Resources/I2Languages.asset"));
        var sequence=terms.IndexOf("    mTerms:\n",StringComparison.Ordinal);
        Assert.That(sequence,Is.GreaterThan(0));
        foreach(var key in new[]{"item.tool_flask_leather.name","item.tool_flask_leather.desc"})
            Assert.That(terms.IndexOf("    - Term: "+key+"\n",sequence,StringComparison.Ordinal),Is.GreaterThan(sequence));
    }
}
