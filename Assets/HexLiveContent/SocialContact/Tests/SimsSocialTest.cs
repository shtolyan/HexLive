#if UNITY_EDITOR
using System;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.TwoPeopleTest;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

public sealed class SimsSocialTest : MonoBehaviour
{
    internal const string Root = "Assets/HexLiveContent/SocialContact";
    private const string Art = "Assets/HexLiveContent/RuntimeSource";

    public enum Interaction { Hug, FirstKiss, BroHug }
    public enum Pairing { Mixed, SameSex }

    [SerializeField] private Interaction interaction;
    [SerializeField] private Pairing pairing;
    [SerializeField] private bool playing = true;
    [SerializeField, Range(0f, 2f)] private float speed = 1f;
    [SerializeField, Range(0f, 1f)] private float progress;
    [SerializeField, Range(-0.25f, 0.25f)] private float separationAdjustment;
    [SerializeField] private bool fitKissContact = true;
    [SerializeField] private bool fitHugContact = true;

    private readonly Animator[] actors = new Animator[4];
    private readonly Transform[] actorRoots = new Transform[4];
    private readonly AnimationClipPlayable[] tracks = new AnimationClipPlayable[4];
    private readonly SocialKissContact[] kissRigs = new SocialKissContact[4];
    private readonly SocialHugContact[] hugRigs = new SocialHugContact[4];
    private PlayableGraph graph;
    private Interaction currentInteraction;
    private Pairing currentPairing;
    private float duration;

    private void Awake()
    {
        Application.runInBackground = true;
        CreateStage();
        Spawn(0, ActorName.Jana, "Яна");
        Spawn(1, ActorName.Kshishtof, "Кшиштоф A");
        Spawn(2, ActorName.Molly, "Молли");
        Spawn(3, ActorName.Kshishtof, "Кшиштоф B");
        BindClips();
        Sample();
    }

    private void Update()
    {
        if (interaction != currentInteraction || pairing != currentPairing)
        {
            progress = 0;
            BindClips();
        }
        if (!graph.IsValid()) return;
        if (playing)
            progress = Mathf.Repeat(progress + Time.unscaledDeltaTime * speed / duration, 1f);
        Sample();
    }

    private void OnDestroy()
    {
        if (graph.IsValid()) graph.Destroy();
        foreach (var rig in hugRigs) rig?.Dispose();
    }

    private void BindClips()
    {
        if (graph.IsValid()) graph.Destroy();
        currentInteraction = interaction;
        currentPairing = pairing;
        var stem = interaction switch
        {
            Interaction.Hug => "a2a_friendly_T_hug_succeed",
            Interaction.FirstKiss => "a2a_romantic_T_kiss_firstKiss_succeed",
            _ => "a2a_friendly_T_broHug_succeed"
        };
        var x = GetClip(stem + "_x");
        var y = GetClip(stem + "_y");
        if (Mathf.Abs(x.length - y.length) > 1f / 30f)
            throw new InvalidOperationException($"Sims pair durations differ: {stem}");
        duration = Mathf.Min(x.length, y.length);
        graph = PlayableGraph.Create("Sims social interactions");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        for (var i = 0; i < actors.Length; i++)
        {
            // In mixed mode, swap X/Y on the second pair to compare both body assignments.
            var roleX = pairing == Pairing.Mixed ? i == 0 || i == 3 : i < 2;
            tracks[i] = AnimationClipPlayable.Create(graph, roleX ? x : y);
            tracks[i].SetApplyFootIK(false);
            tracks[i].SetSpeed(0);
            AnimationPlayableOutput.Create(graph, actors[i].name, actors[i]).SetSourcePlayable(tracks[i]);
        }
        graph.Play();
    }

    private void Sample()
    {
        if (!graph.IsValid()) return;
        var separation = (interaction switch
        {
            Interaction.Hug => 0.76f,
            Interaction.FirstKiss => 0.70f,
            _ => 0.71f
        }) + separationAdjustment;
        var second = pairing == Pairing.Mixed ? 1 : 2;
        var third = pairing == Pairing.Mixed ? 2 : 1;
        Place(actorRoots[0], -1.2f - separation / 2f, 90f);
        Place(actorRoots[second], -1.2f + separation / 2f, 270f);
        Place(actorRoots[third], 1.2f - separation / 2f, 90f);
        Place(actorRoots[3], 1.2f + separation / 2f, 270f);
        foreach (var rig in hugRigs) rig.RestoreFingerPose();
        foreach (var track in tracks) track.SetTime(progress * duration);
        graph.Evaluate(0f);
        if (interaction == Interaction.Hug && fitHugContact)
        {
            var weight = SocialHugContact.Weight(progress);
            if (weight > 0f)
            {
                foreach (var rig in hugRigs) rig.ReadPose();
                FitHug(0, second, weight);
                FitHug(third, 3, weight);
            }
        }
        if (interaction == Interaction.FirstKiss && fitKissContact)
        {
            var weight = SocialKissContact.Weight(progress);
            SocialKissContact.Fit(kissRigs[0], kissRigs[second], weight);
            SocialKissContact.Fit(kissRigs[third], kissRigs[3], weight);
        }
    }

    private void FitHug(int first, int second, float weight)
    {
        hugRigs[first].Fit(hugRigs[second], weight, progress);
        hugRigs[second].Fit(hugRigs[first], weight, progress);
    }

    private static void Place(Transform root, float x, float yaw) =>
        root.SetPositionAndRotation(new Vector3(x, 0f, 0f), Quaternion.Euler(0f, yaw, 0f));

    private static AnimationClip GetClip(string name) => AssetDatabase
        .LoadAllAssetsAtPath($"{Root}/Clips/{name}.fbx").OfType<AnimationClip>()
        .Single(clip => clip.name == name && clip.humanMotion && clip.length > 0f);

    private void Spawn(int index, ActorName kind, string label)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art}/Actors/{kind}.prefab");
        var root = new GameObject(label).transform;
        root.SetParent(transform, false);
        var body = Instantiate(prefab, root);
        body.name = kind.ToString();
        var animator = body.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
            throw new InvalidOperationException($"Invalid HexLive humanoid: {kind}");
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.runtimeAnimatorController = null;
        foreach (var ik in body.GetComponentsInChildren<IK>(true)) ik.enabled = false;
        var bones = body.GetComponentInChildren<BodyBones>();
        bones.Construct(kind);
        if (kind == ActorName.Kshishtof)
        {
            Equip(bones, "TonnyFlash", "Assets/HexLiveContent/Wear/TonnyFlash/TonnyFlash.prefab");
            Equip(bones, "FCO Pants Male", "Assets/HexLiveContent/Wear/FCO Pants Male/FCO Pants Male.prefab");
        }
        else
        {
            Equip(bones, "clothing.shirt_amy", Art + "/Wear/clothing.shirt_amy/AmyShirt.prefab");
            Equip(bones, "clothing.pants_anarchy", Art + "/Wear/clothing.pants_anarchy/AnarchyPants.prefab");
        }
        var skins = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var skin in skins)
        {
            skin.updateWhenOffscreen = true;
            if (skin.name.Contains("Genitalia", StringComparison.OrdinalIgnoreCase))
                skin.enabled = false;
        }
        actorRoots[index] = root;
        actors[index] = animator;
        kissRigs[index] = new SocialKissContact(animator);
        hugRigs[index] = new SocialHugContact(animator, root, skins);
    }

    private static void Equip(BodyBones bones, string id, string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
        if (wear == null) throw new InvalidOperationException($"Missing test garment: {path}");
        bones.Equip(id, wear, null);
        if (!bones.IsEquipped(id))
            Debug.LogWarning($"[SimsSocialTest] Garment has no fit on {bones.name}: {path}");
    }

    private void CreateStage()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Test floor";
        floor.transform.SetParent(transform, false);
        floor.transform.localScale = new Vector3(0.65f, 1f, 0.42f);
        var camera = new GameObject("Social test camera").AddComponent<Camera>();
        camera.transform.SetParent(transform, false);
        camera.transform.position = new Vector3(0f, 1.85f, -4.8f);
        camera.transform.LookAt(new Vector3(0f, 0.96f, 0f));
        camera.fieldOfView = 36f;
        camera.cullingMask &= ~LayerMask.GetMask("Portrait");
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.17f, 0.20f, 0.24f);
        var light = new GameObject("Key light").AddComponent<Light>();
        light.transform.SetParent(transform, false);
        light.transform.rotation = Quaternion.Euler(48f, -30f, 0f);
        light.type = LightType.Directional;
        light.intensity = 1.25f;
        light.shadows = LightShadows.Soft;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.46f, 0.46f, 0.49f);
    }
}
#endif
