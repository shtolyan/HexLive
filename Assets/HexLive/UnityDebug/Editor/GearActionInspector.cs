using System.Linq;
using HexLive.UnityPresentation.Config;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.Editor
{
// §59.5: inspect the existing item card and its capability-matched actions
// together; no second editable action catalogue is introduced.
[CustomEditor(typeof(GearConfig))]
public sealed class GearActionInspector : UnityEditor.Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();
        InspectorElement.FillDefaultInspector(root, serializedObject, this);
        var actions = new Foldout { text = "Действия этого инструмента", value = true };
        root.Add(actions);
        void Refresh()
        {
            actions.Clear();
            var gear = ((GearConfig)target).ToStats();
            foreach (var guid in AssetDatabase.FindAssets("t:WorldObjectConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<WorldObjectConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config?.actions == null) continue;
                foreach (var action in config.actions)
                {
                    if (action?.requiredCapabilities == null ||
                        !action.requiredCapabilities.Any(capability => gear.Has(capability))) continue;
                    var owner = config;
                    actions.Add(new Button(() => Selection.activeObject = owner)
                    {
                        text = config.objectId + " → " + action.actionId,
                        tooltip = "Открыть действие: требования, время и результат"
                    });
                }
            }
            if (actions.childCount == 0) actions.Add(new Label("Нет действий с подходящей способностью."));
        }
        root.TrackSerializedObjectValue(serializedObject, _ => Refresh());
        Refresh();
        return root;
    }
}
}
