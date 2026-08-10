using System;
using System.Collections.Generic;
using System.IO;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>Uniform importer and build contract for every shipped actor body.</summary>
    public sealed class ActorBodyFbxGate : AssetPostprocessor, IPreprocessBuildWithReport
    {
        private const string ActorPrefabsRoot = "Assets/Resources/HexLive/Actors";

        public int callbackOrder => -100;

        private void OnPreprocessModel()
        {
            if (assetImporter is not ModelImporter importer ||
                !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                !IsActorBodyFbx(assetPath))
            {
                return;
            }

            importer.isReadable = true;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var errors = ValidateAllActors();
            if (errors.Count > 0)
            {
                throw new BuildFailedException(
                    "Actor body FBX contract failed:\n" + string.Join("\n", errors));
            }
        }

        [MenuItem("HexLive/Validate/Actor body FBX contract")]
        private static void ValidateFromMenu()
        {
            var errors = ValidateAllActors();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }
            Debug.Log("[ActorBodyFbxGate] Every actor prefab has one readable FBX body.");
        }

        private static List<string> ValidateAllActors()
        {
            var errors = new List<string>();
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { ActorPrefabsRoot });
            foreach (var guid in prefabGuids)
            {
                var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    errors.Add($"{prefabPath}: prefab cannot be loaded");
                    continue;
                }

                if (!ActorBodyResolver.TryResolve(prefab, out var body, out var error))
                {
                    errors.Add($"{prefabPath}: {error}");
                    continue;
                }

                var meshPath = AssetDatabase.GetAssetPath(body.sharedMesh);
                if (!meshPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{prefabPath}: primary body mesh is not an FBX ({meshPath})");
                    continue;
                }

                if (AssetImporter.GetAtPath(meshPath) is not ModelImporter importer ||
                    !importer.isReadable)
                {
                    errors.Add($"{prefabPath}: body FBX is not Read/Write enabled ({meshPath})");
                }

                var animator = prefab.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    errors.Add($"{prefabPath}: Animator is missing");
                }
                else
                {
                    if (animator.avatar == null || !animator.avatar.isValid ||
                        !animator.avatar.isHuman)
                    {
                        errors.Add($"{prefabPath}: Animator requires a valid Humanoid Avatar");
                    }
                    else
                    {
                        var avatarPath = AssetDatabase.GetAssetPath(animator.avatar);
                        if (!avatarPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"{prefabPath}: Animator Avatar is not embedded in an FBX ({avatarPath})");
                        }
                        else if (AssetImporter.GetAtPath(avatarPath) is not ModelImporter avatarImporter ||
                                 avatarImporter.animationType != ModelImporterAnimationType.Human ||
                                 avatarImporter.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                        {
                            errors.Add($"{prefabPath}: Avatar FBX must create its own Humanoid Avatar ({avatarPath})");
                        }
                    }
                }

                foreach (var dependency in AssetDatabase.GetDependencies(prefabPath, true))
                {
                    if (IsLegacyNativeActorGeometry(dependency))
                    {
                        errors.Add($"{prefabPath}: references legacy native actor asset {dependency}");
                    }
                }
            }

            return errors;
        }

        private static bool IsActorBodyFbx(string candidatePath)
        {
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { ActorPrefabsRoot });
            foreach (var guid in prefabGuids)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (prefab != null && ActorBodyResolver.TryResolve(prefab, out var body, out _) &&
                    string.Equals(
                        AssetDatabase.GetAssetPath(body.sharedMesh), candidatePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsLegacyNativeActorGeometry(string path)
        {
            if (!path.StartsWith("Assets/ImportedActors/Actors/", StringComparison.Ordinal) ||
                path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".mesh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileNameWithoutExtension(path).Contains(
                       "Avatar", StringComparison.OrdinalIgnoreCase);
        }
    }
}
