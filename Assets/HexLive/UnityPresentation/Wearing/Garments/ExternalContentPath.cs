using System;
using System.IO;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace HexLive.UnityPresentation.Wearing.Garments
{

/// <summary>
/// One cross-platform home for content shipped separately from the player.
/// Several player builds in one folder intentionally share this directory.
/// </summary>
public static class ExternalContentPath
{
    public const string FolderName = "HexLiveContent";

    public static string Root
    {
        get
        {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
            // macOS: dataPath = <build>/Game.app/Contents. Two parents reach
            // the distribution folder beside the .app.
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", FolderName));
#else
            // Windows/Linux: dataPath = <build>/Game_Data. One parent reaches
            // the distribution folder beside the executable. In the editor it
            // resolves to the project root, preserving the old behaviour.
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", FolderName));
#endif
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InstallAddressablesPath()
    {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        // Existing catalogs were authored with
        //   {Application.dataPath}/../HexLiveContent
        // which means INSIDE the .app on macOS. Rewrite only that legacy root
        // to the shared sibling folder. Bundle/catalog files themselves remain
        // byte-for-byte reusable — no Addressables rebuild is required.
        var legacyRoot = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", FolderName));
        var sharedRoot = Root;
        var previous = Addressables.InternalIdTransformFunc;
        Addressables.InternalIdTransformFunc = location =>
            Rewrite(previous != null ? previous(location) : location.InternalId,
                legacyRoot, sharedRoot);
#endif
    }

    private static string Rewrite(string internalId, string legacyRoot, string sharedRoot)
    {
        if (string.IsNullOrEmpty(internalId) || !Path.IsPathRooted(internalId))
        {
            return internalId;
        }

        string full;
        try
        {
            full = Path.GetFullPath(internalId);
        }
        catch (Exception)
        {
            return internalId;
        }

        if (!full.StartsWith(legacyRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return internalId;
        }

        return sharedRoot + full.Substring(legacyRoot.Length);
    }
}

}
