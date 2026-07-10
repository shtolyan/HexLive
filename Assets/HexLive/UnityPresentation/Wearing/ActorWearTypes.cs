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

// Spec 31B.2: one wear prefab fits every girl — the mesh swaps per actor.
[Serializable]
public class WearConfig
{
    [FormerlySerializedAs("characterName")] public ActorName actorName;

    public float scale = 1f;

    public Mesh mesh;
}

}
