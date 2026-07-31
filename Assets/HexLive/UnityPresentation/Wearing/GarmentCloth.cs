using MagicaCloth2;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Cloth simulation for a garment that should swing instead of riding the skin
// (skirts, long hems). Put this next to the Wear component on the prefab and
// give it a paint map; Wear.Construct calls Build once the piece is fitted.
//
// MagicaCloth2 MeshCloth, built at RUNTIME rather than serialized, because
// Wear.Construct swaps in the per-actor mesh (Marta/Molly/Jana/Jolly each have
// their own fitted copy) and re-parents the garment's bones onto the body's.
// A cloth component baked into the prefab would have captured the wrong mesh.
//
// The paint map says which vertices do what. MagicaCloth point-samples it at
// each vertex's uv0 texel: green = swings, red = anchored to the animated pose,
// anything else = left to plain skinning. Because it is looked up through UVs
// and all four fitted meshes share one UV layout, a single map serves them all.
[DisallowMultipleComponent]
public sealed class GarmentCloth : MonoBehaviour
{
    [Tooltip("Fixed(red) / Move(green) / ignore(black) per-vertex map, sampled through uv0. " +
             "Must be Read/Write enabled with mipmaps OFF — MagicaCloth drops to a " +
             "<=128x128 mip when one exists, which would smear the anchor band.")]
    [SerializeField] private Texture2D paintMap;

    [Header("Simulation")]
    [SerializeField, Range(0f, 10f)] private float gravity = 3f;
    [SerializeField, Range(0f, 1f)] private float damping = 0.05f;
    [Tooltip("Pull back to the animated pose. Higher = stiffer, stays closer to the skinned shape.")]
    [SerializeField, Range(0f, 1f)] private float restoreStiffness = 0.05f;
    [Tooltip("How far a vertex may swing away from its animated pose, in degrees.")]
    [SerializeField, Range(0f, 90f)] private float limitAngle = 45f;
    [Tooltip("Resistance to stretching. Low values let the hem flare.")]
    [SerializeField, Range(0f, 1f)] private float distanceStiffness = 0.5f;
    [Tooltip("How much the walk cycle drags the cloth along. 1 = fully carried, 0 = left behind.")]
    [SerializeField, Range(0f, 1f)] private float depthInertia = 0.7f;
    // Every particle is held this far off every collider, so it is the real
    // "how tight can the garment sit" knob. MagicaCloth's own default is 0.02,
    // which parks the fabric 2 cm off the skin -- fine for a loose skirt,
    // hopeless for a fitted one, and it also makes neighbouring particles
    // (proxy spacing ~12 mm) shove each other around.
    [Tooltip("Particle radius in metres — the standoff between cloth and body. Small = tighter fit.")]
    [SerializeField, Range(0.001f, 0.05f)] private float particleRadius = 0.005f;

    [Header("Proxy mesh")]
    // Vertex-merge distance for the simulated proxy, in metres. This is the one
    // setting a PLEATED garment is fussy about: the folds of a pleat sit only
    // millimetres apart, so MagicaCloth's own skirt-demo value (0.0212) welds
    // neighbouring pleats into each other and the skirt renders as spiky rags.
    // 0.012 keeps the pleats and still reduces 29 K verts to a ~4 K proxy.
    // Going much finer is not an option anyway: the proxy is capped at 32767
    // edges, and 0.006 already fails to build with ProxyMesh_Over32767Edges.
    [Tooltip("Vertex-merge distances for the simulated proxy, in metres. Larger = cheaper and " +
             "coarser; too large welds adjacent pleats together and shreds the garment.")]
    [SerializeField] private float reductionSimpleDistance = 0.012f;
    [SerializeField] private float reductionShapeDistance = 0.014f;

    [Header("Culling")]
    [SerializeField] private float distanceCullingLength = 25f;

    private MagicaCloth _cloth;

    public bool IsBuilt => _cloth != null;

    // Called by Wear.Construct, after the per-actor mesh is in place and the
    // garment's bones have been stitched onto the body's.
    public void Build(BodyBones bodyBones, SkinnedMeshRenderer meshRenderer)
    {
        // BuildAndRun is a play-mode-only call; outside it the garment just
        // renders with its normal skinning.
        if (_cloth != null || meshRenderer == null || Application.isPlaying == false)
        {
            return;
        }

        if (paintMap == null)
        {
            Debug.LogWarning($"GarmentCloth '{name}': no paint map — cloth skipped.", this);
            return;
        }

        if (paintMap.isReadable == false)
        {
            Debug.LogWarning($"GarmentCloth '{name}': paint map '{paintMap.name}' is not " +
                             "Read/Write enabled — cloth skipped.", this);
            return;
        }

        // Simulated vertices leave the skinned silhouette, so the renderer's
        // own bounds no longer describe it — same trap the OnyxHair spring hit.
        meshRenderer.updateWhenOffscreen = true;

        var host = new GameObject("MagicaCloth");
        host.transform.SetParent(transform, false);

        _cloth = host.AddComponent<MagicaCloth>();
        var sdata = _cloth.SerializeData;

        sdata.clothType = ClothProcess.ClothType.MeshCloth;
        sdata.sourceRenderers.Add(meshRenderer);

        sdata.paintMode = ClothSerializeData.PaintMode.Texture_Fixed_Move;
        sdata.paintMaps.Add(paintMap);

        sdata.reductionSetting.simpleDistance = reductionSimpleDistance;
        sdata.reductionSetting.shapeDistance = reductionShapeDistance;

        sdata.gravity = gravity;
        sdata.damping = new CurveSerializeData(damping);
        sdata.radius = new CurveSerializeData(particleRadius);
        sdata.angleRestorationConstraint.stiffness.SetValue(restoreStiffness, 1.0f, 0.5f, true);
        sdata.angleRestorationConstraint.velocityAttenuation = 0.5f;
        sdata.angleLimitConstraint.useAngleLimit = true;
        sdata.angleLimitConstraint.limitAngle.SetValue(limitAngle, 0.0f, 1.0f, true);
        sdata.distanceConstraint.stiffness.SetValue(distanceStiffness, 1.0f, 0.5f, true);
        sdata.tetherConstraint.distanceCompression = 0.9f;
        sdata.inertiaConstraint.depthInertia = depthInertia;
        sdata.inertiaConstraint.movementSpeedLimit.SetValue(true, 3.0f);
        sdata.inertiaConstraint.particleSpeedLimit.SetValue(true, 3.0f);

        // Legs and hips, shared across every cloth piece this girl wears.
        // Edge, not Point: Point only pushes the PROXY particles out, and the
        // rendered mesh (6 mm vertices) dips through between them — measured
        // 159 fabric vertices inside the skin on Point, 100 on Edge.
        sdata.colliderCollisionConstraint.mode = ColliderCollisionConstraint.Mode.Edge;
        sdata.colliderCollisionConstraint.friction = 0.10f;
        var colliders = ClothBodyColliders.For(bodyBones);
        if (colliders != null)
        {
            foreach (var collider in colliders)
            {
                sdata.colliderCollisionConstraint.colliderList.Add(collider);
            }
        }

        // Same crowd budget as the body/hair springs: stop simulating a girl
        // nobody is looking at.
        sdata.cullingSettings.cameraCullingMode = CullingSettings.CameraCullingMode.AnimatorLinkage;
        sdata.cullingSettings.distanceCullingLength =
            new CheckSliderSerializeData(true, distanceCullingLength);

        if (_cloth.BuildAndRun() == false)
        {
            Debug.LogWarning($"GarmentCloth '{name}': MagicaCloth build failed — " +
                             "the garment falls back to plain skinning.", this);
            Destroy(host);
            _cloth = null;
        }
    }
}

}
