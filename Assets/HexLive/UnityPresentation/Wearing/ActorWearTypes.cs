using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace HexLive.UnityPresentation.Wearing
{

// Spec 31B.3: enum orders are serialization contracts with the imported
// wear prefabs (values stored as ints) — never reorder.
public enum ActorName
{
    Molly,
    Jolly,
    Marta,
    Tonny,
    Kshishtof,
    Jana,
    Masha,
    Rita
}

public enum VisualWearLayer
{
    Underwear,
    Wear,
    Outerwear
}

public enum VisualWearSlot
{
    Head,
    EarR,
    EarL,
    Neck,
    Chest,
    ShoulderR,
    ShoulderL,
    ForearmR,
    ForearmL,
    Belly,
    WristR,
    WristL,
    Pelvis,
    HandR,
    HandL,
    ThighR,
    ThighL,
    ShinR,
    ShinL,
    FootR,
    FootL
}

public enum VisualGender
{
    Male,
    Female
}

// §72: which body an actor has. Every garment carries a VisualGender and every
// fit mesh is sculpted for one sex, so a female piece on a male body renders as
// a mangled mesh — the wardrobe has to know who it is dressing.
//
// A lookup rather than a field on the prefab: ActorName's ORDER is a
// serialization contract with the imported wear prefabs and must never change,
// so the sex lives here, in one readable place right next to it.
public static class ActorSex
{
    public static VisualGender Of(ActorName actor)
    {
        switch (actor)
        {
            case ActorName.Tonny:
            case ActorName.Kshishtof:
                return VisualGender.Male;
            default:
                return VisualGender.Female;
        }
    }
}

// Spec 31B.2: one wear prefab fits every girl — the mesh swaps per actor.
[Serializable]
public class WearConfig
{
    [FormerlySerializedAs("characterName")] public ActorName actorName;

    public float scale = 1f;

    // Spec §31B.4B: hair fit. Every hairstyle in the 2026-08 drop was authored
    // on the generic Genesis3 head, so on a girl with her own head morph it can
    // sit low over the eyes or ride high. Metres along the head bone's local Y.
    // Garments leave this at 0 — they are refitted per actor as real meshes.
    public float heightOffset;

    public Mesh mesh;
}

}
