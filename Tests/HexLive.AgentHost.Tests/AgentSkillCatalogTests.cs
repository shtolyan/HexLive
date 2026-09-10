using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentSkillCatalogTests
{
    [Test]
    public void CandidateIsHiddenAndValidatedSkillRefreshesWithoutAffectingOtherProfiles()
    {
        var root = Directory.CreateTempSubdirectory("skill-profile-").FullName;
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "skills"));
            var path = Path.Combine(directory.FullName, "catalog.json");
            File.WriteAllText(path, """{"custom-task":{"status":"candidate","procedure":["unverified"]}}""");
            Assert.That(AgentSkillCatalog.Read(root).ContainsKey("custom-task"), Is.False);
            void Publish(int version) => File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["custom-task"] = new { status = "validated", version, title = "Custom", procedure = new[] { "Read current recipes" }, validationReport = "fixture-report.json" }
            }));
            Publish(1);
            Assert.That(AgentSkillCatalog.Read(root)["custom-task"].GetProperty("version").GetInt32(), Is.EqualTo(1));
            Publish(2);
            Assert.That(AgentSkillCatalog.Read(root)["custom-task"].GetProperty("version").GetInt32(), Is.EqualTo(2));
            Assert.That(AgentSkillCatalog.Read(null).ContainsKey("custom-task"), Is.False);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InvalidEvidenceOrDowngradeCannotReplaceBundledSkill(bool downgrade)
    {
        var root = Directory.CreateTempSubdirectory("skill-invalid-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "skills"));
            File.WriteAllText(Path.Combine(root, "skills/catalog.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["build-bed"] = new { status = "validated", version = downgrade ? 1 : 100, title = "Bed", procedure = new[] { "Build" }, validationReport = downgrade ? "report.json" : "" }
            }));
            Assert.Throws<InvalidDataException>(() => AgentSkillCatalog.Read(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
