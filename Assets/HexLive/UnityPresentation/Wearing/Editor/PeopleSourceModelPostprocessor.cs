#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Editor
{
    // §169: keep body surfaces in the FBX, including all original morph names.
    public sealed class PeopleSourceModelPostprocessor : AssetPostprocessor
    {
        public override uint GetVersion() => 2;
        private void OnPostprocessModel(GameObject model)
        {
            if (assetPath == PeopleAssetPreparation.Root + "/Source/Models/Marta.fbx" ||
                assetPath == PeopleAssetPreparation.Root + "/Source/Models/Kshishtof.fbx")
            {
                PeopleAssetPreparation.SplitImportedBodySurfaces(model);
                // Importer MikkTSpace splits vertices at tangent seams, invalidating
                // the existing vertex-indexed paint maps. Calculate on the final
                // mesh instead, retaining its vertices, UVs and submesh indices.
                foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    renderer.sharedMesh.RecalculateTangents();
            }
        }
    }
}
#endif
