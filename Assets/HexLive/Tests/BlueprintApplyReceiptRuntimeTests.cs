#if UNITY_EDITOR
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class BlueprintApplyReceiptRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
    private static object Get(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);

    [TestCase(false)]
    [TestCase(true)]
    public void PendingApplyKeepsDraftAndPreventsDuplicateOrEditsThenAllowsRetry(bool reject)
    {
        var host = new GameObject("663 isolated real constructor"); host.SetActive(false);
        var designer = host.AddComponent<HutLayoutDesigner>();
        var draft = BuiltInBuildingBlueprints.Hut1Hex();
        var json = BuildingBlueprintJson.Serialize(draft, false);
        var sends = 0;
        Set(designer, "_draft", draft);
        Set(designer, "_worldPlacement", new HutLayoutDesigner.WorldPlacementConfig {
            ExistingOwner = new ObjectId(123), IsRevisionApplied = expected => false,
            OnBuild = d => { Assert.That(BuildingBlueprintJson.Serialize(d, false), Is.EqualTo(json)); sends++; }
        });
        try
        {
            Call(designer, "BuildAndClose");
            Assert.That(Get(designer, "_pendingRevisionJson"), Is.EqualTo(json));
            Assert.That(Get(designer, "_statusKey"), Is.EqualTo("blueprint.status.applying"));
            Call(designer, "BuildAndClose");
            Call(designer, "OnHtmlCommand", "undo", new JObject());
            Assert.That(sends, Is.EqualTo(1));
            Assert.That(BuildingBlueprintJson.Serialize(draft, false), Is.EqualTo(json));
            if (reject)
            {
                ManualOrderFeedback.ReportWorld("Order=UpdateBuildingBlueprint Reason=PlacementBlocked");
                Call(designer, "RefreshWorldRejection");
                Assert.That(Get(designer, "_statusKey"), Is.EqualTo("toast.order_rejected.PlacementBlocked"));
            }
            else
            {
                Set(designer, "_pendingRevisionAt", Time.unscaledTime - 16f);
                Call(designer, "RefreshRevisionReceipt");
                Assert.That(Get(designer, "_statusKey"), Is.EqualTo("blueprint.status.apply_timeout"));
            }
            Assert.That(Get(designer, "_pendingRevisionJson"), Is.Null);
            Assert.That(designer != null, Is.True, "A failed Apply must not close the actual constructor.");
            Assert.That(BuildingBlueprintJson.Serialize(draft, false), Is.EqualTo(json));
            Call(designer, "BuildAndClose");
            Assert.That(sends, Is.EqualTo(2));
        }
        finally { Object.DestroyImmediate(host); }
    }
}
}
#endif
