using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Content
{

// §74: a colonist is a COMPOSITION, not a shipped character.
//
// Before this, one string (NPCState.ActorMesh) decided everything at once —
// body, skin/eye materials, hairstyle, voice bank — because the four girls
// were imported whole and named after themselves. Four independent traits now
// roll separately, so Molly's body can wear Jana's skin, someone else's hair
// and a third voice, under a name of its own.
//
// The pools are IDS, never player-facing text (§58): the mesh/skin ids are
// actor prefab names, hair ids are prefab names, voice ids are the folder
// names under StreamingAssets/HexLive/Sfx/Voices/, and a name id resolves
// through the I2 term `npc.<lowercase id>.name`.
//
// WHAT MUST NOT MOVE: garment fits (WearConfig) and the skin paint-point maps
// (`skin_<Actor>`) key off the MESH, because they are properties of the
// geometry. Only the material set, the hair prefab and the voice folder are
// free to come from someone else.
public static class ColonistAppearance
{
    // Actor prefabs under Resources/HexLive/Actors/. Female only — the §72
    // outsider's male body is authored explicitly and never rolled.
    public static readonly string[] Meshes = { "Marta", "Molly", "Jana", "Jolly" };

    // The donor whose 17 body materials (skin/eyes/lashes/nails) get mapped
    // onto the mesh by material NAME. Same four actresses: they all carry the
    // same slot names, which is what makes the swap a pure rename (§31B.1a
    // regenerated Jolly's from Molly's, so even the shader setup matches).
    public static readonly string[] SkinSets = { "Marta", "Molly", "Jana", "Jolly" };

    // Hair prefabs (§31B.4B). Any Genesis3 hair fits any actress — it skins to
    // the shared head/neck bones — so this pool is not per-mesh. Kept in sync
    // with Assets/ImportedActors/Hair/<Name>.prefab via the appearance catalog.
    //
    // The COLOUR is not here and not anywhere in the simulation: it is derived
    // from the colonist's id on the view side (§103.5), because a hairstyle's
    // colour folders are art, and the sim would have to carry a copy of the
    // whole list to roll one.
    public static readonly string[] Hairstyles =
    {
        "AdellHair", "AsukaHair", "BendineHair", "Bob3Hair", "ChunkyHair",
        "EilisHair", "Hair07", "JelikaHair_32434", "JenniferHair",
        "LeonyPonytail", "LoonaHair", "LowPonytail", "Neu09Hair", "OnyxHair",
        "ShilohHair", "TootsieRollHair",
    };

    // Voices/<char>/ — four female banks of 189 lines each (§67.6). The male
    // `kshishtof` bank is deliberately absent.
    public static readonly string[] VoiceBanks = { "marta", "molly", "jana", "jolly" };

    // Name ids. Latin, because the id travels through traces, saves and the
    // GameObject name; the player sees `npc.<id>.name` from I2 (EN + RU).
    // The four canonical names stay in the pool on purpose — "Marta" is now
    // just a name, and it may well land on Jana's body.
    // NOT in the pool: Tonny / Masha / Rita — they are ActorName members, and
    // a name that parses to an actor invites exactly the confusion §74 removes.
    public static readonly string[] NameIds =
    {
        "Marta", "Molly", "Jana", "Jolly",
        "Nadia", "Vera", "Mira", "Iris", "Lena", "Nika", "Sonya", "Dasha",
        "Kira", "Yuna", "Alma", "Elsa", "Ines", "Runa", "Tessa", "Noor",
        "Lila", "Sana", "Emi", "Zoya", "Maya", "Freya", "Wren", "Talia",
    };

    // "Bald" — a hairstyle id the view understands as "no hair prefab".
    // Not in the pool (nobody washes ashore shaved), but the field accepts it
    // so a hand-authored bootstrap or a future event can use it.
    public const string NoHair = "none";

    public readonly struct Look
    {
        public readonly string Mesh;
        public readonly string SkinSet;
        public readonly string Hairstyle;
        public readonly string VoiceBank;
        public readonly string NameId;

        public Look(string mesh, string skinSet, string hairstyle, string voiceBank, string nameId)
        {
            Mesh = mesh;
            SkinSet = skinSet;
            Hairstyle = hairstyle;
            VoiceBank = voiceBank;
            NameId = nameId;
        }
    }

    // Deterministic per (seed, npcId) — the same seed always rebuilds the same
    // colony, which is what keeps a soak, a bug report and a save reproducible
    // (spec 29C.1). Salt block (74, 74xx) is unused by anything else.
    //
    // Individual traits are FREE to repeat: two girls may share a body, a skin
    // set or a voice — that is the point, "maybe two Janas happen". Exactly
    // three things are de-duplicated:
    //
    // - the NAME, because two identical names are unreadable in the UI;
    // - the HAIRSTYLE, because hair is the loudest silhouette cue and the pool
    //   (16) comfortably covers the colony — every girl gets her own;
    // - the VISIBLE look (mesh + skin set + hairstyle), because two women the
    //   eye cannot tell apart are not variety, they are a bug report waiting to
    //   happen. With unique hair this key is unique for free; it only earns its
    //   keep as the FALLBACK when more girls than hairstyles exist. Voice is
    //   NOT part of that key on purpose: a shared voice is heard one line at a
    //   time and reads as a family resemblance, while a shared silhouette is on
    //   screen permanently.
    //
    // All resolve by walking FORWARD from the rolled index — deterministic and
    // order-stable, so the same seed lands on the same colony every time.
    // Collisions walk the hairstyle axis: it is the widest pool (16), so a
    // dispute is settled by changing her hair rather than her body.
    public static Look Roll(int seed, int npcId,
        ICollection<string> takenNames, ICollection<string> takenLooks,
        ICollection<string> takenHairstyles)
    {
        var mesh = Meshes[(int)(MathUtil.Hash01(seed, npcId, 74, 7401) * Meshes.Length)];
        var skin = SkinSets[(int)(MathUtil.Hash01(seed, npcId, 74, 7402) * SkinSets.Length)];
        var voice = VoiceBanks[(int)(MathUtil.Hash01(seed, npcId, 74, 7404) * VoiceBanks.Length)];

        var hairStart = (int)(MathUtil.Hash01(seed, npcId, 74, 7403) * Hairstyles.Length);
        var hair = Hairstyles[hairStart];
        var hairFound = false;
        if (takenHairstyles != null)
        {
            for (var step = 0; step < Hairstyles.Length; step++)
            {
                var candidate = Hairstyles[(hairStart + step) % Hairstyles.Length];
                if (!takenHairstyles.Contains(candidate) &&
                    (takenLooks == null || !takenLooks.Contains(LookKey(mesh, skin, candidate))))
                {
                    hair = candidate;
                    hairFound = true;
                    break;
                }
            }
        }
        // Fallback for a colony larger than the hair pool: give up on unique
        // hair and only keep the full look distinct, as before.
        if (!hairFound && takenLooks != null)
        {
            for (var step = 0; step < Hairstyles.Length; step++)
            {
                var candidate = Hairstyles[(hairStart + step) % Hairstyles.Length];
                if (!takenLooks.Contains(LookKey(mesh, skin, candidate)))
                {
                    hair = candidate;
                    break;
                }
            }
        }

        var nameStart = (int)(MathUtil.Hash01(seed, npcId, 74, 7405) * NameIds.Length);
        var name = NameIds[nameStart];
        if (takenNames != null)
        {
            for (var step = 0; step < NameIds.Length; step++)
            {
                var candidate = NameIds[(nameStart + step) % NameIds.Length];
                if (!takenNames.Contains(candidate))
                {
                    name = candidate;
                    break;
                }
            }
        }

        return new Look(mesh, skin, hair, voice, name);
    }

    /// <summary>
    /// What the player SEES of a colonist — body, face and hair. Two girls must
    /// never share this key; name and voice are separate axes.
    /// </summary>
    public static string LookKey(string mesh, string skinSet, string hairstyle)
        => mesh + "/" + skinSet + "/" + hairstyle;
}

}
