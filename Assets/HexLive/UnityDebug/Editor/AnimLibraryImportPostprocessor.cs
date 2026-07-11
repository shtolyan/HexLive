using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

/// <summary>
/// Auto-configures any animation FBX dropped into Assets/ImportedActors/AnimLibrary:
/// Humanoid rig (retargets onto Molly/Jana via Mecanim), no materials, in-place root
/// (spec 31B.5: the simulation is the only mover).
///
/// Handles both shapes of source file:
///  - Single-clip Mixamo FBX: the lone clip (Mixamo names them all "mixamo.com") is
///    renamed after the file. Loops by default; suffix the file "_once" for one-shots.
///    Suffix "_toNNN" trims the clip's tail to frame NNN (e.g. "LieDown_once_to166");
///    suffix "_fromNNN" trims its head. Both are stripped from the final clip name.
///  - Multi-clip packs (e.g. Quaternius Universal Animation Library, ~50 clips in one
///    FBX): the pack's own clip names are kept as-is, and each clip loops only when its
///    name ends in "_Loop" (the pack's convention for cyclic motions).
///
/// Defaults are applied ON FIRST IMPORT ONLY (while the .meta file doesn't exist yet).
/// After that the file belongs to the user: inspector tweaks survive Apply/reimport.
/// To re-apply the defaults, delete the .fbx.meta and let Unity reimport.
/// </summary>
public sealed class AnimLibraryImportPostprocessor : AssetPostprocessor
{
    private const string AnimLibraryRoot = "Assets/ImportedActors/AnimLibrary";

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(AnimLibraryRoot) || !assetImporter.importSettingsMissing)
            return;

        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.importCameras = false;
        importer.importLights = false;
    }

    private void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(AnimLibraryRoot) || !assetImporter.importSettingsMissing)
            return;

        var importer = (ModelImporter)assetImporter;
        var clips = importer.defaultClipAnimations;
        var singleClip = clips.Length == 1;

        // Parse the file-name suffixes ("_once", "_fromNNN", "_toNNN") in any order,
        // stripping each so what remains is the clean clip name.
        var fileName = Path.GetFileNameWithoutExtension(assetPath);
        var fileWantsOneShot = false;
        var trimFirst = -1f;
        var trimLast = -1f;
        for (var stripped = true; stripped;)
        {
            stripped = false;
            if (fileName.EndsWith("_once", System.StringComparison.OrdinalIgnoreCase))
            {
                fileWantsOneShot = true;
                fileName = fileName.Substring(0, fileName.Length - "_once".Length);
                stripped = true;
            }
            var range = Regex.Match(fileName, "_(from|to)(\\d+)$", RegexOptions.IgnoreCase);
            if (range.Success)
            {
                var frame = float.Parse(range.Groups[2].Value);
                if (range.Groups[1].Value.Equals("from", System.StringComparison.OrdinalIgnoreCase))
                    trimFirst = frame;
                else
                    trimLast = frame;
                fileName = fileName.Substring(0, range.Index);
                stripped = true;
            }
        }

        for (var i = 0; i < clips.Length; i++)
        {
            if (singleClip)
            {
                // Mixamo names every clip "mixamo.com" — use the file name instead,
                // and take the loop flag from the "_once" file-name suffix.
                clips[i].name = fileName;
                clips[i].loopTime = !fileWantsOneShot;
                if (trimFirst >= 0f)
                    clips[i].firstFrame = trimFirst;
                if (trimLast >= 0f)
                    clips[i].lastFrame = trimLast;
            }
            else
            {
                // A named pack: keep the pack's clip names; loop only cyclic "_Loop" clips.
                clips[i].loopTime = clips[i].name.EndsWith("_Loop", System.StringComparison.OrdinalIgnoreCase);
            }

            clips[i].lockRootRotation = true;
            clips[i].lockRootHeightY = true;
            clips[i].keepOriginalPositionXZ = true;
            clips[i].keepOriginalPositionY = true;
            clips[i].keepOriginalOrientation = true;
        }

        importer.clipAnimations = clips;
    }
}
