#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace HexLive.UnityPresentation.Wearing.Editor
{
// §169.10: preserve authored settings when replacing the generated geometry.
public static class WearPreparationSettings
{
    public static void Restore(Wear target, Wear authored, ActorName actor, float defaultScale)
    {
        var output = new SerializedObject(target);
        if (authored != null)
        {
            var source = new SerializedObject(authored);
            foreach (string field in new[] { "slots", "layer", "noHideUnderwearSlots", "hideWearSlots", "heel", "headwearFit", "hidesHair", "gender" })
                output.CopyFromSerializedProperty(source.FindProperty(field));
            output.CopyFromSerializedProperty(source.FindProperty("configs"));
        }
        var configs = output.FindProperty("configs");
        output.FindProperty("preparedBodyFits").boolValue = true;
        bool found = false;
        for (int i = 0; i < configs.arraySize; i++)
        {
            var row = configs.GetArrayElementAtIndex(i);
            if (row.FindPropertyRelative("actorName").intValue == (int)actor) found = true;
            // Every config must point at the NEWLY exported body-specific mesh,
            // never retain a dependency on the source FBX's previous generation.
            row.FindPropertyRelative("mesh").objectReferenceValue = target.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
            row.FindPropertyRelative("hairLodMeshes").arraySize = 0;
        }
        if (!found)
        {
            int i = configs.arraySize; configs.arraySize++;
            var row = configs.GetArrayElementAtIndex(i);
            row.FindPropertyRelative("actorName").intValue = (int)actor;
            row.FindPropertyRelative("scale").floatValue = defaultScale;
            row.FindPropertyRelative("heightOffset").floatValue = 0;
            row.FindPropertyRelative("mesh").objectReferenceValue = target.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
            row.FindPropertyRelative("hairLodMeshes").arraySize = 0;
        }
        output.ApplyModifiedPropertiesWithoutUndo();
    }
}
}
#endif
