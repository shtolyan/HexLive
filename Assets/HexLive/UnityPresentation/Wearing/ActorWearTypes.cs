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
    Outerwear,

    // Рюкзаки и подсумки. Четвёртый слой нужен ровно потому, что сумка — не
    // верхняя одежда: она надевается ПОВЕРХ куртки, а лежа в одном слое с ней
    // они вытесняли бы друг друга, и выбирать пришлось бы между «тепло» и
    // «есть куда сложить».
    //
    // Только в КОНЕЦ: значение слоя хранится в префабах числом, и вставка
    // посередине молча перекрасила бы половину гардероба.
    Bags
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

// Spec §31B.4C: a heeled shoe is not just a mesh. In DAZ the product ships a
// foot POSE — the heel lifts, the toes bend back, and the shoe is modelled
// around that posed foot. Unity has no equivalent, so a girl wearing pumps
// stands flat inside a shoe shaped for a raised heel and her foot pokes
// through the sole.
//
// The whole thing is two rotations and a lift, read straight out of the DAZ
// preset (Flair pumps: foot +45°, toes −40°; Cindy boots: +28.2° / −27.9° —
// the numbers scale with heel height). BodyBones applies them every LateUpdate,
// because the Animator rewrites the legs each frame and would wipe anything
// applied earlier.
[Serializable]
public struct HeelPose
{
    // Plantarflexion: the foot pitches down so the heel comes off the ground.
    public float footDegrees;

    // The toes bend the other way, keeping the ball of the foot flat on the floor.
    public float toeDegrees;

    // Metres the body rises once it is standing on the ball instead of the sole.
    // Without it she sinks into the ground by exactly the heel height.
    public float lift;

    // The bone's own pitch axis. DAZ turns the foot about X, but an FBX import
    // may permute a bone's local axes, so this stays DATA and is tuned in play
    // rather than trusted blind. Zero means "X", so old prefabs need no edit.
    public Vector3 axis;

    public bool Any => Mathf.Abs(footDegrees) > 0.01f || Mathf.Abs(toeDegrees) > 0.01f;

    public Vector3 Axis => axis.sqrMagnitude < 0.0001f ? Vector3.right : axis.normalized;
}

// §31B.4F: посадка головного убора. Шлемы (bug-329) запечены одной константой
// HeadCenterOffset на все тринадцать штук, и половина сидит криво. Это ручная
// поправка: локальный сдвиг/поворот/масштаб кости head САМОЙ ВЕЩИ уже после
// сшивания на тело. Как HeelPose — default читается как «как отшито», поэтому
// поле добавляется в сериализацию Wear аддитивно и старые префабы не трогает.
// Правится из WardrobeTest (гизмо + числовые поля), никогда из кода.
[Serializable]
public struct HeadwearFit
{
    // Метры вдоль локальных осей кости head тела (она — родитель после сшивки).
    public Vector3 position;

    // Эйлеры в градусах, локально там же.
    public Vector3 rotation;

    // Покомпонентный масштаб. НОЛЬ означает «авторский (1,1,1)» — так вектор,
    // которого в старом префабе не было, не схлопывает шлем в точку.
    public Vector3 scale;

    public bool Any => position.sqrMagnitude > 1e-10f || rotation.sqrMagnitude > 1e-8f ||
        (scale.sqrMagnitude > 1e-10f && (scale - Vector3.one).sqrMagnitude > 1e-10f);

    public Vector3 Scale => scale.sqrMagnitude < 1e-10f ? Vector3.one : scale;
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
