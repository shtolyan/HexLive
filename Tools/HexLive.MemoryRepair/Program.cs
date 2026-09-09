using HexLive.AgentHost;

if (args.Length != 2 || args[0] != "--repair")
{
    Console.Error.WriteLine("Usage: --repair <existing-workspace>. Stop Studio/CLI and back up the workspace first.");
    return 2;
}
var root = Path.GetFullPath(args[1]);
foreach (var path in new[] { root, Path.Combine(root, ".state"), Path.Combine(root, ".state", "state.json") })
{
    if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("ExistingNonLinkedWorkspaceRequired");
}
// No integration, credential, network or model calls. The runtime performs the same migration on load.
var store = new MashaMemoryStore(root);
var archive = await store.SnapshotAsync(default);
Console.WriteLine($"Memory views repaired. Core facts: {archive.CoreMemories.Count}; speakers: {archive.Speakers.Count}; worlds: {archive.Worlds.Count}.");
return 0;
