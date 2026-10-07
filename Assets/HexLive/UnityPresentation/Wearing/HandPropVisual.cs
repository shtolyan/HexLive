using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
// §59.5: both hands share loading, geometry validation, scale and grip rules.
internal static class HandPropVisual
{
    public static GameObject Create(string itemId, Transform hand, bool leftHanded)
    {
        // #243: model and grip live in two entries of one atomic object. Do not
        // instantiate a known tool with a fallback grip during the short window
        // in which `main` is ready but `gear-config` is not: _currentPropId
        // would then make that wrong pose permanent for the whole interaction.
        var gearConfig = Config.GearLibrary.ConfigFor(itemId);
        if (Config.GearLibrary.RequiresAuthoredConfig(itemId) && gearConfig == null)
        {
            return null;
        }

        // Runtime props have one source: object/<id>. A null is an asynchronous
        // state, not permission to freeze a different mesh into this hand.
        var model = Config.GearLibrary.LoadPrefab(itemId);
        if (model == null)
        {
            return null;
        }

        var prop = Object.Instantiate(model, hand);
        if (!ObjectFit.HasRenderableGeometry(prop))
        {
            Object.Destroy(prop);
            prop = null;
            return null;
        }

        prop.name = $"HandProp {itemId}";

        // Some native mirrors keep the source-mesh axis correction on the
        // prefab root. Applying the common grip used to overwrite that root
        // rotation, making a correct ground model turn wrong only in hand.
        var prefabAxisCorrection = prop.transform.localRotation;
        var keepPrefabAxisCorrection = gearConfig?.preservePrefabRotationInHand == true;
        var prefabLocalScale = prop.transform.localScale;

        // Placement priority (each higher tier wins): the gear asset's tuned
        // hand pose (edited live in the AxeChopTest scene) → an "AttachPoint"
        // child authored into the model → a built-in default table → the
        // automatic palm-fit below. All in the acting hand's local space.
        if (Config.GearLibrary.TryGetHandPose(
                itemId, leftHanded, out var cfgPos, out var cfgRot, out var cfgScale))
        {
            prop.transform.localPosition = cfgPos;
            prop.transform.localRotation = keepPrefabAxisCorrection
                ? cfgRot * prefabAxisCorrection
                : cfgRot;
            // #136: Renderer.bounds is a world AABB. Under a non-uniformly
            // scaled hand bone, rotating AFTER fitting changes its measured
            // maximum dimension and makes the same spear grow in the hand.
            // Pose first, normalize that final orientation second, then apply
            // the GearConfig scale as a fine multiplier over the prefab scale.
            ApplyObjectFitScale(prop, itemId, prefabLocalScale, cfgScale);
            return prop;
        }

        // §54.12: RESOURCES (stick / leaf / log / fiber / rope…) ride in hand
        // 1:1 — the prefab's NATIVE scale, exactly the size of the same piece
        // on the ground or in the assembled bed, no palm-fit enlargement. One
        // shared grip transform for all of them (dialed in the inspector on
        // the stick), mirrored for the off hand. Tools and weapons are NOT
        // touched — they keep their tuned gear-asset placements above.
        if (itemId.StartsWith("resource.", System.StringComparison.Ordinal))
        {
            var gripPos = new Vector3(0.069f, -0.063f, 0.007f);
            var gripRot = Quaternion.Euler(0f, -95.855f, 0f);
            if (leftHanded)
            {
                gripPos.x = -gripPos.x;
                var e = gripRot.eulerAngles;
                gripRot = Quaternion.Euler(e.x, -e.y, -e.z);
            }

            prop.transform.localPosition = gripPos;
            prop.transform.localRotation = gripRot;
            // localScale stays as instantiated (the prefab/factory's own) — 1:1.
            return prop;
        }

        if (TryAlignByAttachPoint(prop))
        {
            return prop;
        }

        // Hand-tuned placement for specific props (baked in code) wins over the
        // automatic palm-fit — exact position/rotation/scale in the hand's space.
        if (TryGetHandPropTransform(itemId, leftHanded, out var tunedPos, out var tunedRot, out var tunedScale))
        {
            prop.transform.localPosition = tunedPos;
            prop.transform.localRotation = tunedRot;
            prop.transform.localScale = tunedScale;
            return prop;
        }

        // No tuned placement: establish the final pose before measuring world
        // bounds for exactly the same non-uniform-parent reason as above.
        prop.transform.localPosition = Vector3.zero;
        prop.transform.localRotation = Quaternion.identity;
        ApplyObjectFitScale(prop, itemId, prefabLocalScale, Vector3.one);
        return prop;
    }

    /// <summary>#136: one multiply-contract for every fitted actor prop.
    /// The caller must establish the final rotation first because ObjectFit
    /// measures a world AABB under animated, potentially non-uniform bones.</summary>
    internal static void ApplyObjectFitScale(
        GameObject prop, string itemId, Vector3 prefabLocalScale, Vector3 fineMultiplier)
    {
        var fit = ObjectFit.FitScaleFactor(prop, itemId);
        prop.transform.localScale = Vector3.Scale(prefabLocalScale, fineMultiplier) * fit;
    }

    // Attach-point authoring: if the model carries a direct child transform
    // named "AttachPoint", seat the prop so that point lands at the hand origin
    // (identity). Author the AttachPoint at the item's hand scale — no palm-fit
    // is applied on this path. Returns false when there is no such child.
    private static bool TryAlignByAttachPoint(GameObject prop)
    {
        Transform attach = null;
        foreach (Transform child in prop.transform)
        {
            if (child.name == "AttachPoint")
            {
                attach = child;
                break;
            }
        }

        if (attach == null)
        {
            return false;
        }

        var localPos = attach.localPosition;
        var propRot = Quaternion.Inverse(attach.localRotation);
        prop.transform.localScale = Vector3.one;
        prop.transform.localRotation = propRot;
        prop.transform.localPosition = -(propRot * localPos);
        return true;
    }

    // Per-prop hand placement, tuned in the editor and baked here. Baked values
    // are RIGHT-hand (rHand local space). For a left-handed hold we use a
    // hand-tuned left override if one exists, else mirror the right-hand pose
    // across the body's sagittal plane (negate local X + mirror the rotation).
    private static bool TryGetHandPropTransform(string itemId, bool leftHanded,
        out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale)
    {
        switch (itemId)
        {
            // tool.bottle used to bake an ABSOLUTE scale here, tuned against the
            // old procedural bottle mesh; the AI plastic bottle is 2.3x taller,
            // so that number was a lie. Its pose now lives in the gear asset
            // (bottle.asset, «Хват в руке»), where the scale is a MULTIPLIER
            // over ObjectFit and the model can change size freely.
            case "food.coconut":
                localPosition = new Vector3(0.061f, -0.142f, 0.001f);
                localRotation = Quaternion.Euler(0.808f, 0f, 0f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "food.coconut_pierced":
                localPosition = new Vector3(0.09f, -0.111f, -0.089f);
                localRotation = Quaternion.Euler(-24.896f, 16.767f, 16.891f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "food.coconut_open":
                localPosition = new Vector3(0.041f, -0.107f, -0.034f);
                localRotation = Quaternion.Euler(1.22f, 5.477f, 32.526f);
                localScale = new Vector3(0.7718072f, 0.9210232f, 0.7718072f);
                break;
            case "tool.spear":
                localPosition = new Vector3(0.058f, -0.025f, -0.078f);
                localRotation = Quaternion.Euler(-1.544f, -263.963f, 90.255f);
                localScale = new Vector3(0.405947f, 0.405947f, 0.405947f);
                break;
            default:
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                localScale = Vector3.one;
                return false;
        }

        if (leftHanded && !TryGetLeftHandPropTransform(itemId, ref localPosition, ref localRotation))
        {
            localPosition = new Vector3(-localPosition.x, localPosition.y, localPosition.z);
            var e = localRotation.eulerAngles;
            localRotation = Quaternion.Euler(e.x, -e.y, -e.z);
        }

        return true;
    }

    // Hand-tuned LEFT-hand placements (lHand local space). Add a case here once a
    // prop is tuned for the off hand; anything missing falls back to a mirror.
    private static bool TryGetLeftHandPropTransform(string itemId,
        ref Vector3 localPosition, ref Quaternion localRotation)
    {
        switch (itemId)
        {
            case "tool.bottle":
                localPosition = new Vector3(-0.196f, -0.032f, -0.024f);
                localRotation = Quaternion.Euler(91.974f, 0.001007f, -6.520996f);
                return true;
            default:
                return false;
        }
    }

}
}
