#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Editor
{
    // §169: keep body surfaces in the FBX, including all original morph names.
    public sealed class PeopleSourceModelPostprocessor : AssetPostprocessor
    {
        public override uint GetVersion() => 1;
        private void OnPostprocessModel(GameObject model)
        {
            if (assetPath == PeopleAssetPreparation.Root + "/Source/Models/Marta.fbx" ||
                assetPath == PeopleAssetPreparation.Root + "/Source/Models/Kshishtof.fbx")
                PeopleAssetPreparation.SplitImportedBodySurfaces(model);
        }
    }
}
#endif
