#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Corrected authoring reference only. Bind its WorldVertices/Normals to a SEPARATE
    // final runtime-base Fit; do not use this reference's original RestWorld snapshot
    // with RestoreStandalonePose. The final prefab keeps every serialized Wear setting.
    // Eligibility is semantic (Head slot + nondefault fit), never a model-ID allowlist.
    static Fit ConstructHeadwearUnitReference(GameObject native, ActorName actor, out JObject evidence)
    {
        evidence = new JObject { ["passed"] = false, ["actor"] = actor.ToString(),
            ["assetsModified"] = false, ["referenceOnly"] = true,
            ["coordinateContract"] = "World metres; authored standalone head unit basis times fit.Scale; fit.position rotated by body head rotation without inherited FBX scale.",
            ["restWorldContract"] = "Original ConstructFit snapshot is not a corrected final-prefab restoration pose; use separate final runtime-base Fit." };
        if (native == null || native.GetComponent<Wear>() == null)
            throw new ArgumentException("Native headwear prefab with Wear required", nameof(native));
        var authored = native.GetComponent<Wear>();
        var headwear = authored.GetHeadwearFit();
        if (!authored.Slots.Contains(VisualWearSlot.Head) || !headwear.Any)
            throw new InvalidOperationException("Unit reference requires authored Head slot and nondefault HeadwearFit");
        string nativePath = AssetDatabase.GetAssetPath(native);
        if (string.IsNullOrEmpty(nativePath) || !File.Exists(nativePath))
            throw new InvalidOperationException("Persisted native authoring prefab required for source-integrity proof");
        string settingsBefore = Settings(authored).ToString(Newtonsoft.Json.Formatting.None);
        string hashBefore = FileHash(nativePath), metaBefore = FileHash(nativePath + ".meta");
        evidence["native"] = nativePath;
        evidence["nativeSha256Base64Before"] = hashBefore;
        evidence["nativeSettings"] = JObject.Parse(settingsBefore);
        GameObject standalone = null;
        Fit result = null;
        try
        {
            var source = SourceMesh(authored, actor, out float sourceActorScale);
            if (source == null) throw new InvalidOperationException("Native headwear has no source mesh");
            result = ConstructFit(native, source, source, actor);
            var pathOrder = result.Paths.ToArray();
            var garment = result.Garment.GetComponent<Wear>();
            var head = garment.HeadwearFitBone;
            var bodyHead = result.Body.GetComponent<BodyBones>().GetBone("head");
            if (head == null || bodyHead == null || head.parent == null)
                throw new InvalidOperationException("Actual ConstructFit did not stitch a headwear head bone");
            evidence["bodyHead"] = HeadwearReferenceTransform(bodyHead);
            evidence["runtimeHeadBefore"] = HeadwearReferenceTransform(head);
            evidence["worldBoundsBefore"] = Box(VertexBounds(result.WorldVertices));
            evidence["sourceActorScale"] = sourceActorScale;

            standalone = Object.Instantiate(native);
            standalone.hideFlags = HideFlags.HideAndDontSave;
            var hip = standalone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "hip");
            var authoredHead = standalone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "head");
            evidence["standaloneHeadBeforeActorScale"] = HeadwearReferenceTransform(authoredHead);
            // ConstructFit materializes both canonical actor rows, using its documented
            // native-donor fallback. Apply THAT exact resulting hip scale to the private
            // standalone rig before measuring its unit basis; no source field is edited.
            float effectiveActorScale = garment.GetConfigScale(actor);
            hip.localScale = Vector3.one * effectiveActorScale;
            var unitScale = authoredHead.lossyScale;
            var desiredScale = Vector3.Scale(unitScale, headwear.Scale);
            if (!float.IsFinite(desiredScale.x) || !float.IsFinite(desiredScale.y) || !float.IsFinite(desiredScale.z) ||
                desiredScale.x <= 0 || desiredScale.y <= 0 || desiredScale.z <= 0)
                throw new InvalidOperationException("Unsupported nonpositive or nonfinite authoring unit basis");
            evidence["effectiveConstructFitActorScale"] = effectiveActorScale;
            evidence["standaloneHeadAfterActorScale"] = HeadwearReferenceTransform(authoredHead);
            evidence["authoredWorldUnitScale"] = Vec(unitScale);
            evidence["desiredHeadWorldScale"] = Vec(desiredScale);
            var desiredPosition = bodyHead.position + bodyHead.rotation * headwear.position;
            var desiredRotation = bodyHead.rotation * Quaternion.Euler(headwear.rotation);
            var desired = Matrix4x4.TRS(desiredPosition, desiredRotation, desiredScale);
            var local = head.parent.worldToLocalMatrix * desired;
            head.localPosition = local.GetColumn(3);
            head.localRotation = local.rotation;
            head.localScale = local.lossyScale;
            float residual = 0;
            for (int i = 0; i < 16; i++) residual = Mathf.Max(residual, Mathf.Abs(head.localToWorldMatrix[i] - desired[i]));
            evidence["desiredHeadWorldColumnMajor"] = new JArray(Enumerable.Range(0, 16).Select(i => desired[i]));
            evidence["worldMatrixResidualMax"] = residual;
            if (!float.IsFinite(residual) || residual > .0002f)
                throw new InvalidOperationException("Corrected head world TRS has unsupported parent shear: " + residual);

            // Recompute every geometric field consumed by simplification/rebinding.
            // The existing tiny fitted quality receipt is deliberately not reused.
            if (result.Baked != null) Object.DestroyImmediate(result.Baked);
            result.Baked = new Mesh();
            result.Skin.BakeMesh(result.Baked, true);
            var matrix = result.Skin.transform.localToWorldMatrix;
            result.WorldVertices = result.Baked.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var normalMatrix = matrix.inverse.transpose;
            result.WorldNormals = result.Baked.normals.Select(n => normalMatrix.MultiplyVector(n).normalized).ToArray();
            result.BoneWorld = result.Bones.Select(b => b.localToWorldMatrix).ToArray();
            if (result.WorldVertices.Length != source.vertexCount || result.WorldNormals.Length != source.vertexCount ||
                !result.Paths.SequenceEqual(pathOrder) || !result.Skin.bones.SequenceEqual(result.Bones))
                throw new InvalidOperationException("Native headwear topology, normal count or bone identity/order changed");
            if (result.WorldNormals.Any(n => !float.IsFinite(n.x) || !float.IsFinite(n.y) || !float.IsFinite(n.z)))
                throw new InvalidOperationException("Corrected headwear contains nonfinite normals");
            evidence["runtimeHeadAfter"] = HeadwearReferenceTransform(head);
            evidence["worldBoundsAfter"] = Box(VertexBounds(result.WorldVertices));
            evidence["vertexCount"] = result.WorldVertices.Length;
            evidence["bonePaths"] = new JArray(result.Paths);
            evidence["boneWorldColumnMajor"] = new JArray(result.BoneWorld.Select(m => new JArray(Enumerable.Range(0, 16).Select(i => m[i]))));
            evidence["freshWorldMetresQualityGateRequired"] = true;
            bool unchanged = hashBefore == FileHash(nativePath) && metaBefore == FileHash(nativePath + ".meta") &&
                settingsBefore == Settings(authored).ToString(Newtonsoft.Json.Formatting.None);
            evidence["sourceAndSettingsUnchanged"] = unchanged;
            if (!unchanged) throw new InvalidOperationException("Native headwear source or settings changed during reference construction");
            evidence["passed"] = true;
            return result;
        }
        catch (Exception error)
        {
            evidence["exception"] = error.ToString();
            result?.Dispose();
            throw;
        }
        finally { if (standalone != null) Object.DestroyImmediate(standalone); }
    }

    static JObject HeadwearReferenceTransform(Transform t) => new JObject {
        ["name"] = t.name, ["parent"] = t.parent == null ? null : t.parent.name,
        ["localPosition"] = Vec(t.localPosition), ["localScale"] = Vec(t.localScale),
        ["worldPosition"] = Vec(t.position), ["worldScale"] = Vec(t.lossyScale),
        ["worldColumnMajor"] = new JArray(Enumerable.Range(0, 16).Select(i => t.localToWorldMatrix[i])) };
}
}
#endif
