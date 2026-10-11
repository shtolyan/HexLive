using HexLive.AgentCore.Studio;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.5/§171.7: the bond between ONE character and ONE account lives on the server.
/// It starts at zero and moves only through <see cref="VoiceRelationship"/>'s bounded steps.
/// </summary>
public sealed class RelationshipStore(string directory)
{
    private readonly object _gate = new();

    public VoiceRelationship Load(string characterId, string ownerUserId)
    {
        lock (_gate)
        {
            var path = PathFor(characterId, ownerUserId);
            return File.Exists(path) ? VoiceRelationship.Restore(File.ReadAllText(path))
                : new VoiceRelationship(new RelationshipSnapshot(0, 0, 0, "", null));
        }
    }

    public bool Apply(string characterId, string ownerUserId, string messageId, RelationshipAssessment assessment)
    {
        lock (_gate)
        {
            var relationship = Load(characterId, ownerUserId);
            if (!relationship.Apply([messageId], assessment, DateTimeOffset.UtcNow)) return false;
            Directory.CreateDirectory(directory);
            var path = PathFor(characterId, ownerUserId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, relationship.ExportState());
            File.Move(temp, path, true);
            return true;
        }
    }

    private string PathFor(string characterId, string ownerUserId) =>
        Path.Combine(directory, "bond-" + Safe(characterId) + "-" + Safe(ownerUserId) + ".json");

    private static string Safe(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(s)))[..24];
}
